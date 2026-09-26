using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;

namespace StockAnalyzer.Core.Services.Backtest.Evaluation;

public sealed class EvaluationContentIdentity
{
    internal const string RunIdentityUnavailableReason = "RunIdentityUnavailable";

    public bool IsAvailable { get; }
    public string? Sha256 { get; }
    public int SchemaVersion { get; }
    public string? UnavailableReason { get; }

    internal EvaluationContentIdentity(bool isAvailable, string? sha256, int schemaVersion, string? unavailableReason)
    {
        IsAvailable = isAvailable;
        Sha256 = sha256;
        SchemaVersion = schemaVersion;
        UnavailableReason = unavailableReason;
    }

    internal static EvaluationContentIdentity Unavailable(int schemaVersion, string reason) =>
        new(false, null, schemaVersion, reason);
}

public sealed class InputDataReference
{
    internal const string ProviderUnavailableReason = "ProviderReferenceUnavailable";

    public string? SourceId { get; }
    public string? Version { get; }
    public string? UnavailableReason { get; }

    internal InputDataReference(string? sourceId, string? version, string? unavailableReason)
    {
        SourceId = sourceId;
        Version = version;
        UnavailableReason = unavailableReason;
    }

    internal static InputDataReference Unavailable() =>
        new(null, null, ProviderUnavailableReason);
}

public sealed class BacktestEvaluationMetadata
{
    public string Symbol { get; }
    public int InputDataVersion { get; }
    public TimeFrame Frame { get; }
    public DateTime RequestedStartUtc { get; }
    public DateTime RequestedEndUtc { get; }
    public DateTime? LastProcessedTimestamp { get; }
    public int HistoryStartIndex { get; }
    public int TradingStartIndex { get; }
    public int SampleCount { get; }
    public int AnnualPeriods { get; }
    public decimal AnnualRiskFreeRate { get; }
    public decimal AnnualMAR { get; }
    public int BootstrapSeed { get; }
    public int BootstrapIterations { get; }
    public int FormulaVersion { get; }
    public string AssemblyIdentity { get; }
    public string RuntimeIdentity { get; }
    public InputDataReference InputDataReference { get; }

    internal BacktestEvaluationMetadata(
        string symbol,
        int inputDataVersion,
        TimeFrame frame,
        DateTime requestedStartUtc,
        DateTime requestedEndUtc,
        DateTime? lastProcessedTimestamp,
        int historyStartIndex,
        int tradingStartIndex,
        int sampleCount,
        int annualPeriods,
        decimal annualRiskFreeRate,
        decimal annualMar,
        int bootstrapSeed,
        int bootstrapIterations,
        int formulaVersion,
        string assemblyIdentity,
        string runtimeIdentity,
        InputDataReference inputDataReference)
    {
        Symbol = symbol;
        InputDataVersion = inputDataVersion;
        Frame = frame;
        RequestedStartUtc = requestedStartUtc;
        RequestedEndUtc = requestedEndUtc;
        LastProcessedTimestamp = lastProcessedTimestamp;
        HistoryStartIndex = historyStartIndex;
        TradingStartIndex = tradingStartIndex;
        SampleCount = sampleCount;
        AnnualPeriods = annualPeriods;
        AnnualRiskFreeRate = annualRiskFreeRate;
        AnnualMAR = annualMar;
        BootstrapSeed = bootstrapSeed;
        BootstrapIterations = bootstrapIterations;
        FormulaVersion = formulaVersion;
        AssemblyIdentity = assemblyIdentity;
        RuntimeIdentity = runtimeIdentity;
        InputDataReference = inputDataReference;
    }
}

/// <summary>Immutable output of the owned Legacy/YFinance evaluation flow.</summary>
public sealed class BacktestEvaluationArtifact
{
    public const int CurrentAuditSchemaVersion = 1;
    public const int LegacyRiskExecutionMode = 0;

    public int AuditSchemaVersion => CurrentAuditSchemaVersion;
    public BacktestResult Result { get; }
    public BacktestReport? Report { get; }
    public RunStatus RunStatus { get; }
    public BacktestReportRunFingerprint RunFingerprint { get; }
    public BacktestEvaluationMetadata Metadata { get; }
    public BootstrapDiagnostics BootstrapDiagnostics { get; }
    public SamplingQualification Sampling { get; }
    public DrawdownEpisodeResult RatioDrawdown { get; }
    public DrawdownEpisodeResult AmountDrawdown { get; }
    public int RiskExecutionMode => LegacyRiskExecutionMode;
    public EvaluationContentIdentity ReportIdentity { get; }

    internal BacktestEvaluationArtifact(
        BacktestResult result,
        BacktestReport? report,
        BacktestReportRunFingerprint runFingerprint,
        BacktestEvaluationMetadata metadata,
        BootstrapDiagnostics bootstrapDiagnostics,
        SamplingQualification sampling,
        DrawdownEpisodeResult ratioDrawdown,
        DrawdownEpisodeResult amountDrawdown,
        EvaluationContentIdentity reportIdentity)
    {
        Result = result;
        Report = report;
        RunStatus = result.Status;
        RunFingerprint = runFingerprint;
        Metadata = metadata;
        BootstrapDiagnostics = bootstrapDiagnostics;
        Sampling = sampling;
        RatioDrawdown = ratioDrawdown;
        AmountDrawdown = amountDrawdown;
        ReportIdentity = reportIdentity;
    }
}

public sealed class BacktestEvaluationExportEnvelope
{
    public const int CurrentSchemaVersion = 1;
    public const string LegacyModelId = "LegacyEvaluationEvidence";

    public int SchemaVersion => CurrentSchemaVersion;
    public string ModelId => LegacyModelId;
    public BacktestEvaluationArtifact Artifact { get; }

    public BacktestEvaluationExportEnvelope(BacktestEvaluationArtifact artifact)
    {
        Artifact = artifact ?? throw new ArgumentNullException(nameof(artifact));
    }
}
