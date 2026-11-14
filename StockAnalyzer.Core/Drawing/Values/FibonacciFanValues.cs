using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Drawing.Values;

/// <summary>Data-tab values of the Fibonacci fan (the single definition; the GUI object and the MCP tool both call it).</summary>
public static class FibonacciFanValues
{
    private static readonly double[] RatiosArray = { 0.382, 0.5, 0.618, 1.0 };

    /// <summary>The fan ratio table (also the table the GUI draws).</summary>
    public static ReadOnlySpan<double> Ratios => RatiosArray;

    public static IReadOnlyList<DrawingCalculatedValue> Calculate(DrawingAnchor p1, DrawingAnchor p2, DateTime timestamp, IndicatorColor color)
    {
        double dt = (p2.Time - p1.Time).TotalSeconds;
        if (Math.Abs(dt) < DrawingValueMath.MinTimeDeltaSeconds) return Array.Empty<DrawingCalculatedValue>();

        decimal range = Math.Abs(p2.Price - p1.Price);
        decimal totalDyPrice = p2.Price - p1.Price;
        double timeProgress = (timestamp - p1.Time).TotalSeconds / dt;

        var values = new DrawingCalculatedValue[RatiosArray.Length + 1];
        values[0] = new DrawingCalculatedValue("Range", "Range", range, string.Create(DrawingValueFormat.Culture, $"{range:F3}"), IndicatorColor.Gray);

        for (int i = 0; i < RatiosArray.Length; i++)
        {
            double ratio = RatiosArray[i];
            decimal targetPriceAtEnd = p1.Price + totalDyPrice * (decimal)ratio;
            decimal slope = targetPriceAtEnd - p1.Price;
            decimal linePrice = p1.Price + slope * (decimal)timeProgress;

            values[i + 1] = new DrawingCalculatedValue(string.Create(DrawingValueFormat.Culture, $"Fan_{ratio:F3}"), string.Create(DrawingValueFormat.Culture, $"Fan {ratio:P1}"), linePrice, string.Create(DrawingValueFormat.Culture, $"{linePrice:F3}"), color);
        }

        return values;
    }
}
