using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Drawing.Values;

/// <summary>Data-tab values of the Fibonacci expansion (the single definition; the GUI object and the MCP tool both call it).</summary>
public static class FibonacciExpansionValues
{
    private static readonly float[] LevelsArray = { 0.618f, 1.0f, 1.618f, 2.618f, 4.236f };

    /// <summary>The expansion level table (also the table the GUI draws).</summary>
    public static ReadOnlySpan<float> Levels => LevelsArray;

    public static IReadOnlyList<DrawingCalculatedValue> Calculate(DrawingAnchor p1, DrawingAnchor p2, DrawingAnchor p3, IndicatorColor color)
    {
        decimal impulse = p2.Price - p1.Price;
        decimal absImpulse = Math.Abs(impulse);

        var values = new List<DrawingCalculatedValue>(LevelsArray.Length + 1)
        {
            new DrawingCalculatedValue("Impulse", "Impulse Move", absImpulse, string.Create(DrawingValueFormat.Culture, $"{absImpulse:F3}"), IndicatorColor.Gray)
        };

        foreach (var level in LevelsArray)
        {
            decimal expPrice = p3.Price + impulse * (decimal)level;
            values.Add(new DrawingCalculatedValue(string.Create(DrawingValueFormat.Culture, $"Exp_{level:F3}"), string.Create(DrawingValueFormat.Culture, $"Expansion {level:P1}"), expPrice, string.Create(DrawingValueFormat.Culture, $"{expPrice:F3}"), color));
        }

        return values;
    }
}
