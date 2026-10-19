using System;
using StockAnalyzer.Core.Models.Settings;

namespace StockAnalyzer.Core.Models.Analysis;

/// <summary>
/// Identifies the semantic classification of a heatmap cell return value.
/// </summary>
public enum HeatmapColorKind
{
    NoData,
    Neutral,
    Up,
    Down
}

/// <summary>
/// Pure classification sample representing the direction and interpolation factor [0.0, 1.0].
/// Factor: 0 = base lower-bound color, 1 = full peak color.
/// </summary>
public readonly record struct HeatmapColorSample(HeatmapColorKind Kind, double Factor);

/// <summary>
/// Pure, allocation-free classification engine for heatmap return values.
/// Encapsulates threshold resolution, zero-band separation, and step quantization rules.
/// </summary>
public static class HeatmapColorMapper
{
    public const double MinEffectiveThresholdRatio = 0.001;
    public const double MaxEffectiveThresholdRatio = 1.0;
    public const double NeutralBandRatio = 0.00005; // 0.005%, display value 0.00%
    public const double BoundaryEpsilon = 1e-9;

    /// <summary>
    /// Resolves the effective threshold ratio in [0.001, 1.0] from user percentage and heat multiplier.
    /// Non-finite inputs safely fall back to defaults.
    /// </summary>
    public static double ResolveThresholdRatio(double thresholdPercent, double multiplier)
    {
        if (double.IsNaN(thresholdPercent) || double.IsInfinity(thresholdPercent))
        {
            thresholdPercent = ChartSettingsConstants.DefaultScatterHeatmapThresholdPercent;
        }

        if (double.IsNaN(multiplier) || double.IsInfinity(multiplier))
        {
            multiplier = ChartSettingsConstants.DefaultScatterHeatmapMultiplier;
        }

        return Math.Clamp((thresholdPercent / 100.0) * multiplier, MinEffectiveThresholdRatio, MaxEffectiveThresholdRatio);
    }

    /// <summary>
    /// Classifies a return ratio according to strict ordering rules:
    /// 1. NaN -> NoData
    /// 2. |ret| &lt; 0.00005 -> Neutral
    /// 3. r = min(|ret|, T) / T, direction determined by sign (±inf clamped to r = 1)
    /// 4. StepCount &lt;= 0 -> factor = r, else level = clamp(ceil(r * N - 1e-9), 1, N),
    ///    factor = N &gt; 1 ? (level - 1) / (double)(N - 1) : 0.0.
    /// </summary>
    public static HeatmapColorSample Classify(double returnRatio, double thresholdRatio, int stepCount)
    {
        if (double.IsNaN(returnRatio))
        {
            return new HeatmapColorSample(HeatmapColorKind.NoData, 0.0);
        }

        if (Math.Abs(returnRatio) < NeutralBandRatio)
        {
            return new HeatmapColorSample(HeatmapColorKind.Neutral, 0.0);
        }

        double safeThreshold = Math.Clamp(
            double.IsNaN(thresholdRatio) || double.IsInfinity(thresholdRatio)
                ? (ChartSettingsConstants.DefaultScatterHeatmapThresholdPercent / 100.0)
                : thresholdRatio,
            MinEffectiveThresholdRatio,
            MaxEffectiveThresholdRatio);

        double r = Math.Min(Math.Abs(returnRatio), safeThreshold) / safeThreshold;
        var kind = returnRatio > 0.0 ? HeatmapColorKind.Up : HeatmapColorKind.Down;

        if (stepCount <= 0)
        {
            return new HeatmapColorSample(kind, r);
        }

        int level = Math.Clamp((int)Math.Ceiling((r * stepCount) - BoundaryEpsilon), 1, stepCount);
        double factor = stepCount > 1 ? (level - 1) / (double)(stepCount - 1) : 0.0;
        return new HeatmapColorSample(kind, factor);
    }
}
