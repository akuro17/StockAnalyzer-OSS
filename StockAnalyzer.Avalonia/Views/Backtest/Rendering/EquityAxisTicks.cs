using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;

namespace StockAnalyzer.Avalonia.Views.Backtest.Rendering;

/// <summary>A Y-axis tick: the currency value, its [0,1] fraction inside the Y range it was generated for, and its label.</summary>
public readonly record struct EquityYTick(decimal Value, double Fraction, string Label);

/// <summary>An X-axis tick: a UTC instant in ticks (the unit of the chart viewport) and its label.</summary>
public readonly record struct EquityXTick(long Ticks, string Label);

/// <summary>
/// Pure axis tick generation for the equity chart (Avalonia-free). Y values stay in <c>decimal</c> and in currency units whatever the
/// zoom: zooming only changes the step and the number of label decimals, never the unit. X ticks are calendar-aligned UTC instants;
/// label formats follow the main chart's date axis (<c>AxisRenderer.GetDateLabel</c>: HH:mm, MM/dd, MMM / yyyy, yyyy).
/// </summary>
public static class EquityAxisTicks
{
    private enum StepKind
    {
        Seconds,
        Minutes,
        Hours,
        Days,
        Months,
        Years,
    }

    private readonly record struct StepSpec(StepKind Kind, int Count);

    // The 1-2-5 family is the set of "round" steps. Fixed-length steps divide their parent unit (or, for days, are anchored on
    // DateTime ticks 0 = a Monday), so ticks land on clock/calendar boundaries.
    private static readonly int[] MantissaSteps = { 1, 2, 5 };

    // Month and year steps are the axis' normal unit (as on the main chart: Jan/Feb/Mar..., the year at January). Clock and day steps are
    // only the fallback for a visible range too short to hold two month ticks, so a zoom into a few weeks still has a scale.
    private static readonly StepSpec[] CalendarSteps = BuildCalendarSteps();
    private static readonly StepSpec[] ClockSteps = BuildClockSteps();

    /// <summary>A calendar step is only used when it puts at least this many ticks in view; a lone tick is no scale.</summary>
    private const int MinCalendarTickCount = 2;

    private static StepSpec[] BuildCalendarSteps()
    {
        var steps = new List<StepSpec>();
        AddSteps(steps, StepKind.Months, 1, 2, 3, 6);
        AddSteps(steps, StepKind.Years, 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000);
        return steps.ToArray();
    }

    private static StepSpec[] BuildClockSteps()
    {
        var steps = new List<StepSpec>();
        AddSteps(steps, StepKind.Seconds, 1, 2, 5, 10, 15, 30);
        AddSteps(steps, StepKind.Minutes, 1, 2, 5, 10, 15, 30);
        AddSteps(steps, StepKind.Hours, 1, 2, 3, 6, 12);
        AddSteps(steps, StepKind.Days, 1, 2, 7, 14);
        return steps.ToArray();
    }

    private static void AddSteps(List<StepSpec> steps, StepKind kind, params int[] counts)
    {
        foreach (int count in counts) steps.Add(new StepSpec(kind, count));
    }

    /// <summary>
    /// Round Y ticks (steps of 1, 2 or 5 times a power of ten) inside [<paramref name="yMin"/>, <paramref name="yMax"/>], at most
    /// <paramref name="maxCount"/> of them. Empty for an empty/invalid range, <paramref name="maxCount"/> &lt; 1 or a decimal overflow.
    /// Label decimals follow the step (step 0.5 -> 1 decimal, step 100 -> none).
    /// </summary>
    public static ImmutableArray<EquityYTick> YTicks(decimal yMin, decimal yMax, int maxCount)
    {
        if (maxCount < 1 || yMax <= yMin)
        {
            return ImmutableArray<EquityYTick>.Empty;
        }

        try
        {
            decimal range = yMax - yMin;
            decimal rawStep = range / Math.Max(1, maxCount - 1);
            (int mantissaIndex, int exponent) = FirstStepAtLeast(rawStep);
            // The step only has to be at least rawStep to bound the count; a grid-aligned range can still put one tick more, so grow until it fits.
            for (int attempt = 0; attempt < MantissaSteps.Length * 2; attempt++)
            {
                decimal step = MantissaSteps[mantissaIndex] * Pow10(exponent);
                ImmutableArray<EquityYTick> ticks = BuildYTicks(yMin, yMax, range, step, Math.Max(0, -exponent));
                if (ticks.Length > maxCount)
                {
                    (mantissaIndex, exponent) = NextStep(mantissaIndex, exponent);
                    continue;
                }
                if (ticks.IsEmpty)
                {
                    // The range sits between two multiples of the step: a finer round step puts a tick inside.
                    return FinerStepWithATick(yMin, yMax, range, maxCount, mantissaIndex, exponent);
                }
                return ticks;
            }
        }
        catch (OverflowException)
        {
        }

        return ImmutableArray<EquityYTick>.Empty;
    }

    private static ImmutableArray<EquityYTick> FinerStepWithATick(
        decimal yMin, decimal yMax, decimal range, int maxCount, int mantissaIndex, int exponent)
    {
        for (int attempt = 0; attempt < MantissaSteps.Length; attempt++)
        {
            (mantissaIndex, exponent) = mantissaIndex > 0 ? (mantissaIndex - 1, exponent) : (MantissaSteps.Length - 1, exponent - 1);
            decimal step = MantissaSteps[mantissaIndex] * Pow10(exponent);
            ImmutableArray<EquityYTick> ticks = BuildYTicks(yMin, yMax, range, step, Math.Max(0, -exponent));
            if (ticks.Length > maxCount)
            {
                break;
            }
            if (!ticks.IsEmpty)
            {
                return ticks;
            }
        }
        return ImmutableArray<EquityYTick>.Empty;
    }

    private static ImmutableArray<EquityYTick> BuildYTicks(
        decimal yMin, decimal yMax, decimal range, decimal step, int decimals)
    {
        string format = "F" + decimals.ToString(CultureInfo.InvariantCulture);
        ImmutableArray<EquityYTick>.Builder builder = ImmutableArray.CreateBuilder<EquityYTick>();
        decimal value = Math.Ceiling(yMin / step) * step;
        while (value <= yMax)
        {
            decimal shown = value == 0m ? 0m : value;
            builder.Add(new EquityYTick(shown, (double)(shown - yMin) / (double)range, shown.ToString(format, EquityLabelFormats.Culture)));
            value += step;
        }
        return builder.ToImmutable();
    }

    /// <summary>The smallest 1-2-5 step (as mantissa index and power of ten) that is at least <paramref name="raw"/> (raw &gt; 0).</summary>
    private static (int MantissaIndex, int Exponent) FirstStepAtLeast(decimal raw)
    {
        int exponent = 0;
        // Bring 10^exponent to the decade of raw. decimal holds 28-29 significant digits, so the exponent range is bounded.
        while (Pow10(exponent) > raw && exponent > -MaxDecimalScale) exponent--;
        while (exponent < MaxDecimalScale && Pow10(exponent + 1) <= raw) exponent++;

        int mantissaIndex = 0;
        while (MantissaSteps[mantissaIndex] * Pow10(exponent) < raw)
        {
            (mantissaIndex, exponent) = NextStep(mantissaIndex, exponent);
        }
        return (mantissaIndex, exponent);
    }

    private static (int MantissaIndex, int Exponent) NextStep(int mantissaIndex, int exponent) =>
        mantissaIndex + 1 < MantissaSteps.Length ? (mantissaIndex + 1, exponent) : (0, exponent + 1);

    private const int MaxDecimalScale = 28;

    private static decimal Pow10(int exponent)
    {
        decimal result = 1m;
        for (int i = 0; i < Math.Abs(exponent); i++)
        {
            result = exponent > 0 ? result * 10m : result / 10m;
        }
        return result;
    }

    /// <summary>
    /// Calendar-aligned X ticks inside [<paramref name="startTicks"/>, <paramref name="endTicks"/>] (UTC ticks): the smallest step whose
    /// labels, measured by <paramref name="measureLabelWidth"/>, keep at least <paramref name="minGap"/> between neighbours over a plot of
    /// <paramref name="plotWidth"/> DIP. Month (then year) steps are tried first and need two ticks in view; only when none fits does the
    /// axis fall back to day/hour/minute/second steps. Empty for an empty range/width or when no step has a tick inside.
    /// </summary>
    public static ImmutableArray<EquityXTick> XTicks(
        long startTicks, long endTicks, double plotWidth, Func<string, double> measureLabelWidth, double minGap)
    {
        ArgumentNullException.ThrowIfNull(measureLabelWidth);
        if (endTicks <= startTicks || !double.IsFinite(plotWidth) || plotWidth <= 0d)
        {
            return ImmutableArray<EquityXTick>.Empty;
        }

        // Each label needs minGap of its own, so no step with more ticks than this can ever fit; skip it without generating.
        double tickLimit = (plotWidth / Math.Max(minGap, double.Epsilon)) + 1d;
        ImmutableArray<EquityXTick> calendar = FirstFittingStep(
            CalendarSteps, MinCalendarTickCount, startTicks, endTicks, plotWidth, measureLabelWidth, minGap, tickLimit);
        return calendar.IsEmpty
            ? FirstFittingStep(ClockSteps, 1, startTicks, endTicks, plotWidth, measureLabelWidth, minGap, tickLimit)
            : calendar;
    }

    private static ImmutableArray<EquityXTick> FirstFittingStep(
        StepSpec[] steps, int minTickCount, long startTicks, long endTicks, double plotWidth,
        Func<string, double> measureLabelWidth, double minGap, double tickLimit)
    {
        foreach (StepSpec step in steps)
        {
            List<long>? instants = GenerateInstants(step, startTicks, endTicks, tickLimit);
            if (instants is null || instants.Count < minTickCount)
            {
                continue;
            }

            ImmutableArray<EquityXTick> ticks = LabelInstants(step, instants);
            if (LabelsFit(ticks, startTicks, endTicks, plotWidth, measureLabelWidth, minGap))
            {
                return ticks;
            }
        }
        return ImmutableArray<EquityXTick>.Empty;
    }

    /// <summary>Instants of one step inside the range, or null when the step would produce more than <paramref name="tickLimit"/> ticks.</summary>
    private static List<long>? GenerateInstants(StepSpec step, long startTicks, long endTicks, double tickLimit)
    {
        var instants = new List<long>();
        switch (step.Kind)
        {
            case StepKind.Seconds:
            case StepKind.Minutes:
            case StepKind.Hours:
            case StepKind.Days:
            {
                long unit = FixedUnitTicks(step);
                long first = ((startTicks + unit - 1) / unit) * unit;
                if (first > endTicks) return instants;
                if (((endTicks - first) / (double)unit) + 1d > tickLimit) return null;
                for (long t = first; t <= endTicks; t += unit) instants.Add(t);
                return instants;
            }
            case StepKind.Months:
                return GenerateCalendar(step.Count, startTicks, endTicks, tickLimit, monthsPerUnit: 1);
            default:
                return GenerateCalendar(step.Count, startTicks, endTicks, tickLimit, monthsPerUnit: 12);
        }
    }

    /// <summary>Month/year ticks on the 1st of a month whose month index (year*12 + month-1) is a multiple of count*monthsPerUnit (years: January of a year divisible by count).</summary>
    private static List<long>? GenerateCalendar(int count, long startTicks, long endTicks, double tickLimit, int monthsPerUnit)
    {
        var instants = new List<long>();
        int stepMonths = count * monthsPerUnit;
        DateTime start = new(startTicks, DateTimeKind.Utc);
        long monthIndex = ((long)start.Year * 12) + (start.Month - 1);
        if (MonthStart(monthIndex).Ticks < startTicks) monthIndex++;
        monthIndex = ((monthIndex + stepMonths - 1) / stepMonths) * stepMonths;

        while (true)
        {
            long year = monthIndex / 12;
            if (year > DateTime.MaxValue.Year) break;
            if (year >= DateTime.MinValue.Year)
            {
                long ticks = MonthStart(monthIndex).Ticks;
                if (ticks > endTicks) break;
                if (instants.Count + 1 > tickLimit) return null;
                instants.Add(ticks);
            }
            monthIndex += stepMonths;
        }
        return instants;
    }

    private static DateTime MonthStart(long monthIndex) =>
        new((int)(monthIndex / 12), (int)(monthIndex % 12) + 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static long FixedUnitTicks(StepSpec step) => step.Kind switch
    {
        StepKind.Seconds => TimeSpan.TicksPerSecond * step.Count,
        StepKind.Minutes => TimeSpan.TicksPerMinute * step.Count,
        StepKind.Hours => TimeSpan.TicksPerHour * step.Count,
        _ => TimeSpan.TicksPerDay * step.Count,
    };

    private static ImmutableArray<EquityXTick> LabelInstants(StepSpec step, List<long> instants)
    {
        ImmutableArray<EquityXTick>.Builder builder = ImmutableArray.CreateBuilder<EquityXTick>(instants.Count);
        foreach (long ticks in instants)
        {
            var instant = new DateTime(ticks, DateTimeKind.Utc);
            builder.Add(new EquityXTick(ticks, FormatLabel(step.Kind, instant)));
        }
        return builder.MoveToImmutable();
    }

    private static string FormatLabel(StepKind kind, DateTime instant)
    {
        CultureInfo culture = EquityLabelFormats.Culture;
        bool atMidnight = instant.TimeOfDay == TimeSpan.Zero;
        return kind switch
        {
            StepKind.Seconds => atMidnight
                ? instant.ToString(EquityLabelFormats.MonthDay, culture)
                : instant.ToString(EquityLabelFormats.ClockSeconds, culture),
            StepKind.Minutes or StepKind.Hours => atMidnight
                ? instant.ToString(EquityLabelFormats.MonthDay, culture)
                : instant.ToString(EquityLabelFormats.ClockMinutes, culture),
            StepKind.Days => instant.ToString(EquityLabelFormats.MonthDay, culture),
            // Same split as the main chart's date axis: the year at a year change, the month name otherwise.
            StepKind.Months => instant.Month == 1
                ? instant.ToString(EquityLabelFormats.Year, culture)
                : instant.ToString(EquityLabelFormats.MonthName, culture),
            _ => instant.ToString(EquityLabelFormats.Year, culture),
        };
    }

    private static bool LabelsFit(
        ImmutableArray<EquityXTick> ticks, long startTicks, long endTicks, double plotWidth, Func<string, double> measureLabelWidth, double minGap)
    {
        double span = endTicks - startTicks;
        double previousRight = double.NegativeInfinity;
        foreach (EquityXTick tick in ticks)
        {
            double width = measureLabelWidth(tick.Label);
            double center = (tick.Ticks - startTicks) / span * plotWidth;
            double left = center - (width / 2d);
            if (left < previousRight + minGap)
            {
                return false;
            }
            previousRight = center + (width / 2d);
        }
        return true;
    }
}
