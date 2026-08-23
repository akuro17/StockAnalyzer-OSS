namespace StockAnalyzer.Core.Models;

/// <summary>
/// Specifies the price type / field extracted from OHLCV candles for indicator calculations.
/// </summary>
public enum PriceType
{
    Close,
    Open,
    High,
    Low,
    Median,    // (High + Low) / 2
    Typical,   // (High + Low + Close) / 3
    Weighted   // (High + Low + 2 * Close) / 4
}
