using System;
using System.Runtime.CompilerServices;
using StockAnalyzer.Core.Drawing;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Provides unified mathematical, geometric, and defensive date utilities for drawing tools.
/// The pure price/time math is defined once in <see cref="DrawingValueMath"/> (StockAnalyzer.Core) so that the GUI drawing objects
/// and the headless drawing-value calculators cannot drift apart; the members below keep their signatures and forward to it.
/// </summary>
public static class DrawingMath
{
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
        return DrawingValueMath.InterpolatePrice(t1, p1, t2, p2, timestamp);
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
        return DrawingValueMath.CalculateSlopePerDay(t1, p1, t2, p2);
    }

    /// <summary>
    /// Calculates the percentage change from startPrice to endPrice.
    /// Returns 0 if startPrice is zero.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static decimal CalculatePercentageChange(decimal startPrice, decimal endPrice)
    {
        return DrawingValueMath.CalculatePercentageChange(startPrice, endPrice);
    }

    /// <summary>
    /// Safely adds seconds to a DateTime without throwing ArgumentOutOfRangeException,
    /// clamping the result to DateTime.MinValue or DateTime.MaxValue.
    /// </summary>
    public static DateTime SafeAddSeconds(DateTime time, double seconds)
    {
        return DrawingValueMath.SafeAddSeconds(time, seconds);
    }

    /// <summary>
    /// Computes the screen X coordinate by projecting forward/backward a specified number of days (or bars)
    /// from an origin chart point, respecting gapless time-mapping when available.
    /// </summary>
    public static double GetScreenXFromOffsetDays(ChartPoint origin, double days, ICoordinateTransform transform)
    {
        if (transform == null) return 0;

        if (transform.TimeMap != null && transform.TimeMap.Count > 0 && transform is GenericCoordinateTransform gct)
        {
            double originIdx = gct.GetFractionalIndex(origin.Time);
            double targetIdx = originIdx + days;
            return transform.GetXFromIndex(targetIdx);
        }

        DateTime targetTime = SafeAddSeconds(origin.Time, days * 86400.0);
        return transform.ChartToScreen(new ChartPoint(targetTime, origin.Price)).X;
    }
}
