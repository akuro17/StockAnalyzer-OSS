using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using StockAnalyzer.Core.Models.Indicators.Statistics;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators;

/// <summary>
/// Single source of truth for indicator output series that are computed and served (screener,
/// data export) but deliberately NOT charted: their unit / scale (degrees, bars) would dominate a
/// chart or sub-panel whose actually-drawn series are price-scaled or bounded to <c>[-1, 1]</c>.
///
/// The chart renderer (<c>IndicatorRenderer</c>), the price panel snapshot (<c>ChartDataSnapshot</c>),
/// the sub-panel auto-range calculator (<c>PanelValueRangeCalculator</c>), the AI chart image
/// (<c>AiChartSeriesSelector</c>) and the inspector (<c>DataWindowViewModel</c>) all consult this, so the
/// exclusion is defined exactly once instead of being duplicated per call site.
///
/// Besides the fixed per-type table, a series can be hidden only while a parameter of its indicator says so
/// (<c>ParameterDependentSuppressedSeries</c>, for example the KST Signal while <c>ShowSignal</c> is off): the chart,
/// the sub-panel range and the AI image then leave it out, while the series stays in the indicator result, the
/// DataWindow, the screener and the backtest. The chart-side callers pass the parameter object of the indicator setting.
/// </summary>
public static class IndicatorChartSuppression
{
    // Same values as the Main / Upper / Lower series under another name; must be declared before the tables that use it.
    private static readonly string[] SsaResidualBandAliasSeries =
    {
        nameof(CoreSSAResidualBandIndicator.CenterBand),
        nameof(CoreSSAResidualBandIndicator.UpperBand),
        nameof(CoreSSAResidualBandIndicator.LowerBand),
        nameof(CoreSSAResidualBandIndicator.Center),
    };

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
                nameof(CoreCointegrationSpreadIndicator.HalfLife),
            },
            [IndicatorType.Donchian] = new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(CoreDonchianChannelIndicator.Position),
            },
            // BandWidth is a percentage (not drawn on the price panel, still shown in the DataWindow) and the aliases
            // repeat the drawn Main / Upper / Lower series; all of them stay in the result so the screener can resolve them.
            [IndicatorType.SSAResidualBand] = new HashSet<string>(SsaResidualBandAliasSeries, StringComparer.Ordinal)
            {
                nameof(CoreSSAResidualBandIndicator.BandWidth),
            },
            [IndicatorType.CrossTickerDtw] = new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(CoreCrossTickerDtwIndicator.NormalizedDistance),
                nameof(CoreCrossTickerDtwIndicator.SimilarityScore),
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
            [IndicatorType.SSAResidualBand] = new HashSet<string>(SsaResidualBandAliasSeries, StringComparer.Ordinal),
        };

    /// <summary>
    /// A series that is hidden from the chart only while a parameter of its indicator says so (for example a
    /// "Show ..." switch). The series always stays in the indicator result, the DataWindow and the screener.
    /// </summary>
    private readonly record struct ParameterDependentSuppression(string SeriesName, Func<CoreIndicatorParameterBase?, bool> IsHidden);

    // KST Signal: always calculated; drawn only when ShowSignal is on. A missing parameter object means the defaults, so it is hidden too.
    private static readonly IReadOnlyDictionary<IndicatorType, ParameterDependentSuppression> ParameterDependentSuppressedSeries =
        new Dictionary<IndicatorType, ParameterDependentSuppression>
        {
            [IndicatorType.KST] = new ParameterDependentSuppression(
                nameof(CoreKstIndicator.Signal),
                static parameters => parameters is not CoreKstParameter { ShowSignal: true }),
        };

    /// <summary>
    /// True when <paramref name="seriesName"/> of the given indicator <paramref name="type"/> must
    /// not be drawn on the chart and must be kept out of the sub-panel auto-range. Allocation-free.
    /// <paramref name="parameters"/> is the parameter object of the indicator setting; it only matters for the series
    /// that a parameter hides (see <see cref="ParameterDependentSuppression"/>), so every other answer is unchanged.
    /// </summary>
    public static bool IsSuppressed(IndicatorType? type, string seriesName, CoreIndicatorParameterBase? parameters = null)
    {
        if (type is not { } t)
        {
            return false;
        }

        if (SuppressedSeries.TryGetValue(t, out IReadOnlySet<string>? set) && set.Contains(seriesName))
        {
            return true;
        }

        return ParameterDependentSuppressedSeries.TryGetValue(t, out ParameterDependentSuppression rule)
               && string.Equals(rule.SeriesName, seriesName, StringComparison.Ordinal)
               && rule.IsHidden(parameters);
    }

    /// <summary>
    /// True when <paramref name="seriesName"/> of the given indicator <paramref name="type"/> must
    /// not be displayed in the DataWindow inspector. Allocation-free.
    /// </summary>
    public static bool IsDataWindowSuppressed(IndicatorType? type, string seriesName) =>
        type is { } t
        && SuppressedDataWindowSeries.TryGetValue(t, out IReadOnlySet<string>? set)
        && set.Contains(seriesName);
}
