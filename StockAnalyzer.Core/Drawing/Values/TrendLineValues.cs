using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Drawing.Values;

/// <summary>Data-tab values of the trend line (the single definition; the GUI object and the MCP tool both call it).</summary>
public static class TrendLineValues
{
    public static IReadOnlyList<DrawingCalculatedValue> Calculate(DrawingAnchor p1, DrawingAnchor p2, DateTime timestamp, IndicatorColor color)
    {
        decimal priceAtTime = DrawingValueMath.InterpolatePrice(p1.Time, p1.Price, p2.Time, p2.Price, timestamp);

        return new DrawingCalculatedValue[]
        {
            new DrawingCalculatedValue("TrendLinePrice", "TrendLine Price", priceAtTime, string.Create(DrawingValueFormat.Culture, $"{priceAtTime:F3}"), color)
        };
    }
}
