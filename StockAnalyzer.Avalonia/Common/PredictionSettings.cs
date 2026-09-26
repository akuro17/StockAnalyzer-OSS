using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Avalonia.Common;

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
    public int WindowSize { get; set; } = 75;
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
    }
}
