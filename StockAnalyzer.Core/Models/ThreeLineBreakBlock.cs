namespace StockAnalyzer.Core.Models;

/// <summary>
/// Immutable Data Transfer Object representing a Three Line Break block.
/// </summary>
public readonly record struct ThreeLineBreakBlock(
    DateTime StartDate,
    DateTime EndDate,
    decimal OpenPrice,
    decimal ClosePrice,
    bool IsUp
)
{
    public decimal High => Math.Max(OpenPrice, ClosePrice);
    public decimal Low => Math.Min(OpenPrice, ClosePrice);
}
