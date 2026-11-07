using System;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// <see cref="TimeFrameAggregator.ReferenceDate"/>: the date the bars are known up to (the as-of date, never later than the last stored daily bar). It is the one definition behind the
/// partial-period flag of sa_get_candles and of the Screener bar service, so the table below is the rule those two applied before it existed.
/// </summary>
public sealed class TimeFrameAggregatorReferenceDateTests
{
    private static readonly DateTime LastStored = new(2026, 10, 30); // a Friday

    [Theory]
    [InlineData(null, "2026-10-30")]          // no as-of date: the last stored day
    [InlineData("2026-10-28", "2026-10-28")]  // as-of before the last stored day: the as-of date
    [InlineData("2026-10-30", "2026-10-30")]  // the same day
    [InlineData("2026-10-31", "2026-10-30")]  // after: never later than the last stored day (a Saturday does not make the week complete)
    [InlineData("2030-01-01", "2026-10-30")]
    public void IsTheAsOfDateButNeverLaterThanTheLastStoredDay(string? asOf, string expected)
    {
        DateOnly? asOfDate = asOf is null ? null : DateOnly.Parse(asOf, System.Globalization.CultureInfo.InvariantCulture);

        DateTime reference = TimeFrameAggregator.ReferenceDate(LastStored, asOfDate);

        Assert.Equal(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), reference);
    }

    [Fact]
    public void TheTimeOfTheLastStoredBarIsDropped()
    {
        DateTime reference = TimeFrameAggregator.ReferenceDate(new DateTime(2026, 10, 30, 15, 30, 0), null);

        Assert.Equal(new DateTime(2026, 10, 30), reference);
        Assert.Equal(TimeSpan.Zero, reference.TimeOfDay);
    }

    [Fact]
    public void AnAsOfDateWithTheTimeOfTheLastBarStillCountsAsTheSameDay()
    {
        DateTime reference = TimeFrameAggregator.ReferenceDate(new DateTime(2026, 10, 30, 15, 30, 0), new DateOnly(2026, 10, 30));

        Assert.Equal(new DateTime(2026, 10, 30), reference);
    }

    [Theory]
    [InlineData("2026-10-30", TimeFrame.W1, true)]   // the week ends Sunday 2026-11-01: a Friday reference leaves it partial (SA_AI_INTEGRATION: partial_period)
    [InlineData("2026-11-01", TimeFrame.W1, false)]
    [InlineData("2026-10-30", TimeFrame.MN1, true)]  // the month ends 2026-10-31
    [InlineData("2026-10-31", TimeFrame.MN1, false)]
    public void FeedsIsPartialPeriodAsTheReferenceDate(string referenceText, TimeFrame frame, bool expectedPartial)
    {
        DateTime barTimestamp = new(2026, 10, 26); // first trading day of the week of 2026-10-26; in the month of October
        DateTime lastStored = DateTime.Parse(referenceText, System.Globalization.CultureInfo.InvariantCulture);

        DateTime reference = TimeFrameAggregator.ReferenceDate(lastStored, null);

        Assert.Equal(expectedPartial, TimeFrameAggregator.IsPartialPeriod(barTimestamp, frame, reference));
    }
}
