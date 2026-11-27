namespace StockAnalyzer.Core.Drawing;

/// <summary>
/// The one definition of "the bars of a drawing range" for the range-analysis drawing tools (DSL expansion: GarchVolatilityCone, PolynomialRegressionProjection and the tools after it).
/// A range is the bars whose <c>Timestamp</c> lies in <c>[min(time1, time2), max(time1, time2)]</c>, both ends included, oldest first; the same rule as
/// <c>RegressionTrendValues.Fit</c>, <c>DatePriceRangeValues</c> and <c>DistributionRangeCalculator</c>. Those three keep their own private copies on purpose (unrelated
/// refactoring is out of scope); moving them onto this class is a candidate for a later improvement.
/// </summary>
public static class DrawingRangeBars
{
    /// <summary>The bar span used for a future bar time when the candle list is too short to measure one (one day).</summary>
    public static readonly TimeSpan FallbackBarSpan = TimeSpan.FromDays(1);

    /// <summary>
    /// Finds the first and the last bar of the range between the two times (either order). <paramref name="candles"/> must be oldest first.
    /// Returns false (and -1 for both indexes) when no bar lies in the range or the list is empty.
    /// </summary>
    public static bool TryFind(IReadOnlyList<CoreCandleData>? candles, DateTime time1, DateTime time2, out int firstIndex, out int lastIndex)
    {
        firstIndex = -1;
        lastIndex = -1;
        if (candles == null || candles.Count == 0)
        {
            return false;
        }

        DateTime start = time1 < time2 ? time1 : time2;
        DateTime end = time1 < time2 ? time2 : time1;

        // Lower bound: the first bar with Timestamp >= start.
        int low = 0;
        int high = candles.Count - 1;
        int first = -1;
        while (low <= high)
        {
            int mid = low + ((high - low) >> 1);
            if (candles[mid].Timestamp >= start)
            {
                first = mid;
                high = mid - 1;
            }
            else
            {
                low = mid + 1;
            }
        }

        // Upper bound: the last bar with Timestamp <= end.
        low = 0;
        high = candles.Count - 1;
        int last = -1;
        while (low <= high)
        {
            int mid = low + ((high - low) >> 1);
            if (candles[mid].Timestamp <= end)
            {
                last = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        if (first < 0 || last < 0 || first > last)
        {
            return false;
        }

        firstIndex = first;
        lastIndex = last;
        return true;
    }

    /// <summary>
    /// The bar span of a candle list: the mean distance between the first and the last bar time (the rule <c>ArimaProjectionAnalysis</c> uses when no span is given),
    /// <see cref="FallbackBarSpan"/> when the list has fewer than two bars or the mean is not positive.
    /// </summary>
    public static TimeSpan MeanBarSpan(IReadOnlyList<CoreCandleData> candles)
    {
        if (candles.Count >= 2)
        {
            TimeSpan total = candles[candles.Count - 1].Timestamp - candles[0].Timestamp;
            TimeSpan mean = TimeSpan.FromTicks(total.Ticks / (candles.Count - 1));
            if (mean > TimeSpan.Zero)
            {
                return mean;
            }
        }

        return FallbackBarSpan;
    }

    /// <summary>
    /// The time of the bar <paramref name="step"/> bars after the bar at <paramref name="lastIndex"/> (step 0 is that bar itself): the time of the existing bar when there is one,
    /// otherwise the newest bar time plus <paramref name="barSpan"/> for every bar beyond the data (the rule of <c>ArimaProjectionAnalysis</c>).
    /// </summary>
    public static DateTime FutureBarTime(IReadOnlyList<CoreCandleData> candles, int lastIndex, int step, TimeSpan barSpan)
    {
        int targetIndex = lastIndex + step;
        if (targetIndex < candles.Count)
        {
            return candles[targetIndex].Timestamp;
        }

        int beyond = targetIndex - candles.Count + 1;
        return candles[candles.Count - 1].Timestamp + TimeSpan.FromTicks(barSpan.Ticks * beyond);
    }

    /// <summary>
    /// The position of <paramref name="time"/> on the bar axis: the bar index plus the fraction of the way to the next bar (linear in time between two bars). Before the first bar the position is
    /// negative and after the last bar it is beyond <c>Count - 1</c>, in steps of <paramref name="barSpan"/>. The inverse of <see cref="TimeAtBarPosition"/>.
    /// </summary>
    public static double BarPositionOf(IReadOnlyList<CoreCandleData> candles, DateTime time, TimeSpan barSpan)
    {
        int last = candles.Count - 1;
        if (time <= candles[0].Timestamp)
        {
            return (time - candles[0].Timestamp).Ticks / (double)barSpan.Ticks;
        }

        if (time >= candles[last].Timestamp)
        {
            return last + ((time - candles[last].Timestamp).Ticks / (double)barSpan.Ticks);
        }

        // The last bar at or before the time.
        int low = 0;
        int high = last;
        while (low < high)
        {
            int mid = low + ((high - low + 1) >> 1);
            if (candles[mid].Timestamp <= time)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        long gap = (candles[low + 1].Timestamp - candles[low].Timestamp).Ticks;
        return low + ((time - candles[low].Timestamp).Ticks / (double)gap);
    }

    /// <summary>The time at a position on the bar axis (see <see cref="BarPositionOf"/>); linear in time between two bars, in steps of <paramref name="barSpan"/> outside the data.</summary>
    public static DateTime TimeAtBarPosition(IReadOnlyList<CoreCandleData> candles, double position, TimeSpan barSpan)
    {
        int last = candles.Count - 1;
        if (position <= 0.0)
        {
            return candles[0].Timestamp + TimeSpan.FromTicks((long)Math.Round(position * barSpan.Ticks));
        }

        if (position >= last)
        {
            return candles[last].Timestamp + TimeSpan.FromTicks((long)Math.Round((position - last) * barSpan.Ticks));
        }

        int index = (int)Math.Floor(position);
        double fraction = position - index;
        long gap = (candles[index + 1].Timestamp - candles[index].Timestamp).Ticks;
        return candles[index].Timestamp + TimeSpan.FromTicks((long)Math.Round(fraction * gap));
    }
}
