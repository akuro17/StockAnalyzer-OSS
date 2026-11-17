using System;
using System.Collections.Generic;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Avalonia.Common;

/// <summary>
/// Single source of truth for the Timeframe row of the Indicator Manager and the Indicator Properties dialog (visibility, enabled state, tooltip,
/// the reset when an indicator joins a chain, and which indicators may be picked as a chain input). Free of Avalonia types so both view models share it.
/// </summary>
public static class IndicatorTimeframeUiRules
{
    /// <summary>The choices in the order of the scale (declaration order), shown with localized texts.</summary>
    public static IReadOnlyList<IndicatorTimeframe> Options { get; } = new[]
    {
        IndicatorTimeframe.SameAsChart,
        IndicatorTimeframe.Weekly,
        IndicatorTimeframe.Monthly,
    };

    /// <summary>True when the setting applies to the indicator at all (hidden for Pivots, block, cross-ticker, breadth, non-causal, Anchored VWAP and Price).</summary>
    public static bool IsShown(CoreIndicatorSettings? settings)
        => settings?.TypeEnum is { } type && IndicatorTimeframePolicy.SupportsIndicatorTimeframe(type, settings.ParameterObject);

    /// <summary>Whether the row can be edited: it applies to the indicator and the indicator is not part of a chain (a chain is calculated on one bar series; the definition is <see cref="IndicatorTimeframePolicy"/>).</summary>
    public static bool IsEnabled(CoreIndicatorSettings? settings, IEnumerable<CoreIndicatorSettings>? all)
        => settings != null && IsShown(settings) && !IndicatorTimeframePolicy.HasOwnChainInput(settings) && !IndicatorTimeframePolicy.IsReferencedByOther(settings, all);

    /// <summary>The reason the row is disabled, or null while it is enabled or hidden (nothing to explain).</summary>
    public static string? ToolTip(CoreIndicatorSettings? settings, IEnumerable<CoreIndicatorSettings>? all)
        => settings != null && IsShown(settings) && !IsEnabled(settings, all)
            ? LocalizationManager.Instance["Tooltip_TimeframeDisabledChained"]
            : null;

    /// <summary>
    /// Puts an indicator that has just joined a chain back on the chart's own time frame. Call right after the Source Indicator / Dynamic Period Driver id is set and
    /// before change notifications are raised, so the row never shows a value the pipeline will ignore. Returns true when the value was changed.
    /// </summary>
    public static bool ResetIfChained(CoreIndicatorSettings? settings)
    {
        if (settings == null || settings.Timeframe == IndicatorTimeframe.SameAsChart || !IndicatorTimeframePolicy.HasOwnChainInput(settings))
        {
            return false;
        }

        settings.Timeframe = IndicatorTimeframe.SameAsChart;
        return true;
    }

    /// <summary>
    /// False for an indicator that is calculated on another time frame than the chart's: it cannot feed a chain (the chain runs on the chart's bars).
    /// <paramref name="isCurrentSelection"/> keeps an already chosen reference in the list so a selection never silently disappears.
    /// </summary>
    public static bool IsUsableAsChainInput(CoreIndicatorSettings candidate, bool isCurrentSelection)
        => isCurrentSelection || candidate.Timeframe == IndicatorTimeframe.SameAsChart || !IsShown(candidate);
}
