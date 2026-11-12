using System.Runtime.CompilerServices;

namespace StockAnalyzer.Core.Drawing;

/// <summary>
/// Pure price/time math of the drawing tools, shared by the GUI drawing objects (through StockAnalyzer.Avalonia's DrawingMath)
/// and by the headless drawing-value calculators. Time is calendar time, price is <see cref="decimal"/>.
/// </summary>
public static class DrawingValueMath
{
    /// <summary>Two times closer than this many seconds are the same time (interpolation and the two-point calculators return no line).</summary>
    internal const double MinTimeDeltaSeconds = 1e-4;

    /// <summary>
    /// Two times closer than this many days (8.64 seconds) have no slope. Kept different from <see cref="MinTimeDeltaSeconds"/> on purpose:
    /// it is the threshold the GUI has always used for the slope.
    /// </summary>
    internal const double MinSlopeTimeDeltaDays = 1e-4;

    /// <summary>
    /// A cycle or wave period shorter than this many seconds is no period (the time cycles and the sine line return no values).
    /// A third limit, kept apart from <see cref="MinTimeDeltaSeconds"/> because the GUI has always used a whole second for the period tools.
    /// </summary>
    internal const double MinPeriodSeconds = 1.0;

    /// <summary>Seconds in a calendar day (24 hours): the unit conversion of the period and the slope texts.</summary>
    internal const double SecondsPerDay = 86400.0;

    /// <summary>
    /// Linearly interpolates or extrapolates price at the specified timestamp based on two time/price pairs.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static decimal InterpolatePrice(DateTime t1, decimal p1, DateTime t2, decimal p2, DateTime timestamp)
    {
        double dt = (t2 - t1).TotalSeconds;
        if (Math.Abs(dt) < MinTimeDeltaSeconds) return p1;

        double ratio = (timestamp - t1).TotalSeconds / dt;
        return p1 + (decimal)ratio * (p2 - p1);
    }

    /// <summary>
    /// Calculates the daily slope (price change per 24 hours) between two time/price pairs.
    /// Returns null if the timestamps are identical or nearly identical.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static decimal? CalculateSlopePerDay(DateTime t1, decimal p1, DateTime t2, decimal p2)
    {
        double totalDays = (t2 - t1).TotalDays;
        if (Math.Abs(totalDays) < MinSlopeTimeDeltaDays) return null;

        return (p2 - p1) / (decimal)totalDays;
    }

    /// <summary>
    /// Calculates the percentage change from startPrice to endPrice.
    /// Returns 0 if startPrice is zero.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static decimal CalculatePercentageChange(decimal startPrice, decimal endPrice)
    {
        if (startPrice == 0m) return 0m;
        return ((endPrice - startPrice) / startPrice) * 100m;
    }

    /// <summary>
    /// Safely adds seconds to a DateTime without throwing ArgumentOutOfRangeException,
    /// clamping the result to DateTime.MinValue or DateTime.MaxValue.
    /// </summary>
    public static DateTime SafeAddSeconds(DateTime time, double seconds)
    {
        if (double.IsNaN(seconds)) return time;
        if (double.IsPositiveInfinity(seconds)) return DateTime.MaxValue;
        if (double.IsNegativeInfinity(seconds)) return DateTime.MinValue;

        try
        {
            if (seconds > 0)
            {
                double maxAddSeconds = (DateTime.MaxValue - time).TotalSeconds;
                if (seconds >= maxAddSeconds) return DateTime.MaxValue;
            }
            else if (seconds < 0)
            {
                double minAddSeconds = (DateTime.MinValue - time).TotalSeconds;
                if (seconds <= minAddSeconds) return DateTime.MinValue;
            }
            return time.AddSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return seconds > 0 ? DateTime.MaxValue : DateTime.MinValue;
        }
    }
}
