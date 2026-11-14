using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Drawing.Values;

/// <summary>Data-tab values of the parallel channel (the single definition; the GUI object and the MCP tool both call it).</summary>
public static class ParallelChannelValues
{
    /// <param name="mode">
    /// <see cref="ChannelMode.Parallel"/> (the default, output unchanged) or <see cref="ChannelMode.FlatOpposite"/>:
    /// the opposite edge is the constant price of P3, so its value is P3's price, the midline is (base edge value + P3 price) / 2
    /// and the channel width is the distance between the two edge values at <paramref name="timestamp"/>.
    /// Item names and order are the same in both modes; the slope is always that of the base edge P1-P2.
    /// </param>
    public static IReadOnlyList<DrawingCalculatedValue> Calculate(DrawingAnchor p1, DrawingAnchor p2, DrawingAnchor p3, DateTime timestamp, IndicatorColor color, ChannelMode mode = ChannelMode.Parallel)
    {
        double dt = (p2.Time - p1.Time).TotalSeconds;
        if (Math.Abs(dt) < DrawingValueMath.MinTimeDeltaSeconds) return Array.Empty<DrawingCalculatedValue>();

        decimal basePrice = DrawingValueMath.InterpolatePrice(p1.Time, p1.Price, p2.Time, p2.Price, timestamp);
        decimal baseAtP3 = DrawingValueMath.InterpolatePrice(p1.Time, p1.Price, p2.Time, p2.Price, p3.Time);
        decimal priceOffset = p3.Price - baseAtP3;

        decimal parallelPrice = mode == ChannelMode.FlatOpposite ? p3.Price : basePrice + priceOffset;
        decimal upper = Math.Max(basePrice, parallelPrice);
        decimal lower = Math.Min(basePrice, parallelPrice);
        decimal mid = (basePrice + parallelPrice) / 2m;
        decimal channelWidth = mode == ChannelMode.FlatOpposite ? Math.Abs(parallelPrice - basePrice) : Math.Abs(priceOffset);

        var slope = DrawingValueMath.CalculateSlopePerDay(p1.Time, p1.Price, p2.Time, p2.Price);
        var values = new DrawingCalculatedValue[slope.HasValue ? 5 : 4];
        values[0] = new DrawingCalculatedValue("UpperLine", "Upper Channel", upper, string.Create(DrawingValueFormat.Culture, $"{upper:F3}"), color);
        values[1] = new DrawingCalculatedValue("Midline", "Midline", mid, string.Create(DrawingValueFormat.Culture, $"{mid:F3}"), IndicatorColor.Gray);
        values[2] = new DrawingCalculatedValue("LowerLine", "Lower Channel", lower, string.Create(DrawingValueFormat.Culture, $"{lower:F3}"), color);
        values[3] = new DrawingCalculatedValue("ChannelWidth", "Channel Width", channelWidth, string.Create(DrawingValueFormat.Culture, $"{channelWidth:F3}"), IndicatorColor.Gray);

        if (slope.HasValue)
        {
            values[4] = new DrawingCalculatedValue("Slope", "Slope", slope.Value, string.Create(DrawingValueFormat.Culture, $"{slope.Value:+0.000;-0.000;0.000} / day"), IndicatorColor.Gray);
        }

        return values;
    }
}
