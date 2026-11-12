using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Drawing.Values;

/// <summary>Data-tab values of the Gann box (the single definition; the GUI object and the MCP tool both call it).</summary>
public static class GannBoxValues
{
    private static readonly double[] GridRatiosArray = { 0.125, 0.25, 0.375, 0.5, 0.625, 0.75, 0.875 };

    /// <summary>The grid ratio table (also the table the GUI draws).</summary>
    public static ReadOnlySpan<double> GridRatios => GridRatiosArray;

    public static IReadOnlyList<DrawingCalculatedValue> Calculate(DrawingAnchor p1, DrawingAnchor p2, IndicatorColor color)
    {
        decimal topPrice = Math.Max(p1.Price, p2.Price);
        decimal bottomPrice = Math.Min(p1.Price, p2.Price);
        decimal height = topPrice - bottomPrice;
        double days = Math.Abs((p2.Time - p1.Time).TotalDays);

        var values = new List<DrawingCalculatedValue>(GridRatiosArray.Length + 4)
        {
            new DrawingCalculatedValue("HighPrice", "Box High", topPrice, string.Create(DrawingValueFormat.Culture, $"{topPrice:F3}"), color),
            new DrawingCalculatedValue("LowPrice", "Box Low", bottomPrice, string.Create(DrawingValueFormat.Culture, $"{bottomPrice:F3}"), color),
            new DrawingCalculatedValue("Height", "Price Height", height, string.Create(DrawingValueFormat.Culture, $"{height:F3}"), IndicatorColor.Gray),
            new DrawingCalculatedValue("Duration", "Time Duration", (decimal)Math.Round(days, 1), string.Create(DrawingValueFormat.Culture, $"{days:F1} days"), IndicatorColor.Gray)
        };

        foreach (var ratio in GridRatiosArray)
        {
            decimal levelPrice = bottomPrice + height * (decimal)ratio;
            int eighths = (int)Math.Round(ratio * 8.0);
            values.Add(new DrawingCalculatedValue(string.Create(DrawingValueFormat.Culture, $"Gann_{eighths}_8"), string.Create(DrawingValueFormat.Culture, $"Level {eighths}/8"), levelPrice, string.Create(DrawingValueFormat.Culture, $"{levelPrice:F3}"), IndicatorColor.Gray));
        }

        return values;
    }
}
