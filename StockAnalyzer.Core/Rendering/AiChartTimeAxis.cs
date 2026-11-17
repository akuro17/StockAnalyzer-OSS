namespace StockAnalyzer.Core.Rendering;

/// <summary>
/// Turns a time into a position in bar units (0 is the first bar of the time map, 1.5 half way between the second and the third), for the saved drawings of an image. It is the rule of the
/// application's chart (<c>GenericCoordinateTransform.GetFractionalIndex</c>, the Gapless time axis of the standard chart; a test compares the two): between two bars the position is linear in
/// time (a weekend takes no bars); before the first and after the last bar it continues with the average bar interval <c>(last - first) / count</c> of the time map (one day when that is not positive).
/// One deliberate difference: for a time before the first bar the chart returns the mirrored (positive) value, a sign slip (its inverse GetTimeFromFractionalIndex gives an earlier time for a negative index); the image
/// returns the negative value, the inverse of the chart's own GetTimeFromFractionalIndex, and a test pins that. The time map is every bar the caller loaded, not only the drawn ones, so a drawing that starts before the drawn window sits where the chart puts it; the caller then subtracts the index of the
/// first drawn bar. A time that coincides with a bar is exactly that bar's index.
/// </summary>
public static class AiChartTimeAxis
{
    /// <summary>The average bar interval used outside the time map when the map spans no time (a single bar, or equal times).</summary>
    private static readonly double FallbackIntervalMilliseconds = TimeSpan.FromDays(1).TotalMilliseconds;

    /// <summary>The position of <paramref name="time"/> in bar units of <paramref name="barTimes"/>, which must hold at least one time in chronological order.</summary>
    public static double BarPosition(IReadOnlyList<DateTime> barTimes, DateTime time)
    {
        int count = barTimes.Count;
        if (count == 0)
        {
            throw new ArgumentException("At least one bar time.", nameof(barTimes));
        }

        double average = (barTimes[count - 1] - barTimes[0]).TotalMilliseconds / count;
        if (average <= 0)
        {
            average = FallbackIntervalMilliseconds;
        }

        if (time <= barTimes[0])
        {
            return (time - barTimes[0]).TotalMilliseconds / average;
        }

        if (time >= barTimes[count - 1])
        {
            return (count - 1) + ((time - barTimes[count - 1]).TotalMilliseconds / average);
        }

        int low = 0;
        int high = count - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            int comparison = barTimes[middle].CompareTo(time);
            if (comparison == 0)
            {
                return middle;
            }

            if (comparison < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        DateTime before = barTimes[high];
        DateTime after = barTimes[low];
        return high + ((time - before).TotalMilliseconds / (after - before).TotalMilliseconds);
    }
}
