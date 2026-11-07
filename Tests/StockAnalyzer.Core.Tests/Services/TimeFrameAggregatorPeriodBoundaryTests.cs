using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// Characterization of how daily bars are grouped into W1 and MN1 bars at the boundaries the callers rely on (MCP Part 1 external review, T5): a week is the ISO week (so it can hold days of two
/// calendar years and of two months), a month is the calendar month, and the timestamp of a bar is the first day of the period that has a stored bar.
/// </summary>
public sealed class TimeFrameAggregatorPeriodBoundaryTests
{
    /// <summary>One bar per listed day: Open = 100 + day index, High = Open + 5, Low = Open - 5, Close = Open + 1, Volume = 1000 + day index.</summary>
    private static List<CandleData> Days(params string[] dates)
        => dates.Select((date, index) => new CandleData(DateTime.Parse(date, System.Globalization.CultureInfo.InvariantCulture), 100m + index, 105m + index, 95m + index, 101m + index, 1000L + index)).ToList();

    private static List<string> Range(string first, string last)
    {
        var days = new List<string>();
        for (DateTime day = DateTime.Parse(first, System.Globalization.CultureInfo.InvariantCulture); day <= DateTime.Parse(last, System.Globalization.CultureInfo.InvariantCulture); day = day.AddDays(1))
        {
            days.Add(day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        }

        return days;
    }

    [Fact]
    public void AnIsoWeekAcrossTheYearBoundaryIsOneWeeklyBarStampedWithItsFirstDay()
    {
        // Monday 2025-12-29 .. Friday 2026-01-02 is ISO week 1 of 2026 (week-year 2026): one bar, not one per calendar year.
        List<CandleData> daily = Days("2025-12-29", "2025-12-30", "2025-12-31", "2026-01-01", "2026-01-02");

        IReadOnlyList<CandleData> weekly = TimeFrameAggregator.AggregateDailyToWeekly(daily);

        CandleData bar = Assert.Single(weekly);
        Assert.Equal(new DateTime(2025, 12, 29), bar.Timestamp);
        Assert.Equal(100m, bar.Open);                    // the Open of 2025-12-29
        Assert.Equal(109m, bar.High);                    // the highest High (index 4: 105 + 4)
        Assert.Equal(95m, bar.Low);                      // the lowest Low (index 0)
        Assert.Equal(105m, bar.Close);                   // the Close of 2026-01-02 (101 + 4)
        Assert.Equal(1000L + 1001L + 1002L + 1003L + 1004L, bar.Volume);
    }

    [Fact]
    public void TheSameDaysAreTwoMonthlyBarsAtTheCalendarMonthBoundary()
    {
        List<CandleData> daily = Days("2025-12-29", "2025-12-30", "2025-12-31", "2026-01-01", "2026-01-02");

        IReadOnlyList<CandleData> monthly = TimeFrameAggregator.AggregateDailyToMonthly(daily);

        Assert.Equal(new[] { new DateTime(2025, 12, 29), new DateTime(2026, 1, 1) }, monthly.Select(bar => bar.Timestamp).ToArray());
        Assert.Equal(1000L + 1001L + 1002L, monthly[0].Volume);
        Assert.Equal(1003L + 1004L, monthly[1].Volume);
    }

    [Fact]
    public void TheFiftyThirdIsoWeekHoldsTheNewYearDaysAndTheNextMondayStartsANewBar()
    {
        // 2020 has 53 ISO weeks: Monday 2020-12-28 .. Sunday 2021-01-03 is one week, Monday 2021-01-04 begins week 1 of 2021.
        List<CandleData> daily = Days(Range("2020-12-28", "2021-01-04").ToArray());

        IReadOnlyList<CandleData> weekly = TimeFrameAggregator.AggregateDailyToWeekly(daily);

        Assert.Equal(new[] { new DateTime(2020, 12, 28), new DateTime(2021, 1, 4) }, weekly.Select(bar => bar.Timestamp).ToArray());
        Assert.Equal(Enumerable.Range(0, 7).Sum(i => 1000L + i), weekly[0].Volume);
        Assert.Equal(1007L, weekly[1].Volume);
    }

    [Fact]
    public void TheTimestampIsTheFirstStoredDayOfThePeriodNotTheFirstCalendarDay()
    {
        // The week of Monday 2026-01-05 has no bar on Monday (a holiday): the weekly bar is stamped Tuesday 2026-01-06. March 2026 starts at the stored Tuesday 2026-03-03.
        IReadOnlyList<CandleData> weekly = TimeFrameAggregator.AggregateDailyToWeekly(Days("2026-01-06", "2026-01-07", "2026-01-08", "2026-01-09"));
        IReadOnlyList<CandleData> monthly = TimeFrameAggregator.AggregateDailyToMonthly(Days("2026-03-03", "2026-03-04", "2026-03-31"));

        Assert.Equal(new DateTime(2026, 1, 6), Assert.Single(weekly).Timestamp);
        Assert.Equal(new DateTime(2026, 3, 3), Assert.Single(monthly).Timestamp);
    }

    [Theory]
    [InlineData("2025-12-29", TimeFrame.W1, "2026-01-04")]  // the Sunday of the week that crosses the year
    [InlineData("2025-12-29", TimeFrame.MN1, "2025-12-31")]
    [InlineData("2020-12-28", TimeFrame.W1, "2021-01-03")]  // the 53rd ISO week of 2020
    public void ThePeriodEndOfTheFirstStoredDayOfAPeriodThatCrossesAYearIsTheCalendarEndOfThatPeriod(string barDate, TimeFrame frame, string expectedEnd)
    {
        Assert.Equal(DateTime.Parse(expectedEnd, System.Globalization.CultureInfo.InvariantCulture), TimeFrameAggregator.PeriodEnd(DateTime.Parse(barDate, System.Globalization.CultureInfo.InvariantCulture), frame));
    }
}
