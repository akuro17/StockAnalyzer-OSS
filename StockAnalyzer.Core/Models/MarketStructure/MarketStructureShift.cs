namespace StockAnalyzer.Core.Models.MarketStructure;

/// <summary>
/// Represents a single detected market structure shift (BOS or CHoCH) at a specific point in the price data.
/// </summary>
public class MarketStructureShift
{
    /// <summary>The type of structure shift detected.</summary>
    public MarketStructureType Type { get; }

    /// <summary>The candle index where the structure shift was confirmed.</summary>
    public int Index { get; }

    /// <summary>The time the structure shift was confirmed.</summary>
    public System.DateTime Time { get; }

    /// <summary>The price level at which the structure shift was confirmed.</summary>
    public decimal Price { get; }

    /// <summary>The previous pivot high price that was broken or held.</summary>
    public decimal PreviousPivotHigh { get; }

    /// <summary>The index of the previous pivot high.</summary>
    public int PreviousPivotHighIndex { get; }
    
    /// <summary>The time of the previous pivot high.</summary>
    public System.DateTime PreviousPivotHighTime { get; }

    /// <summary>The previous pivot low price that was broken or held.</summary>
    public decimal PreviousPivotLow { get; }

    /// <summary>The index of the previous pivot low.</summary>
    public int PreviousPivotLowIndex { get; }
    
    /// <summary>The time of the previous pivot low.</summary>
    public System.DateTime PreviousPivotLowTime { get; }

    public MarketStructureShift(
        MarketStructureType type,
        int index,
        System.DateTime time,
        decimal price,
        decimal previousPivotHigh,
        int previousPivotHighIndex,
        System.DateTime previousPivotHighTime,
        decimal previousPivotLow,
        int previousPivotLowIndex,
        System.DateTime previousPivotLowTime)
    {
        Type = type;
        Index = index;
        Time = time;
        Price = price;
        PreviousPivotHigh = previousPivotHigh;
        PreviousPivotHighIndex = previousPivotHighIndex;
        PreviousPivotHighTime = previousPivotHighTime;
        PreviousPivotLow = previousPivotLow;
        PreviousPivotLowIndex = previousPivotLowIndex;
        PreviousPivotLowTime = previousPivotLowTime;
    }

    public override string ToString()
        => $"{Type} at index {Index} (Price={Price}, PrevHigh={PreviousPivotHigh}@{PreviousPivotHighIndex}, PrevLow={PreviousPivotLow}@{PreviousPivotLowIndex})";
}
