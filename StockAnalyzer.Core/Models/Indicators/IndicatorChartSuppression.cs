using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using StockAnalyzer.Core.Models.Indicators.Statistics;

namespace StockAnalyzer.Core.Models.Indicators;

/// <summary>
/// Single source of truth for indicator output series that are computed and served (screener,
/// data export) but deliberately NOT charted: their unit / scale (degrees, bars) would dominate a
/// chart or sub-panel whose actually-drawn series are price-scaled or bounded to <c>[-1, 1]</c>.
///
/// The chart renderer (<c>IndicatorRenderer</c>), the price panel snapshot (<c>ChartDataSnapshot</c>),
/// the sub-panel auto-range calculator (<c>PanelValueRangeCalculator</c>), and the inspector
/// (<c>DataWindowViewModel</c>) all consult this, so the exclusion is defined exactly once
/// instead of being duplicated per call site.
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
            [IndicatorType.ClothoidOscillator] = new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(CoreClothoidOscillatorIndicator.RawCurvatureRate),
            },
            [IndicatorType.ClothoidMovingAverage] = new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(CoreClothoidMovingAverageIndicator.EffectiveLag),
                nameof(CoreClothoidMovingAverageIndicator.EffectiveSampleSize),
            },
            [IndicatorType.CrossCorrelation] = new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(CoreCrossCorrelationIndicator.OptimalLag),
            },
            [IndicatorType.CointegrationSpread] = new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(CoreCointegrationSpreadIndicator.Intercept),
            },
        };

    private static readonly IReadOnlyDictionary<IndicatorType, IReadOnlySet<string>> SuppressedDataWindowSeries =
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
            [IndicatorType.ClothoidOscillator] = new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(CoreClothoidOscillatorIndicator.RawCurvatureRate),
            },
            [IndicatorType.ClothoidMovingAverage] = new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(CoreClothoidMovingAverageIndicator.EffectiveLag),
                nameof(CoreClothoidMovingAverageIndicator.EffectiveSampleSize),
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

    /// <summary>
    /// True when <paramref name="seriesName"/> of the given indicator <paramref name="type"/> must
    /// not be displayed in the DataWindow inspector. Allocation-free.
    /// </summary>
    public static bool IsDataWindowSuppressed(IndicatorType? type, string seriesName) =>
        type is { } t
        && SuppressedDataWindowSeries.TryGetValue(t, out IReadOnlySet<string>? set)
        && set.Contains(seriesName);
}
