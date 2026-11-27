using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Models.Settings;

/// <summary>
/// Strongly-typed configuration for AI Prediction (ONNX).
/// Bound from the "Prediction" section of appsettings.json via IOptions.
/// </summary>
public class PredictionSettings
{
    /// <summary>
    /// Model file, resolved at load time by
    /// <see cref="StockAnalyzer.Core.Common.PathDiscovery.ResolvePredictionModelPath(string)"/>:
    /// a bare filename or <c>Models/</c>-prefixed value lands in <c>&lt;DataRoot&gt;/Models/</c>
    /// (the canonical runtime store, gitignored, operator-swappable); an absolute path is used
    /// verbatim; a file already present next to the executable still wins for back-compat.
    /// </summary>
    public string ModelPath { get; set; } = "trend_predictor.onnx";

    /// <summary>
    /// Default model input length in bars. Single definition: <c>PredictionSettingsManager.DefaultWindowSize</c> re-exposes it, and it
    /// must equal the "Prediction:WindowSize" of appsettings*.json and the Python <c>dataset.DEFAULT_WINDOW</c>.
    /// </summary>
    public const int DefaultWindowSize = 60;

    public int WindowSize { get; set; } = DefaultWindowSize;
    public PredictionFeatureMode FeatureMode { get; set; } = PredictionFeatureMode.OhlcvMinMax;

    /// <summary>Learning objective the configured model was trained for.</summary>
    public TargetType TargetType { get; set; } = TargetType.Classification;

    /// <summary>
    /// Composed-feature channel spec, meaningful only when <see cref="FeatureMode"/> is
    /// <see cref="PredictionFeatureMode.ComposedFeatures"/>; otherwise <see langword="null"/>.
    /// Bound from the "Prediction" section of appsettings.json like every other member here.
    /// </summary>
    public FeatureSpec? FeatureSpec { get; set; }

    /// <summary>
    /// D03 resource bound (StockAnalyzer/CLAUDE.md decision register): the maximum number of
    /// channels a composed-feature spec may carry. Configuration-driven (not a compiled
    /// constant) per the Change Policy's hardcoded-value prohibition; mirrored on the Python
    /// training side by the <c>SA_MAX_COMPOSED_CHANNELS</c> environment variable.
    /// </summary>
    public int MaxComposedChannels { get; set; } = 1024;

    /// <summary>
    /// D03 resource bound: the maximum size, in megabytes, of one inference call's input
    /// tensor. Mirrored on the Python training side by <c>SA_MAX_COMPOSED_TENSOR_SIZE_MB</c>.
    /// </summary>
    public int MaxComposedTensorSizeMB { get; set; } = 256;

    public float ConfidenceThreshold { get; set; } = 0.5f;
    public string? InputNodeName { get; set; }
    public string? OutputNodeName { get; set; }
    public string[] ClassLabels { get; set; } = new[] { "Up", "Down", "Neutral" };
    public int RetryMaxAttempts { get; set; } = 3;
    public int RetryBaseDelayMs { get; set; } = 50;
    public int RetryMaxDelayMs { get; set; } = 500;

    public const int MinNumThreads = 1;
    public const int MaxIntraOpNumThreads = 64;
    public const int MaxInterOpNumThreads = 16;
    public const int DefaultIntraOpNumThreads = 1;
    public const int DefaultInterOpNumThreads = 1;
    public const bool DefaultAllowIntraOpSpinning = false;

    /// <summary>
    /// ONNX Runtime intra-op thread count per session. <see langword="null"/> delegates thread count selection to ORT automatically;
    /// valid non-null values are [1, 64]. Default 1 minimizes synchronization overhead on batch-1 chart inference.
    /// </summary>
    public int? IntraOpNumThreads { get; set; } = DefaultIntraOpNumThreads;

    /// <summary>
    /// ONNX Runtime inter-op thread count per session. <see langword="null"/> leaves the ORT default; valid non-null values are [1, 16].
    /// Under the current fixed <c>ExecutionMode.ORT_SEQUENTIAL</c>, values above 1 only apply if <c>ORT_PARALLEL</c> is enabled in the future.
    /// </summary>
    public int? InterOpNumThreads { get; set; } = DefaultInterOpNumThreads;

    /// <summary>
    /// Sets <c>session.intra_op.allow_spinning</c> (<c>"1"</c> or <c>"0"</c>). Default <see langword="false"/> avoids CPU busy-spin contention against UI/Skia rendering.
    /// </summary>
    public bool AllowIntraOpSpinning { get; set; } = DefaultAllowIntraOpSpinning;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ModelPath)) throw new System.InvalidOperationException("PredictionSettings: ModelPath cannot be empty.");
        if (WindowSize <= 0) throw new System.InvalidOperationException("PredictionSettings: WindowSize must be positive.");
        if (MaxComposedChannels <= 0) throw new System.InvalidOperationException("PredictionSettings: MaxComposedChannels must be positive.");
        if (MaxComposedTensorSizeMB <= 0) throw new System.InvalidOperationException("PredictionSettings: MaxComposedTensorSizeMB must be positive.");
        if (ConfidenceThreshold < 0.0f || ConfidenceThreshold > 1.0f) throw new System.InvalidOperationException("PredictionSettings: ConfidenceThreshold must be between 0.0 and 1.0.");
        if (ClassLabels == null || ClassLabels.Length == 0) throw new System.InvalidOperationException("PredictionSettings: ClassLabels cannot be empty.");
        if (RetryMaxAttempts < 1) throw new System.InvalidOperationException("PredictionSettings: RetryMaxAttempts must be at least 1 (required by Polly RetryStrategyOptions).");
        if (RetryBaseDelayMs <= 0) throw new System.InvalidOperationException("PredictionSettings: RetryBaseDelayMs must be positive.");
        if (RetryMaxDelayMs < RetryBaseDelayMs) throw new System.InvalidOperationException("PredictionSettings: RetryMaxDelayMs must be >= RetryBaseDelayMs.");
        if (IntraOpNumThreads is { } intraOp && (intraOp < MinNumThreads || intraOp > MaxIntraOpNumThreads))
            throw new System.InvalidOperationException($"PredictionSettings: IntraOpNumThreads must be null or between {MinNumThreads} and {MaxIntraOpNumThreads}.");
        if (InterOpNumThreads is { } interOp && (interOp < MinNumThreads || interOp > MaxInterOpNumThreads))
            throw new System.InvalidOperationException($"PredictionSettings: InterOpNumThreads must be null or between {MinNumThreads} and {MaxInterOpNumThreads}.");
    }
}
