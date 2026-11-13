namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>
/// The bars whose high, low and close feed the pivot levels of a bar. Unit: bars of the chart's own time frame; the period is
/// found with <c>TimeFrameAggregator.GetPeriodKey</c> (W1 = ISO week, Monday start; MN1 = calendar month).
/// </summary>
public enum PivotSourcePeriod
{
    /// <summary>The previous bar (the behaviour before this parameter existed).</summary>
    PreviousBar = 0,

    /// <summary>The previous completed ISO week.</summary>
    Week = 1,

    /// <summary>The previous completed calendar month.</summary>
    Month = 2,
}
