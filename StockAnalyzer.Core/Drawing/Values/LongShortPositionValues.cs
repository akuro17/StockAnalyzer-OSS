using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Drawing.Values;

/// <summary>
/// Data-tab values of the long and short position tools (the single definition; the GUI object and the MCP tools both call it).
/// The control points are Entry, Stop and Target in that order. This is a pure calculation: it never refuses a point order
/// (a drag in the GUI and the reading of saved drawings must not fail); <see cref="IsOrdered"/> says whether the order is the valid one,
/// and the callers decide what an invalid order means.
/// </summary>
public static class LongShortPositionValues
{
    /// <summary>True when Stop &lt; Entry &lt; Target (long) or Target &lt; Entry &lt; Stop (short).</summary>
    public static bool IsOrdered(decimal entry, decimal stop, decimal target, bool isLong)
        => isLong ? stop < entry && entry < target : target < entry && entry < stop;

    /// <param name="indicatorValueAtEntry">The ATR the GUI bound the stop to, when there is one; it adds the ATR item.</param>
    public static IReadOnlyList<DrawingCalculatedValue> Calculate(
        DrawingAnchor entry,
        DrawingAnchor stop,
        DrawingAnchor target,
        IndicatorColor entryColor,
        IndicatorColor targetColor,
        IndicatorColor stopColor,
        decimal? indicatorValueAtEntry = null)
    {
        decimal risk = Math.Abs(entry.Price - stop.Price);
        decimal reward = Math.Abs(target.Price - entry.Price);
        decimal ratio = risk != 0 ? reward / risk : 0;

        var values = new List<DrawingCalculatedValue>(5)
        {
            new("Entry", "Entry", entry.Price, string.Create(DrawingValueFormat.Culture, $"{entry.Price:F2}"), entryColor),
            new("Target", "Target", target.Price, string.Create(DrawingValueFormat.Culture, $"{target.Price:F2} ({FormatDiff(target.Price - entry.Price)})"), targetColor),
            new("Stop", "Stop Loss", stop.Price, string.Create(DrawingValueFormat.Culture, $"{stop.Price:F2} ({FormatDiff(stop.Price - entry.Price)})"), stopColor),
            new("RiskReward", "R/R Ratio", ratio, string.Create(DrawingValueFormat.Culture, $"1 : {ratio:F2}"), IndicatorColor.Gray),
        };

        if (indicatorValueAtEntry.HasValue)
        {
            values.Add(new DrawingCalculatedValue("ATR_Entry", "ATR (Entry)", indicatorValueAtEntry.Value, string.Create(DrawingValueFormat.Culture, $"{indicatorValueAtEntry.Value:F3}"), IndicatorColor.Gray));
        }

        return values;
    }

    private static string FormatDiff(decimal diff) => diff > 0 ? string.Create(DrawingValueFormat.Culture, $"+{diff:F2}") : string.Create(DrawingValueFormat.Culture, $"{diff:F2}");
}
