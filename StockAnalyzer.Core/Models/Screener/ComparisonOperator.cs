namespace StockAnalyzer.Core.Models.Screener;

/// <summary>
/// Comparison operator for screener conditions.
/// </summary>
public enum ComparisonOperator
{
    /// <summary>
    /// Greater than (&gt;)
    /// </summary>
    GreaterThan,

    /// <summary>
    /// Greater than or equal to (&gt;=)
    /// </summary>
    GreaterThanOrEqual,

    /// <summary>
    /// Less than (&lt;)
    /// </summary>
    LessThan,

    /// <summary>
    /// Less than or equal to (&lt;=)
    /// </summary>
    LessThanOrEqual,

    /// <summary>
    /// Equal to (==)
    /// </summary>
    Equal,

    /// <summary>
    /// Not equal to (!=)
    /// </summary>
    NotEqual,

    /// <summary>
    /// Contains (*=)
    /// </summary>
    Contains,

    /// <summary>
    /// Does not contain (!*=)
    /// </summary>
    DoesNotContain
}

public static class ComparisonOperatorExtensions
{
    public static string ToSymbolString(this ComparisonOperator op) => op switch
    {
        ComparisonOperator.GreaterThan => ">",
        ComparisonOperator.GreaterThanOrEqual => ">=",
        ComparisonOperator.LessThan => "<",
        ComparisonOperator.LessThanOrEqual => "<=",
        ComparisonOperator.Equal => "==",
        ComparisonOperator.NotEqual => "!=",
        ComparisonOperator.Contains => "*=",
        ComparisonOperator.DoesNotContain => "!*=",
        _ => ">"
    };
}
