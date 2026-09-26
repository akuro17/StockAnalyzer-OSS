namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Classification of curve local extrema in price space.
/// </summary>
public enum ExtremaType : byte
{
    High = 1, // Peak / Price Resistance (Screen upward convex)
    Low = 2   // Trough / Price Support (Screen downward convex)
}

/// <summary>
/// Pure mathematical result from screen-space Bézier analysis (Zero-Allocation).
/// </summary>
public readonly record struct BezierExtremum(
    double ScreenY,
    ExtremaType Type,
    int SegmentIndex,
    double ParameterT
);

/// <summary>
/// Chart-projected extrema level with high-precision Price and pixel-snapped rendering coordinates.
/// </summary>
public readonly record struct ExtremaLevel(
    decimal Price,
    double ScreenY,
    float SnappedY,
    ExtremaType Type,
    int SegmentIndex,
    double ParameterT,
    int TouchCount = 1,
    double Prominence = 0.0
);
