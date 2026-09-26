using System;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

/// <summary>
/// Pure-function proof of the causal forward-fill rule (plan section 3.2.3 of
/// Y:\Temp\sa_implementation_plan_BacktestComparisonSignals.md): a still-forming higher-timeframe
/// period's own value must never be visible to a base bar inside that same period, only once the NEXT
/// period has started (i.e. that period has fully closed). This is called out in the plan as "the
/// single most important test in this entire feature."
/// </summary>
public class TimeframeAlignmentTests
{
    private static DateTime D(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static CandleData Bar(DateTime timestamp) => new(timestamp, 1m, 1m, 1m, 1m, 1);

    [Fact]
    public void WeeklyValue_OnlyChanges_OnBarAfterThatWeekFullyCloses()
    {
        // Weekly bars are period-START stamped (confirmed against real on-disk data): week 1 starts
        // Mon 2026-01-05, week 2 starts Mon 2026-01-12, week 3 starts Mon 2026-01-19.
        var weeklyBars = ImmutableArray.Create(
            Bar(D(2026, 1, 5)),
            Bar(D(2026, 1, 12)),
            Bar(D(2026, 1, 19)));
        var weeklyValues = ImmutableArray.Create<decimal?>(10m, 20m, 30m);

        // Daily bars spanning week 1 and into week 2.
        var dailyBars = ImmutableArray.Create(
            Bar(D(2026, 1, 5)),  // Mon, week 1 start
            Bar(D(2026, 1, 6)),  // Tue, still week 1 (still forming)
            Bar(D(2026, 1, 9)),  // Fri, still week 1 (still forming)
            Bar(D(2026, 1, 12)), // Mon, week 2 start -> week 1 has just closed
            Bar(D(2026, 1, 13))); // Tue, still week 2

        ImmutableArray<decimal?> aligned = TimeframeAlignment.Align(dailyBars, weeklyBars, weeklyValues);

        // No prior week has closed yet during week 1 itself -> null, never week 1's own (10m) forming value.
        Assert.Null(aligned[0]);
        Assert.Null(aligned[1]);
        Assert.Null(aligned[2]);
        // The first bar of week 2 is exactly when week 1's value (10m) becomes safe to use.
        Assert.Equal(10m, aligned[3]);
        Assert.Equal(10m, aligned[4]);
    }

    [Fact]
    public void MonthlyValue_OnlyChanges_OnBarAfterThatMonthFullyCloses()
    {
        // Same rule, no special-cased date arithmetic for Monthly (plan section 3.2.3).
        var monthlyBars = ImmutableArray.Create(
            Bar(D(2026, 1, 1)),
            Bar(D(2026, 2, 1)),
            Bar(D(2026, 3, 1)));
        var monthlyValues = ImmutableArray.Create<decimal?>(100m, 200m, 300m);

        var dailyBars = ImmutableArray.Create(
            Bar(D(2026, 1, 1)),  // Jan starts
            Bar(D(2026, 1, 30)), // still Jan (still forming)
            Bar(D(2026, 2, 1)),  // Feb starts -> Jan has just closed
            Bar(D(2026, 2, 15))); // still Feb

        ImmutableArray<decimal?> aligned = TimeframeAlignment.Align(dailyBars, monthlyBars, monthlyValues);

        Assert.Null(aligned[0]);
        Assert.Null(aligned[1]);
        Assert.Equal(100m, aligned[2]);
        Assert.Equal(100m, aligned[3]);
    }

    [Fact]
    public void BeforeFirstHigherTimeframePeriod_ReturnsNull()
    {
        var weeklyBars = ImmutableArray.Create(Bar(D(2026, 1, 5)));
        var weeklyValues = ImmutableArray.Create<decimal?>(10m);
        var dailyBars = ImmutableArray.Create(Bar(D(2026, 1, 1))); // before the first weekly period even starts

        ImmutableArray<decimal?> aligned = TimeframeAlignment.Align(dailyBars, weeklyBars, weeklyValues);

        Assert.Null(aligned[0]);
    }

    [Fact]
    public void NullHigherTimeframeValue_PropagatesAsNull_NotThrown()
    {
        // A higher-timeframe indicator's own warmup period (e.g. SMA(20) on only 5 weekly bars) is
        // itself null - alignment must forward-fill that null, not crash or invent a value.
        var weeklyBars = ImmutableArray.Create(Bar(D(2026, 1, 5)), Bar(D(2026, 1, 12)));
        var weeklyValues = ImmutableArray.Create<decimal?>(null, 20m);
        var dailyBars = ImmutableArray.Create(Bar(D(2026, 1, 12)));

        ImmutableArray<decimal?> aligned = TimeframeAlignment.Align(dailyBars, weeklyBars, weeklyValues);

        Assert.Null(aligned[0]);
    }

    [Fact]
    public void MismatchedLengths_Throws()
    {
        var weeklyBars = ImmutableArray.Create(Bar(D(2026, 1, 5)), Bar(D(2026, 1, 12)));
        var weeklyValues = ImmutableArray.Create<decimal?>(10m);
        var dailyBars = ImmutableArray.Create(Bar(D(2026, 1, 5)));

        Assert.Throws<ArgumentException>(() => TimeframeAlignment.Align(dailyBars, weeklyBars, weeklyValues));
    }
}
