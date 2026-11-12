using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Drawing.Values;

/// <summary>Data-tab values of the parallel channel (the single definition; the GUI object and the MCP tool both call it).</summary>
public static class ParallelChannelValues
{
    public static IReadOnlyList<DrawingCalculatedValue> Calculate(DrawingAnchor p1, DrawingAnchor p2, DrawingAnchor p3, DateTime timestamp, IndicatorColor color)
    {
        double dt = (p2.Time - p1.Time).TotalSeconds;
        if (Math.Abs(dt) < DrawingValueMath.MinTimeDeltaSeconds) return Array.Empty<DrawingCalculatedValue>();

        decimal basePrice = DrawingValueMath.InterpolatePrice(p1.Time, p1.Price, p2.Time, p2.Price, timestamp);
        decimal baseAtP3 = DrawingValueMath.InterpolatePrice(p1.Time, p1.Price, p2.Time, p2.Price, p3.Time);
        decimal priceOffset = p3.Price - baseAtP3;

        decimal parallelPrice = basePrice + priceOffset;
        decimal upper = Math.Max(basePrice, parallelPrice);
        decimal lower = Math.Min(basePrice, parallelPrice);
        decimal mid = (basePrice + parallelPrice) / 2m;
        decimal channelWidth = Math.Abs(priceOffset);

        var values = new List<DrawingCalculatedValue>(5)
        {
            new DrawingCalculatedValue("UpperLine", "Upper Channel", upper, string.Create(DrawingValueFormat.Culture, $"{upper:F3}"), color),
            new DrawingCalculatedValue("Midline", "Midline", mid, string.Create(DrawingValueFormat.Culture, $"{mid:F3}"), IndicatorColor.Gray),
            new DrawingCalculatedValue("LowerLine", "Lower Channel", lower, string.Create(DrawingValueFormat.Culture, $"{lower:F3}"), color),
            new DrawingCalculatedValue("ChannelWidth", "Channel Width", channelWidth, string.Create(DrawingValueFormat.Culture, $"{channelWidth:F3}"), IndicatorColor.Gray)
        };

        var slope = DrawingValueMath.CalculateSlopePerDay(p1.Time, p1.Price, p2.Time, p2.Price);
        if (slope.HasValue)
        {
            values.Add(new DrawingCalculatedValue("Slope", "Slope", slope.Value, string.Create(DrawingValueFormat.Culture, $"{slope.Value:+0.000;-0.000;0.000} / day"), IndicatorColor.Gray));
        }

        return values;
    }
}
