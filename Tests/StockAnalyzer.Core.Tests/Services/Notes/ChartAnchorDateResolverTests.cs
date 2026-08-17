using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services.Notes;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services.Notes;

public class ChartAnchorDateResolverTests
{
    private static CandleData Candle(DateTime date) => new(date, 100m, 101m, 99m, 100m, 1000);

    [Fact]
    public void Resolve_EmptyCandles_ReturnsNull()
    {
        var result = ChartAnchorDateResolver.Resolve(Array.Empty<CandleData>(), new DateTime(2026, 8, 12), TimeFrame.D1);

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_Daily_ExactMatch_ReturnsSameIndex()
    {
        var candles = new List<CandleData>
        {
            Candle(new DateTime(2026, 8, 3)),  // Mon
            Candle(new DateTime(2026, 8, 4)),  // Tue
            Candle(new DateTime(2026, 8, 5)),  // Wed
            Candle(new DateTime(2026, 8, 6)),  // Thu
            Candle(new DateTime(2026, 8, 7)),  // Fri
        };

        var index = ChartAnchorDateResolver.Resolve(candles, new DateTime(2026, 8, 5), TimeFrame.D1);

        Assert.Equal(2, index);
    }

    [Fact]
    public void Resolve_Daily_HolidayDate_FallsBackToNearestPastTradingDay()
    {
        var candles = new List<CandleData>
        {
            Candle(new DateTime(2026, 8, 3)),
            Candle(new DateTime(2026, 8, 4)),
            Candle(new DateTime(2026, 8, 5)),
            Candle(new DateTime(2026, 8, 6)),
            Candle(new DateTime(2026, 8, 7)),  // Fri
            // Aug 8-9 is a weekend (no candle); Aug 11 is a market holiday (skipped below)
            Candle(new DateTime(2026, 8, 10)), // Mon
            Candle(new DateTime(2026, 8, 12)), // Wed - Aug 11 (Tue) was a holiday
        };

        // Saturday - no trading day exists for it.
        var index = ChartAnchorDateResolver.Resolve(candles, new DateTime(2026, 8, 8), TimeFrame.D1);
        Assert.Equal(4, index); // Aug 7 (Fri)

        // A specific mid-week holiday with trading days both before and after it.
        var holidayIndex = ChartAnchorDateResolver.Resolve(candles, new DateTime(2026, 8, 11), TimeFrame.D1);
        Assert.Equal(5, holidayIndex); // Aug 10 (Mon), nearest past trading day
    }

    [Fact]
    public void Resolve_Daily_DateBeforeAllData_FallsBackToNearestFutureTradingDay()
    {
        var candles = new List<CandleData>
        {
            Candle(new DateTime(2026, 8, 3)),
            Candle(new DateTime(2026, 8, 4)),
            Candle(new DateTime(2026, 8, 5)),
        };

        // A date before the ticker's listing / before any loaded data.
        var index = ChartAnchorDateResolver.Resolve(candles, new DateTime(2020, 1, 1), TimeFrame.D1);

        Assert.Equal(0, index); // Aug 3 - nearest future trading day
    }

    [Fact]
    public void Resolve_Daily_DateAfterAllData_FallsBackToNearestPastTradingDay()
    {
        var candles = new List<CandleData>
        {
            Candle(new DateTime(2026, 8, 3)),
            Candle(new DateTime(2026, 8, 4)),
            Candle(new DateTime(2026, 8, 5)),
        };

        var index = ChartAnchorDateResolver.Resolve(candles, new DateTime(2030, 1, 1), TimeFrame.D1);

        Assert.Equal(2, index); // Aug 5 - nearest past trading day
    }

    [Fact]
    public void Resolve_Weekly_AnchorWithinWeek_MatchesWeeklyCandleEvenWhenMondayWasAHoliday()
    {
        // Weekly candles are timestamped at their period's first actual trading day
        // (TimeFrameAggregator.AggregateDailyToWeekly). Here Monday Aug 3 was a holiday, so the
        // first trading day of that ISO week is Tuesday Aug 4.
        var candles = new List<CandleData>
        {
            Candle(new DateTime(2026, 7, 27)), // previous week (Monday)
            Candle(new DateTime(2026, 8, 4)),  // this week's first trading day (Tuesday)
            Candle(new DateTime(2026, 8, 10)), // next week (Monday)
        };

        // Anchor date is Wednesday within the same ISO week as the Aug 4 candle.
        var index = ChartAnchorDateResolver.Resolve(candles, new DateTime(2026, 8, 5), TimeFrame.W1);

        Assert.Equal(1, index);
    }

    [Fact]
    public void Resolve_Weekly_AnchorOnSunday_RoundsIntoTheContainingIsoWeek()
    {
        var candles = new List<CandleData>
        {
            Candle(new DateTime(2026, 8, 3)),  // Monday - start of the week containing Aug 9 (Sunday)
            Candle(new DateTime(2026, 8, 10)),
        };

        // Sunday Aug 9 belongs to the ISO week starting Monday Aug 3.
        var index = ChartAnchorDateResolver.Resolve(candles, new DateTime(2026, 8, 9), TimeFrame.W1);

        Assert.Equal(0, index);
    }

    [Fact]
    public void Resolve_Monthly_AnchorWithinMonth_MatchesMonthlyCandleEvenWhenFirstOfMonthWasAHoliday()
    {
        // August 1, 2026 is a Saturday; the month's first trading day is Aug 3 (Monday).
        var candles = new List<CandleData>
        {
            Candle(new DateTime(2026, 7, 1)),
            Candle(new DateTime(2026, 8, 3)), // first trading day of August
            Candle(new DateTime(2026, 9, 1)),
        };

        var index = ChartAnchorDateResolver.Resolve(candles, new DateTime(2026, 8, 20), TimeFrame.MN1);

        Assert.Equal(1, index);
    }

    [Fact]
    public void Resolve_Monthly_MonthWithNoData_FallsBackToNearestPastMonth()
    {
        var candles = new List<CandleData>
        {
            Candle(new DateTime(2026, 6, 1)),
            Candle(new DateTime(2026, 7, 1)),
            // No candle exists for August at all.
            Candle(new DateTime(2026, 9, 1)),
        };

        var index = ChartAnchorDateResolver.Resolve(candles, new DateTime(2026, 8, 15), TimeFrame.MN1);

        Assert.Equal(1, index); // July - nearest past period
    }
}
