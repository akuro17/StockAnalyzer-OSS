using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Drawing.Values;

/// <summary>Data-tab values of the price label (the single definition; the GUI object and the MCP tool both call it).</summary>
public static class PriceLabelValues
{
    public static IReadOnlyList<DrawingCalculatedValue> Calculate(DrawingAnchor point, decimal? currentPrice, IndicatorColor color)
    {
        decimal price = point.Price;

        var values = new List<DrawingCalculatedValue>(2)
        {
            new DrawingCalculatedValue("Price", "Price", price, string.Create(DrawingValueFormat.Culture, $"{price:F3}"), color)
        };

        if (currentPrice.HasValue)
        {
            decimal diff = currentPrice.Value - price;
            decimal diffPct = DrawingValueMath.CalculatePercentageChange(price, currentPrice.Value);
            values.Add(new DrawingCalculatedValue("PriceDiff", "Price Diff", diff, string.Create(DrawingValueFormat.Culture, $"{diff:+0.000;-0.000;0.000} ({diffPct:+0.00;-0.00;0.00}%)"), IndicatorColor.Gray));
        }

        return values;
    }
}
