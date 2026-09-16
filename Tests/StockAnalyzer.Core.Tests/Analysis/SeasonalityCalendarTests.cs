using System;
using StockAnalyzer.Core.Analysis;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

public class SeasonalityCalendarTests
{
    [Theory]
    [InlineData(2023)] // non-leap
    [InlineData(2024)] // leap
    public void CanonicalDayIndex_January1_IsZero(int year)
        => Assert.Equal(0, SeasonalityCalendar.CanonicalDayIndex(new DateTime(year, 1, 1)));

    [Theory]
    [InlineData(2023)]
    [InlineData(2024)]
    public void CanonicalDayIndex_February28_IsFiftyEight(int year)
        => Assert.Equal(58, SeasonalityCalendar.CanonicalDayIndex(new DateTime(year, 2, 28)));

    [Fact]
    public void CanonicalDayIndex_February29_IsFiftyNine_OnLeapYears()
        => Assert.Equal(59, SeasonalityCalendar.CanonicalDayIndex(new DateTime(2024, 2, 29)));

    [Fact]
    public void CanonicalDayIndex_March1_IsSixty_InBothLeapAndNonLeapYears()
    {
        Assert.Equal(60, SeasonalityCalendar.CanonicalDayIndex(new DateTime(2023, 3, 1)));
        Assert.Equal(60, SeasonalityCalendar.CanonicalDayIndex(new DateTime(2024, 3, 1)));
    }

    [Fact]
    public void CanonicalDayIndex_December31_Is365_InBothLeapAndNonLeapYears()
    {
        Assert.Equal(365, SeasonalityCalendar.CanonicalDayIndex(new DateTime(2023, 12, 31)));
        Assert.Equal(365, SeasonalityCalendar.CanonicalDayIndex(new DateTime(2024, 12, 31)));
    }

    [Theory]
    [InlineData(2023)] // non-leap: 365 days, no slot 59
    [InlineData(2024)] // leap: 366 days
    public void CanonicalDayIndex_StaysWithinZeroTo365_ForEveryDayOfTheYear(int year)
    {
        var day = new DateTime(year, 1, 1);
        var end = new DateTime(year, 12, 31);
        while (day <= end)
        {
            int index = SeasonalityCalendar.CanonicalDayIndex(day);
            Assert.InRange(index, 0, 365);
            day = day.AddDays(1);
        }
    }

    [Fact]
    public void CanonicalDayIndex_EveryCalendarDate_MatchesBetweenLeapAndNonLeapYears()
    {
        // 2023 (non-leap) vs 2024 (leap): the same month/day must map to the same canonical slot for
        // every date except 29 February, which exists only in the leap year.
        var nonLeapDay = new DateTime(2023, 1, 1);
        while (nonLeapDay.Year == 2023)
        {
            int leap = SeasonalityCalendar.CanonicalDayIndex(new DateTime(2024, nonLeapDay.Month, nonLeapDay.Day));
            int nonLeap = SeasonalityCalendar.CanonicalDayIndex(nonLeapDay);
            Assert.Equal(leap, nonLeap);
            nonLeapDay = nonLeapDay.AddDays(1);
        }
    }

    [Fact]
    public void SeasonalPosition_DividesTheCanonicalIndexBy366()
    {
        Assert.Equal(0d, SeasonalityCalendar.SeasonalPosition(new DateTime(2023, 1, 1)), 12);
        Assert.Equal(365d / 366d, SeasonalityCalendar.SeasonalPosition(new DateTime(2024, 12, 31)), 12);
        Assert.True(SeasonalityCalendar.SeasonalPosition(new DateTime(2023, 12, 31)) < 1d);
    }

    [Fact]
    public void PlotAngleRadians_PlacesTheYearStartAtTheTwelveOClockApexAndAdvancesClockwise()
    {
        Assert.Equal(Math.PI / 2d, SeasonalityCalendar.PlotAngleRadians(0d), 12);

        double previous = SeasonalityCalendar.PlotAngleRadians(0d);
        for (double u = 0.05d; u < 1d; u += 0.05d)
        {
            double angle = SeasonalityCalendar.PlotAngleRadians(u);
            Assert.True(angle < previous, $"angle at u={u} should decrease (clockwise)");
            previous = angle;
        }
    }

    [Fact]
    public void PlotAngleRadians_SameCalendarDate_MatchesBetweenLeapAndNonLeapYears()
    {
        double nonLeap = SeasonalityCalendar.PlotAngleRadians(SeasonalityCalendar.SeasonalPosition(new DateTime(2023, 3, 1)));
        double leap = SeasonalityCalendar.PlotAngleRadians(SeasonalityCalendar.SeasonalPosition(new DateTime(2024, 3, 1)));
        Assert.Equal(nonLeap, leap, 12);
    }

    [Fact]
    public void CanonicalDaysPerYear_Is366()
        => Assert.Equal(366, SeasonalityCalendar.CanonicalDaysPerYear);

    [Theory]
    [InlineData(2023)]
    [InlineData(2024)]
    public void CanonicalDayIndex_WithStartMonth4_April1_IsZero(int year)
    {
        Assert.Equal(0, SeasonalityCalendar.CanonicalDayIndex(new DateTime(year, 4, 1), 4));
        Assert.Equal(0d, SeasonalityCalendar.SeasonalPosition(new DateTime(year, 4, 1), 4), 12);
        Assert.Equal(Math.PI / 2d, SeasonalityCalendar.PlotAngleRadians(SeasonalityCalendar.SeasonalPosition(new DateTime(year, 4, 1), 4)), 12);
    }

    [Theory]
    [InlineData(2023)]
    [InlineData(2024)]
    public void CanonicalDayIndex_WithStartMonth4_March31_Is365(int year)
    {
        Assert.Equal(365, SeasonalityCalendar.CanonicalDayIndex(new DateTime(year, 3, 31), 4));
    }
}
