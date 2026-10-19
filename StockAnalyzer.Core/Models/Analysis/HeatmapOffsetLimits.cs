using System;

namespace StockAnalyzer.Core.Models.Analysis;

/// <summary>
/// Code contract for heatmap day-offset bounds and clamping.
/// Defines the valid range [0, 9999] for daily bar lookback offsets.
/// </summary>
public static class HeatmapOffsetLimits
{
    public const int Min = 0;
    public const int Max = 9999;

    public static int Clamp(int value) => Math.Clamp(value, Min, Max);
}
