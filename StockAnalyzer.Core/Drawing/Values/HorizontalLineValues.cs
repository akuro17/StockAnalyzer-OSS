using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Drawing.Values;

/// <summary>Data-tab values of the horizontal line (the single definition; the GUI object and the MCP tool both call it).</summary>
public static class HorizontalLineValues
{
    public static IReadOnlyList<DrawingCalculatedValue> Calculate(DrawingAnchor point, IndicatorColor color)
    {
        decimal price = point.Price;

        return new DrawingCalculatedValue[]
        {
            new DrawingCalculatedValue("Price", "Price", price, string.Create(DrawingValueFormat.Culture, $"{price:F3}"), color)
        };
    }
}
