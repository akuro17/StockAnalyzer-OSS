using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.Indicators;

/// <summary>
/// Single source of truth for presenting the per-bar effective period that a dynamic-period driver
/// (<c>CoreIndicatorSettings.DynamicPeriodIndicatorId</c>) feeds into an indicator.
/// The unit is always "bars", independent of the chart timeframe. Calculation only: the localised row label and unit word live in the Avalonia layer
/// (<c>EffectivePeriodText</c>, locale keys <c>DataWindow_Row_EffectivePeriod</c> / <c>DataWindow_Unit_Bars</c>).
/// </summary>
public static class EffectiveDynamicPeriodDisplay
{
    /// <summary>Text shown when the driver produced no value at the requested bar.</summary>
    public const string UnavailableText = "-";

    private const int MinEffectivePeriod = 1;

    /// <summary>
    /// Returns the periods series to display, or null when the indicator is not driven by a dynamic period
    /// (no driver configured, or the result carries no per-bar periods).
    /// </summary>
    public static IReadOnlyList<decimal?>? Resolve(string? dynamicPeriodIndicatorId, IIndicatorResult? result)
    {
        if (string.IsNullOrEmpty(dynamicPeriodIndicatorId) || result == null) return null;
        return result.EffectiveDynamicPeriods;
    }

    /// <summary>Effective period (bars, rounded half away from zero, at least 1) at <paramref name="index"/>, or null when unavailable.</summary>
    public static int? GetEffectivePeriod(IReadOnlyList<decimal?> periods, int index)
    {
        if (index < 0 || index >= periods.Count) return null;
        decimal? raw = periods[index];
        if (!raw.HasValue) return null;

        decimal rounded = Math.Round(raw.Value, MidpointRounding.AwayFromZero);
        return (int)Math.Clamp(rounded, MinEffectivePeriod, int.MaxValue);
    }
}
