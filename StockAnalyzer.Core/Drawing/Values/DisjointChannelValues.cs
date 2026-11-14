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
    public static IReadOnlyList<DrawingCalculatedValue> Calculate(DrawingAnchor p1, DrawingAnchor p2, DrawingAnchor p3, DrawingAnchor p4, DateTime timestamp, IndicatorColor color)
    {
        decimal line1 = DrawingValueMath.InterpolatePrice(p1.Time, p1.Price, p2.Time, p2.Price, timestamp);
        decimal line2 = DrawingValueMath.InterpolatePrice(p3.Time, p3.Price, p4.Time, p4.Price, timestamp);
        decimal mid = (line1 + line2) / 2m;
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
