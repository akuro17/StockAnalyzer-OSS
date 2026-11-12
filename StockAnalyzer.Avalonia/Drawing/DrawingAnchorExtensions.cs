using StockAnalyzer.Core.Drawing;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>Converts the GUI control point to the UI-free anchor the Core drawing-value calculators take.</summary>
internal static class DrawingAnchorExtensions
{
    public static DrawingAnchor ToAnchor(this ChartPoint point) => new(point.Time, point.Price);
}
