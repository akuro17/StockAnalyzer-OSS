namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>
/// When the cumulative VWAP sums restart. Unit: calendar periods of the bar's timestamp, independent of the chart time frame's bar count.
/// Weekly = ISO week (Monday start), Monthly = calendar month, Quarterly = calendar quarter, Yearly = calendar year (keys in CoreVwapIndicator).
/// </summary>
public enum VwapResetPeriod
{
    /// <summary>Never reset: the sums run from the first bar (the behaviour before this parameter existed).</summary>
    Cumulative = 0,
    Weekly = 1,
    Monthly = 2,
    Quarterly = 3,
    Yearly = 4,
}
