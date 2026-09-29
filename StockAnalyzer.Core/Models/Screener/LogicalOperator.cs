namespace StockAnalyzer.Core.Models.Screener;

/// <summary>
/// Logical operator for combining multiple screening conditions.
/// </summary>
public enum LogicalOperator
{
    /// <summary>
    /// All conditions must be met (intersection).
    /// </summary>
    And,

    /// <summary>
    /// At least one condition must be met (union).
    /// </summary>
    Or
}
