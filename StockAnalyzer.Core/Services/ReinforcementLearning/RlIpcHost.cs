using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services.Backtest.Engine;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>One serialized local request stream, bounded lifetime, no retry of financial mutations.</summary>
internal sealed partial class RlIpcHost : IDisposable
{
    private sealed record Session(IRlTradingSession Trading, RlEpisodeBlock Block, string Role, int Seed, int Checkpoint)
    {
        public int Rejections { get; set; }
        public int NoOps { get; set; }
        public int PublishedFills { get; set; }
        public RlSessionSnapshot? Snapshot { get; set; }
        public List<RlEntryExecution> EntryAttempts { get; } = new();
        public List<float[]> RawActions { get; } = new();
    }
    private readonly Guid _runId;
    private readonly RlSourceSnapshot _source;
    private readonly BacktestConfiguration _costs;
    private readonly string _directory;
    private readonly IReadOnlyDictionary<string, string> _metadata;
    private readonly IProgress<RlTrainingProgress>? _progress;
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _disposedSessions = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Role, int Seed, int Checkpoint), List<RlEpisodeEvaluation>> _reports = new();
    private readonly Dictionary<int, int> _trainingTransitions = new();
    private readonly List<RlUpdateDiagnostics> _updates = new();
    private readonly Dictionary<(string Role, int Seed, int Checkpoint), (RlActionDiagnosticCounter All, RlActionDiagnosticCounter Entry)> _actionDiagnostics = new();
    private readonly Dictionary<(string Role, int Seed, int Checkpoint, string Block), RlInformationUsage> _informationUsage = new();
    private FixedScaler? _scaler;
    private readonly RlQModel? _direction;
    private bool Continuous => _direction is not null;
    private bool ConstraintEvidence => Continuous && _metadata.ContainsKey(RlConstraintContract.MetadataKey);
    private string ContractHash => Continuous ? RlContinuousContract.ContractHash : RlContract.ContractHash;
    private long _sequence;
    private readonly RlWorkLedger _work = new();
    private RlFinancialAccount? _lastSuccessfulAccount;
    public int Transitions => _work.Transitions;
    public int EpisodeStarts => _work.EpisodeStarts;
    public (int Seed, int Checkpoint)? Selected { get; private set; }
    public Dictionary<string, object> Baselines { get; } = new();
    public RlIpcHost(Guid runId, RlSourceSnapshot source, BacktestConfiguration costs, string directory,
        IReadOnlyDictionary<string, string> metadata, IProgress<RlTrainingProgress>? progress, bool continuous = false)
    {
        _runId = runId; _source = source; _costs = costs; _directory = directory; _metadata = metadata; _progress = progress;
        if (continuous) _direction = new RlQModel(Path.Combine(directory, RlContinuousContract.DirectionModel));
    }

    public object Handle(JsonElement request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long sequence = request.GetProperty("sequence").GetInt64();
        if (request.GetProperty("protocol_version").GetInt32() != RlContract.SchemaVersion
            || request.GetProperty("run_id").GetString() != _runId.ToString("N")
            || request.GetProperty("contract_hash").GetString() != ContractHash || sequence != _sequence + 1)
            throw new InvalidDataException("Stale, duplicate, or incompatible RL request.");
        _sequence = sequence;
        string method = request.GetProperty("method").GetString() ?? "";
        string sessionId = request.GetProperty("session_id").GetString() ?? "";
        var payload = request.GetProperty("payload");
        object result;
        switch (method)
        {
            case "Ready":
                if (_scaler is not null) throw new InvalidDataException("Duplicate RL readiness.");
                if (_metadata.ContainsKey("temporal_specification_id")
                    && (!payload.TryGetProperty("temporal_producer_revision", out var producer)
                        || producer.GetString() != RlTemporalContract.ProducerRevision))
                    throw new RlTemporalException(RlTemporalRejection.UnknownProducer, "Worker does not implement the captured temporal producer protocol.");
                _scaler = FixedScaler.Load(Path.Combine(_directory, RlFiles.Model), _metadata);
                result = new { ready = true }; break;
            case "Progress":
                _progress?.Report(new(_runId, sequence, "Training", payload.GetProperty("message").GetString() ?? "", payload.GetProperty("steps").GetInt32()));
                result = new { accepted = true }; break;
            case "UpdateDiagnostics" when Continuous:
                var update = payload.Deserialize<RlUpdateDiagnostics>(RlFiles.Json) ?? throw new InvalidDataException("Missing guarded PPO update.");
                RlDiagnosticsValidator.ValidateUpdate(update, _source.Revision, _metadata["diagnostics_dependencies_sha256"]);
                int prior = _updates.LastOrDefault(u => u.Seed == update.Seed)?.CheckpointStep ?? 0;
                if (_updates.Count >= RlDiagnosticsContract.UpdateLimit || update.CheckpointStep != prior + RlContinuousContract.RolloutSteps
                    || _trainingTransitions.GetValueOrDefault(update.Seed) != update.CheckpointStep)
                    throw new InvalidDataException("Duplicate/out-of-order PPO rollout diagnostics.");
                _updates.Add(update);
                result = new { accepted = true }; break;
            case "Map" when Continuous:
                var proposal = ReadRawActions(payload);
                result = RlContinuousContract.Map(proposal[0], proposal[1]); break;
            case "RestoreCheckpoint" when Continuous && Checkpoints is not null:
                result = RestoreCheckpoint(cancellationToken); break;
            case "PrepareCheckpoint" when Continuous && Checkpoints is not null:
                result = PrepareCheckpoint(payload); break;
            case "CommitCheckpoint" when Continuous && Checkpoints is not null:
                result = Checkpoints.Commit(payload.GetProperty("id").GetString()!);
                var committed = RlCheckpointStore.Read<RlCheckpointStore.Manifest>(System.IO.Path.Combine(Checkpoints.ResumePath!, RlCheckpointContract.Manifest));
                _progress?.Report(new(_runId, _sequence, "Training", $"Checkpoint saved: {committed.Phase}; seed={committed.Seed}", committed.Steps)); break;
            case "Reset": result = Reset(sessionId, payload, cancellationToken); break;
            case "Step": result = Step(sessionId, payload, cancellationToken); break;
            case "Dispose":
                if (!_sessions.Remove(sessionId, out var disposed)) throw new InvalidDataException("Unknown/already disposed RL session.");
                disposed.Trading.Dispose(); _disposedSessions.Add(sessionId); result = new { disposed = true }; break;
            case "Select":
                if (Selected is not null) throw new InvalidDataException("Duplicate candidate selection.");
                var available = _reports.Where(p => p.Key.Role == "validation" && p.Value.Count == _source.Catalog.Validation.Length).ToArray();
                if (available.Length == 0) throw new InvalidDataException("Candidate validation is incomplete.");
                var best = available.OrderByDescending(p => p.Value.Sum(e => RlEvaluation.Parse(e.TerminalReturn)) / p.Value.Count)
                    .ThenBy(p => p.Key.Checkpoint).ThenBy(p => p.Key.Seed).First().Key;
                if (payload.GetProperty("seed").GetInt32() != best.Seed || payload.GetProperty("checkpoint").GetInt32() != best.Checkpoint)
                    throw new InvalidDataException("Candidate selection disagrees with exact decimal Core validation.");
                Selected = (best.Seed, best.Checkpoint); result = new { selected = true }; break;
            default: throw new InvalidDataException("Unknown RL protocol method.");
        }
        return new { protocol_version = RlContract.SchemaVersion, run_id = _runId.ToString("N"), sequence,
            session_id = sessionId, contract_hash = ContractHash, result };
    }

    private object Reset(string id, JsonElement payload, CancellationToken ct)
    {
        if (_scaler is null || !Guid.TryParseExact(id, "N", out _) || _disposedSessions.Contains(id))
            throw new InvalidDataException("RL reset identity/readiness is invalid.");
        string role = payload.GetProperty("role").GetString() ?? "";
        string blockId = payload.GetProperty("block_id").GetString() ?? "";
        int seed = payload.GetProperty("seed").GetInt32();
        int checkpoint = payload.GetProperty("checkpoint").GetInt32();
        var blocks = role switch { "train" => _source.Catalog.Train, "validation" => _source.Catalog.Validation,
            "oos" when Selected == (seed, checkpoint) => _source.Catalog.Oos,
            "parity" when Continuous && Selected == (seed, checkpoint) => _source.Catalog.Validation,
            _ => throw new InvalidDataException("Unsupported evaluation role/selection.") };
        var block = blocks.FirstOrDefault(b => b.Id == blockId) ?? throw new InvalidDataException("Block is outside declared partition.");
        if (_sessions.Remove(id, out var previous)) previous.Trading.Dispose();
        // At most one train session and one temporarily paused evaluation session; one processor steps serially.
        if (_sessions.Count >= RlContract.EnvironmentCount + 1) throw new InvalidDataException("RL active session limit exceeded.");
        CountStart();
        var engine = new BacktestEngine(IndicatorFactory.Default);
        var trading = Continuous ? engine.CreateRlContinuousSession(_source.CreateInput(block), _costs)
            : engine.CreateRlSession(_source.CreateInput(block), _costs);
        var session = new Session(trading, block, role, seed, checkpoint);
        _sessions.Add(id, session);
        var snapshot = trading.Reset(ct);
        _work.Succeeded(true);
        session.Snapshot = snapshot;
        RecordInformation(session, snapshot, reset: true);
        return new { observation = RlObservationBuilder.Build(snapshot, _scaler), info = SnapshotInfo(session, snapshot) };
    }

    private object Step(string id, JsonElement payload, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(id, out var session)) throw new InvalidDataException("Unknown RL session.");
        int action = Continuous ? (int)RlContract.SelectAction(_direction!.Infer(RlObservationBuilder.Build(session.Snapshot!, _direction.Scaler)))
            : payload.GetProperty("action").GetInt32();
        if (!Enum.IsDefined((RlAction)action)) throw new InvalidDataException("Illegal RL action.");
        CountTransition();
        RlStepResult step;
        if (Continuous)
        {
            var proposals = ReadRawActions(payload);
            bool entryEligible = (RlAction)action == RlAction.Buy && session.Snapshot!.Quantity == 0m
                && !session.Snapshot.PendingEntry && !session.Snapshot.DeferredMarketExit;
            step = ((IRlContinuousTradingSession)session.Trading).Step((RlAction)action, proposals[0], proposals[1], ct);
            session.RawActions.Add(proposals);
            RecordFinancialSuccess(step);
            var key = (session.Role, session.Seed, session.Checkpoint);
            if (!_actionDiagnostics.TryGetValue(key, out var counters))
            {
                if (_actionDiagnostics.Count >= RlDiagnosticsContract.UpdateLimit) throw new InvalidDataException("Action diagnostic group bound exceeded.");
                _actionDiagnostics.Add(key, counters = (new(), new()));
            }
            counters.All.Add(step.ActorAction!);
            if (entryEligible) counters.Entry.Add(step.ActorAction!);
        }
        else { step = session.Trading.Step((RlAction)action, ct); RecordFinancialSuccess(step); }
        session.Snapshot = step.Snapshot;
        RecordInformation(session, step.Snapshot, reset: false);
        if (session.Role == "train") _trainingTransitions[session.Seed] = _trainingTransitions.GetValueOrDefault(session.Seed) + 1;
        if (step.Outcome == RlActionOutcome.Rejected) session.Rejections++;
        else if (step.Outcome is not (RlActionOutcome.Submitted or RlActionOutcome.Hold)) session.NoOps++;
        Dictionary<string, object> info = SnapshotInfo(session, step.Snapshot);
        info["action"] = step.Action.ToString(); info["outcome"] = step.Outcome.ToString();
        info["rejection_reason"] = step.RejectionReason?.ToString() ?? "";
        info["decimal_reward"] = RlEvaluation.DecimalText(step.Reward);
        if (step.ActorAction is not null) info["actor_action"] = step.ActorAction;
        var result = session.Trading.GetResult();
        if (ConstraintEvidence)
        {
            info["entry_execution"] = RlConstraintEvaluation.ToWire(step.EntryExecution);
            if (step.EntryExecution is not null && session.Role is "validation" or "oos") session.EntryAttempts.Add(step.EntryExecution);
        }
        if (Continuous && session.Role == "parity" && _metadata.ContainsKey("financial_parity_specification_id"))
            info["financial_trace"] = RlFinancialTrace.Capture(step, result).ToWire();
        info["fills"] = result.Fills.Skip(session.PublishedFills).Select(fill => new { fill.BarIndex, fill.FillTime, side = fill.Side.ToString(),
            price = RlEvaluation.DecimalText(fill.Price), quantity = RlEvaluation.DecimalText(fill.Quantity), commission = RlEvaluation.DecimalText(fill.Commission),
            slippage_amount = RlEvaluation.DecimalText(fill.SlippageAmount) }).ToArray();
        session.PublishedFills = result.Fills.Length;
        if (step.Terminated && session.Role is not ("train" or "parity"))
        {
            var key = (session.Role, session.Seed, session.Checkpoint);
            if (!_reports.TryGetValue(key, out var rows)) _reports.Add(key, rows = new());
            if (rows.Any(e => e.BlockId == session.Block.Id)) throw new InvalidDataException("Repeated evaluation block.");
            var row = RlEvaluation.Finish(session.Block, step.Snapshot, result, session.Rejections, session.NoOps);
            if (ConstraintEvidence) row = row with { Constraints = RlConstraintEvaluation.ToWire(RlConstraintEvaluation.Finish(step.Snapshot, result, session.EntryAttempts)) };
            rows.Add(row); info["episode"] = row;
            int required = session.Role == "validation" ? _source.Catalog.Validation.Length : _source.Catalog.Oos.Length;
            if (rows.Count == required) info["mean_return"] = RlEvaluation.DecimalText(rows.Sum(e => RlEvaluation.Parse(e.TerminalReturn)) / rows.Count);
        }
        float reward = (float)step.Reward;
        if (!float.IsFinite(reward)) throw new InvalidDataException("RL reward ML conversion is nonfinite.");
        return new { observation = RlObservationBuilder.Build(step.Snapshot, _scaler!), reward, terminated = step.Terminated,
            truncated = step.Truncated, info };
    }

    private Dictionary<string, object> SnapshotInfo(Session session, RlSessionSnapshot snapshot) => new()
    {
        ["source_revision"] = _source.Revision, ["block_id"] = session.Block.Id, ["anchor"] = snapshot.MarketAnchor,
        ["market_anchor_bar_index"] = snapshot.MarketAnchorBarIndex, ["completed_decisions"] = snapshot.CompletedDecisions,
        ["remaining_decisions"] = snapshot.RemainingDecisions, ["terminal_reason"] = snapshot.TerminalReason.ToString(),
        ["execution_mode"] = ExecutionModel.YFinanceApproximate.ToString(), ["disclosure"] = RlFiles.Disclosure,
        ["account"] = new { cash = RlEvaluation.DecimalText(snapshot.EquityPoint.Cash), equity = RlEvaluation.DecimalText(snapshot.EquityPoint.Equity),
            held_margin = RlEvaluation.DecimalText(snapshot.EquityPoint.HeldMargin), quantity = RlEvaluation.DecimalText(snapshot.Quantity),
            entry_price = RlEvaluation.DecimalText(snapshot.EntryPrice), unrealized_pnl = RlEvaluation.DecimalText(snapshot.UnrealizedPnL),
            peak_equity = RlEvaluation.DecimalText(snapshot.PeakEquity), drawdown = RlEvaluation.DecimalText(snapshot.Drawdown),
            maximum_drawdown = RlEvaluation.DecimalText(snapshot.MaximumDrawdown), snapshot.HeldBars, snapshot.PendingEntry, snapshot.PendingExit,
            active_stop_price = snapshot.ActiveStopPrice.HasValue ? RlEvaluation.DecimalText(snapshot.ActiveStopPrice.Value) : null,
            retained_stop_distance = RlEvaluation.DecimalText(snapshot.RetainedStopDistance), snapshot.DeferredMarketExit,
            fields = snapshot.AccountFields.Select(RlEvaluation.DecimalText).ToArray() },
    };

    public void EvaluateBaselines(CancellationToken ct)
    {
        var sma = RlBaselinePolicies.CreateSmaSignals(_source);
        var map = _source.Catalog.Train.Concat(_source.Catalog.Validation).Concat(_source.Catalog.Oos).ToDictionary(b => b.Id,
            b => Enumerable.Range(0, RlContract.EpisodeHorizon).Select(i => (int)sma[b.StartIndex + RlContract.WindowLength - 1 + i]).ToArray());
        File.WriteAllText(Path.Combine(_directory, RlFiles.BaselineActions), JsonSerializer.Serialize(map, RlFiles.Json), TrainingConfigJson.Utf8NoBom);
        foreach (var blocks in new[] { _source.Catalog.Validation, _source.Catalog.Oos })
        {
            if (blocks.IsEmpty) continue;
            var reports = new Dictionary<string, object>();
            foreach (string policy in RlBaselinePolicies.EvaluationPolicyIds(Continuous))
            {
                var seedReports = new List<Dictionary<string, object>>();
                var allSeedRows = new List<RlEpisodeEvaluation>();
                foreach (int seed in policy == RlContract.RandomBaselineId ? RlContract.CandidateSeeds : RlContract.CandidateSeeds.Take(1))
                {
                    var rows = new List<RlEpisodeEvaluation>();
                    foreach (var block in blocks)
                    {
                        ct.ThrowIfCancellationRequested(); CountStart();
                        var engine = new BacktestEngine(IndicatorFactory.Default);
                        using var session = Continuous ? engine.CreateRlContinuousSession(_source.CreateInput(block), _costs)
                            : engine.CreateRlSession(_source.CreateInput(block), _costs);
                        var state = session.Reset(ct); _work.Succeeded(true); int rejected = 0, noOps = 0;
                        var entries = new List<RlEntryExecution>();
                        var random = new Random(seed + RlContract.RandomBaselineSeedOffset);
                        while (!state.Terminated)
                        {
                            int decision = state.CompletedDecisions;
                            var action = policy switch
                            {
                                RlContract.SmaBaselineId => sma[block.StartIndex + RlContract.WindowLength - 1 + decision],
                                RlContract.BuyHoldBaselineId => decision == 0 ? RlAction.Buy : RlAction.Hold,
                                RlContract.RandomBaselineId => (RlAction)random.Next(RlContract.ActionCount),
                                RlContinuousContract.PairedBaselineId => RlContract.SelectAction(_direction!.Infer(RlObservationBuilder.Build(state, _direction.Scaler))),
                                _ => RlAction.Hold,
                            };
                            CountTransition(); var step = Continuous ? ((IRlContinuousTradingSession)session).StepFixedControl(action, ct)
                                : session.Step(action, ct); RecordFinancialSuccess(step); state = step.Snapshot;
                            if (step.Outcome == RlActionOutcome.Rejected) rejected++;
                            else if (step.Outcome is not (RlActionOutcome.Submitted or RlActionOutcome.Hold)) noOps++;
                            if (ConstraintEvidence && step.EntryExecution is not null) entries.Add(step.EntryExecution);
                        }
                        var result = session.GetResult();
                        var row = RlEvaluation.Finish(block, state, result, rejected, noOps);
                        if (ConstraintEvidence) row = row with { Constraints = RlConstraintEvaluation.ToWire(RlConstraintEvaluation.Finish(state, result, entries)) };
                        rows.Add(row);
                    }
                    if (_source.ExpandedEvaluation) allSeedRows.AddRange(rows);
                    var report = RlEvaluation.Aggregate(rows, _source.ExpandedEvaluation); report["seed"] = seed + RlContract.RandomBaselineSeedOffset;
                    seedReports.Add(report);
                }
                var summary = seedReports[0];
                if (policy == RlContract.RandomBaselineId)
                {
                    var means = seedReports.Select(r => RlEvaluation.Parse((string)r["mean_return"])).ToArray();
                    summary = new(summary) { ["seeds"] = seedReports, ["mean_return"] = RlEvaluation.DecimalText(means.Sum() / means.Length),
                        ["min_seed_return"] = RlEvaluation.DecimalText(means.Min()), ["max_seed_return"] = RlEvaluation.DecimalText(means.Max()),
                        ["mean_maximum_drawdown"] = RlEvaluation.DecimalText(seedReports.Sum(r => RlEvaluation.Parse((string)r["mean_maximum_drawdown"])) / seedReports.Count),
                        ["worst_maximum_drawdown"] = RlEvaluation.DecimalText(seedReports.Max(r => RlEvaluation.Parse((string)r["worst_maximum_drawdown"]))) };
                    if (_source.ExpandedEvaluation) summary["return_distribution"] = RlConstraintEvaluation.ToWire(
                        RlAllBlockEvaluation.Distribution(allSeedRows.Select(r => RlEvaluation.Parse(r.TerminalReturn))));
                }
                reports.Add(policy, summary);
            }
            Baselines.Add(blocks[0].Partition, reports);
        }
    }

    public Dictionary<string, object> CreateReport(bool smoke, string evaluationRevision)
    {
        var selected = Selected ?? throw new InvalidDataException("No selected RL candidate.");
        var validation = _reports[("validation", selected.Seed, selected.Checkpoint)];
        var oos = smoke ? new List<RlEpisodeEvaluation>() : _reports.GetValueOrDefault(("oos", selected.Seed, selected.Checkpoint))
            ?? throw new InvalidDataException("Selected OOS evaluation missing.");
        if (validation.Count != _source.Catalog.Validation.Length || (!smoke && oos.Count != _source.Catalog.Oos.Length))
            throw new InvalidDataException("Incomplete independent RL evaluation.");
        int requiredCandidates = smoke ? 1 : RlContract.CandidateSeeds.Length;
        int requiredCheckpoints = RlContract.TrainingTransitionsPerSeed / RlContract.ValidationCheckpointInterval;
        if (_reports.Count(p => p.Key.Role == "validation" && p.Value.Count == _source.Catalog.Validation.Length) != requiredCandidates * requiredCheckpoints)
            throw new InvalidDataException("Required candidate/checkpoint evidence missing.");
        int requiredTransitions = smoke ? RlContract.SmokeTrainingTransitions
            : Continuous ? RlContinuousContract.TrainingTransitionsPerSeed : RlContract.TrainingTransitionsPerSeed;
        if (_trainingTransitions.Count != requiredCandidates || _trainingTransitions.Any(p => !RlContract.CandidateSeeds.Contains(p.Key) || p.Value != requiredTransitions))
            throw new InvalidDataException("Required exact per-seed training transition evidence missing.");
        var report = new Dictionary<string, object>()
        {
            ["schema_version"] = _source.ExpandedEvaluation ? RlAllBlockEvaluationContract.SchemaVersion : RlContract.SchemaVersion, ["smoke_only"] = smoke, ["parity_verified"] = false,
            ["contract_hash"] = ContractHash, ["source_revision"] = _source.Revision,
            ["evaluation_revision"] = evaluationRevision, ["symbol"] = _source.Manifest.Symbol,
            ["candidate_seed"] = selected.Seed, ["checkpoint_step"] = selected.Checkpoint,
            ["validation_mean_return"] = (string)RlEvaluation.Aggregate(validation)["mean_return"],
            ["oos_mean_return"] = oos.Count == 0 ? "0" : (string)RlEvaluation.Aggregate(oos)["mean_return"],
            ["validation_episodes"] = validation, ["oos_episodes"] = oos,
            ["candidate_evaluations"] = _reports.Where(p => p.Key.Role == "validation").Select(p => new { seed = p.Key.Seed, checkpoint_step = p.Key.Checkpoint, summary = RlEvaluation.Aggregate(p.Value, _source.ExpandedEvaluation) }).ToArray(),
            ["training_transitions"] = _trainingTransitions,
            ["information_usage"] = _informationUsage.Values.OrderBy(u => u.Role, StringComparer.Ordinal)
                .ThenBy(u => u.Seed).ThenBy(u => u.Checkpoint).ThenBy(u => u.BlockId, StringComparer.Ordinal).ToArray(),
            ["baselines"] = Baselines, ["configuration"] = _costs,
            ["resources"] = new { processed_transitions = Transitions, episode_starts = EpisodeStarts, work = _work.Snapshot() },
            ["disclosure"] = RlFiles.Disclosure + (Continuous ? " " + RlContinuousContract.Disclosure : ""),
        };
        if (Continuous && _metadata.ContainsKey("diagnostics_specification_id")) report["diagnostics"] = CreateDiagnostics();
        if (ConstraintEvidence)
        {
            IReadOnlyList<RlEpisodeEvaluation> Baseline(string partition) => Baselines.TryGetValue(partition, out var policies)
                ? JsonSerializer.SerializeToElement(policies, RlFiles.Json).GetProperty(RlContinuousContract.PairedBaselineId)
                    .GetProperty("episodes").Deserialize<RlEpisodeEvaluation[]>(RlFiles.Json)!
                : Array.Empty<RlEpisodeEvaluation>();
            var dependency = JsonSerializer.Deserialize<RlTemporalEvidence>(File.ReadAllText(Path.Combine(_directory, RlTemporalContract.DirectionEvidenceFile)), RlFiles.Json)!;
            var constraints = RlConstraintEvaluation.Create(_source, validation, oos,
                Baseline("validation"), Baseline("oos"), dependency.Status, JsonSerializer.SerializeToElement(Baselines, RlFiles.Json));
            report["constraint_diagnostics"] = RlConstraintEvaluation.ToWire(constraints);
            if (_source.ExpandedEvaluation)
            {
                report[RlAllBlockEvaluationContract.MetadataKey] = RlAllBlockEvaluationContract.SpecificationId;
                report[RlAllBlockEvaluationContract.ReportKey] = RlConstraintEvaluation.ToWire(RlAllBlockEvaluation.Create(
                    _directory, _source, _costs, validation, oos, constraints, _metadata["frozen_direction_model_id"], smoke));
            }
        }
        return report;
    }
    public object CreateDiagnostics() => new
    {
        specification_id = RlDiagnosticsContract.SpecificationId, source_revision = _source.Revision,
        contract_hash = ContractHash, dependencies_sha256 = _metadata.GetValueOrDefault("diagnostics_dependencies_sha256"),
        selected_seed = Selected?.Seed, selected_checkpoint = Selected?.Checkpoint,
        entry_definition = "pre-step Buy with flat quantity, no pending entry and no deferred market exit; includes NoEntry/rejected attempts",
        updates = _updates.ToArray(),
        actions = _actionDiagnostics.OrderBy(p => p.Key.Role, StringComparer.Ordinal).ThenBy(p => p.Key.Seed).ThenBy(p => p.Key.Checkpoint)
            .Select(p => new RlActionDiagnostics(p.Key.Role, p.Key.Seed, p.Key.Checkpoint, p.Value.All.Snapshot(), p.Value.Entry.Snapshot())).ToArray(),
    };
    private void CountTransition() { _work.Attempt(false); Checkpoints?.Charge(_work.Transitions, _work.EpisodeStarts); }
    private void RecordFinancialSuccess(RlStepResult step)
    { _work.Succeeded(false); _lastSuccessfulAccount = RlFinancialAccount.From(step.Snapshot); }
    private void RecordInformation(Session session, RlSessionSnapshot snapshot, bool reset)
    {
        if (session.Role is not ("train" or "validation")) return;
        var key = (session.Role, session.Seed, session.Checkpoint, session.Block.Id);
        _informationUsage.TryGetValue(key, out var previous);
        int index = checked(session.Block.StartIndex + Math.Max(snapshot.MarketAnchorBarIndex,
            snapshot.TerminalEventBarIndex ?? snapshot.MarketAnchorBarIndex));
        _informationUsage[key] = new(session.Role, session.Seed, session.Checkpoint, session.Block.Id,
            Math.Max(previous?.LastObservedIndex ?? index, index),
            checked((previous?.Transitions ?? 0) + (reset ? 0 : 1)),
            checked((previous?.Resets ?? 0) + (reset ? 1 : 0)));
    }
    private static float[] ReadRawActions(JsonElement payload)
    {
        var values = payload.GetProperty("actions");
        if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() != RlContinuousContract.ActionCount)
            throw new InvalidDataException("Actor proposals must be a two-element numeric array.");
        var result = new float[RlContinuousContract.ActionCount];
        for (int i = 0; i < result.Length; i++)
        {
            if (values[i].ValueKind != JsonValueKind.Number || !values[i].TryGetSingle(out result[i]) || !float.IsFinite(result[i]))
                throw new InvalidDataException("Actor proposals must be finite float32 JSON numbers.");
        }
        return result;
    }
    private void CountStart() { _work.Attempt(true); Checkpoints?.Charge(_work.Transitions, _work.EpisodeStarts); }
    public void AccountQualificationWork(bool start, bool success)
    { if (success) _work.Succeeded(start); else if (start) CountStart(); else CountTransition(); }
    public void RequireQualificationCapacity(int count, int starts) => _work.RequireCapacity(count, starts);
    public object CreateFailureTrace() => new
    {
        specification_id = RlFinancialParityContract.SpecificationId,
        source_revision = _source.Revision, contract_hash = ContractHash, last_request_sequence = _sequence,
        work = _work.Snapshot(), last_successful_account = _lastSuccessfulAccount is null ? (JsonElement?)null
            : JsonSerializer.SerializeToElement(_lastSuccessfulAccount, RlFinancialParityContract.TraceJson),
    };
    public void CountQualificationTransitions(int count, int starts)
    {
        _work.ReserveRegistration(count, starts);
        Checkpoints?.Charge(_work.Transitions, _work.EpisodeStarts);
    }
    public void Dispose() { foreach (var session in _sessions.Values) session.Trading.Dispose(); _sessions.Clear(); _direction?.Dispose(); }
}
