using System;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.Backtest.Engine;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>Generation-leased, account-aware Q inference and causal replay. Invoke outside render.</summary>
public sealed class QValueService : IQValueService
{
    private readonly IRlModelGenerationRegistry _registry;
    private readonly ILogger<QValueService> _logger;

    public QValueService(IRlModelGenerationRegistry registry, ILogger<QValueService>? logger = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger ?? NullLogger<QValueService>.Instance;
    }

    /// <summary>Compare exported native Q fixtures to real C# CPU ONNX execution and exact greedy actions.</summary>
    public static void ValidateParity(string artifactDirectory)
    {
        using var model = new RlQModel(Path.Combine(artifactDirectory, RlFiles.Model));
        model.ValidateParity(Path.Combine(artifactDirectory, RlFiles.Parity));
    }

    public static void ValidateParityDirectory(string artifactDirectory) => ValidateParity(artifactDirectory);

    public RlQSnapshot Infer(string modelId, float[] observation, RlSessionSnapshot account,
        string sourceRevision, string symbol, string requestKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestKey);
        ArgumentNullException.ThrowIfNull(account);
        RlQModel.ValidateObservation(observation);
        using var lease = _registry.AcquireRl(modelId) ?? throw new InvalidOperationException("Unavailable: RL generation does not exist.");
        var generation = _registry.GetRlGeneration(modelId) ?? throw new InvalidOperationException("Unavailable: RL generation identity was lost.");
        if (generation.SourceRevision != sourceRevision || generation.Symbol != symbol || generation.ContractHash != RlContract.ContractHash)
            throw new InvalidDataException("Unavailable: source, symbol or account contract is incompatible with the selected generation.");
        string directory = Path.GetDirectoryName(lease.ModelPath)!;
        BacktestConfiguration costs = LoadCosts(directory);
        ValidateAccount(account, costs);
        using var capturedJob = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, RlFiles.Job)));
        var limits = capturedJob.RootElement.GetProperty("resource_limits").Deserialize<Models.Training.TrainingResourceLimits>(RlFiles.Json)
            ?? throw new InvalidDataException("Unavailable: captured resource contract is missing.");
        var source = RlSourceSnapshot.Load(directory, limits);
        bool supported = source.Catalog.Train.Concat(source.Catalog.Validation).Concat(source.Catalog.Oos).Any(block =>
            source.Bars.AsSpan(block.StartIndex + account.CompletedDecisions, RlContract.WindowLength).SequenceEqual(account.MarketWindow.AsSpan()));
        if (!supported) throw new InvalidDataException("Unavailable: account window is outside the selected generation's source support.");
        using var model = new RlQModel(lease.ModelPath);
        float[] owned = (float[])observation.Clone();
        if (!owned.AsSpan().SequenceEqual(RlObservationBuilder.Build(account, model.Scaler)))
            throw new InvalidDataException("Unavailable: observation does not match the supplied account and generation scaler.");
        float[] q = model.Infer(owned);
        _logger.LogDebug("RL Q evaluated generation {ModelId} at market anchor {Anchor}", modelId, account.MarketAnchor);
        return new RlQSnapshot(requestKey, modelId, symbol, account.MarketAnchor, q.ToImmutableArray(),
            RlContract.SelectAction(q), account, RlFiles.Disclosure, RlFiles.Target);
    }

    public Task<RlQSnapshot> ReplayAsync(RlReplayRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Owned managed/native leases remain inside the worker until it actually exits after cancellation.
        return Task.Run(() => Replay(request, cancellationToken), cancellationToken);
    }

    private RlQSnapshot Replay(RlReplayRequest request, CancellationToken cancellationToken)
    {
        if (request.DecisionCount < 0 || request.DecisionCount >= RlContract.EpisodeHorizon)
            throw new ArgumentOutOfRangeException(nameof(request), "A Q replay cursor must identify a nonterminal decision from 0 to 127.");
        using var lease = _registry.AcquireRl(request.ModelId) ?? throw new InvalidOperationException("Unavailable: RL generation does not exist.");
        var generation = _registry.GetRlGeneration(request.ModelId) ?? throw new InvalidOperationException("Unavailable: RL generation is missing.");
        bool continuous = generation.Target == RlContinuousContract.Target;
        if (request.PolicyId != generation.Target && request.PolicyId != RlContract.SmaBaselineId && request.PolicyId != RlContract.RandomBaselineId
            && request.PolicyId != RlContract.HoldBaselineId && request.PolicyId != RlContract.BuyHoldBaselineId
            && !(continuous && request.PolicyId == RlContinuousContract.PairedBaselineId))
            throw new ArgumentException("Unknown or incompatible RL replay policy.", nameof(request));
        string directory = Path.GetDirectoryName(lease.ModelPath)!;
        using var job = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, RlFiles.Job)));
        var limits = job.RootElement.GetProperty("resource_limits").Deserialize<Models.Training.TrainingResourceLimits>(RlFiles.Json)
            ?? throw new InvalidDataException("Unavailable: saved resource limits are missing.");
        var source = RlSourceSnapshot.Load(request.SourceDirectory, limits);
        if (source.Revision != generation.SourceRevision || source.Manifest.Symbol != generation.Symbol)
            throw new InvalidDataException("Unavailable: replay source does not match the generation's immutable dataset.");
        var block = source.Catalog.Train.Concat(source.Catalog.Validation).Concat(source.Catalog.Oos).SingleOrDefault(b => b.Id == request.BlockId)
            ?? throw new InvalidDataException("Unavailable: replay block is outside the validated catalog.");
        var costs = LoadCosts(directory);
        using var dependencyLease = continuous ? _registry.AcquireRl(generation.FrozenDirectionModelId!)
            ?? throw new InvalidDataException("Unavailable: frozen direction dependency was lost.") : null;
        RlTemporalEvidence? temporalEvidence = null;
        bool temporal = RlTemporalValidator.IsVersioned(job.RootElement);
        if (temporal != (generation.TemporalSpecificationId is not null)
            || (temporal && generation.TemporalSpecificationId != RlTemporalContract.SpecificationId))
            throw new RlTemporalException(RlTemporalRejection.IdentityMismatch, "Replay manifest and job temporal revisions differ.");
        if (continuous && temporal)
        {
            var reconstructed = _registry.CaptureRlTemporalEvidence(generation.FrozenDirectionModelId!)
                ?? throw new RlTemporalException(RlTemporalRejection.MissingEvidence, "Frozen dependency temporal proof is unavailable.");
            var direction = job.RootElement.GetProperty("direction").Deserialize<RlDirectionDependency>(RlFiles.Json)!;
            temporalEvidence = RlTemporalValidator.ValidateDependency(directory, job.RootElement, direction, source, reconstructed, new[] { block });
        }
        using var model = new RlQModel(continuous ? Path.Combine(directory, RlContinuousContract.DirectionModel) : lease.ModelPath);
        using var actor = continuous ? new RlQModel(lease.ModelPath, continuous: true) : null;
        if (continuous && temporal && (actor!.Metadata["temporal_snapshot_sha256"] != RlTemporalValidator.JobIdentity(job.RootElement)
            || actor.Metadata["direction_temporal_sha256"] != job.RootElement.GetProperty("direction").GetProperty("temporal_evidence_sha256").GetString()))
            throw new RlTemporalException(RlTemporalRejection.IdentityMismatch, "Replay costs, source, configuration or dependency proof differs from the ONNX snapshot.");
        var engine = new BacktestEngine(new IndicatorFactory());
        using var session = continuous ? engine.CreateRlContinuousSession(source.CreateInput(block), costs)
            : engine.CreateRlSession(source.CreateInput(block), costs);
        RlSessionSnapshot account = session.Reset(cancellationToken);
        int[]? sma = null;
        if (request.PolicyId == RlContract.SmaBaselineId)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, RlFiles.BaselineActions)));
            sma = document.RootElement.GetProperty(block.Id).Deserialize<int[]>()
                ?? throw new InvalidDataException("Unavailable: saved registered-SMA policy actions are missing.");
            if (sma.Length != RlContract.EpisodeHorizon || sma.Any(a => a < 0 || a >= RlContract.ActionCount))
                throw new InvalidDataException("Unavailable: saved baseline action support is invalid.");
        }
        for (int i = 0; i < request.DecisionCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (account.Terminated) throw new InvalidOperationException("Unavailable: replay cursor follows a naturally terminated episode.");
            RlAction action = request.PolicyId == generation.Target || request.PolicyId == RlContinuousContract.PairedBaselineId
                ? RlContract.SelectAction(model.Infer(RlObservationBuilder.Build(account, model.Scaler)))
                : sma is not null ? (RlAction)sma[i]
                : RlBaselinePolicies.GetAction(request.PolicyId, source, block, i,
                    RlContract.CandidateSeeds[0]);
            if (continuous)
            {
                var trading = (IRlContinuousTradingSession)session;
                if (request.PolicyId == generation.Target)
                {
                    var raw = actor!.Infer(RlObservationBuilder.Build(account, model.Scaler));
                    account = trading.Step(action, raw[0], raw[1], cancellationToken).Snapshot;
                }
                else account = trading.StepFixedControl(action, cancellationToken).Snapshot;
            }
            else account = session.Step(action, cancellationToken).Snapshot;
        }
        if (account.Terminated) throw new InvalidOperationException("Unavailable: Q is not an executable action at an ended cursor.");
        ValidateAccount(account, costs, continuous);
        float[] observation = RlObservationBuilder.Build(account, model.Scaler);
        float[] q = model.Infer(observation);
        RlContinuousReadout? readout = null;
        if (continuous)
        {
            var raw = actor!.Infer(observation);
            var action = RlContinuousContract.Map(raw[0], raw[1]);
            var policyAction = request.PolicyId == generation.Target || request.PolicyId == RlContinuousContract.PairedBaselineId
                ? RlContract.SelectAction(q) : sma is not null ? (RlAction)sma[request.DecisionCount]
                    : RlBaselinePolicies.GetAction(request.PolicyId, source, block, request.DecisionCount, RlContract.CandidateSeeds[0]);
            readout = RlConstraintEvaluation.Readout(account, costs, action, policyAction, request.PolicyId, generation.FrozenDirectionModelId!);
        }
        RlConstraintReport? constraints = null;
        RlAllBlockEvaluationReport? expanded = null;
        if (continuous && actor!.Metadata.ContainsKey(RlConstraintContract.MetadataKey))
        {
            using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, RlFiles.Model + RlFiles.EvaluationSuffix)));
            constraints = report.RootElement.GetProperty("constraint_diagnostics").Deserialize<RlConstraintReport>(RlFinancialParityContract.TraceJson);
            if (source.ExpandedEvaluation) expanded = report.RootElement.GetProperty(RlAllBlockEvaluationContract.ReportKey)
                .Deserialize<RlAllBlockEvaluationReport>(RlFinancialParityContract.TraceJson);
        }
        string accountRevision = Common.ContentHash.OfText(JsonSerializer.Serialize(account, RlFiles.Json));
        string identity = string.Join("\n", request.ModelId, generation.Symbol, source.Revision, block.Id,
            request.PolicyId, request.DecisionCount.ToString(CultureInfo.InvariantCulture),
            account.MarketAnchor.ToString("O", CultureInfo.InvariantCulture), accountRevision, ObservationHash(observation));
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        cancellationToken.ThrowIfCancellationRequested();
        return new RlQSnapshot(key, request.ModelId, generation.Symbol, account.MarketAnchor, q.ToImmutableArray(),
            RlContract.SelectAction(q), account, RlFiles.Disclosure +
                $" Acquisition={source.Manifest.AcquisitionProvenanceStatus}; ingestion={source.Manifest.IngestionImputationStatus}; precision={source.Manifest.SourceStorageDtype}."
                + (continuous ? " " + RlContinuousContract.Disclosure : "")
                + (continuous && temporalEvidence is null ? " " + RlTemporalContract.LegacyDisclosure : ""), request.PolicyId)
                { Continuous = readout, TemporalSpecificationId = generation.TemporalSpecificationId,
                    TemporalStatus = temporalEvidence?.Status ?? generation.TemporalStatus, EvaluationConstraints = constraints, AllBlockEvaluation = expanded };
    }

    private static string ObservationHash(float[] observation)
    {
        var bytes = new byte[checked(observation.Length * sizeof(float))];
        for (int i = 0; i < observation.Length; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * sizeof(float), sizeof(float)), BitConverter.SingleToInt32Bits(observation[i]));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static BacktestConfiguration LoadCosts(string directory)
    {
        using var job = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, RlFiles.Job)));
        var costs = job.RootElement.GetProperty("costs").Deserialize<BacktestConfiguration>(RlFiles.Json)
            ?? throw new InvalidDataException("Unavailable: saved account costs are missing.");
        costs.Validate();
        return costs;
    }

    private static void ValidateAccount(RlSessionSnapshot account, BacktestConfiguration costs, bool continuous = false)
    {
        if (!continuous && (account.ActiveStopPrice is not null || account.RetainedStopDistance != 0m || account.DeferredMarketExit))
            throw new InvalidDataException("Unavailable: protective stop/deferred state requires a T16 account.");
        if (account.Terminated || account.Status != RunStatus.Completed || account.TerminalReason != RlTerminalReason.None
            || account.CompletedDecisions < 0 || account.CompletedDecisions >= RlContract.EpisodeHorizon
            || account.RemainingDecisions != RlContract.EpisodeHorizon - account.CompletedDecisions
            || account.MarketAnchorBarIndex != RlContract.WindowLength - 1 + account.CompletedDecisions
            || account.AccountFields.IsDefault || account.AccountFields.Length != RlContract.AccountFieldCount
            || account.MarketWindow.IsDefault || account.MarketWindow.Length != RlContract.WindowLength
            || account.MarketWindow[^1].Timestamp != account.MarketAnchor || account.TerminalEventBarIndex is not null
            || account.EquityPoint.BarIndex != account.MarketAnchorBarIndex || account.EquityPoint.Timestamp != account.MarketAnchor
            || account.Quantity < 0m || account.Quantity > (continuous ? RlContract.MaxPositionQuantity : RlContract.FixedQuantity)
            || account.Quantity != decimal.Floor(account.Quantity) || account.PeakEquity < costs.InitialCapital
            || account.PeakEquity < account.EquityPoint.Equity || account.EquityPoint.Equity <= 0m
            || account.EquityPoint.Equity != account.EquityPoint.Cash + account.EquityPoint.HeldMargin + account.UnrealizedPnL
            || account.Drawdown != 1m - account.EquityPoint.Equity / account.PeakEquity
            || account.MaximumDrawdown < account.Drawdown || account.MaximumDrawdown < 0m
            || (account.Quantity == 0m ? account.EntryPrice != 0m || account.HeldBars != 0 || account.UnrealizedPnL != 0m
                : account.EntryPrice <= 0m || account.HeldBars < 1 || account.HeldBars > account.CompletedDecisions))
            throw new InvalidDataException("Unavailable: account cursor or decimal financial identity is incompatible.");
        for (int i = 0; i < account.MarketWindow.Length; i++)
        {
            var bar = account.MarketWindow[i];
            if (bar.Timestamp.Kind != DateTimeKind.Utc || !bar.IsValid() || bar.Open <= 0 || bar.High <= 0 || bar.Low <= 0 || bar.Close <= 0
                || (i > 0 && bar.Timestamp <= account.MarketWindow[i - 1].Timestamp))
                throw new InvalidDataException("Unavailable: account market window is invalid.");
        }
        decimal e0 = costs.InitialCapital;
        var point = account.EquityPoint;
        if (point.HeldMargin != account.Quantity * account.EntryPrice * RlContract.InitialMarginRatio
            || point.MarketValue != account.Quantity * account.MarketWindow[^1].Close)
            throw new InvalidDataException("Unavailable: marked position identity is invalid.");
        var expected = RlAccountObservation.Build(account.Quantity, account.EntryPrice, account.MarketWindow[^1].Close,
            point, account.UnrealizedPnL, e0, account.Drawdown, account.HeldBars, account.RemainingDecisions,
            account.PendingEntry, account.PendingExit, account.ActiveStopPrice, account.RetainedStopDistance, account.DeferredMarketExit);
        if (!expected.SequenceEqual(account.AccountFields)) throw new InvalidDataException("Unavailable: normalized account fields disagree with decimal state.");
    }
}
