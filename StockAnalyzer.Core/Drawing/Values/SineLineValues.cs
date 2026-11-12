using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Drawing.Values;

/// <summary>Data-tab values of the sine line (the single definition; the GUI object and the MCP tool both call it).</summary>
public static class SineLineValues
{
    public static IReadOnlyList<DrawingCalculatedValue> Calculate(DrawingAnchor p1, DrawingAnchor p2, DateTime timestamp, IndicatorColor color)
    {
        double periodSeconds = Math.Abs((p2.Time - p1.Time).TotalSeconds);
        if (periodSeconds < DrawingValueMath.MinPeriodSeconds) return Array.Empty<DrawingCalculatedValue>();

        double periodDays = periodSeconds / DrawingValueMath.SecondsPerDay;
        decimal centerPrice = p1.Price;
        decimal amplitude = Math.Abs(p2.Price - p1.Price);

        double angle = 2.0 * Math.PI * (timestamp - p1.Time).TotalSeconds / periodSeconds;
        decimal waveOffset = (p2.Price - p1.Price) * (decimal)Math.Sin(angle);
        decimal sinePrice = centerPrice + waveOffset;

        return new DrawingCalculatedValue[]
        {
            new DrawingCalculatedValue("SinePrice", "Sine Price", sinePrice, string.Create(DrawingValueFormat.Culture, $"{sinePrice:F3}"), color),
            new DrawingCalculatedValue("CenterPrice", "Center Price", centerPrice, string.Create(DrawingValueFormat.Culture, $"{centerPrice:F3}"), IndicatorColor.Gray),
            new DrawingCalculatedValue("Amplitude", "Amplitude", amplitude, string.Create(DrawingValueFormat.Culture, $"{amplitude:F3}"), IndicatorColor.Gray),
            new DrawingCalculatedValue("Period", "Wave Period", (decimal)Math.Round(periodDays, 1), string.Create(DrawingValueFormat.Culture, $"{periodDays:F1} days"), IndicatorColor.Gray)
        };
    }
}
