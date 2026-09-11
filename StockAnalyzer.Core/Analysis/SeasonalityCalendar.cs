using System;
using StockAnalyzer.Core.MathUtils;

namespace StockAnalyzer.Core.Analysis;

/// <summary>
/// The single time-coordinate mapping shared by every part of the Seasonality Chart: each per-year
/// trace sample, the mean seasonal path, the month spokes, and the linear year overlay. It folds any
/// calendar date onto one fixed 366-slot ring so the <em>same calendar date lands at the same angle
/// in every overlaid year</em>, leap or not (decision D1 / D3, revised in SEASONALITY_CHART_SPEC.md
/// v1.3.0).
///
/// <para>
/// <see cref="CanonicalDayIndex"/> is <c>date.DayOfYear - 1</c>, then, in a non-leap year, every day
/// from 1 March on is shifted forward by one so that 29 February always occupies slot 59 and 1 March
/// always occupies slot 60. The result is 0..365:
/// 1 Jan → 0, 28 Feb → 58, 29 Feb → 59 (leap years only), 1 Mar → 60, 31 Dec → 365.
/// </para>
///
/// <para>
/// This replaces the earlier split where per-sample positions used a leap-aware 365/366 denominator
/// while the month spokes and the mean path used a fixed 365 basis — a mix that drifted the same
/// calendar date by up to ~1 day between years and let the mean path's 31 December bin wrap onto the
/// 1 January apex.
/// </para>
/// </summary>
public static class SeasonalityCalendar
{
    /// <summary>
    /// Slots on the canonical ring: one per calendar day including 29 February. Every overlaid year,
    /// the mean path and the month spokes divide the year by this same number, so the divisor never
    /// changes with the year's actual length. This is the single source of truth for the value 366 in
    /// the seasonality engines and renderers.
    /// </summary>
    public const int CanonicalDaysPerYear = 366;

    /// <summary>
    /// Zero-based canonical day-of-year for <paramref name="date"/>, in <c>[0, 365]</c>. A non-leap
    /// year has no slot 59 (29 February); every later day is shifted forward one so a given month/day
    /// keeps one fixed index across leap and non-leap years.
    /// </summary>
    public static int CanonicalDayIndex(DateTime date)
    {
        int index = date.DayOfYear - 1;
        if (!DateTime.IsLeapYear(date.Year) && date.Month >= 3)
        {
            index++;
        }

        return index;
    }

    /// <summary>
    /// Canonical day index in <c>[0, 365]</c> offset by <paramref name="startMonth"/> (1..12).
    /// When <paramref name="startMonth"/> is 1 (January), returns <see cref="CanonicalDayIndex(DateTime)"/>.
    /// For any other starting month, day 1 of that month maps to slot 0 and subsequent days advance cyclically.
    /// </summary>
    public static int CanonicalDayIndex(DateTime date, uint startMonth)
    {
        int rawIndex = CanonicalDayIndex(date);
        if (startMonth <= 1)
        {
            return rawIndex;
        }

        int baseIndex = CanonicalDayIndex(new DateTime(2001, (int)startMonth, 1));
        return (rawIndex - baseIndex + CanonicalDaysPerYear) % CanonicalDaysPerYear;
    }

    /// <summary>
    /// Year-progress fraction <c>u = CanonicalDayIndex / CanonicalDaysPerYear</c>, in <c>[0, 1)</c>.
    /// 1 January is 0; 31 December is <c>365 / 366</c> and never reaches 1, so it does not fold back
    /// onto the 1 January apex.
    /// </summary>
    public static double SeasonalPosition(DateTime date)
        => CanonicalDayIndex(date) / (double)CanonicalDaysPerYear;

    /// <summary>
    /// Year-progress fraction relative to <paramref name="startMonth"/> in <c>[0, 1)</c>.
    /// Day 1 of <paramref name="startMonth"/> is 0.
    /// </summary>
    public static double SeasonalPosition(DateTime date, uint startMonth)
        => CanonicalDayIndex(date, startMonth) / (double)CanonicalDaysPerYear;

    /// <summary>
    /// Plot angle for a year-progress fraction: <c>theta = pi/2 - 2*pi*u</c>. 1 January (<c>u = 0</c>)
    /// is the 12 o'clock apex and the angle advances clockwise. Shared by the per-sample position, the
    /// mean path and the polar month spokes so the three cannot drift apart.
    /// </summary>
    public static double PlotAngleRadians(double seasonalPosition)
        => (Math.PI / 2d) - (MathConstants.TwoPi * seasonalPosition);
}
