using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services.Analysis;

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

            int warmup = BaseWarmupBars(parameterObject);
            if (warmup > max) max = warmup;
        }
        return max;
    }

    /// <summary>Completed higher-frame periods added on top of the indicator's own warm-up: the leading period is dropped and one more closes the alignment (<c>TimeframeAlignment</c> reads the period before the current one).</summary>
    public const int AlignmentSafetyPeriods = 2;

    /// <summary>Daily bars per ISO week (trading days), the D1 to W1 step.</summary>
    public const int WeeklyMultiplierFromDaily = 5;

    /// <summary>Daily bars per calendar month (trading days), the D1 to MN1 step.</summary>
    public const int MonthlyMultiplierFromDaily = 21;

    /// <summary>
    /// Warm-up that also covers indicators calculated on a higher frame than the chart (<see cref="CoreIndicatorSettings.Timeframe"/>). The result is in bars of the CHART frame.
    /// For an enabled indicator that supports the setting and whose effective frame is coarser than <paramref name="chartFrame"/> (and only when
    /// <paramref name="applyTimeframeScaling"/>: the chart type draws time-based bars) the need is <c>(max(0, B) + <see cref="AlignmentSafetyPeriods"/>) x M</c>, with B the base warm-up
    /// of the parameter object (0 when there is none, e.g. OBV) and M the chart bars per higher-frame bar (D1 to W1 = 5, D1 to MN1 = 21; a Monthly indicator on a Weekly chart is refused by the pipeline, so it is not scaled).
    /// An indicator that reads or is read by another indicator is calculated on the chart's own frame by the pipeline, so it is not scaled either.
    /// Every other indicator contributes B, exactly as the one-argument overload. A chart frame without such a step (an intraday frame) has no defined M: its indicators are not scaled.
    /// </summary>
    public static int CalculateRequiredLookback(IEnumerable<CoreIndicatorSettings> indicatorSettings, TimeFrame chartFrame, bool applyTimeframeScaling = true)
    {
        // The list is walked twice only when an indicator asks for scaling (the chain participants are worked out over the complete list, as the pipeline does).
        var all = indicatorSettings as IReadOnlyList<CoreIndicatorSettings> ?? indicatorSettings.ToList();
        HashSet<string>? chainParticipants = null;
        int max = 0;
        foreach (var settings in all)
        {
            if (!settings.IsEnabled) continue;

            var parameterObject = settings.ParameterObject;
            int baseWarmup = parameterObject == null ? 0 : BaseWarmupBars(parameterObject);
            int need = baseWarmup;

            if (applyTimeframeScaling
                && settings.Timeframe != IndicatorTimeframe.SameAsChart
                && settings.TypeEnum is { } type
                && IndicatorTimeframePolicy.SupportsIndicatorTimeframe(type, parameterObject)
                && !(chainParticipants ??= IndicatorTimeframePlan.CollectChainParticipants(all)).Contains(settings.Id))
            {
                int multiplier = ChartBarsPerHigherFrameBar(chartFrame, settings.Timeframe.ToTimeFrame(chartFrame));
                if (multiplier > 1)
                {
                    need = (Math.Max(0, baseWarmup) + AlignmentSafetyPeriods) * multiplier;
                }
            }

            if (need > max) max = need;
        }

        return max;
    }

    /// <summary>The chart bars one bar of <paramref name="effectiveFrame"/> spans, or 1 when the effective frame is not coarser or the step is not defined.</summary>
    private static int ChartBarsPerHigherFrameBar(TimeFrame chartFrame, TimeFrame effectiveFrame) => (chartFrame, effectiveFrame) switch
    {
        (TimeFrame.D1, TimeFrame.W1) => WeeklyMultiplierFromDaily,
        (TimeFrame.D1, TimeFrame.MN1) => MonthlyMultiplierFromDaily,
        _ => 1,
    };

    /// <summary>The larger of the parameter object's declared warm-up and a plain public int "Period" (0 for an object with neither).</summary>
    private static int BaseWarmupBars(CoreIndicatorParameterBase parameterObject)
    {
        int max = parameterObject.GetRequiredWarmupBars();

        PropertyInfo? periodProperty;
        try
        {
            periodProperty = parameterObject.GetType().GetProperty("Period", BindingFlags.Public | BindingFlags.Instance);
        }
        catch (AmbiguousMatchException)
        {
            return max;
        }

        if (periodProperty != null && periodProperty.PropertyType == typeof(int)
            && periodProperty.GetValue(parameterObject) is int period && period > max)
        {
            max = period;
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
            or ChartType.Dot or ChartType.HeikinAshi or ChartType.ThreeLineBreak => true,
        _ => false
    };
}
