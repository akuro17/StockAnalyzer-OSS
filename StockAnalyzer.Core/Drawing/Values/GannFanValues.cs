using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Drawing.Values;

/// <summary>Data-tab values of the Gann fan (the single definition; the GUI object and the MCP tool both call it).</summary>
public static class GannFanValues
{
    // Standard Gann Angles: 1x8, 1x4, 1x3, 1x2, 1x1, 2x1, 3x1, 4x1, 8x1 (time x price).
    private static readonly (int time, int price)[] RatiosArray =
    {
        (1, 8), (1, 4), (1, 3), (1, 2),
        (1, 1),
        (2, 1), (3, 1), (4, 1), (8, 1)
    };

    /// <summary>The Gann angle table (also the table the GUI draws).</summary>
    public static ReadOnlySpan<(int time, int price)> Ratios => RatiosArray;

    public static IReadOnlyList<DrawingCalculatedValue> Calculate(DrawingAnchor p1, DrawingAnchor p2, DateTime timestamp, IndicatorColor color)
    {
        double dt = (p2.Time - p1.Time).TotalSeconds;
        if (Math.Abs(dt) < DrawingValueMath.MinTimeDeltaSeconds) return Array.Empty<DrawingCalculatedValue>();

        decimal range = Math.Abs(p2.Price - p1.Price);
        decimal dy = p2.Price - p1.Price;
        double timeProgress = (timestamp - p1.Time).TotalSeconds / dt;

        var values = new DrawingCalculatedValue[RatiosArray.Length + 1];
        values[0] = new DrawingCalculatedValue("Range", "Range", range, string.Create(DrawingValueFormat.Culture, $"{range:F3}"), IndicatorColor.Gray);

        for (int i = 0; i < RatiosArray.Length; i++)
        {
            var ratio = RatiosArray[i];
            decimal slopeFactor = (decimal)ratio.price / (decimal)ratio.time;
            decimal fanPrice = p1.Price + (decimal)timeProgress * dy * slopeFactor;
            values[i + 1] = new DrawingCalculatedValue(string.Create(DrawingValueFormat.Culture, $"Gann_{ratio.time}x{ratio.price}"), string.Create(DrawingValueFormat.Culture, $"Gann {ratio.time}x{ratio.price}"), fanPrice, string.Create(DrawingValueFormat.Culture, $"{fanPrice:F3}"), color);
        }

        return values;
    }
}
