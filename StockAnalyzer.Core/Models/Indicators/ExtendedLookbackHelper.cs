using System;
using System.Collections.Generic;
using System.Reflection;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Models.Indicators;

/// <summary>
/// Determines how many extra historical bars indicator calculation should request beyond the
/// displayed candle range, purely as warm-up input (never rendered as candles).
/// </summary>
public static class ExtendedLookbackHelper
{
    /// <summary>
    /// Max of: (a) each enabled indicator's declared <see cref="CoreIndicatorParameterBase.GetRequiredWarmupBars"/>,
    /// and (b) the value of a plain public int "Period" property, when present. Multi-period indicators
    /// that have not been audited and given an explicit GetRequiredWarmupBars() override contribute 0
    /// from both sources (staged rollout).
    /// </summary>
    public static int CalculateRequiredLookback(IEnumerable<CoreIndicatorSettings> indicatorSettings)
    {
        int max = 0;
        foreach (var settings in indicatorSettings)
        {
            if (!settings.IsEnabled) continue;
            var parameterObject = settings.ParameterObject;
            if (parameterObject == null) continue;

            int declaredWarmup = parameterObject.GetRequiredWarmupBars();
            if (declaredWarmup > max) max = declaredWarmup;

            PropertyInfo? periodProperty;
            try
            {
                periodProperty = parameterObject.GetType().GetProperty("Period", BindingFlags.Public | BindingFlags.Instance);
            }
            catch (AmbiguousMatchException)
            {
                continue;
            }

            if (periodProperty == null || periodProperty.PropertyType != typeof(int)) continue;
            if (periodProperty.GetValue(parameterObject) is int period && period > max)
            {
                max = period;
            }
        }
        return max;
    }

    /// <summary>
    /// Computes how many entries to retain, from the trim start index onward, when removing an
    /// extended-lookback warm-up prefix from one indicator's result. Equals <paramref name="baseDisplayCount"/>
    /// for indicators whose every named series aligns 1:1 with <paramref name="calculatedCandleCount"/>; for an
    /// indicator with a series longer than that (e.g. Ichimoku's forward-shifted Senkou spans), extends the
    /// retained window so the trailing future-projected surplus is not discarded by the trim.
    /// </summary>
    public static int CalculateRetainedCount(IIndicatorResult result, int baseDisplayCount, int calculatedCandleCount)
    {
        int maxSeriesLength = calculatedCandleCount;
        foreach (var seriesName in result.SeriesNamesList)
        {
            int len = result.GetSeries(seriesName).Count;
            if (len > maxSeriesLength) maxSeriesLength = len;
        }
        return baseDisplayCount + (maxSeriesLength - calculatedCandleCount);
    }

    /// <summary>
    /// True for chart types whose indicator calculation runs on a time-series-shaped candle
    /// array (raw or virtualized-index-mapped) and can therefore accept extra leading bars.
    /// Renko/Kagi/PointAndFigure calculate on a derived brick/column series and are excluded.
    /// </summary>
    public static bool IsEligibleChartType(ChartType chartType) => chartType switch
    {
        ChartType.Candlestick or ChartType.OHLCBar or ChartType.Line or ChartType.Area
            or ChartType.HeikinAshi or ChartType.ThreeLineBreak => true,
        _ => false
    };
}
