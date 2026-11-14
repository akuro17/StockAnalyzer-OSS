using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Drawing.Values;

/// <summary>
/// Geometry and Data-tab values of the pitchfork, all in chart coordinates (time, decimal price); the GUI object projects the anchor and the
/// median point to the screen, so the drawn lines, the hit area and these values share one definition.
/// Anchor: Andrews = P1; Schiff = (P1 time, mean of P1 and P2 price); Modified Schiff = (mid time of P1 and P2, mean of P1 and P2 price).
/// Median point = (mid time of P2 and P3, mean of P2 and P3 price). Mid time = first time + half the calendar seconds between the two times.
/// </summary>
public static class PitchforkValues
{
    /// <summary>The point the median line starts from.</summary>
    public static DrawingAnchor GetAnchor(PitchforkKind kind, DrawingAnchor p1, DrawingAnchor p2)
    {
        switch (kind)
        {
            case PitchforkKind.Schiff:
                return new DrawingAnchor(p1.Time, (p1.Price + p2.Price) / 2m);
            case PitchforkKind.ModifiedSchiff:
                return new DrawingAnchor(MidTime(p1.Time, p2.Time), (p1.Price + p2.Price) / 2m);
            default:
                return p1;
        }
    }

    /// <summary>The midpoint of P2 and P3, the point the median line passes through.</summary>
    public static DrawingAnchor GetMedianPoint(DrawingAnchor p2, DrawingAnchor p3)
        => new(MidTime(p2.Time, p3.Time), (p2.Price + p3.Price) / 2m);

    /// <summary>
    /// Base point Q(f) = M + f * (P2 - M) of a parallel line at multiplier <paramref name="factor"/> in chart coordinates
    /// (ticks for time, decimal for price). Q(1) = P2 and Q(-1) = P3.
    /// </summary>
    public static DrawingAnchor GetParallelAnchor(DrawingAnchor p2, DrawingAnchor median, decimal factor)
    {
        long deltaTicks = p2.Time.Ticks - median.Time.Ticks;
        decimal scaledTicks = factor * deltaTicks;
        long targetTicks = ClampTicks(median.Time.Ticks + (long)Math.Round(scaledTicks, MidpointRounding.AwayFromZero));
        decimal targetPrice = median.Price + factor * (p2.Price - median.Price);
        return new DrawingAnchor(new DateTime(targetTicks, median.Time.Kind), targetPrice);
    }

    /// <summary>Translates <paramref name="origin"/> by the median-line vector (M - A) in chart coordinates.</summary>
    public static DrawingAnchor OffsetByMedianVector(DrawingAnchor origin, DrawingAnchor anchor, DrawingAnchor median)
    {
        long deltaTicks = median.Time.Ticks - anchor.Time.Ticks;
        long targetTicks = ClampTicks(origin.Time.Ticks + deltaTicks);
        decimal targetPrice = origin.Price + (median.Price - anchor.Price);
        return new DrawingAnchor(new DateTime(targetTicks, origin.Time.Kind), targetPrice);
    }

    private static long ClampTicks(long ticks)
        => Math.Clamp(ticks, DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks);

    public static IReadOnlyList<DrawingCalculatedValue> Calculate(PitchforkKind kind, DrawingAnchor p1, DrawingAnchor p2, DrawingAnchor p3, DateTime timestamp, IndicatorColor color)
    {
        DrawingAnchor anchor = GetAnchor(kind, p1, p2);
        DrawingAnchor median = GetMedianPoint(p2, p3);

        decimal medianLinePrice = DrawingValueMath.InterpolatePrice(anchor.Time, anchor.Price, median.Time, median.Price, timestamp);

        decimal offset2 = p2.Price - median.Price;
        decimal offset3 = p3.Price - median.Price;
        decimal maxOffset = Math.Max(offset2, offset3);
        decimal minOffset = Math.Min(offset2, offset3);

        decimal upper = medianLinePrice + maxOffset;
        decimal lower = medianLinePrice + minOffset;

        return new DrawingCalculatedValue[]
        {
            new DrawingCalculatedValue("UpperLine", "Upper Line", upper, string.Create(DrawingValueFormat.Culture, $"{upper:F3}"), color),
            new DrawingCalculatedValue("MedianLine", "Median Line", medianLinePrice, string.Create(DrawingValueFormat.Culture, $"{medianLinePrice:F3}"), color),
            new DrawingCalculatedValue("LowerLine", "Lower Line", lower, string.Create(DrawingValueFormat.Culture, $"{lower:F3}"), color),
            new DrawingCalculatedValue("ForkType", "Type", null, kind.ToString(), IndicatorColor.Gray)
        };
    }

    private static DateTime MidTime(DateTime first, DateTime second)
        => DrawingValueMath.SafeAddSeconds(first, (second - first).TotalSeconds / 2.0);
}
