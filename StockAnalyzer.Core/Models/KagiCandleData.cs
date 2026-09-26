namespace StockAnalyzer.Core.Models;

/// <summary>
/// Represents a Kagi chart segment converted into a CandleData format for rendering.
/// </summary>
public sealed record KagiCandleData
{
    public DateTime Timestamp { get; init; }
    public decimal Open { get; init; }
    public decimal High { get; init; }
    public decimal Low { get; init; }
    public decimal Close { get; init; }
    public decimal Volume { get; init; }

    /// <summary>
    /// True if this segment is "Yang" (Thick/Green/Bullish Line).
    /// False if "Yin" (Thin/Red/Bearish Line).
    /// </summary>
    public bool IsYang { get; init; }

    /// <summary>
    /// The previous inflection point price (Shoulder/Waist price).
    /// Used for rendering horizontal connectors.
    /// </summary>
    public decimal ReferencePrice { get; init; }

    /// <summary>
    /// Creates a KagiCandle with standard properties.
    /// </summary>
    public static KagiCandleData Create(DateTime time, decimal open, decimal close, bool isYang, decimal refPrice, long volume)
    {
        return new KagiCandleData
        {
            Timestamp = time,
            Open = open,
            Close = close,
            High = Math.Max(open, close),
            Low = Math.Min(open, close),
            Volume = volume,
            IsYang = isYang,
            ReferencePrice = refPrice
        };
    }
}
