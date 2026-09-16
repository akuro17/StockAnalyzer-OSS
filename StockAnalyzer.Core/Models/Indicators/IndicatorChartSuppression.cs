using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.Indicators.Advanced;

namespace StockAnalyzer.Core.Models.Indicators;

/// <summary>
/// Single source of truth for indicator output series that are computed and served (screener,
/// data export) but deliberately NOT charted: their unit / scale (degrees, bars) would dominate a
/// sub-panel whose actually-drawn series are bounded to <c>[-1, 1]</c>.
///
/// Both the chart renderer (<c>IndicatorRenderer</c>) and the sub-panel auto-range calculator
/// (<c>PanelValueRangeCalculator</c>) consult this, so the exclusion is defined exactly once
/// instead of being duplicated per call site. "Not drawn" and "excluded from auto-range" use the
/// identical set, so a single predicate covers every consumer.
/// </summary>
public static class IndicatorChartSuppression
{
    // Series names are bound with nameof to the producing indicator's public series property, which
    // is the exact name CoreIndicatorBase.CreateAutomaticResult emits (prop.Name). "Main" has no
    // backing property, so it comes from IndicatorResult.MainSeriesName.
    private static readonly IReadOnlyDictionary<IndicatorType, IReadOnlySet<string>> SuppressedSeries =
        new Dictionary<IndicatorType, IReadOnlySet<string>>
        {
            [IndicatorType.IFFTInstantaneousPhase] = new HashSet<string>(StringComparer.Ordinal)
            {
                IndicatorResult.MainSeriesName,
                nameof(CoreIfftInstantaneousPhaseIndicator.PhaseDelta),
                nameof(CoreIfftInstantaneousPhaseIndicator.LocalPeriod),
                nameof(CoreIfftInstantaneousPhaseIndicator.PhaseStability),
            },
            [IndicatorType.PolarPhase] = new HashSet<string>(StringComparer.Ordinal)
            {
                IndicatorResult.MainSeriesName,
                nameof(CorePolarPhaseIndicator.PhaseStability),
            },
            [IndicatorType.DMI] = new HashSet<string>(StringComparer.Ordinal)
            {
                IndicatorResult.MainSeriesName,
            },
        };

    /// <summary>
    /// True when <paramref name="seriesName"/> of the given indicator <paramref name="type"/> must
    /// not be drawn on the chart and must be kept out of the sub-panel auto-range. Allocation-free.
    /// </summary>
    public static bool IsSuppressed(IndicatorType? type, string seriesName) =>
        type is { } t
        && SuppressedSeries.TryGetValue(t, out IReadOnlySet<string>? set)
        && set.Contains(seriesName);
}
