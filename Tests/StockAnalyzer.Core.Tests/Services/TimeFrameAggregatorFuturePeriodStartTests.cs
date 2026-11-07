using System;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// The date of a projected W1 or MN1 point is the calendar start of the future period (MCP Part 3 review, T9): a future period's first trading day is unknown,
/// and a fixed step of 7 or 30 days drifts off the calendar.
/// </summary>
public sealed class TimeFrameAggregatorFuturePeriodStartTests
{
    [Theory]
    [InlineData("2024-01-16", 0, "2024-01-15")] // the week is labelled Tuesday after a Monday holiday; its start is the Monday
    [InlineData("2024-01-16", 1, "2024-01-22")]
    [InlineData("2024-01-16", 2, "2024-01-29")]
    [InlineData("2024-12-30", 1, "2025-01-06")] // the week of 2024-12-30 holds days of two calendar years
    [InlineData("2024-02-23", 2, "2024-03-04")] // across a leap day
    public void W1_IsTheMondayOfTheLastBarsWeekPlusSevenDaysPerPeriod(string lastBar, int offset, string expected)
        => Assert.Equal(DateTime.Parse(expected), TimeFrameAggregator.FuturePeriodStart(DateTime.Parse(lastBar), TimeFrame.W1, offset));

    [Theory]
    [InlineData("2024-06-03", 0, "2024-06-01")] // the first trading day of June 2024 is Monday the 3rd; the period starts on the 1st
    [InlineData("2024-06-03", 1, "2024-07-01")]
    [InlineData("2024-06-03", 7, "2025-01-01")]
    [InlineData("2024-01-31", 1, "2024-02-01")] // a month-end label must not make AddMonths clamp to the 29th
    [InlineData("2024-01-31", 13, "2025-02-01")]
    [InlineData("2023-12-04", 2, "2024-02-01")]
    public void MN1_IsTheFirstOfTheMonthPlusOneMonthPerPeriod(string lastBar, int offset, string expected)
        => Assert.Equal(DateTime.Parse(expected), TimeFrameAggregator.FuturePeriodStart(DateTime.Parse(lastBar), TimeFrame.MN1, offset));

    [Fact]
    public void FrameWithoutPeriodStart_IsRefused()
        => Assert.Throws<NotSupportedException>(() => TimeFrameAggregator.FuturePeriodStart(new DateTime(2024, 1, 2), TimeFrame.D1, 1));

    [Fact]
    public void NegativeOffset_IsRefused()
        => Assert.Throws<ArgumentOutOfRangeException>(() => TimeFrameAggregator.FuturePeriodStart(new DateTime(2024, 1, 2), TimeFrame.W1, -1));

    [Theory]
    [InlineData(0.5, 0.0, 1e-8)] // flat reconstruction: the floor keeps the clustering from collapsing to zero width
    [InlineData(0.5, 4.0, 2.0)]
    [InlineData(2.0, 3.0, 6.0)]
    public void SsaSupportResistance_EffectiveClusterTolerance_IsTheMultiplierTimesSigmaWithAFloor(double multiplier, double sigma, double expected)
        => Assert.Equal(expected, SsaSupportResistanceEngine.EffectiveClusterTolerance(multiplier, sigma), precision: 12);
}
