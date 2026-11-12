using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Drawing.Values;

/// <summary>
/// Data-tab values of the time cycles tool (the single definition; the GUI object and the MCP tool both call it).
/// A time on a cycle boundary starts a new cycle (phase 0%, as the GUI's cycle index floor((x - start) / interval) says);
/// NextCycle is the first boundary strictly after the time.
/// </summary>
public static class TimeCyclesValues
{
    public static IReadOnlyList<DrawingCalculatedValue> Calculate(DrawingAnchor p1, DrawingAnchor p2, DateTime timestamp, IndicatorColor color)
    {
        double intervalSeconds = Math.Abs((p2.Time - p1.Time).TotalSeconds);
        if (intervalSeconds < DrawingValueMath.MinPeriodSeconds) return Array.Empty<DrawingCalculatedValue>();

        double intervalDays = intervalSeconds / DrawingValueMath.SecondsPerDay;
        double elapsedSeconds = (timestamp - p1.Time).TotalSeconds;
        double cycleProgress = ((elapsedSeconds % intervalSeconds) + intervalSeconds) % intervalSeconds / intervalSeconds;
        long nextCycleK = (long)Math.Floor(elapsedSeconds / intervalSeconds) + 1;
        DateTime nextCycleTime = DrawingValueMath.SafeAddSeconds(p1.Time, nextCycleK * intervalSeconds);

        return new DrawingCalculatedValue[]
        {
            new DrawingCalculatedValue("Period", "Cycle Period", (decimal)Math.Round(intervalDays, 1), string.Create(DrawingValueFormat.Culture, $"{intervalDays:F1} days"), color),
            new DrawingCalculatedValue("CycleProgress", "Cycle Phase", (decimal)Math.Round(cycleProgress * 100.0, 1), string.Create(DrawingValueFormat.Culture, $"{cycleProgress * 100.0:F1}%"), IndicatorColor.Gray),
            new DrawingCalculatedValue("NextCycle", "Next Cycle", null, nextCycleTime.ToString("yyyy-MM-dd HH:mm", DrawingValueFormat.Culture), color)
        };
    }
}
