using System.Collections.Immutable;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Avalonia.Views.Backtest.Rendering;

/// <summary>
/// Pure lookups of an equity point by time (Avalonia-free). Both need the strictly increasing timestamps the layout already requires
/// of a drawable series; they do not re-validate it.
/// </summary>
public static class EquityNearestPoint
{
    /// <summary>
    /// Index of the point closest in time to <paramref name="ticks"/>; the earlier point when two are equally close. -1 for an empty
    /// series or when <paramref name="ticks"/> lies outside [first, last] by more than <paramref name="toleranceTicks"/> (the pointer is
    /// not over the data; the tolerance absorbs the rounding of a pointer-to-time conversion at the very edge).
    /// </summary>
    public static int FindIndex(ImmutableArray<EquityPoint> points, long ticks, long toleranceTicks = 0)
    {
        if (points.IsDefaultOrEmpty)
        {
            return -1;
        }

        long first = points[0].Timestamp.Ticks;
        long last = points[^1].Timestamp.Ticks;
        if (ticks < first - toleranceTicks || ticks > last + toleranceTicks)
        {
            return -1;
        }

        int after = FirstIndexAtOrAfter(points, ticks);
        if (after == 0) return 0;
        if (after == points.Length) return points.Length - 1;

        long distanceToBefore = ticks - points[after - 1].Timestamp.Ticks;
        long distanceToAfter = points[after].Timestamp.Ticks - ticks;
        return distanceToBefore <= distanceToAfter ? after - 1 : after;
    }

    /// <summary>Index of the point whose timestamp is exactly <paramref name="ticks"/>, or -1 when there is none (e.g. before the evaluation start).</summary>
    public static int FindExactIndex(ImmutableArray<EquityPoint> points, long ticks)
    {
        if (points.IsDefaultOrEmpty)
        {
            return -1;
        }

        int index = FirstIndexAtOrAfter(points, ticks);
        return index < points.Length && points[index].Timestamp.Ticks == ticks ? index : -1;
    }

    /// <summary>First index whose timestamp is &gt;= <paramref name="ticks"/> (Length when every point is earlier). Binary search.</summary>
    public static int FirstIndexAtOrAfter(ImmutableArray<EquityPoint> points, long ticks) => Partition(points, ticks, inclusive: false);

    /// <summary>First index whose timestamp is &gt; <paramref name="ticks"/> (Length when none is later). Binary search.</summary>
    public static int FirstIndexAfter(ImmutableArray<EquityPoint> points, long ticks) => Partition(points, ticks, inclusive: true);

    // The first index whose timestamp is not before ticks (inclusive: not before-or-equal).
    private static int Partition(ImmutableArray<EquityPoint> points, long ticks, bool inclusive)
    {
        int low = 0;
        int high = points.IsDefault ? 0 : points.Length;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            long timestamp = points[middle].Timestamp.Ticks;
            if (inclusive ? timestamp <= ticks : timestamp < ticks) low = middle + 1;
            else high = middle;
        }
        return low;
    }
}
