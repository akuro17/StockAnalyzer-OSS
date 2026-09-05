using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace StockAnalyzer.Core.Models.Training;

/// <summary>
/// The single hand-off contract for a model-training run. The same vocabulary is shared by
/// three consumers: the Avalonia training wizard that fills it in, the
/// <c>run_training.py --config &lt;job.json&gt;</c> orchestrator that executes it, and the ONNX
/// <c>metadata_props</c> stamped onto the produced model. JSON shape (snake_case property
/// names, lower-case enum spellings) is fixed by <see cref="TrainingConfigJson"/> and mirrored
/// by the Python dataclass.
/// </summary>
/// <remarks>
/// This release trains OHLCV-only models (<see cref="PredictionFeatureMode.OhlcvMinMax"/>).
/// <see cref="TargetType"/> selects a classification (historical default) or regression objective;
/// <see cref="NSplits"/> / <see cref="Gap"/> / <see cref="OosTailDays"/> expose the walk-forward
/// and out-of-sample holdout controls. Indicator features and ensembles remain out of scope and
/// are added additively later.
/// </remarks>
public sealed record TrainingJobConfig
{
    /// <summary>
    /// Ticker symbols the run trains on, already resolved from the wizard's scope selection
    /// (all tickers / a watchlist / a portfolio). Must be non-empty and free of blank entries.
    /// </summary>
    public required string[] Symbols { get; init; }

    /// <summary>Inclusive lower bound applied to the parquet <c>date</c> column; <see langword="null"/> = no lower bound.</summary>
    public DateOnly? StartDate { get; init; }

    /// <summary>Inclusive upper bound applied to the parquet <c>date</c> column; <see langword="null"/> = no upper bound.</summary>
    public DateOnly? EndDate { get; init; }

    /// <summary>Bar-aggregation level; selects the source parquet directory.</summary>
    public TrainingTimeframe Timeframe { get; init; } = TrainingTimeframe.Daily;

    /// <summary>Which Python trainer executes the run.</summary>
    public TrainingFramework Framework { get; init; } = TrainingFramework.PyTorch;

    /// <summary>
    /// Model architecture within the chosen <see cref="Framework"/> (for example <c>lstm</c>,
    /// <c>gru</c>, <c>cnn</c>, <c>gbdt</c>). Free-form so new architectures need no enum change;
    /// the trainer validates the concrete value.
    /// </summary>
    public required string Architecture { get; init; }

    /// <summary>
    /// Feature normalization strategy. <see cref="PredictionFeatureMode.OhlcvMinMax"/> (the fixed
    /// 5-channel base) and <see cref="PredictionFeatureMode.ComposedFeatures"/> (a user-composed
    /// variable-width set described by <see cref="FeatureSpec"/>) are the training-supported modes
    /// in this release.
    /// </summary>
    public PredictionFeatureMode FeatureMode { get; init; } = PredictionFeatureMode.OhlcvMinMax;

    /// <summary>
    /// The ordered input channels for a <see cref="PredictionFeatureMode.ComposedFeatures"/> run.
    /// Required and non-empty when <see cref="FeatureMode"/> is
    /// <see cref="PredictionFeatureMode.ComposedFeatures"/>; MUST be <see langword="null"/> for every
    /// other mode. Echoed into the ONNX <c>metadata_props["feature_spec"]</c> for inference-time
    /// cross-checking.
    /// </summary>
    public FeatureSpec? FeatureSpec { get; init; }

    /// <summary>Look-back length in bars. Must be positive.</summary>
    public required int WindowSize { get; init; }

    /// <summary>Forward label horizon in bars. Must be positive.</summary>
    public required int Horizon { get; init; }

    /// <summary>
    /// Learning objective. <see cref="Training.TargetType.Classification"/> (the historical default)
    /// trains the 3-class Up/Down/Neutral head; <see cref="Training.TargetType.Regression"/> trains a
    /// single-value forward-log-return head.
    /// </summary>
    public TargetType TargetType { get; init; } = TargetType.Classification;

    /// <summary>
    /// Expanding-window walk-forward fold count handed to the trainers. Defaults to
    /// <see cref="WalkForwardDataRequirement.DefaultSplitCount"/> (the C# mirror of
    /// <c>dataset.DEFAULT_WF_SPLITS</c>). Must be at least 2.
    /// </summary>
    public int NSplits { get; init; } = WalkForwardDataRequirement.DefaultSplitCount;

    /// <summary>
    /// Purge margin in bars between each walk-forward fold's train and test blocks.
    /// <see langword="null"/> lets the Python side apply its default of <c>WindowSize + Horizon - 1</c>.
    /// When set, must be non-negative.
    /// </summary>
    public int? Gap { get; init; }

    /// <summary>
    /// Length in calendar days of a fixed out-of-sample tail withheld from both training and
    /// cross-validation and evaluated exactly once. <see langword="null"/> disables the holdout.
    /// When set, must be non-negative.
    /// </summary>
    public int? OosTailDays { get; init; }

    /// <summary>
    /// Trainer hyper-parameters as string key/value pairs (for example <c>hidden</c>,
    /// <c>layers</c>, <c>epochs</c>, <c>lr</c>, <c>num_leaves</c>). Kept as strings so the set of
    /// keys is trainer-defined and the config stays serializer-agnostic; the trainer parses
    /// and range-checks each value. Defaults to empty.
    /// </summary>
    public IReadOnlyDictionary<string, string> Hyperparameters { get; init; }
        = new Dictionary<string, string>();

    /// <summary>
    /// Optional user-supplied base name (no extension, no directory) for the produced artifact.
    /// When <see langword="null"/> or blank the orchestrator derives one as
    /// <c>{scope}_{timeframe}_{task}_{arch}_{feattag}_{RunId}</c>.
    /// </summary>
    public string? OutputName { get; init; }

    /// <summary>
    /// Orchestrator-assigned run identifier, set by <see cref="Services.TrainingOrchestrator"/>
    /// immediately before this config is serialized to the wire JSON -- never populated by the
    /// wizard UI. Threaded through to <c>run_training.py</c> so both sides share exactly one
    /// identifier for the experiment-log folder name, the trainer's own <c>run_id=</c> log line,
    /// and the derived ONNX artifact filename stem. Replaces the previous design where each side
    /// independently derived its own timestamp-based id from two different clocks (C#'s
    /// pre-launch UTC vs Python's post-launch local time), which were never guaranteed to match.
    /// </summary>
    public string? RunId { get; init; }

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when the config cannot produce a valid run.
    /// Checks required cardinality and ranges only; it does not touch the file system.
    /// </summary>
    public void Validate()
    {
        if (Symbols is null || Symbols.Length == 0)
        {
            throw new InvalidOperationException("TrainingJobConfig: Symbols cannot be empty.");
        }

        if (Symbols.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException("TrainingJobConfig: Symbols cannot contain blank entries.");
        }

        if (string.IsNullOrWhiteSpace(Architecture))
        {
            throw new InvalidOperationException("TrainingJobConfig: Architecture cannot be empty.");
        }

        if (WindowSize <= 0)
        {
            throw new InvalidOperationException("TrainingJobConfig: WindowSize must be positive.");
        }

        if (Horizon <= 0)
        {
            throw new InvalidOperationException("TrainingJobConfig: Horizon must be positive.");
        }

        if (NSplits < 2)
        {
            throw new InvalidOperationException("TrainingJobConfig: NSplits must be at least 2.");
        }

        if (Gap is { } gap && gap < 0)
        {
            throw new InvalidOperationException("TrainingJobConfig: Gap cannot be negative.");
        }

        if (OosTailDays is { } oosTailDays && oosTailDays < 0)
        {
            throw new InvalidOperationException("TrainingJobConfig: OosTailDays cannot be negative.");
        }

        if (StartDate is { } start && EndDate is { } end && start > end)
        {
            throw new InvalidOperationException("TrainingJobConfig: StartDate must be on or before EndDate.");
        }

        if (FeatureMode is not (PredictionFeatureMode.OhlcvMinMax or PredictionFeatureMode.ComposedFeatures))
        {
            throw new InvalidOperationException(
                $"TrainingJobConfig: FeatureMode '{FeatureMode}' is not supported yet; " +
                "only OhlcvMinMax and ComposedFeatures are available in this release.");
        }

        if (FeatureMode == PredictionFeatureMode.ComposedFeatures)
        {
            if (FeatureSpec is null)
            {
                throw new InvalidOperationException(
                    "TrainingJobConfig: FeatureMode 'ComposedFeatures' requires a non-null FeatureSpec.");
            }

            if (!FeatureSpec.IsValid(out var featureSpecError))
            {
                throw new InvalidOperationException($"TrainingJobConfig: FeatureSpec is invalid. {featureSpecError}");
            }
        }
        else if (FeatureSpec is not null)
        {
            throw new InvalidOperationException(
                $"TrainingJobConfig: FeatureSpec must be null unless FeatureMode is 'ComposedFeatures' (was '{FeatureMode}').");
        }

        if (Hyperparameters is null)
        {
            throw new InvalidOperationException("TrainingJobConfig: Hyperparameters cannot be null (use an empty map).");
        }

        if (OutputName is not null
            && OutputName.Length > 0
            && OutputName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException("TrainingJobConfig: OutputName contains invalid file-name characters.");
        }
    }
}
