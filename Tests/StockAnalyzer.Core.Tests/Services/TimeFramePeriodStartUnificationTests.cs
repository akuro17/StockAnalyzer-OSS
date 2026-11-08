using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public sealed class TimeFramePeriodStartUnificationTests
{
    private static List<CandleData> Days(params string[] dates)
        => dates.Select((date, index) => new CandleData(
            DateTime.Parse(date, CultureInfo.InvariantCulture),
            100m + index,
            105m + index,
            95m + index,
            101m + index,
            1000L + index)).ToList();

    [Fact]
    public void AggregateToPeriodStart_WhenMondayIsHoliday_StampsMondayPeriodStart()
    {
        // 2024-01-15 (Monday) was MLK day in the US, first trading day is Tuesday 2024-01-16
        var daily = Days("2024-01-16", "2024-01-17", "2024-01-18", "2024-01-19");

        var weeklyLegacy = TimeFrameAggregator.Aggregate(daily, TimeFrame.W1);
        var weeklyUnified = TimeFrameAggregator.AggregateToPeriodStart(daily, TimeFrame.W1);

        // Legacy Aggregate stamps FirstTradingDay (Tuesday)
        Assert.Equal(new DateTime(2024, 1, 16), weeklyLegacy[0].Timestamp);

        // Unified AggregateToPeriodStart stamps PeriodStart (Monday)
        Assert.Equal(new DateTime(2024, 1, 15), weeklyUnified[0].Timestamp);
        Assert.Equal(100m, weeklyUnified[0].Open);
        Assert.Equal(104m, weeklyUnified[0].Close);
        Assert.Equal(daily.Sum(d => d.Volume), weeklyUnified[0].Volume);
    }

    [Fact]
    public void AggregateWithMetadata_WhenMondayIsHoliday_CarriesPeriodStartAndFirstTradingDay()
    {
        var daily = Days("2024-01-16", "2024-01-17", "2024-01-18", "2024-01-19");

        var weeklyWithMeta = TimeFrameAggregator.AggregateWithMetadata(daily, TimeFrame.W1);

        var bar = Assert.Single(weeklyWithMeta);
        Assert.Equal(new DateTime(2024, 1, 15), bar.PeriodStart);
        Assert.Equal(new DateTime(2024, 1, 15), bar.Candle.Timestamp);
        Assert.Equal(new DateTime(2024, 1, 16), bar.FirstTradingDay);
        Assert.Equal(new DateTime(2024, 1, 19), bar.LastTradingDay);
        Assert.Equal(4, bar.ConstituentDailyBarCount);
    }

    [Fact]
    public void AggregateToPeriodStart_WhenMonth1stIsHoliday_Stamps1stOfMonth()
    {
        // 2024-01-01 is New Year's Day (Monday), first trading day is Tuesday 2024-01-02
        var daily = Days("2024-01-02", "2024-01-03", "2024-01-04", "2024-01-05");

        var monthlyLegacy = TimeFrameAggregator.Aggregate(daily, TimeFrame.MN1);
        var monthlyUnified = TimeFrameAggregator.AggregateToPeriodStart(daily, TimeFrame.MN1);
        var monthlyMeta = TimeFrameAggregator.AggregateWithMetadata(daily, TimeFrame.MN1);

        // Legacy Aggregate stamps FirstTradingDay (2024-01-02)
        Assert.Equal(new DateTime(2024, 1, 2), monthlyLegacy[0].Timestamp);

        // Unified stamps PeriodStart (2024-01-01)
        Assert.Equal(new DateTime(2024, 1, 1), monthlyUnified[0].Timestamp);

        // Metadata carries both
        var metaBar = Assert.Single(monthlyMeta);
        Assert.Equal(new DateTime(2024, 1, 1), metaBar.PeriodStart);
        Assert.Equal(new DateTime(2024, 1, 2), metaBar.FirstTradingDay);
        Assert.Equal(new DateTime(2024, 1, 5), metaBar.LastTradingDay);
        Assert.Equal(4, metaBar.ConstituentDailyBarCount);
    }

    [Fact]
    public void AsOfCandleWindow_Build_StampsW1AndMN1WithPeriodStart()
    {
        var daily = Days("2024-01-16", "2024-01-17", "2024-01-18", "2024-01-19");
        var cutDate = new DateOnly(2024, 1, 19);

        var weeklyBars = AsOfCandleWindow.Build(daily, cutDate, TimeFrame.W1, count: 10);
        var monthlyBars = AsOfCandleWindow.Build(daily, cutDate, TimeFrame.MN1, count: 10);

        Assert.Equal(new DateTime(2024, 1, 15), Assert.Single(weeklyBars).Timestamp);
        Assert.Equal(new DateTime(2024, 1, 1), Assert.Single(monthlyBars).Timestamp);
    }

    [Fact]
    public async Task TimeFrameManager_GetCandlesAsync_StampsW1AndMN1WithPeriodStart()
    {
        var daily = Days("2024-01-16", "2024-01-17", "2024-01-18", "2024-01-19");
        var dataService = new FakeDataService(daily);
        var timeFrameManager = new TimeFrameManager(dataService);

        var weeklyBars = await timeFrameManager.GetCandlesAsync("TEST", TimeFrame.W1, count: 10);
        var monthlyBars = await timeFrameManager.GetCandlesAsync("TEST", TimeFrame.MN1, count: 10);

        Assert.Equal(new DateTime(2024, 1, 15), Assert.Single(weeklyBars).Timestamp);
        Assert.Equal(new DateTime(2024, 1, 1), Assert.Single(monthlyBars).Timestamp);
    }

    [Theory]
    [InlineData("2024-01-16", TimeFrame.W1, "2024-01-15")]
    [InlineData("2024-01-15", TimeFrame.W1, "2024-01-15")]
    [InlineData("2024-01-02", TimeFrame.MN1, "2024-01-01")]
    [InlineData("2024-01-01", TimeFrame.MN1, "2024-01-01")]
    public void PeriodStart_IsIdempotent_AndPreservesGroupingKey(string inputDateStr, TimeFrame tf, string expectedDateStr)
    {
        DateTime input = DateTime.Parse(inputDateStr, CultureInfo.InvariantCulture);
        DateTime expected = DateTime.Parse(expectedDateStr, CultureInfo.InvariantCulture);

        DateTime start1 = TimeFrameAggregator.PeriodStart(input, tf);
        DateTime start2 = TimeFrameAggregator.PeriodStart(start1, tf);

        Assert.Equal(expected, start1);
        Assert.Equal(start1, start2);
        Assert.Equal(TimeFrameAggregator.GetPeriodKey(input, tf), TimeFrameAggregator.GetPeriodKey(start1, tf));
    }

    private sealed class FakeDataService : IDataService
    {
        private readonly IReadOnlyList<CandleData> _data;
        public FakeDataService(IReadOnlyList<CandleData> data) => _data = data;

        public Task<IReadOnlyList<CandleData>> LoadCandlesAsync(string symbol, TimeFrame timeFrame, int count = 100)
            => Task.FromResult(_data);
    }
}
