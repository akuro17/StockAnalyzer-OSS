using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Drawing.Values;

/// <summary>Data-tab values of the angle tool (the single definition; the GUI object and the MCP tool both call it).</summary>
public static class AngleToolValues
{
    public static IReadOnlyList<DrawingCalculatedValue> Calculate(DrawingAnchor p1, DrawingAnchor p2, DateTime timestamp, IndicatorColor color)
    {
        decimal linePrice = DrawingValueMath.InterpolatePrice(p1.Time, p1.Price, p2.Time, p2.Price, timestamp);
        decimal priceDelta = p2.Price - p1.Price;
        decimal deltaPct = DrawingValueMath.CalculatePercentageChange(p1.Price, p2.Price);

        var values = new List<DrawingCalculatedValue>(3)
        {
            new("LinePrice", "Line Price", linePrice, string.Create(DrawingValueFormat.Culture, $"{linePrice:F3}"), color),
            new("PriceDelta", "Price Change", priceDelta, string.Create(DrawingValueFormat.Culture, $"{priceDelta:+0.000;-0.000;0.000} ({deltaPct:+0.00;-0.00;0.00}%)"), IndicatorColor.Gray),
        };

        decimal? slope = DrawingValueMath.CalculateSlopePerDay(p1.Time, p1.Price, p2.Time, p2.Price);
        if (slope.HasValue)
        {
            values.Add(new DrawingCalculatedValue("Slope", "Slope", slope.Value, string.Create(DrawingValueFormat.Culture, $"{slope.Value:+0.000;-0.000;0.000} / day"), IndicatorColor.Gray));
        }

        return values;
    }
}
