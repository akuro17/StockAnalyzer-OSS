using System;
using System.Runtime.CompilerServices;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Provides unified mathematical, geometric, and defensive date utilities for drawing tools.
/// </summary>
public static class DrawingMath
{
    private const double Epsilon = 1e-4;

    /// <summary>
    /// Linearly interpolates or extrapolates price at the specified timestamp based on two chart points.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static decimal InterpolatePrice(ChartPoint p1, ChartPoint p2, DateTime timestamp)
    {
        return InterpolatePrice(p1.Time, p1.Price, p2.Time, p2.Price, timestamp);
    }

    /// <summary>
    /// Linearly interpolates or extrapolates price at the specified timestamp based on two time/price pairs.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static decimal InterpolatePrice(DateTime t1, decimal p1, DateTime t2, decimal p2, DateTime timestamp)
    {
        double dt = (t2 - t1).TotalSeconds;
        if (Math.Abs(dt) < Epsilon) return p1;

        double ratio = (timestamp - t1).TotalSeconds / dt;
        return p1 + (decimal)ratio * (p2 - p1);
    }

    /// <summary>
    /// Calculates the daily slope (price change per 24 hours) between two chart points.
    /// Returns null if the points have identical or nearly identical timestamps.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static decimal? CalculateSlopePerDay(ChartPoint p1, ChartPoint p2)
    {
        return CalculateSlopePerDay(p1.Time, p1.Price, p2.Time, p2.Price);
    }

    /// <summary>
    /// Calculates the daily slope (price change per 24 hours) between two time/price pairs.
    /// Returns null if the timestamps are identical or nearly identical.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static decimal? CalculateSlopePerDay(DateTime t1, decimal p1, DateTime t2, decimal p2)
    {
        double totalDays = (t2 - t1).TotalDays;
        if (Math.Abs(totalDays) < Epsilon) return null;

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
