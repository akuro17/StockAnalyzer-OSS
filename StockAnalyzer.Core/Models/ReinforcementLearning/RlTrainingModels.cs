using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Models.ReinforcementLearning;

/// <summary>Run inputs are frozen before the first await; mathematical defaults come only from RlContract.</summary>
public sealed record RlTrainingJob(
    string Symbol, DateOnly StartDate, DateOnly EndDateExclusive, BacktestConfiguration Costs,
    string? PreparedSourceDirectory = null, bool SmokeOnly = false)
{
    public string? FrozenDirectionModelId { get; init; }
    public string? EvaluationSpecificationId { get; init; }
}

public sealed record RlTrainingProgress(Guid RunId, long Sequence, string State, string Message, int CompletedSteps);
public sealed record RlTrainingResult(Guid RunId, string Directory, string ModelPath, string EvaluationPath,
    string? ModelId, bool SmokeOnly, string SourceRevision);

public static class RlFiles
{
    public const string TrainingFolder = "TrainingArtifacts";
    public const string RlFolder = "RL";
    public const string SourceFolder = "RlSnapshots";
    public const string LocalProvider = RlSourceContract.Provider;
    public const string LocalProviderVersion = RlSourceContract.ProviderVersion;
    public const string SourceManifest = "source.json";
    public const string Bars = "bars.json";
    public const string RawParquet = "source.parquet";
    public const string MetadataParquet = "metadata.parquet";
    public const string Actions = "actions.json";
    public const string Catalog = "catalog.json";
    public const string BaselineActions = "baseline_actions.json";
    public const string Job = "job.json";
    public const string Model = "policy.onnx";
    public const string ScalerSuffix = ".scaler.json";
    public const string EvaluationSuffix = ".metrics.json";
    public const string Native = "policy.pt";
    public const string Parity = "parity.json";
    public const string Dependencies = "dependencies.json";
    public const string Manifest = "manifest.json";
    public const string Active = "active.json";
    public const string TrainerScript = "training/train_rl.py";
    public const string DependenciesScript = "training/rl_dependencies.py";
    public const string VirtualEnvironment = ".venv";
    public const string Target = "reinforcement_learning_q";
    public const string FeatureMode = "composed_features";
    public const string EvaluationRevision = "rl-chronological-60-20-20-gap64-v1";
    public const string Disclosure = "Stored-price OHLC approximation from local Data/Daily. Original acquisition, price adjustment, corporate-action coverage and past imputation are unverified. Prices are used unchanged; unknown split-contaminated support cannot be excluded. No execution/PIT or total-return evidence; dividend cash is excluded.";
    public static JsonSerializerOptions Json { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}

public sealed record RlSourceManifest(
    int SchemaVersion, string Provider, string ProviderVersion, string Symbol, string QuoteCurrency,
    string ExchangeTimezone, DateTimeOffset RetrievalStartedUtc, DateTimeOffset RetrievalCompletedUtc,
    string PreparedFileSha256, string ActionsFileSha256, int RowCount, string Adjustment,
    string AcquisitionProvenanceStatus, string IngestionImputationStatus,
    string SourceStorageDtype, string FinalityPolicy, DateOnly CapturedExchangeDate,
    DateOnly PeriodStartInclusive, DateOnly PeriodEndExclusive,
    Dictionary<string, JsonElement> AcquisitionParameters)
{
    public string? InstrumentType { get; init; }
    public string? MappedSymbol { get; init; }
    public string? SourcePathIdentity { get; init; }
    public string? SourceFileSha256 { get; init; }
    public string? MetadataPathIdentity { get; init; }
    public string? MetadataFileSha256 { get; init; }
    public string? EvidencePolicy { get; init; }
    public string? OriginalAcquisitionStatus { get; init; }
    public string? CorporateActionCoverageStatus { get; init; }
    public int SourceRows { get; init; }
    public DateOnly? FirstDate { get; init; }
    public DateOnly? LastDate { get; init; }
    public DateOnly? ActionsPeriodStart { get; init; }
    public DateOnly? ActionsPeriodEnd { get; init; }
    public bool CurrentHistoryRevision { get; init; }
    public string? DateLabelSemantics { get; init; }
    public ImmutableArray<DateOnly> KnownImputedDates { get; init; } = ImmutableArray<DateOnly>.Empty;
}

public sealed record RlCorporateAction(DateOnly Date, string Kind, string Value);
public sealed record RlEpisodeBlock(string Id, string Partition, int StartIndex, int EndExclusive);
public sealed record RlEpisodeCatalog(ImmutableArray<RlEpisodeBlock> Train,
    ImmutableArray<RlEpisodeBlock> Validation, ImmutableArray<RlEpisodeBlock> Oos,
    ImmutableArray<string> Excluded, int TrainCut, int ValidationCut)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? EvaluationSpecificationId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? SchemaVersion { get; init; }
}

public sealed record RlReplayRequest(string ModelId, string SourceDirectory, string BlockId,
    int DecisionCount, string PolicyId);

public sealed record RlQSnapshot(string RequestKey, string ModelId, string Symbol, DateTime Anchor,
    ImmutableArray<float> QValues, RlAction SelectedAction, RlSessionSnapshot Account,
    string Disclosure, string PolicyId)
{
    public RlContinuousReadout? Continuous { get; init; }
    public string? TemporalSpecificationId { get; init; }
    public RlTemporalBoundStatus TemporalStatus { get; init; }
    public RlConstraintReport? EvaluationConstraints { get; init; }
    public RlAllBlockEvaluationReport? AllBlockEvaluation { get; init; }
}

public sealed record RlGeneration(string ModelId, string Symbol, string SourceRevision,
    string ContractHash, string EvaluationRevision, decimal ValidationReturn,
    decimal OosReturn, Dictionary<string, string> Files, DateTimeOffset CreatedUtc)
{
    public string Target { get; init; } = RlFiles.Target;
    public string? FrozenDirectionModelId { get; init; }
    public string? TemporalSpecificationId { get; init; }
    public RlTemporalBoundStatus TemporalStatus { get; init; }
}

/// <summary>Resource checks are performed before allocations or interpreter launch.</summary>
public static class RlTrainingValidation
{
    public static TrainingResourceLimits FreezeLimits(TrainingResourceOverrides? overrides = null, bool continuous = false)
    {
        var limits = TrainingResourceGuard.Resolve(overrides ?? new TrainingResourceOverrides());
        limits.Validate();
        if (limits.MaxChannels < RlContract.MarketChannelCount)
            throw new InvalidOperationException("The captured channel limit cannot admit OHLCV.");
        // SB3 owns two observations, action int64, reward/done/timeout float32 and sampled batch tensors.
        long rowBytes = checked(RlContract.ObservationLength * sizeof(float) * 2L + sizeof(long) + sizeof(float) * 3L);
        long replay = checked((long)(RlContract.ReplayCapacity + RlContract.BatchSize) * rowBytes
            + RlContract.BatchSize * sizeof(long) + RlContract.IpcFrameByteLimit * 2L);
        if (continuous)
            replay = checked((long)(RlContinuousContract.RolloutSteps + RlContract.BatchSize) *
                (RlContract.ObservationLength * sizeof(float) + ((long)RlContinuousContract.ActionCount + RlContinuousContract.RolloutAuxiliaryFieldCount) * sizeof(float))
                + RlContract.IpcFrameByteLimit * 2L);
        if (replay > RlContract.BufferByteLimit || replay > (long)limits.MaxTensorSizeMiB * TrainingResourceOverrides.MiB)
            throw new InvalidOperationException("RL replay cannot fit captured resource limits.");
        return limits;
    }

    public static RlTrainingJob Freeze(RlTrainingJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (string.IsNullOrWhiteSpace(job.Symbol) || job.Symbol.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || job.Symbol.Contains('/') || job.Symbol.Contains('\\'))
            throw new ArgumentException("A safe, explicit symbol is required.", nameof(job));
        if (job.StartDate >= job.EndDateExclusive)
            throw new ArgumentException("The source date interval must be nonempty.", nameof(job));
        if (job.PreparedSourceDirectory is not null)
            throw new ArgumentException("External prepared market sources are prohibited; use existing Data/Daily files.", nameof(job));
        if (RlAllBlockEvaluationContract.IsExpanded(job.EvaluationSpecificationId) && job.FrozenDirectionModelId is null)
            throw new ArgumentException("All-block evaluation requires an explicit qualified frozen T15 direction.", nameof(job));
        return job with { Symbol = job.Symbol.Trim(), Costs = RlContract.CreateConfiguration(job.Costs) };
    }
}
