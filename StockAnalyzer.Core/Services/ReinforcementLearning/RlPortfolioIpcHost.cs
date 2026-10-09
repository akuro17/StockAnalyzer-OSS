using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>
/// Core side of the portfolio worker protocol: one serialized local request stream, bounded lifetime, no retry of financial mutations. The
/// worker proposes logits only; the host masks, quantizes and steps the Core-owned <see cref="RlPortfolioSession"/>, builds every observation and
/// decides the candidate selection from exact decimal validation returns. The envelope matches <c>RlIpcHost</c> (protocol version, run id,
/// contract hash, strictly increasing sequence, session id).
/// </summary>
internal sealed class RlPortfolioIpcHost : IDisposable
{
    public const string TrainRole = "train";
    public const string ValidationRole = "validation";

    private sealed record Session(RlPortfolioSession Trading, RlPanelBlock Block, string Role, int Seed, int Checkpoint, ImmutableArray<int> Tradable)
    {
        public RlPortfolioSnapshot Snapshot { get; set; } = null!;
    }

    private readonly Guid _runId;
    private readonly IRlPanelBars _bars;
    private readonly RlPanelCatalog _catalog;
    private readonly FixedScaler _scaler;
    private readonly string _contractHash;
    private readonly int _maxSymbols;
    private readonly BacktestConfiguration _costs;
    private readonly int _maxWeightPercent;
    private readonly IProgress<RlTrainingProgress>? _progress;
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _disposedSessions = new(StringComparer.Ordinal);
    private readonly Dictionary<(int Seed, int Checkpoint), Dictionary<string, decimal>> _validation = new();
    private readonly Dictionary<int, int> _trainingTransitions = new();
    private long _sequence;
    private bool _ready;

    public (int Seed, int Checkpoint)? Selected { get; private set; }

    /// <summary>Training transitions stepped per seed, ordered by seed.</summary>
    public ImmutableArray<RlPortfolioSeedProgress> TrainingTransitionsBySeed
        => _trainingTransitions.OrderBy(p => p.Key).Select(p => new RlPortfolioSeedProgress(p.Key, p.Value)).ToImmutableArray();

    /// <summary>
    /// Every candidate with a validation result for each validation block, in the selection order (highest exact decimal mean, then smaller
    /// checkpoint, then smaller seed). The first one is the only acceptable selection.
    /// </summary>
    public ImmutableArray<RlPortfolioCandidate> Candidates
    {
        get
        {
            int needed = Blocks(ValidationRole).Length;
            return _validation.Where(p => p.Value.Count == needed)
                .Select(p => new RlPortfolioCandidate(p.Key.Seed, p.Key.Checkpoint, p.Value.Values.Sum() / p.Value.Count,
                    p.Value.OrderBy(b => b.Key, StringComparer.Ordinal).Select(b => new RlPortfolioBlockReturn(b.Key, b.Value)).ToImmutableArray()))
                .OrderByDescending(c => c.MeanReturn).ThenBy(c => c.Checkpoint).ThenBy(c => c.Seed).ToImmutableArray();
        }
    }

    public RlPortfolioIpcHost(Guid runId, IRlPanelBars bars, RlPanelCatalog catalog, FixedScaler scaler, int maxSymbols,
        BacktestConfiguration costs, int maxWeightPercent, IProgress<RlTrainingProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(scaler);
        ArgumentNullException.ThrowIfNull(costs);
        if (maxSymbols < RlPortfolioContract.MinimumSymbols || bars.SymbolCount > maxSymbols)
            throw new ArgumentException("The model needs at least two slots and one slot per panel symbol.", nameof(maxSymbols));
        if (!RlPortfolioObservationLayout.TryFromContractHash(scaler.FeatureContractHash, out var layout))
            throw new ArgumentException("The scaler names no known observation layout.", nameof(scaler));
        _contractHash = layout.ContractHash;
        _runId = runId; _bars = bars; _catalog = catalog; _scaler = scaler; _maxSymbols = maxSymbols; _costs = costs;
        _maxWeightPercent = maxWeightPercent; _progress = progress;
    }

    public object Handle(JsonElement request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long sequence = request.GetProperty("sequence").GetInt64();
        if (request.GetProperty("protocol_version").GetInt32() != RlContract.SchemaVersion
            || request.GetProperty("run_id").GetString() != _runId.ToString("N")
            || request.GetProperty("contract_hash").GetString() != _contractHash || sequence != _sequence + 1)
            throw new InvalidDataException("Stale, duplicate, or incompatible RL portfolio request.");
        _sequence = sequence;
        string method = request.GetProperty("method").GetString() ?? "";
        string sessionId = request.GetProperty("session_id").GetString() ?? "";
        var payload = request.GetProperty("payload");
        object result;
        switch (method)
        {
            case "Ready":
                if (_ready) throw new InvalidDataException("Duplicate RL portfolio readiness.");
                _ready = true;
                result = new { ready = true }; break;
            case "Progress":
                _progress?.Report(new(_runId, sequence, "Training", payload.GetProperty("message").GetString() ?? "", payload.GetProperty("steps").GetInt32()));
                result = new { accepted = true }; break;
            case "Reset": result = Reset(sessionId, payload, cancellationToken); break;
            case "Step": result = Step(sessionId, payload, cancellationToken); break;
            case "Dispose":
                if (!_sessions.Remove(sessionId, out var disposed)) throw new InvalidDataException("Unknown/already disposed RL portfolio session.");
                disposed.Trading.Dispose(); _disposedSessions.Add(sessionId); result = new { disposed = true }; break;
            case "Select": result = Select(payload); break;
            default: throw new InvalidDataException("Unknown RL portfolio protocol method.");
        }
        return new { protocol_version = RlContract.SchemaVersion, run_id = _runId.ToString("N"), sequence,
            session_id = sessionId, contract_hash = _contractHash, result };
    }

    private ImmutableArray<RlPanelBlock> Blocks(string role) => role switch
    {
        TrainRole => _catalog.CellBlocks(RlPortfolioContract.TrainPartition, RlPanelSymbolGroup.Train),
        ValidationRole => RlPortfolioSelectionCell.Blocks(_catalog),
        _ => throw new InvalidDataException("Unsupported RL portfolio role."),
    };

    private object Reset(string id, JsonElement payload, CancellationToken ct)
    {
        if (!_ready || !Guid.TryParseExact(id, "N", out _) || _disposedSessions.Contains(id))
            throw new InvalidDataException("RL portfolio reset identity/readiness is invalid.");
        string role = payload.GetProperty("role").GetString() ?? "";
        string blockId = payload.GetProperty("block_id").GetString() ?? "";
        int seed = payload.GetProperty("seed").GetInt32();
        int checkpoint = payload.GetProperty("checkpoint").GetInt32();
        var block = Blocks(role).FirstOrDefault(b => b.Id == blockId) ?? throw new InvalidDataException("Block is outside the declared partition.");
        if (_sessions.Remove(id, out var previous)) previous.Trading.Dispose();
        // At most one training session and one temporarily paused evaluation session.
        if (_sessions.Count >= RlContract.EnvironmentCount + 1) throw new InvalidDataException("RL portfolio active session limit exceeded.");
        var tradable = _catalog.EligibleIn(block, RlPanelSymbolGroup.Train);
        var trading = new RlPortfolioSession(_bars, block, tradable, _costs);
        var session = new Session(trading, block, role, seed, checkpoint, tradable);
        _sessions.Add(id, session);
        session.Snapshot = trading.Reset(ct);
        return new { observation = Observation(session), info = Info(session, session.Snapshot) };
    }

    private object Step(string id, JsonElement payload, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(id, out var session)) throw new InvalidDataException("Unknown RL portfolio session.");
        var logits = ReadLogits(payload);
        var mask = RlPortfolioObservationBuilder.SlotMask(_bars.SymbolCount, _maxSymbols, session.Tradable);
        var weights = RlPortfolioWeights.FromLogits(logits, mask, _maxWeightPercent);
        var result = session.Trading.Step(weights.Assets.Take(_bars.SymbolCount).ToImmutableArray(), ct);
        session.Snapshot = result.Snapshot;
        if (session.Role == TrainRole) _trainingTransitions[session.Seed] = _trainingTransitions.GetValueOrDefault(session.Seed) + 1;
        var info = Info(session, result.Snapshot);
        info["decimal_reward"] = result.Reward.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (result.Terminated)
        {
            decimal terminalReturn = (result.Snapshot.Equity - _costs.InitialCapital) / _costs.InitialCapital;
            info["terminal_return"] = terminalReturn.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (session.Role == ValidationRole)
            {
                var key = (session.Seed, session.Checkpoint);
                if (!_validation.TryGetValue(key, out var reports)) _validation[key] = reports = new Dictionary<string, decimal>(StringComparer.Ordinal);
                if (!reports.TryAdd(session.Block.Id, terminalReturn)) throw new InvalidDataException("Duplicate RL portfolio validation episode.");
                if (reports.Count == Blocks(ValidationRole).Length)
                    info["mean_return"] = (reports.Values.Sum() / reports.Count).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        return new { observation = Observation(session), reward = (float)result.Reward, terminated = result.Terminated, truncated = false, info };
    }

    private object Select(JsonElement payload)
    {
        if (Selected is not null) throw new InvalidDataException("Duplicate candidate selection.");
        var candidates = Candidates;
        if (candidates.IsEmpty) throw new InvalidDataException("Candidate validation is incomplete.");
        var best = candidates[0];
        if (payload.GetProperty("seed").GetInt32() != best.Seed || payload.GetProperty("checkpoint").GetInt32() != best.Checkpoint)
            throw new InvalidDataException("Candidate selection disagrees with exact decimal Core validation.");
        Selected = (best.Seed, best.Checkpoint);
        return new { selected = true };
    }

    private float[] ReadLogits(JsonElement payload)
    {
        var actions = payload.GetProperty("actions");
        if (actions.ValueKind != JsonValueKind.Array || actions.GetArrayLength() != _maxSymbols + 1)
            throw new InvalidDataException("RL portfolio actions must hold one cash logit plus one logit per slot.");
        var logits = new float[_maxSymbols + 1];
        int index = 0;
        foreach (var value in actions.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Number || !float.IsFinite(logits[index] = (float)value.GetDouble()))
                throw new InvalidDataException("RL portfolio actions must be finite float32 numbers.");
            index++;
        }
        return logits;
    }

    private float[] Observation(Session session) => RlPortfolioObservationBuilder.Build(_bars, _maxSymbols, session.Snapshot, session.Tradable, _scaler);

    private static Dictionary<string, object> Info(Session session, RlPortfolioSnapshot snapshot) => new()
    {
        ["block_id"] = session.Block.Id,
        ["role"] = session.Role,
        ["completed_decisions"] = snapshot.CompletedDecisions,
        ["remaining_decisions"] = snapshot.RemainingDecisions,
        ["equity"] = snapshot.Equity.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["cash_weight"] = snapshot.CashWeight.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["order_count"] = snapshot.OrderCount,
        ["rejected_buy_count"] = snapshot.RejectedBuyCount,
    };

    public void Dispose()
    {
        foreach (var session in _sessions.Values) session.Trading.Dispose();
        _sessions.Clear();
    }
}
