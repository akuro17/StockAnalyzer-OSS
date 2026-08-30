using System.Collections.Generic;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>
/// One row of the training wizard's per-fold results table. Built from a fold-scoped
/// <c>METRIC:</c> line emitted by <c>run_training.evaluate_folds</c> (keys <c>fold</c>,
/// <c>n_splits</c>, <c>fold_n</c>, <c>fold_accuracy</c>, <c>fold_macro_f1</c>,
/// <c>fold_baseline_accuracy</c>, <c>fold_multi_logloss</c>). The numbers come from an
/// inference-only pass of the exported ONNX over each walk-forward fold's validation block,
/// so they describe the final model's behavior across time slices - not a re-trained CV.
/// </summary>
public sealed record FoldMetricRow
{
    /// <summary>Zero-based fold index as reported by the trainer.</summary>
    public required int Fold { get; init; }

    /// <summary>Total walk-forward split count for the run.</summary>
    public required int Splits { get; init; }

    /// <summary>Pooled validation-sample count for this fold.</summary>
    public required int SampleCount { get; init; }

    /// <summary>Fold accuracy, 0-1.</summary>
    public required double Accuracy { get; init; }

    /// <summary>Majority-class baseline accuracy for this fold, 0-1.</summary>
    public required double BaselineAccuracy { get; init; }

    /// <summary>Macro-averaged F1 for this fold, 0-1.</summary>
    public required double MacroF1 { get; init; }

    /// <summary>Multi-class log loss, or <see cref="double.NaN"/> when the trainer could not compute it.</summary>
    public required double MultiLogloss { get; init; }

    /// <summary>Human-facing "1/5" fold label (one-based over the split count).</summary>
    public string FoldLabel => Splits > 0 ? $"{Fold + 1}/{Splits}" : $"{Fold + 1}";

    /// <summary>Accuracy lift over the majority baseline; may be negative.</summary>
    public double AccuracyOverBaseline => Accuracy - BaselineAccuracy;

    /// <summary>
    /// Builds a row from a <c>METRIC:</c> payload, or returns <see langword="null"/> when the
    /// dictionary is not a fold row (no <c>fold</c> / <c>fold_accuracy</c> key) - which is how
    /// the out-of-sample line and the final aggregate line are skipped.
    /// </summary>
    public static FoldMetricRow? FromMetric(IReadOnlyDictionary<string, double>? metric)
    {
        if (metric is null
            || !metric.TryGetValue("fold", out var fold)
            || !metric.ContainsKey("fold_accuracy"))
        {
            return null;
        }

        return new FoldMetricRow
        {
            Fold = ToInt(fold),
            Splits = metric.TryGetValue("n_splits", out var splits) ? ToInt(splits) : 0,
            SampleCount = metric.TryGetValue("fold_n", out var n) ? ToInt(n) : 0,
            Accuracy = Value(metric, "fold_accuracy"),
            BaselineAccuracy = Value(metric, "fold_baseline_accuracy"),
            MacroF1 = Value(metric, "fold_macro_f1"),
            MultiLogloss = Value(metric, "fold_multi_logloss"),
        };
    }

    private static double Value(IReadOnlyDictionary<string, double> metric, string key)
        => metric.TryGetValue(key, out var v) ? v : double.NaN;

    private static int ToInt(double value)
        => double.IsNaN(value) || double.IsInfinity(value) ? 0 : (int)value;
}
