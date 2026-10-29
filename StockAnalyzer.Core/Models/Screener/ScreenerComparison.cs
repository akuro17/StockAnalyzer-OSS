namespace StockAnalyzer.Core.Models.Screener;

/// <summary>
/// The decision of a <see cref="ComparisonOperator"/> between two values, with the Screener's rule: an operator that is not one of the six value comparisons
/// (the cross operators belong to the backtest) is never met. Used by the evaluation that reads a bar set (<see cref="ScreenerIndicatorEntry.IsMetOnBars"/>).
/// </summary>
public static class ScreenerComparison
{
    public static bool Compare(ComparisonOperator op, decimal left, decimal right) => op switch
    {
        ComparisonOperator.GreaterThan => left > right,
        ComparisonOperator.LessThan => left < right,
        ComparisonOperator.GreaterThanOrEqual => left >= right,
        ComparisonOperator.LessThanOrEqual => left <= right,
        ComparisonOperator.Equal => left == right,
        ComparisonOperator.NotEqual => left != right,
        _ => false
    };
}
