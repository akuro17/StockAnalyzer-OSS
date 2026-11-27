namespace StockAnalyzer.Core.Drawing;

/// <summary>Why a fixed-period drawing range is snapped (see <see cref="DrawingRangeSnap"/>).</summary>
public enum RangeSnapKind
{
    /// <summary>The placement of a new drawing has finished.</summary>
    Placement,

    /// <summary>One of the two handles was released; the dragged point decides which side moved.</summary>
    HandleRelease,

    /// <summary>The whole drawing was moved.</summary>
    BodyMove,

    /// <summary>The period was changed in the settings dialog.</summary>
    PeriodChange,
}

/// <summary>The snapped range: the bar indexes, their times and the period that applies.</summary>
public readonly record struct RangeSnapResult(int StartIndex, int EndIndex, DateTime StartTime, DateTime EndTime, int Period)
{
    /// <summary>The number of bars of the snapped range.</summary>
    public int BarCount => EndIndex - StartIndex + 1;
}

/// <summary>
/// The one definition of the end-point snap of a drawing range that is exactly <c>Period</c> bars long (the Robust Regression Trend; settings expansion DS-6 = B).
/// The two anchors are put in time order first (a range drawn from right to left behaves like one drawn from left to right):
/// <list type="bullet">
/// <item>The start is the first bar at or after the earlier anchor time; the end is the bar <c>Period - 1</c> bars later, stopped at the last bar of the data (the range is then shorter than the period).</item>
/// <item><see cref="RangeSnapKind.HandleRelease"/>: when the LATER handle was dragged, the width up to the bar at or before it becomes the new period (clamped to the period limits; the start stays); when the EARLIER handle
/// was dragged the period is unchanged and the window slides (the start is the first bar at or after the dragged time). Equal times count as the later handle.</item>
/// <item><see cref="RangeSnapKind.Placement"/>, <see cref="RangeSnapKind.BodyMove"/> and <see cref="RangeSnapKind.PeriodChange"/> keep the given period.</item>
/// </list>
/// Pure and allocation free: it reads the candle times only and never changes anything; the caller writes the result back. It must only be called at an explicit user-confirmed edit (placement, handle release,
/// move completion, settings OK), never from a recalculation, a data refresh or a load.
/// </summary>
public static class DrawingRangeSnap
{
    /// <summary>
    /// Computes the snapped range of the anchors at <paramref name="time1"/> and <paramref name="time2"/> (candles oldest first). <paramref name="draggedPointIndex"/> (0 or 1) is only read for
    /// <see cref="RangeSnapKind.HandleRelease"/>. Returns false when there is no bar to snap to (empty data, or the earlier anchor lies after the last bar).
    /// </summary>
    public static bool TryCompute(
        IReadOnlyList<CoreCandleData>? candles,
        DateTime time1,
        DateTime time2,
        int period,
        int minPeriod,
        int maxPeriod,
        RangeSnapKind kind,
        int draggedPointIndex,
        out RangeSnapResult result)
    {
        result = default;
        if (candles == null || candles.Count == 0 || minPeriod < 1 || maxPeriod < minPeriod)
        {
            return false;
        }

        int newPeriod = Math.Clamp(period, minPeriod, maxPeriod);
        DateTime earlier = time1 <= time2 ? time1 : time2;
        int startIndex;

        if (kind == RangeSnapKind.HandleRelease && (draggedPointIndex == 0 || draggedPointIndex == 1))
        {
            DateTime dragged = draggedPointIndex == 0 ? time1 : time2;
            DateTime other = draggedPointIndex == 0 ? time2 : time1;
            if (dragged >= other)
            {
                // The later handle: the width up to it is the new period; the start stays at the other (earlier) anchor.
                startIndex = FirstAtOrAfter(candles, other);
                if (startIndex < 0)
                {
                    return false;
                }

                int endRaw = LastAtOrBefore(candles, dragged);
                int width = endRaw - startIndex + 1;
                newPeriod = Math.Clamp(width, minPeriod, maxPeriod);
            }
            else
            {
                // The earlier handle: the window slides, the period stays.
                startIndex = FirstAtOrAfter(candles, dragged);
            }
        }
        else
        {
            startIndex = FirstAtOrAfter(candles, earlier);
        }

        if (startIndex < 0)
        {
            return false;
        }

        int endIndex = Math.Min(startIndex + newPeriod - 1, candles.Count - 1);
        result = new RangeSnapResult(startIndex, endIndex, candles[startIndex].Timestamp, candles[endIndex].Timestamp, newPeriod);
        return true;
    }

    /// <summary>The index of the first bar with <c>Timestamp &gt;= time</c>, or -1 when every bar is earlier.</summary>
    private static int FirstAtOrAfter(IReadOnlyList<CoreCandleData> candles, DateTime time)
    {
        int low = 0;
        int high = candles.Count - 1;
        int found = -1;
        while (low <= high)
        {
            int mid = low + ((high - low) >> 1);
            if (candles[mid].Timestamp >= time)
            {
                found = mid;
                high = mid - 1;
            }
            else
            {
                low = mid + 1;
            }
        }

        return found;
    }

    /// <summary>The index of the last bar with <c>Timestamp &lt;= time</c>, or -1 when every bar is later.</summary>
    private static int LastAtOrBefore(IReadOnlyList<CoreCandleData> candles, DateTime time)
    {
        int low = 0;
        int high = candles.Count - 1;
        int found = -1;
        while (low <= high)
        {
            int mid = low + ((high - low) >> 1);
            if (candles[mid].Timestamp <= time)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return found;
    }
}
