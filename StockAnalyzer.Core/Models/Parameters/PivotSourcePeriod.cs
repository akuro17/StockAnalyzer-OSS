using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>
/// The bars whose high, low and close feed the pivot levels of a bar. Unit: bars of the chart's own time frame; the period is
/// found with <c>TimeFrameAggregator.GetPeriodKey</c> (W1 = ISO week, Monday start; MN1 = calendar month).
/// The members are an ordered scale shown in declaration order with localized texts (the integers 0/1/2 are persisted: never renumber or rename).
/// </summary>
[EnumDisplaySort(SortAlphabetically = false)]
public enum PivotSourcePeriod
{
    /// <summary>The previous bar (the behaviour before this parameter existed).</summary>
    [Description("Same as Chart")]
    PreviousBar = 0,

    /// <summary>The previous completed ISO week.</summary>
    [Description("Weekly")]
    Week = 1,

    /// <summary>The previous completed calendar month.</summary>
    [Description("Monthly")]
    Month = 2,
}
