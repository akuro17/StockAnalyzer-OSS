using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Drawing.Values;

/// <summary>Data-tab values of the Fibonacci retracement (the single definition; the GUI object and the MCP tool both call it).</summary>
public static class FibonacciRetracementValues
{
    private static readonly float[] DefaultLevelsArray = { 0f, 0.236f, 0.382f, 0.5f, 0.618f, 0.786f, 1.0f };
    private static readonly float[] ExtensionLevelsArray = { -3.236f, -1.618f, -0.618f, -0.272f, 0f, 0.236f, 0.382f, 0.5f, 0.618f, 0.786f, 1.0f, 1.272f, 1.618f, 2.618f, 4.236f };

    /// <summary>The default level table (also the level table the GUI draws).</summary>
    public static ReadOnlySpan<float> DefaultLevels => DefaultLevelsArray;

    /// <summary>The level table used when extensions are shown.</summary>
    public static ReadOnlySpan<float> ExtensionLevels => ExtensionLevelsArray;

    public static IReadOnlyList<DrawingCalculatedValue> Calculate(DrawingAnchor p1, DrawingAnchor p2, decimal? currentPrice, IndicatorColor color, bool showExtensions)
    {
        decimal range = Math.Abs(p1.Price - p2.Price);
        decimal yDiffPrice = p1.Price - p2.Price;

        float[] levels = showExtensions ? ExtensionLevelsArray : DefaultLevelsArray;
        int count = 1 + levels.Length + (currentPrice.HasValue ? 1 : 0);
        var values = new DrawingCalculatedValue[count];
        int index = 0;
        values[index++] = new DrawingCalculatedValue("Range", "Range", range, string.Create(DrawingValueFormat.Culture, $"{range:F3}"), IndicatorColor.Gray);

        for (int i = 0; i < levels.Length; i++)
        {
            float level = levels[i];
            decimal levelPrice = p2.Price + yDiffPrice * (decimal)level;
            string label = string.Create(DrawingValueFormat.Culture, $"Fib {level:P1}");
            string key = string.Create(DrawingValueFormat.Culture, $"Fib_{level:F3}");
            values[index++] = new DrawingCalculatedValue(key, label, levelPrice, string.Create(DrawingValueFormat.Culture, $"{levelPrice:F3}"), color);
        }

        if (currentPrice.HasValue)
        {
            decimal minDiff = decimal.MaxValue;
            string nearestLabel = string.Empty;
            decimal nearestPrice = 0m;
            for (int i = 0; i < levels.Length; i++)
            {
                float level = levels[i];
                decimal levelPrice = p2.Price + yDiffPrice * (decimal)level;
                decimal diff = Math.Abs(currentPrice.Value - levelPrice);
                if (diff < minDiff)
                {
                    minDiff = diff;
                    nearestLabel = string.Create(DrawingValueFormat.Culture, $"{level:P1}");
                    nearestPrice = levelPrice;
                }
            }
            decimal priceDelta = currentPrice.Value - nearestPrice;
            string deltaSign = priceDelta >= 0 ? "+" : "";
            values[index] = new DrawingCalculatedValue("NearestLevel", "Nearest Level", nearestPrice, string.Create(DrawingValueFormat.Culture, $"{nearestLabel} ({deltaSign}{priceDelta:F3})"), IndicatorColor.FromRgb(255, 215, 0));
        }

        return values;
    }
}
