using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Drawing.Values;

/// <summary>Data-tab values of the ray (the single definition; the GUI object and the MCP tool both call it).</summary>
public static class RayValues
{
    public static IReadOnlyList<DrawingCalculatedValue> Calculate(DrawingAnchor p1, DrawingAnchor p2, DateTime timestamp, decimal? currentPrice, IndicatorColor color)
    {
        decimal rayPrice = DrawingValueMath.InterpolatePrice(p1.Time, p1.Price, p2.Time, p2.Price, timestamp);

        string rayPriceText = string.Create(DrawingValueFormat.Culture, $"{rayPrice:F3}");
        if (currentPrice.HasValue)
        {
            decimal diff = currentPrice.Value - rayPrice;
            rayPriceText += string.Create(DrawingValueFormat.Culture, $" ({diff:+0.000;-0.000;0.000})");
        }

        var slope = DrawingValueMath.CalculateSlopePerDay(p1.Time, p1.Price, p2.Time, p2.Price);
        var values = new DrawingCalculatedValue[slope.HasValue ? 2 : 1];
        values[0] = new DrawingCalculatedValue("RayPrice", "Ray Price", rayPrice, rayPriceText, color);
        if (slope.HasValue)
        {
            values[1] = new DrawingCalculatedValue("Slope", "Slope", slope.Value, string.Create(DrawingValueFormat.Culture, $"{slope.Value:+0.000;-0.000;0.000} / day"), IndicatorColor.Gray);
        }

        return values;
    }
}
