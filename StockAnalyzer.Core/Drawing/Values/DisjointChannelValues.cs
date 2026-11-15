using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Drawing.Values;

/// <summary>
/// Data-tab values of the disjoint channel (two independent edges P1-P2 and P3-P4) at a time. Four fixed items:
/// Line1 (edge 1 price), Line2 (edge 2 price), Mid ((Line1 + Line2) / 2), Width (|Line1 - Line2|). Unit: price-native.
/// Each edge price is <see cref="DrawingValueMath.InterpolatePrice"/> (straight-line extrapolation outside the edge; an edge whose
/// two anchors share a time yields that function's own result, its first anchor's price). The raw anchors are not part of the values.
/// </summary>
public static class DisjointChannelValues
{
    /// <summary>
    /// End points of the centre line, in chart coordinates: the time of each end is the integer mean of the two time ticks (P1 and P3 for the start, P2 and P4 for the end)
    /// and its price is <c>Mid</c> at that time, so the segment lies on the straight line of the <c>Mid</c> value (owner decision D-2 = B, 2026-11-16).
    /// With P1 and P3 at one time and P2 and P4 at one time this is the midpoint of P1/P3 and of P2/P4.
    /// </summary>
    public static (DrawingAnchor Start, DrawingAnchor End) GetCenterLine(DrawingAnchor p1, DrawingAnchor p2, DrawingAnchor p3, DrawingAnchor p4)
    {
        DateTime startTime = MeanTime(p1.Time, p3.Time);
        DateTime endTime = MeanTime(p2.Time, p4.Time);
        return (new DrawingAnchor(startTime, MidAt(p1, p2, p3, p4, startTime)), new DrawingAnchor(endTime, MidAt(p1, p2, p3, p4, endTime)));
    }

    /// <summary>The integer mean of the two time ticks (the sum of two tick counts stays below <see cref="long.MaxValue"/>); the kind of <paramref name="first"/>.</summary>
    private static DateTime MeanTime(DateTime first, DateTime second)
        => new((first.Ticks + second.Ticks) / 2, first.Kind);

    /// <summary><c>Mid</c> at <paramref name="time"/>: the mean of the two edge prices.</summary>
    private static decimal MidAt(DrawingAnchor p1, DrawingAnchor p2, DrawingAnchor p3, DrawingAnchor p4, DateTime time)
    {
        decimal line1 = DrawingValueMath.InterpolatePrice(p1.Time, p1.Price, p2.Time, p2.Price, time);
        decimal line2 = DrawingValueMath.InterpolatePrice(p3.Time, p3.Price, p4.Time, p4.Price, time);
        return MidOf(line1, line2);
    }

    /// <summary>The one definition of <c>Mid</c> from the two edge prices of one time.</summary>
    private static decimal MidOf(decimal line1, decimal line2) => (line1 + line2) / 2m;

    public static IReadOnlyList<DrawingCalculatedValue> Calculate(DrawingAnchor p1, DrawingAnchor p2, DrawingAnchor p3, DrawingAnchor p4, DateTime timestamp, IndicatorColor color)
    {
        decimal line1 = DrawingValueMath.InterpolatePrice(p1.Time, p1.Price, p2.Time, p2.Price, timestamp);
        decimal line2 = DrawingValueMath.InterpolatePrice(p3.Time, p3.Price, p4.Time, p4.Price, timestamp);
        decimal mid = MidOf(line1, line2);
        decimal width = Math.Abs(line1 - line2);

        return new DrawingCalculatedValue[]
        {
            new("Line1", "Line 1", line1, string.Create(DrawingValueFormat.Culture, $"{line1:F3}"), color),
            new("Line2", "Line 2", line2, string.Create(DrawingValueFormat.Culture, $"{line2:F3}"), color),
            new("Mid", "Midline", mid, string.Create(DrawingValueFormat.Culture, $"{mid:F3}"), IndicatorColor.Gray),
            new("Width", "Channel Width", width, string.Create(DrawingValueFormat.Culture, $"{width:F3}"), IndicatorColor.Gray)
        };
    }
}
