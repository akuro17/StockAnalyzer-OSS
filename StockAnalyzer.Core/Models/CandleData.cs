using System;

namespace StockAnalyzer.Core.Models;

/// <summary>
/// Immutable OHLCV candle data structure.
/// This is the canonical CandleData for cross-project use.
/// </summary>
public readonly record struct CandleData(
    DateTime Timestamp,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume
)
{
    public DateTime Time => Timestamp;
    // public DateTime Date => Timestamp.Date; // Removed to prevent CS0200 errors and enforce Timestamp usage
    
    public decimal BodyHeight => Math.Abs(Close - Open);
    public decimal UpperShadow => High - Math.Max(Open, Close);
    public decimal LowerShadow => Math.Min(Open, Close) - Low;

    public bool IsValid()
    {
        return High >= Low &&
               High >= Open &&
               High >= Close &&
               Low <= Open &&
               Low <= Close &&
               Volume >= 0;
    }

    public bool IsBullish => Close >= Open;

    public static CandleData CreateUnchecked(DateTime timestamp, decimal open, decimal high, decimal low, decimal close, long volume)
    {
        return new CandleData(timestamp, open, high, low, close, volume);
    }
}

/// <summary>
/// Time frame enumeration for chart intervals.
/// </summary>
public enum TimeFrame
{
    M1,   // 1 Minute
    M5,   // 5 Minutes
    M15,  // 15 Minutes
    M30,  // 30 Minutes
    H1,   // 1 Hour
    H4,   // 4 Hours
    D1,   // Daily
    W1,   // Weekly
    MN1   // Monthly
}
