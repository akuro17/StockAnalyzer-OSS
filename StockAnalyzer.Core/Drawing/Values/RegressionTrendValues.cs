using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services.Analysis;

namespace StockAnalyzer.Core.Drawing.Values;

/// <summary>
/// A regression fit over the bars between two times: the fit itself (x = 0 at the first bar of the range, one per bar), the range and the bar times
/// the evaluation time is mapped to a bar number with (<see cref="BarTimes"/> in the order of x, <see cref="TailTimes"/> the existing bars after the range).
/// </summary>
public readonly record struct RegressionTrendFit(
    RegressionService.RegressionResult Result,
    DateTime Start,
    DateTime End,
    IReadOnlyList<DateTime> BarTimes,
    IReadOnlyList<DateTime> TailTimes);

/// <summary>
/// Data-tab values of the regression trend (the single definition; the GUI object and the MCP tool both call it).
/// The fit is the ordinary least squares line of the closes against the bar number (<see cref="RegressionService"/>); the band is the standard deviation of that fit times the multiplier.
/// </summary>
public static class RegressionTrendValues
{
    /// <summary>The multiplier a new regression trend starts with.</summary>
    public const decimal DefaultStdDevMultiplier = 2.0m;

    /// <summary>The smallest multiplier (the settings range of the GUI object).</summary>
    public const double MinStdDevMultiplierRange = 0.1;

    /// <summary>The largest multiplier (the settings range of the GUI object).</summary>
    public const double MaxStdDevMultiplierRange = 10.0;

    /// <summary>The band item name: sign, multiplier with at most two decimals, then "σ Band"; formatted with <see cref="DrawingValueFormat.Culture"/> so the decimal separator is always ".".</summary>
    public static string BandName(char sign, decimal multiplier)
        => string.Create(DrawingValueFormat.Culture, $"{sign}{multiplier:0.##}σ Band");

    /// <summary>
    /// Fits the bars whose time is within the range of the two times (either order; both ends included). <paramref name="candles"/> must be oldest first.
    /// An invalid fit (fewer than two bars) has <see cref="RegressionService.RegressionResult.IsValid"/> false and no tail.
    /// </summary>
    public static RegressionTrendFit Fit(IReadOnlyList<CoreCandleData> candles, DateTime time1, DateTime time2)
    {
        DateTime start = time1 < time2 ? time1 : time2;
        DateTime end = time1 < time2 ? time2 : time1;

        var range = new List<CoreCandleData>();
        var tail = new List<DateTime>();
        for (int i = 0; i < candles.Count; i++)
        {
            DateTime time = candles[i].Timestamp;
            if (time >= start && time <= end)
            {
                range.Add(candles[i]);
            }
            else if (time > end)
            {
                tail.Add(time);
            }
        }

        RegressionService.RegressionResult result = new RegressionService().Calculate(range);
        if (!result.IsValid)
        {
            return new RegressionTrendFit(result, start, end, Array.Empty<DateTime>(), Array.Empty<DateTime>());
        }

        var barTimes = new DateTime[result.Count];
        for (int i = 0; i < barTimes.Length; i++)
        {
            barTimes[i] = range[i].Timestamp;
        }

        return new RegressionTrendFit(result, start, end, barTimes, tail);
    }

    /// <summary>The values of a fit at <paramref name="timestamp"/>. The names of the two band items are built here.</summary>
    public static IReadOnlyList<DrawingCalculatedValue> Calculate(RegressionTrendFit fit, DateTime timestamp, decimal stdDevMultiplier, bool extendRight, IndicatorColor color)
        => Calculate(fit, timestamp, stdDevMultiplier, extendRight, BandName('+', stdDevMultiplier), BandName('-', stdDevMultiplier), color);

    /// <summary>The values of a fit at <paramref name="timestamp"/>; the caller supplies the band item names (the GUI caches them so the call allocates no string for them).</summary>
    public static IReadOnlyList<DrawingCalculatedValue> Calculate(
        RegressionTrendFit fit,
        DateTime timestamp,
        decimal stdDevMultiplier,
        bool extendRight,
        string upperBandName,
        string lowerBandName,
        IndicatorColor color)
    {
        RegressionService.RegressionResult result = fit.Result;
        if (!result.IsValid)
        {
            return Array.Empty<DrawingCalculatedValue>();
        }

        int x = 0;
        if (fit.Start != fit.End && result.Count > 1)
        {
            if (timestamp >= fit.Start && timestamp <= fit.End)
            {
                // Inside the range: the bar number is the position of the newest bar at or before the time (non-trading days do not shift it).
                int index = BinarySearch(fit.BarTimes, timestamp);
                if (index < 0)
                {
                    index = ~index - 1;
                }

                x = Math.Clamp(index, 0, result.Count - 1);
            }
            else if (extendRight && fit.TailTimes.Count > 0 && timestamp > fit.End && timestamp <= fit.TailTimes[fit.TailTimes.Count - 1])
            {
                // On trailing existing bars when extendRight is on: j = index + 1 (1-based after the range end), so x = (n - 1) + j = n + index.
                int index = BinarySearch(fit.TailTimes, timestamp);
                if (index < 0)
                {
                    index = ~index - 1;
                }

                index = Math.Clamp(index, 0, fit.TailTimes.Count - 1);
                x = result.Count + index;
            }
            else
            {
                double totalSec = (fit.End - fit.Start).TotalSeconds;
                if (totalSec != 0)
                {
                    double curSec = (timestamp - fit.Start).TotalSeconds;
                    x = (int)Math.Round((curSec / totalSec) * (result.Count - 1));
                }
            }
        }

        decimal center = result.GetValueAt(x);
        decimal std = result.StdDev * stdDevMultiplier;
        decimal upper = center + std;
        decimal lower = center - std;

        return new DrawingCalculatedValue[]
        {
            new("RegressionCenter", "Regression Center", center, string.Create(DrawingValueFormat.Culture, $"{center:F3}"), color),
            new("RegressionUpper2", upperBandName, upper, string.Create(DrawingValueFormat.Culture, $"{upper:F3}"), color),
            new("RegressionLower2", lowerBandName, lower, string.Create(DrawingValueFormat.Culture, $"{lower:F3}"), color),
            new("Slope", "Slope", result.Slope, string.Create(DrawingValueFormat.Culture, $"{result.Slope:F4}"), color),
            new("StdDev", "StdDev", result.StdDev, string.Create(DrawingValueFormat.Culture, $"{result.StdDev:F3}"), IndicatorColor.Gray),
        };
    }

    /// <summary>Array.BinarySearch semantics on a sorted list: the index of the time, or the bitwise complement of the index of the next larger one.</summary>
    private static int BinarySearch(IReadOnlyList<DateTime> times, DateTime value)
    {
        if (times is DateTime[] array)
        {
            return Array.BinarySearch(array, value);
        }

        int low = 0;
        int high = times.Count - 1;
        while (low <= high)
        {
            int mid = low + ((high - low) >> 1);
            int comparison = times[mid].CompareTo(value);
            if (comparison == 0)
            {
                return mid;
            }

            if (comparison < 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return ~low;
    }
}
