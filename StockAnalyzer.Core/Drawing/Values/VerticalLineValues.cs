using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Drawing.Values;

/// <summary>Data-tab values of the vertical line (the single definition; the GUI object and the MCP tool both call it).</summary>
public static class VerticalLineValues
{
    public static IReadOnlyList<DrawingCalculatedValue> Calculate(DrawingAnchor point, DateTime timestamp, IndicatorColor color)
    {
        var lineTime = point.Time;

        var values = new List<DrawingCalculatedValue>(2)
        {
            new DrawingCalculatedValue("LineDate", "Line Date", null, lineTime.ToString("yyyy-MM-dd HH:mm", DrawingValueFormat.Culture), color)
        };

        double offsetDays = (timestamp - lineTime).TotalDays;
        decimal offsetDaysDec = (decimal)Math.Round(offsetDays, 1);
        string sign = offsetDays >= 0 ? "+" : "";
        values.Add(new DrawingCalculatedValue("Offset", "Time Offset", offsetDaysDec, string.Create(DrawingValueFormat.Culture, $"{sign}{offsetDays:F1} days"), IndicatorColor.Gray));

        return values;
    }
}
