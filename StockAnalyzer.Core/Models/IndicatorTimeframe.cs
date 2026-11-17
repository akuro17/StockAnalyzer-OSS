using System.ComponentModel;

namespace StockAnalyzer.Core.Models;

/// <summary>
/// The time frame an indicator is calculated on, relative to the chart it is drawn on. Unit: bars of the chosen frame
/// (a period is read in bars of that frame, never in bars of the chart). Values are persisted as integers (workspace JSON):
/// never renumber or reuse a value; append only. 0/1/2 are pinned equal to <c>PivotSourcePeriod</c> by a test.
/// </summary>
[EnumDisplaySort(SortAlphabetically = false)]
public enum IndicatorTimeframe
{
    /// <summary>The indicator is calculated on the chart's own bars (the behaviour before this setting existed).</summary>
    [Description("Same as Chart")]
    SameAsChart = 0,

    /// <summary>The indicator is calculated on completed ISO weeks (W1) and carried onto the chart bars causally.</summary>
    [Description("Weekly")]
    Weekly = 1,

    /// <summary>The indicator is calculated on completed calendar months (MN1) and carried onto the chart bars causally.</summary>
    [Description("Monthly")]
    Monthly = 2,
}

public static class IndicatorTimeframeExtensions
{
    /// <summary>
    /// The frame the indicator is calculated on: the chart's own frame for <see cref="IndicatorTimeframe.SameAsChart"/>,
    /// otherwise W1 / MN1. An undefined value maps to <paramref name="chartFrame"/> (the same-as-chart path).
    /// </summary>
    public static TimeFrame ToTimeFrame(this IndicatorTimeframe timeframe, TimeFrame chartFrame) => timeframe switch
    {
        IndicatorTimeframe.Weekly => TimeFrame.W1,
        IndicatorTimeframe.Monthly => TimeFrame.MN1,
        _ => chartFrame,
    };
}
