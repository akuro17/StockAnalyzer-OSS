namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Gradient fill patterns for 2D closed chart drawing objects.
/// </summary>
public enum DrawingGradientType : byte
{
    None = 0,             // Solid fill
    LinearVertical = 1,   // Top to Bottom
    LinearHorizontal = 2, // Left to Right
    LinearDiagonal = 3,   // P1 to P2 vector
    Radial = 4            // Center to circumscribed edge
}
