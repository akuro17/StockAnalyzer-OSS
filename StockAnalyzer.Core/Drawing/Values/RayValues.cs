using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Drawing.Values;

/// <summary>Data-tab values of the ray (the single definition; the GUI object and the MCP tool both call it).</summary>
public static class RayValues
{
    public static IReadOnlyList<DrawingCalculatedValue> Calculate(DrawingAnchor p1, DrawingAnchor p2, DateTime timestamp, decimal? currentPrice, IndicatorColor color)
    {
        decimal rayPrice = DrawingValueMath.InterpolatePrice(p1.Time, p1.Price, p2.Time, p2.Price, timestamp);

        var values = new List<DrawingCalculatedValue>();

        string rayPriceText = string.Create(DrawingValueFormat.Culture, $"{rayPrice:F3}");
        if (currentPrice.HasValue)
        {
            decimal diff = currentPrice.Value - rayPrice;
            rayPriceText += string.Create(DrawingValueFormat.Culture, $" ({diff:+0.000;-0.000;0.000})");
        }
        values.Add(new DrawingCalculatedValue("RayPrice", "Ray Price", rayPrice, rayPriceText, color));

        var slope = DrawingValueMath.CalculateSlopePerDay(p1.Time, p1.Price, p2.Time, p2.Price);
        if (slope.HasValue)
        {
            values.Add(new DrawingCalculatedValue("Slope", "Slope", slope.Value, string.Create(DrawingValueFormat.Culture, $"{slope.Value:+0.000;-0.000;0.000} / day"), IndicatorColor.Gray));
        }

        return values;
    }
}
