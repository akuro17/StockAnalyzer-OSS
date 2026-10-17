using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// The held-out confidence histogram of a classification model: counts of the windows by their largest class probability, in <see cref="Bins"/> equal bins over
/// [0, 1] (bin <c>b</c> holds <c>[b/bins, (b+1)/bins)</c>, the last bin also a probability of exactly 1).
/// </summary>
public sealed record ConfidenceHistogram(int Bins, long Total, IReadOnlyList<long> Counts)
{
    /// <summary>
    /// The share (0..1) of windows at or above <paramref name="threshold"/>, counted from bin <c>ceil(threshold * bins)</c>: a threshold that is not a multiple of
    /// <c>1/bins</c> is rounded up and the share is then a LOWER bound of the exact one. Null for a threshold outside (0, 1].
    /// </summary>
    public double? ShareAtOrAbove(float threshold) => Share(threshold, upperBound: false);

    /// <summary>
    /// The UPPER bound of the same share, counted from bin <c>floor(threshold * bins)</c> (at most the last bin): every window that could reach the threshold. It equals
    /// <see cref="ShareAtOrAbove"/> for a multiple of <c>1/bins</c>. A share of 0 here means that no window reaches the threshold.
    /// </summary>
    public double? ShareUpperBound(float threshold) => Share(threshold, upperBound: true);

    private double? Share(float threshold, bool upperBound)
    {
        if (!float.IsFinite(threshold) || threshold <= 0f || threshold > 1f) return null;
        decimal position = (decimal)threshold * Bins;
        int firstBin = upperBound
            ? (int)Math.Min(Bins - 1, Math.Max(0m, Math.Floor(position)))
            : (int)Math.Min(Bins, Math.Max(0m, Math.Ceiling(position)));
        return (double)Counts.Skip(firstBin).Sum() / Total;
    }
}

/// <summary>
/// How often a classification model is confident on its independent hold-out: the share of held-out windows whose largest class probability reaches a
/// threshold, read from the histogram the training wrote into <c>.onnx.metrics.json</c> (<c>confidence_reach</c>, see <c>metrics.confidence_histogram</c>).
/// A model whose share is 0 never names a class at that threshold, so the chart shows "no decision" for it. Shown as information, and <see cref="ModelAutoActivationGate"/>
/// stops the automatic activation of such a model; choosing it by hand is never refused. The functions here read the metrics file themselves; a caller that needs
/// several facts of one file reads it once with <see cref="ModelEvaluationSummary"/>.
/// </summary>
public static class ModelConfidenceReach
{
    /// <summary>Key of the object in the metrics file.</summary>
    public const string Key = "confidence_reach";

    /// <summary>Keys inside the object; written by <c>metrics.confidence_histogram</c> (pinned to its source by a test).</summary>
    public const string BinsKey = "bins", CountKey = "n", HistogramKey = "max_probability_histogram";

    /// <summary>The largest bin count a reader accepts; a larger value is a damaged or foreign file.</summary>
    private const int MaximumBins = 1000;

    /// <summary>
    /// The lower bound of the share (0..1) of held-out windows whose largest probability is at or above <paramref name="threshold"/> (see
    /// <see cref="ConfidenceHistogram.ShareAtOrAbove"/>). Null when it cannot be told: no file, no key, a damaged histogram (bins outside 1..1000, a length other than
    /// bins, a negative or non-integer count, a total other than n, n = 0) or a threshold outside (0, 1].
    /// </summary>
    public static double? ShareAtOrAbove(string metricsPath, float threshold) => ModelEvaluationSummary.Read(metricsPath).Reach?.ShareAtOrAbove(threshold);

    /// <summary>The upper bound of the same share (see <see cref="ConfidenceHistogram.ShareUpperBound"/>); use it where a decision must be certain (<see cref="ModelAutoActivationGate"/>).</summary>
    public static double? ShareUpperBound(string metricsPath, float threshold) => ModelEvaluationSummary.Read(metricsPath).Reach?.ShareUpperBound(threshold);

    /// <summary>The lower bound for a registered generation (its metrics file is read through a lease, so the files cannot go away meanwhile).</summary>
    public static double? ShareAtOrAbove(ModelGenerationManifest manifest, IModelGenerationRegistry registry, float threshold) =>
        ModelEvaluationSummary.Read(manifest, registry).Reach?.ShareAtOrAbove(threshold);

    /// <summary>The upper bound for a registered generation, read through a lease as above.</summary>
    public static double? ShareUpperBound(ModelGenerationManifest manifest, IModelGenerationRegistry registry, float threshold) =>
        ModelEvaluationSummary.Read(manifest, registry).Reach?.ShareUpperBound(threshold);

    /// <summary>The histogram of the metrics document <paramref name="root"/>, or null when the key is absent or the histogram is damaged.</summary>
    internal static ConfidenceHistogram? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(Key, out var reach) || reach.ValueKind != JsonValueKind.Object
            || !reach.TryGetProperty(BinsKey, out var binsElement) || !binsElement.TryGetInt32(out var bins) || bins < 1 || bins > MaximumBins
            || !reach.TryGetProperty(CountKey, out var countElement) || !countElement.TryGetInt64(out var total) || total <= 0
            || !reach.TryGetProperty(HistogramKey, out var histogramElement) || histogramElement.ValueKind != JsonValueKind.Array
            || histogramElement.GetArrayLength() != bins) return null;

        var counts = new long[bins];
        int index = 0;
        foreach (var element in histogramElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var count) || count < 0) return null;
            counts[index++] = count;
        }
        return counts.Sum() == total ? new ConfidenceHistogram(bins, total, counts) : null;
    }
}
