using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Drawing.Values;

/// <summary>Data-tab values of the Fibonacci channel (the single definition; the GUI object and the MCP tool both call it).</summary>
public static class FibonacciChannelValues
{
    private static readonly float[] LevelsArray = { 0.0f, 0.618f, 1.0f, 1.618f, 2.618f };

    /// <summary>The channel level table (also the table the GUI draws).</summary>
    public static ReadOnlySpan<float> Levels => LevelsArray;

    public static IReadOnlyList<DrawingCalculatedValue> Calculate(DrawingAnchor p1, DrawingAnchor p2, DrawingAnchor p3, DateTime timestamp, IndicatorColor color)
    {
        double dt = (p2.Time - p1.Time).TotalSeconds;
        if (Math.Abs(dt) < DrawingValueMath.MinTimeDeltaSeconds) return Array.Empty<DrawingCalculatedValue>();

        decimal basePrice = DrawingValueMath.InterpolatePrice(p1.Time, p1.Price, p2.Time, p2.Price, timestamp);
        decimal baseAtP3 = DrawingValueMath.InterpolatePrice(p1.Time, p1.Price, p2.Time, p2.Price, p3.Time);
        decimal channelWidth = p3.Price - baseAtP3;

        var values = new List<DrawingCalculatedValue>(LevelsArray.Length + 2)
        {
            new DrawingCalculatedValue("BaseLine", "Base Line", basePrice, string.Create(DrawingValueFormat.Culture, $"{basePrice:F3}"), color),
            new DrawingCalculatedValue("ChannelWidth", "Channel Width", Math.Abs(channelWidth), string.Create(DrawingValueFormat.Culture, $"{Math.Abs(channelWidth):F3}"), IndicatorColor.Gray)
        };

        foreach (var level in LevelsArray)
        {
            decimal levelPrice = basePrice + channelWidth * (decimal)level;
            values.Add(new DrawingCalculatedValue(string.Create(DrawingValueFormat.Culture, $"Channel_{level:F3}"), string.Create(DrawingValueFormat.Culture, $"Fib Channel {level:P1}"), levelPrice, string.Create(DrawingValueFormat.Culture, $"{levelPrice:F3}"), color));
        }

        return values;
    }
}
