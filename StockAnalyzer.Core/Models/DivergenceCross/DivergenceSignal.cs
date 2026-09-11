namespace StockAnalyzer.Core.Models.DivergenceCross;

/// <summary>
/// Represents a detected divergence between price and an indicator.
/// Contains the start and end pivot points for both price and indicator series.
/// </summary>
public sealed class DivergenceSignal
{
    /// <summary>The type of divergence detected.</summary>
    public SignalType Type { get; }

    /// <summary>Start index (earlier pivot) in the candle array for price.</summary>
    public int PriceStartIndex { get; }

    /// <summary>End index (later pivot) in the candle array for price.</summary>
    public int PriceEndIndex { get; }

    /// <summary>Price value at the start pivot.</summary>
    public decimal PriceStartValue { get; }

    /// <summary>Price value at the end pivot.</summary>
    public decimal PriceEndValue { get; }

    /// <summary>Start index in the data array for the indicator pivot.</summary>
    public int IndicatorStartIndex { get; }

    /// <summary>End index in the data array for the indicator pivot.</summary>
    public int IndicatorEndIndex { get; }

    /// <summary>Indicator value at the start pivot.</summary>
    public decimal IndicatorStartValue { get; }

    /// <summary>Indicator value at the end pivot.</summary>
    public decimal IndicatorEndValue { get; }

    public DivergenceSignal(
        SignalType type,
        int priceStartIndex,
        int priceEndIndex,
        decimal priceStartValue,
        decimal priceEndValue,
        int indicatorStartIndex,
        int indicatorEndIndex,
        decimal indicatorStartValue,
        decimal indicatorEndValue)
    {
        Type = type;
        PriceStartIndex = priceStartIndex;
        PriceEndIndex = priceEndIndex;
        PriceStartValue = priceStartValue;
        PriceEndValue = priceEndValue;
        IndicatorStartIndex = indicatorStartIndex;
        IndicatorEndIndex = indicatorEndIndex;
        IndicatorStartValue = indicatorStartValue;
        IndicatorEndValue = indicatorEndValue;
    }

    public override string ToString()
        => $"{Type} Price[{PriceStartIndex}..{PriceEndIndex}] Ind[{IndicatorStartIndex}..{IndicatorEndIndex}]";
}
