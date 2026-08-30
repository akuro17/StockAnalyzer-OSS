namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Specifies the price field used for range spline extraction (Prompt 61-7 / FR-61-7-02).
/// </summary>
public enum PriceField
{
    Close,
    High,
    Low,
    Open,
    MedianHL,
    TypicalHLC,
    WeightedHLCC
}
