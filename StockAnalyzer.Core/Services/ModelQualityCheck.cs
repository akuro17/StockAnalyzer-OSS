using System;
using System.Linq;
using System.Text.Json;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// An objective "is this classification model better than guessing" check on the held-out evaluation the training wrote beside the model
/// (<c>.onnx.metrics.json</c>). A model whose log-loss is not below the log-loss of a uniform guess (ln K for K classes) has probabilities that carry no
/// usable information, however its F1 or accuracy happen to look. It is shown as a hint, and <see cref="ModelAutoActivationGate"/> uses it to stop the AUTOMATIC
/// activation after a registration; choosing the generation by hand is never refused because of it. The functions that take a path read the file themselves;
/// a caller that needs several facts of one file reads it once with <see cref="ModelEvaluationSummary"/>.
/// </summary>
public static class ModelQualityCheck
{
    /// <summary>Key of the held-out (final outer fold) multi-class log-loss in the metrics file; written by <c>metrics.py</c>.</summary>
    public const string LogLossKey = "fold_multi_logloss";

    private const char ClassSeparator = ',';

    /// <summary>
    /// The log-loss of a guess that gives every class the same probability.
    /// </summary>
    public static double UniformLogLoss(int classCount) => Math.Log(classCount);

    /// <summary>
    /// True when the evaluation's log-loss is not below the uniform guess, false when it is, null when it cannot be told (no metrics file, no log-loss,
    /// a regression model, fewer than two classes, unreadable file).
    /// </summary>
    public static bool? IsNotBetterThanUniform(string metricsPath, string classOrder) =>
        ClassCount(classOrder) < 2 ? null : IsNotBetterThanUniform(ModelEvaluationSummary.Read(metricsPath), classOrder);

    /// <summary>The same check on an evaluation that was read already.</summary>
    public static bool? IsNotBetterThanUniform(ModelEvaluationSummary summary, string classOrder) =>
        ClassCount(classOrder) is var classCount && classCount >= 2 && summary.LogLoss is { } logLoss ? logLoss >= UniformLogLoss(classCount) : null;

    /// <summary>The same check for a registered generation (its metrics file is read through a lease, so the files cannot go away meanwhile).</summary>
    public static bool? IsNotBetterThanUniform(ModelGenerationManifest manifest, IModelGenerationRegistry registry) =>
        IsNotBetterThanUniform(ModelEvaluationSummary.Read(manifest, registry), manifest.ClassOrder);

    /// <summary>The finite log-loss of the metrics document <paramref name="root"/>, or null when it is absent, not a number or not finite.</summary>
    internal static double? ParseLogLoss(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(LogLossKey, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var logLoss) && double.IsFinite(logLoss) ? logLoss : null;

    private static int ClassCount(string classOrder) =>
        string.IsNullOrWhiteSpace(classOrder) ? 0 : classOrder.Split(ClassSeparator).Count(label => label.Length > 0);
}
