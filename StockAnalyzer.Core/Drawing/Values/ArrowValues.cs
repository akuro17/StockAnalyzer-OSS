using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Drawing.Values;

/// <summary>Data-tab values of the arrow (the single definition; the GUI object and the MCP tool both call it).</summary>
public static class ArrowValues
{
    public static IReadOnlyList<DrawingCalculatedValue> Calculate(DrawingAnchor p1, DrawingAnchor p2, DateTime timestamp, IndicatorColor color)
    {
        decimal arrowPrice = DrawingValueMath.InterpolatePrice(p1.Time, p1.Price, p2.Time, p2.Price, timestamp);

        var values = new List<DrawingCalculatedValue>();
        values.Add(new DrawingCalculatedValue("ArrowPrice", "Arrow Price", arrowPrice, string.Create(DrawingValueFormat.Culture, $"{arrowPrice:F3}"), color));

        decimal priceDelta = p2.Price - p1.Price;
        string direction = priceDelta > 0 ? "Up (Bullish)" : (priceDelta < 0 ? "Down (Bearish)" : "Neutral");
        var dirColor = priceDelta > 0 ? IndicatorColor.Bullish : (priceDelta < 0 ? IndicatorColor.Bearish : IndicatorColor.Gray);
        values.Add(new DrawingCalculatedValue("Direction", "Direction", null, direction, dirColor));

        decimal deltaPct = DrawingValueMath.CalculatePercentageChange(p1.Price, p2.Price);
        values.Add(new DrawingCalculatedValue("PriceDelta", "Price Change", priceDelta, string.Create(DrawingValueFormat.Culture, $"{priceDelta:+0.000;-0.000;0.000} ({deltaPct:+0.00;-0.00;0.00}%)"), dirColor));

        double days = Math.Abs((p2.Time - p1.Time).TotalDays);
        values.Add(new DrawingCalculatedValue("Duration", "Duration", (decimal)Math.Round(days, 1), string.Create(DrawingValueFormat.Culture, $"{days:F1} days"), IndicatorColor.Gray));

        return values;
    }
}
