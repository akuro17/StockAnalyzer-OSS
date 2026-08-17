namespace StockAnalyzer.Core.Models.Screener;

/// <summary>
/// Specifies the target type of the right-hand side in a screener comparison condition.
/// </summary>
public enum RightHandTargetMode
{
    /// <summary>
    /// Compare against a static scalar numeric value (e.g. 50, 100).
    /// </summary>
    NumericValue,

    /// <summary>
    /// Compare against another indicator series (e.g. SMA(50), EMA(20)).
    /// </summary>
    Indicator,

    /// <summary>
    /// Compare against a text string value (e.g. "Technology", "Apple").
    /// </summary>
    StringValue
}
