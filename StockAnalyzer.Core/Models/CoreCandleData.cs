namespace StockAnalyzer.Core.Models;

public record CoreCandleData(
    DateTime Timestamp,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume
)
{
    public bool IsBullish => Close >= Open;
}
