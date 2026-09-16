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
    Midpoint,  // (Open + Close) / 2
    Typical,   // (High + Low + Close) / 3
    Weighted,  // (High + Low + 2 * Close) / 4
    Average,   // (Open + High + Low + Close) / 4
    HeikinAshiOpen,  // Heikin-Ashi open price
    HeikinAshiHigh,  // Heikin-Ashi high price
    HeikinAshiLow,   // Heikin-Ashi low price
    HeikinAshiClose, // Heikin-Ashi close price
    TrueHigh,  // Today's high, or the previous close, whichever is higher
    TrueLow    // Today's low, or the previous close, whichever is lower
}
