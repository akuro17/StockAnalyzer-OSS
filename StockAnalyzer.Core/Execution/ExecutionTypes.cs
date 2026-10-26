using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Execution;

/// <summary>Where a sandboxed training job is. <see cref="Succeeded"/>, <see cref="Failed"/> and <see cref="Cancelled"/> are final.</summary>
public enum TrainingJobState
{
    Running,
    Cancelling,
    Validating,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>Why a job is <see cref="TrainingJobState.Failed"/>.</summary>
public enum TrainingFailureReason
{
    /// <summary>The trainer exited with an error, produced no ONNX, or raised an error.</summary>
    TrainingFailed,

    /// <summary>The run exceeded its time limit.</summary>
    Timeout,

    /// <summary>The produced file is not a loadable ONNX model.</summary>
    InvalidOnnx,
}

/// <summary>The values of a sandboxed training run that no client chooses. The one definition: the job configuration is built from it and the capabilities tool reports it.</summary>
public sealed record EphemeralFixedValues(
    TrainingTimeframe Timeframe,
    Models.PredictionFeatureMode FeatureMode,
    TrainingInitializationMode InitializationMode,
    TrainingEnsembleMethod EnsembleMethod,
    bool FixedZScore,
    bool Calibration,
    bool Cpcv)
{
    public static EphemeralFixedValues Current { get; } = new(
        TrainingTimeframe.Daily,
        Models.PredictionFeatureMode.OhlcvMinMax,
        TrainingInitializationMode.Fresh,
        TrainingEnsembleMethod.None,
        FixedZScore: false,
        Calibration: false,
        Cpcv: false);
}

/// <summary>
/// What a client may choose for one sandboxed supervised training run. Everything else is fixed by the gateway: daily bars, the OHLCV
/// min-max features, a fresh model, no ensemble, no fixed z-score, no calibration, no CPCV. Ranges are checked by
/// <see cref="TrainingJobConfig.Validate"/> and <c>TrainingResourceGuard</c>, not here.
/// </summary>
public sealed record EphemeralTrainingRequest
{
    /// <summary>One or more symbols, none twice (compared ignoring case).</summary>
    public required IReadOnlyList<string> Symbols { get; init; }

    public DateOnly? StartDate { get; init; }

    public DateOnly? EndDate { get; init; }

    public required TrainingFramework Framework { get; init; }

    /// <summary>One of <see cref="TrainingArchitectureCatalog.For"/> for <see cref="Framework"/>.</summary>
    public required string Architecture { get; init; }

    public required int WindowSize { get; init; }

    public required int Horizon { get; init; }

    public TargetType TargetType { get; init; } = TargetType.Classification;

    public IReadOnlyDictionary<string, string> Hyperparameters { get; init; } = new Dictionary<string, string>();

    public int NSplits { get; init; } = WalkForwardDataRequirement.DefaultSplitCount;

    public TrainingTimeframe Timeframe { get; init; } = EphemeralFixedValues.Current.Timeframe;

    public PredictionFeatureMode FeatureMode { get; init; } = EphemeralFixedValues.Current.FeatureMode;

    public FeatureSpec? FeatureSpec { get; init; }

    public TrainingInitializationMode InitializationMode { get; init; } = EphemeralFixedValues.Current.InitializationMode;

    public TrainingEnsembleMethod EnsembleMethod { get; init; } = EphemeralFixedValues.Current.EnsembleMethod;

    public bool FixedZScore { get; init; } = EphemeralFixedValues.Current.FixedZScore;

    public double? ClipSigma { get; init; }

    public bool Calibration { get; init; } = EphemeralFixedValues.Current.Calibration;

    public bool Cpcv { get; init; } = EphemeralFixedValues.Current.Cpcv;

    /// <summary>
    /// The file-name stem of every ephemeral model. A short fixed stem on purpose: the trainer derives a long default stem (scope, task, architecture,
    /// features and the run id) and builds temporary names from it inside the job folder, and a Windows path of 260 characters or more (without the
    /// long-path setting) made a real run fail at its last step (measured 2026-10-24). The job folder already carries the run id.
    /// </summary>
    public const string OutputStem = "model";

    /// <summary>The training job configuration of this request, with the gateway's fixed values, for the job <paramref name="jobId"/> writing into <paramref name="artifactsDirectory"/>.</summary>
    internal TrainingJobConfig ToJobConfig(string jobId, string artifactsDirectory) => new()
    {
        Symbols = Symbols.ToArray(),
        StartDate = StartDate,
        EndDate = EndDate,
        Timeframe = Timeframe,
        Framework = Framework,
        Architecture = Architecture,
        FeatureMode = FeatureMode,
        FeatureSpec = FeatureSpec,
        InitializationMode = InitializationMode,
        EnsembleMethod = EnsembleMethod,
        FixedZScore = FixedZScore,
        ClipSigma = ClipSigma,
        Calibration = Calibration,
        Cpcv = Cpcv,
        WindowSize = WindowSize,
        Horizon = Horizon,
        TargetType = TargetType,
        NSplits = NSplits,
        Hyperparameters = new Dictionary<string, string>(Hyperparameters),
        OutputName = OutputStem,
        RunId = jobId,
        ArtifactsDirectory = artifactsDirectory,
    };
}

/// <summary>What <see cref="ISandboxedExecutionGateway.StartTrainingAsync"/> returns at once.</summary>
public sealed record TrainingJobTicket(string JobId, TrainingJobState State);

/// <summary>A produced model that loads: its files (names only), size, hash and the shapes its first input and output declare.</summary>
public sealed record EphemeralArtifact(
    string ArtifactId,
    string OnnxFileName,
    long SizeBytes,
    string Sha256,
    string MetricsFileName,
    bool Loads,
    IReadOnlyList<long> InputShape,
    IReadOnlyList<long> OutputShape);

/// <summary>What kind of job a job id names; both kinds share one job table, one state machine and one concurrency limit.</summary>
public enum TrainingJobKind
{
    Supervised,
    ReinforcementLearning,
    PortfolioReinforcementLearning,
}

/// <summary>
/// What a client may choose for one sandboxed reinforcement-learning run. Everything else is fixed by the gateway: the smoke run of the
/// application's RL window (one seed, <c>RlContract.SmokeTrainingTransitions</c> transitions, nothing registrable), the discrete T15 policy, and the
/// factory costs of the Backtest window. Omitted dates are proposed from the stored data exactly as the RL window pre-fills them.
/// </summary>
public sealed record EphemeralRlRequest
{
    public required string Symbol { get; init; }

    public DateOnly? StartDate { get; init; }

    /// <summary>The first day NOT in the period (the RL window's "End Date" is exclusive).</summary>
    public DateOnly? EndDateExclusive { get; init; }

    /// <summary>
    /// When true, the ephemeral job folder and policy.onnx are retained under EphemeralSandbox.Root
    /// for EphemeralRetentionMinutes (15 min) or until explicit lease disposal.
    /// Default is false to strictly preserve 100% legacy behavior.
    /// </summary>
    public bool RetainArtifact { get; init; } = false;
}

/// <summary>
/// What a client may choose for one sandboxed portfolio reinforcement-learning run. Everything else is fixed by the gateway:
/// the smoke run of the application's Portfolio RL window (1 seed, 1024 transitions, factory costs, evaluation only).
/// </summary>
public sealed record EphemeralPortfolioRlRequest
{
    public required IReadOnlyList<string> Symbols { get; init; }

    public DateOnly? StartDate { get; init; }

    public DateOnly? EndDateExclusive { get; init; }

    public bool SymbolHoldout { get; init; } = true;

    public int MaxAssetWeightPercent { get; init; } = 100;

    public RlPanelEvaluationMode Mode { get; init; } = RlPanelEvaluationMode.Standard;

    /// <summary>
    /// When true, the ephemeral job folder and policy.onnx are retained under EphemeralSandbox.Root
    /// for EphemeralRetentionMinutes (15 min) or until explicit lease disposal.
    /// Default is false to strictly preserve 100% legacy behavior.
    /// </summary>
    public bool RetainArtifact { get; init; } = false;
}

/// <summary>
/// What a succeeded RL smoke run leaves to read. The mean return is the exact decimal text of the evaluation report (never a double: it is a financial
/// value); the files themselves are deleted when the job ends, since no tool reads them.
/// </summary>
public sealed record EphemeralRlResult(
    string ValidationMeanReturn,
    int CandidateSeed,
    int CheckpointStep,
    int TrainingTransitions,
    long ModelSizeBytes,
    string ModelSha256,
    string SourceRevision);

/// <summary>
/// What a succeeded portfolio RL smoke run leaves to read: the baseline comparisons, selected seed, and model identity.
/// </summary>
public sealed record EphemeralPortfolioRlResult(
    string PolicyReturn,
    string CashReturn,
    string EqualBuyHoldReturn,
    string EqualDailyRebalancedReturn,
    int SelectedSeed,
    int SelectedCheckpoint,
    string UniverseSha256,
    string CalendarSha256,
    long ModelSizeBytes,
    string ModelSha256,
    decimal? PolicyCompoundReturn = null,
    decimal? CashCompoundReturn = null,
    decimal? EqualBuyHoldCompoundReturn = null,
    decimal? EqualDailyRebalancedCompoundReturn = null,
    int? EvaluatedBlockCount = null);

/// <summary>
/// A snapshot of one job. Messages are cut to <see cref="ExecutionLimits.MaxStatusMessageChars"/>; paths are removed by the tool layer. A supervised job
/// carries <see cref="Request"/>; a reinforcement-learning job carries <see cref="RlRequest"/> (the period actually used) and, once it succeeded, <see cref="RlResult"/>.
/// </summary>
public sealed record TrainingJobStatus(
    string JobId,
    TrainingJobState State,
    string? Stage,
    int ProgressPercent,
    TimeSpan Elapsed,
    IReadOnlyDictionary<string, double>? Metrics,
    IReadOnlyList<string> Messages,
    EphemeralArtifact? Artifact,
    TrainingFailureReason? FailureReason,
    EphemeralTrainingRequest? Request,
    TrainingJobKind Kind = TrainingJobKind.Supervised,
    EphemeralRlRequest? RlRequest = null,
    EphemeralRlResult? RlResult = null,
    EphemeralPortfolioRlRequest? PortfolioRlRequest = null,
    EphemeralPortfolioRlResult? PortfolioRlResult = null);

/// <summary>Another job cannot start: the allowed number of running jobs is reached.</summary>
public sealed class ExecutionBusyException() : InvalidOperationException("The allowed number of running training jobs is reached.");

/// <summary>No job has this id.</summary>
public sealed class JobNotFoundException(string jobId) : InvalidOperationException("No training job has this id.")
{
    public string JobId { get; } = jobId;
}

/// <summary>No such artifact, or <c>latest</c> was asked for and no job has succeeded.</summary>
public sealed class ArtifactNotFoundException(string artifactId) : InvalidOperationException("No such artifact.")
{
    public string ArtifactId { get; } = artifactId;
}

/// <summary>
/// The job folder is so deep that the files of the job would need a path of <see cref="ExecutionLimits.PathCharsLimit"/> characters or more, which the trainer
/// cannot open on Windows (the temporary folder of the server process is too long). Nothing was created.
/// </summary>
public sealed class ExecutionPathTooLongException(int requiredChars)
    : InvalidOperationException($"The files of a job would need paths of {requiredChars} characters; the trainer cannot open paths of {ExecutionLimits.PathCharsLimit} characters or more.")
{
    /// <summary>Length of the longest path the job would create.</summary>
    public int RequiredChars { get; } = requiredChars;
}

/// <summary>No Python interpreter is installed (the gateway never installs one).</summary>
public sealed class PythonEnvironmentMissingException() : InvalidOperationException("The Python interpreter is not installed.");

/// <summary>
/// The managed Python environment of reinforcement learning (the application's own <c>.venv</c>, not the embedded interpreter) does not exist, or its
/// interpreter cannot import the trainer's packages. <see cref="MissingModule"/> is the top-level module the interpreter named, when it named one (a plain
/// identifier, never a path).
/// </summary>
public sealed class RlEnvironmentMissingException(string? missingModule = null) : InvalidOperationException("The managed Python environment for reinforcement learning is not installed.")
{
    public string? MissingModule { get; } = missingModule;
}

/// <summary>The stored data cannot give the period a reinforcement-learning run needs: no usable stored bars, or fewer than the required number.</summary>
public sealed class InsufficientStoredDataException(int requiredBars, int availableBars)
    : InvalidOperationException("The stored daily data of the symbol is not enough for a reinforcement-learning run.")
{
    public int RequiredBars { get; } = requiredBars;

    /// <summary>Zero when the symbol has no usable stored bars at all (not stored, no equity metadata).</summary>
    public int AvailableBars { get; } = availableBars;
}

/// <summary>
/// The request is refused by one of the application's own rules. It carries both kinds of failure the rules raise (an
/// <see cref="ArgumentException"/> naming a parameter, an <see cref="InvalidOperationException"/> naming none) as one type, so a caller maps
/// one exception to one error code.
/// </summary>
public sealed class ExecutionRequestInvalidException : Exception
{
    public ExecutionRequestInvalidException(string message, string? parameterName, Exception inner)
        : base(message, inner)
    {
        ParameterName = parameterName;
    }

    /// <summary>The request or configuration member that is wrong (the rule's <see cref="ArgumentException.ParamName"/>), or null when the rule names none.</summary>
    public string? ParameterName { get; }
}
