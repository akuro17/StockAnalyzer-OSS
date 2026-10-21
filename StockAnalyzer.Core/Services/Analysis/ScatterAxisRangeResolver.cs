using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;

namespace StockAnalyzer.Core.Services.Analysis;

/// <summary>
/// Decides whether a scatter metric has a fixed value range, and which. The single definition of "fixed" is the one the chart panels already use
/// (<c>PanelValueRangeCalculator</c>): the indicator's default settings carry BOTH <see cref="CoreIndicatorSettings.MinValue"/> and
/// <see cref="CoreIndicatorSettings.MaxValue"/>. No range is inferred for an indicator that has none (a mathematically bounded indicator
/// without a registered range is not fixed), and a Column or a Price source is never fixed.
/// The range is a chart DISPLAY range of the whole indicator, so an output that is not bounded by it is not fixed either
/// (<see cref="OutputsOutsideTheirRange"/>; owner decision F-27 = A).
/// </summary>
public static class ScatterAxisRangeResolver
{
    /// <summary>
    /// The (indicator, output) pairs whose values leave the indicator's fixed range (found 2026-10-21, plan finding F-27; completed 2026-10-22 with
    /// <c>IFFTInstantaneousPhase.PhaseStability</c> and <c>SSAAnomaly.ZScore</c>, which the real daily, weekly and monthly data leave although the first audit
    /// series did not show them): an angle in degrees against [-1,1], a period in bars, a price-scale line against [-4,4], ... They get an automatic axis.
    /// One table for every time frame: whether an output is bounded by its range does not depend on the bar size. The test
    /// <c>ScatterAxisRangeResolverTests</c> fails when an audit series shows an output outside its range that is not in this table, and when a pair of this
    /// table leaves its range on none of the audit series.
    /// Sorted by indicator type name, then output.
    /// </summary>
    public static IReadOnlyList<(IndicatorType Type, string Output)> OutputsOutsideTheirRange { get; } = new[]
    {
        (IndicatorType.Autocorrelation, "DominantPeriod"),
        (IndicatorType.Autocorrelation, "RawPeriod"),
        (IndicatorType.CrossCorrelation, "OptimalLag"),
        (IndicatorType.GranvilleLaw, "MA"),
        (IndicatorType.IFFTInstantaneousPhase, "LocalPeriod"),
        (IndicatorType.IFFTInstantaneousPhase, "PhaseAngle"),
        (IndicatorType.IFFTInstantaneousPhase, "PhaseDelta"),
        (IndicatorType.IFFTInstantaneousPhase, "PhaseStability"),
        (IndicatorType.PolarPhase, "PhaseStability"),
        (IndicatorType.PolarPhase, "PolarPhaseAngle"),
        (IndicatorType.SSAAnomaly, "ZScore"),
        (IndicatorType.SSASNR, "SignalPurity"),
    };

    public static bool TryResolve(
        ScatterMetricDefinition? definition,
        IScreenerCatalogProvider catalogProvider,
        IIndicatorFactory? indicatorFactory,
        out AxisRange range)
    {
        ArgumentNullException.ThrowIfNull(catalogProvider);
        range = default;

        ScreenerIndicatorSideConfig? source = definition?.Source;
        if (source is null || source.CategoryType != ScreenerItemCategoryType.Indicator || source.IndicatorType == IndicatorType.Price)
        {
            return false;
        }

        if (IsOutsideItsRange(source, indicatorFactory))
        {
            return false;
        }

        CoreIndicatorSettings? settings = catalogProvider.GetDefaultSettings(source.IndicatorType, indicatorFactory);
        if (settings?.MinValue is not decimal min || settings.MaxValue is not decimal max || min >= max)
        {
            return false;
        }

        // The only decimal -> double conversion of the range: the scatter axis is in double data coordinates.
        range = new AxisRange((double)min, (double)max);
        return true;
    }

    private static bool IsOutsideItsRange(ScreenerIndicatorSideConfig source, IIndicatorFactory? indicatorFactory)
    {
        foreach ((IndicatorType type, string output) in OutputsOutsideTheirRange)
        {
            // Compared as the same series: an alias of the main series and the main series itself are one output.
            if (type == source.IndicatorType
                && string.Equals(
                    IndicatorOutputSeriesResolver.Normalize(type, source.OutputName, null, indicatorFactory),
                    IndicatorOutputSeriesResolver.Normalize(type, output, null, indicatorFactory),
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
