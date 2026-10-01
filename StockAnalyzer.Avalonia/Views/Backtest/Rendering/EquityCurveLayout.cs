using System;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Avalonia.Views.Backtest.Rendering;

/// <summary>Task 12 boundary-condition states from spec §5.7's draw table (width/height&lt;=0 is handled by the control itself, before this layer is even asked to build).</summary>
public enum EquityCurveDisplayState
{
    Empty,
    SinglePoint,
    Line,
    Unavailable,
}

/// <summary>One equity point projected to [0,1] fractions within the current data's own X/Y domain.</summary>
public readonly record struct EquityCurveNormalizedPoint(double XFraction, double YFraction);

/// <summary>
/// Pure decimal-Equity -&gt; normalized-fraction projection (spec §5.7 "coordinate-transform layer"):
/// zero Avalonia/Skia dependency, so Task 17's boundary-condition facts (Empty/SinglePoint/zero-range/
/// overflow) can exercise it directly. Screen-pixel projection (which needs the control's current
/// plot rect in DIP) is a second, trivial step done by <see cref="EquityCurveControl"/> every paint -
/// it only multiplies these fractions by the current plot width/height, so resizing the window never
/// requires rebuilding this layer.
/// </summary>
public sealed class EquityCurveLayout
{
    public EquityCurveDisplayState State { get; }
    public ImmutableArray<EquityCurveNormalizedPoint> Points { get; }

    /// <summary>Index, in the source equity series, of <c>Points[0]</c> (0 for a full-range layout); <c>Points[i]</c> is source point <c>FirstIndex + i</c>.</summary>
    public int FirstIndex { get; }

    public decimal YMin { get; }
    public decimal YMax { get; }
    public DateTime ViewportStartUtc { get; }
    public DateTime ViewportEndUtc { get; }

    /// <summary>The two ends of the visible range as text (<see cref="EquityLabelFormats.Instant"/>), formatted once per build; the X axis
    /// shows them when the range is too short for any calendar tick and for a one-point series.</summary>
    public string ViewportStartLabel { get; }
    public string ViewportEndLabel { get; }

    private EquityCurveLayout(
        EquityCurveDisplayState state, ImmutableArray<EquityCurveNormalizedPoint> points, int firstIndex,
        decimal yMin, decimal yMax, DateTime viewportStartUtc, DateTime viewportEndUtc,
        string viewportStartLabel, string viewportEndLabel)
    {
        State = state;
        Points = points;
        FirstIndex = firstIndex;
        YMin = yMin;
        YMax = yMax;
        ViewportStartUtc = viewportStartUtc;
        ViewportEndUtc = viewportEndUtc;
        ViewportStartLabel = viewportStartLabel;
        ViewportEndLabel = viewportEndLabel;
    }

    private static readonly EquityCurveLayout EmptyInstance = new(
        EquityCurveDisplayState.Empty, ImmutableArray<EquityCurveNormalizedPoint>.Empty, 0, 0m, 0m, default, default,
        string.Empty, string.Empty);

    private static readonly EquityCurveLayout UnavailableInstance = new(
        EquityCurveDisplayState.Unavailable, ImmutableArray<EquityCurveNormalizedPoint>.Empty, 0, 0m, 0m, default, default,
        string.Empty, string.Empty);

    /// <summary>
    /// Builds the layout for one equity sample. DT-2 order is preserved throughout: every Equity
    /// difference (range, per-point offset) is computed in <c>decimal</c> first; only the final
    /// normalized fraction is cast to <c>double</c>. A <c>decimal</c> range/offset that overflows
    /// (spec §5.7 "range overflow") yields <see cref="EquityCurveDisplayState.Unavailable"/> instead
    /// of throwing, so the caller can fall back to the ChartUnavailable message while the domain
    /// result (MetricsTable/TradeListView) stays intact.
    /// </summary>
    public static EquityCurveLayout Build(ImmutableArray<EquityPoint> points)
    {
        if (points.IsDefaultOrEmpty)
        {
            return EmptyInstance;
        }

        // The whole data span is the visible range: the output is the one every pre-viewport caller relied on.
        return Build(points, points[0].Timestamp.Ticks, points[^1].Timestamp.Ticks);
    }

    /// <summary>
    /// Builds the layout of the visible time range [<paramref name="visibleStartTicks"/>, <paramref name="visibleEndTicks"/>] (UTC ticks).
    /// The drawn slice is the in-range points plus one bracketing neighbour on each side, so a line reaches the plot edge; X fractions are
    /// relative to the visible range (neighbours fall outside [0,1] and are clipped by the plot) and the Y range is fitted over the drawn
    /// slice, so the line never leaves the plot vertically. When no point is in range the two bracketing points are drawn; when the range
    /// does not touch the data span at all nothing is drawn and Y is fitted over the whole series. The whole series is validated
    /// (<see cref="EquitySeriesRule"/>) whatever the range, so a zoom never hides an invalid input. Every Equity difference stays in
    /// <c>decimal</c>; only the final fraction is a <c>double</c>.
    /// </summary>
    public static EquityCurveLayout Build(ImmutableArray<EquityPoint> points, long visibleStartTicks, long visibleEndTicks)
    {
        if (points.IsDefaultOrEmpty)
        {
            return EmptyInstance;
        }

        return EquitySeriesRule.IsValid(points)
            ? BuildValidated(points, visibleStartTicks, visibleEndTicks)
            : UnavailableInstance;
    }

    /// <summary>
    /// <see cref="Build(ImmutableArray{EquityPoint}, long, long)"/> for a non-empty series the caller has already proven valid with
    /// <see cref="EquitySeriesRule"/> (the control does so once per result): skips the O(n) validation, so a pan or zoom costs only the visible slice.
    /// </summary>
    internal static EquityCurveLayout BuildValidated(ImmutableArray<EquityPoint> points, long visibleStartTicks, long visibleEndTicks)
    {
        (int sliceStart, int sliceEnd) = ResolveDrawnSlice(points, visibleStartTicks, visibleEndTicks);
        bool hasSlice = sliceStart <= sliceEnd;
        (int fitStart, int fitEnd) = hasSlice ? (sliceStart, sliceEnd) : (0, points.Length - 1);

        decimal yMin = points[fitStart].Equity;
        decimal yMax = points[fitStart].Equity;
        for (int i = fitStart + 1; i <= fitEnd; i++)
        {
            decimal equity = points[i].Equity;
            if (equity < yMin) yMin = equity;
            if (equity > yMax) yMax = equity;
        }

        try
        {
            if (yMax - yMin == 0m)
            {
                // Spec §5.7 "Y range == 0 (constant) -> +/-1 currency-unit display band".
                yMin -= 1m;
                yMax += 1m;
            }
        }
        catch (OverflowException)
        {
            return UnavailableInstance;
        }

        DateTime viewportStartUtc = ToUtc(visibleStartTicks);
        DateTime viewportEndUtc = ToUtc(visibleEndTicks);

        ImmutableArray<EquityCurveNormalizedPoint>.Builder builder =
            ImmutableArray.CreateBuilder<EquityCurveNormalizedPoint>(hasSlice ? sliceEnd - sliceStart + 1 : 0);
        if (hasSlice)
        {
            for (int i = sliceStart; i <= sliceEnd; i++)
            {
                builder.Add(Normalize(points[i], visibleStartTicks, visibleEndTicks, yMin, yMax));
            }
        }

        EquityCurveDisplayState state = points.Length == 1
            ? EquityCurveDisplayState.SinglePoint
            : EquityCurveDisplayState.Line;
        return new EquityCurveLayout(
            state, builder.MoveToImmutable(), hasSlice ? sliceStart : 0, yMin, yMax, viewportStartUtc, viewportEndUtc,
            viewportStartUtc.ToString(EquityLabelFormats.Instant, EquityLabelFormats.Culture),
            viewportEndUtc.ToString(EquityLabelFormats.Instant, EquityLabelFormats.Culture));
    }

    /// <summary>
    /// The one projection of an equity point onto the layout's [0,1] fractions (the line, the crosshair and the trade markers all use it,
    /// so they cannot drift apart). <paramref name="yMin"/>/<paramref name="yMax"/> are a built layout's <see cref="YMin"/>/<see cref="YMax"/>
    /// (a non-empty range, already widened for a constant series); the Y difference stays <c>decimal</c> until the final division.
    /// </summary>
    public static EquityCurveNormalizedPoint Normalize(
        EquityPoint point, long visibleStartTicks, long visibleEndTicks, decimal yMin, decimal yMax)
    {
        long totalSpanTicks = visibleEndTicks - visibleStartTicks;
        double xFraction = totalSpanTicks <= 0
            ? 0.0
            : (point.Timestamp.Ticks - visibleStartTicks) / (double)totalSpanTicks;

        // Bounded by the layout's range (proven representable when it was built), so this cannot overflow for a drawn point.
        decimal yOffset = point.Equity - yMin;
        double yFraction = (double)yOffset / (double)(yMax - yMin);
        return new EquityCurveNormalizedPoint(xFraction, yFraction);
    }

    /// <summary>Inclusive index range of the drawn slice (see <see cref="Build(ImmutableArray{EquityPoint}, long, long)"/>); start &gt; end means nothing is drawn. Two binary searches.</summary>
    private static (int Start, int End) ResolveDrawnSlice(ImmutableArray<EquityPoint> points, long startTicks, long endTicks)
    {
        int count = points.Length;
        int firstInRange = EquityNearestPoint.FirstIndexAtOrAfter(points, startTicks);
        int pastRange = Math.Max(EquityNearestPoint.FirstIndexAfter(points, endTicks), firstInRange);

        int lastInRange = pastRange - 1;
        // With no point in range, firstInRange == pastRange, so [firstInRange - 1, firstInRange] brackets the range.
        int start = firstInRange - 1;
        int end = firstInRange <= lastInRange ? lastInRange + 1 : firstInRange;
        if (firstInRange <= lastInRange)
        {
            start = Math.Max(start, 0);
            end = Math.Min(end, count - 1);
            return (start, end);
        }

        return start < 0 || end > count - 1 ? (1, 0) : (start, end);
    }

    private static DateTime ToUtc(long ticks) =>
        new(Math.Clamp(ticks, DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks), DateTimeKind.Utc);
}
