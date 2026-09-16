using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests;

public class TimeFrameAggregatorTests
{
    private static List<CandleData> CreateDailyCandles(int count, DateTime startDate, decimal startPrice = 100m)
    {
        var candles = new List<CandleData>();
        decimal price = startPrice;
        for (int i = 0; i < count; i++)
        {
            var date = startDate.AddDays(i);
            // Skip weekends for realistic data
            if (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday)
            {
                count++; // extend to compensate
                continue;
            }

            candles.Add(new CandleData(
                Timestamp: date,
                Open: price,
                High: price + 2m,
                Low: price - 1m,
                Close: price + 1m,
                Volume: 1000L + i
            ));
            price += 1m;
        }
        return candles;
    }

    [Fact]
    public void AggregateDailyToWeekly_FiveDays_ProducesOneWeeklyCandle()
    {
        // Monday 2026-01-05 through Friday 2026-01-09
        var monday = new DateTime(2026, 1, 5);
        var candles = new List<CandleData>();
        for (int i = 0; i < 5; i++)
        {
            candles.Add(new CandleData(
                Timestamp: monday.AddDays(i),
                Open: 100m + i,
                High: 105m + i,
                Low: 95m + i,
                Close: 101m + i,
                Volume: 1000L
            ));
        }

        var result = TimeFrameAggregator.AggregateDailyToWeekly(candles);

        Assert.Single(result);
        var weekly = result[0];
        Assert.Equal(100m, weekly.Open);          // First day's Open
        Assert.Equal(109m, weekly.High);           // Max High (105+4)
        Assert.Equal(95m, weekly.Low);             // Min Low
        Assert.Equal(105m, weekly.Close);          // Last day's Close (101+4)
        Assert.Equal(5000L, weekly.Volume);         // Sum of volumes
        Assert.Equal(monday, weekly.Timestamp);     // First day's timestamp
    }

    [Fact]
    public void AggregateDailyToWeekly_TwoWeeks_ProducesTwoCandles()
    {
        var monday1 = new DateTime(2026, 1, 5);
        var monday2 = new DateTime(2026, 1, 12);
        var candles = new List<CandleData>();

        // Week 1: Mon-Fri
        for (int i = 0; i < 5; i++)
        {
            candles.Add(new CandleData(monday1.AddDays(i), 100m, 110m, 90m, 105m, 500L));
        }
        // Week 2: Mon-Fri
        for (int i = 0; i < 5; i++)
        {
            candles.Add(new CandleData(monday2.AddDays(i), 200m, 210m, 190m, 205m, 600L));
        }

        var result = TimeFrameAggregator.AggregateDailyToWeekly(candles);

        Assert.Equal(2, result.Count);
        Assert.Equal(100m, result[0].Open);
        Assert.Equal(200m, result[1].Open);
    }

    [Fact]
    public void AggregateDailyToMonthly_TwoMonths_ProducesTwoCandles()
    {
        var candles = new List<CandleData>();

        // January: 20 trading days
        for (int i = 1; i <= 20; i++)
        {
            candles.Add(new CandleData(
                new DateTime(2026, 1, i), 100m + i, 110m + i, 90m + i, 105m + i, 1000L));
        }

        // February: 15 trading days
        for (int i = 1; i <= 15; i++)
        {
            candles.Add(new CandleData(
                new DateTime(2026, 2, i), 200m + i, 210m + i, 190m + i, 205m + i, 2000L));
        }

        var result = TimeFrameAggregator.AggregateDailyToMonthly(candles);

        Assert.Equal(2, result.Count);

        // January candle
        Assert.Equal(new DateTime(2026, 1, 1), result[0].Timestamp);
        Assert.Equal(101m, result[0].Open);        // First day's Open
        Assert.Equal(130m, result[0].High);         // Max High (110+20)
        Assert.Equal(91m, result[0].Low);           // Min Low (90+1)
        Assert.Equal(125m, result[0].Close);        // Last day's Close (105+20)
        Assert.Equal(20_000L, result[0].Volume);    // Sum

        // February candle
        Assert.Equal(new DateTime(2026, 2, 1), result[1].Timestamp);
        Assert.Equal(201m, result[1].Open);
    }

    [Fact]
    public void Aggregate_EmptyInput_ReturnsEmptyList()
    {
        var result = TimeFrameAggregator.Aggregate(Array.Empty<CandleData>(), TimeFrame.W1);
        Assert.Empty(result);
    }

    [Fact]
    public void Aggregate_NullInput_ReturnsEmptyList()
    {
        var result = TimeFrameAggregator.Aggregate(null!, TimeFrame.W1);
        Assert.Empty(result);
    }

    [Fact]
    public void Aggregate_DailyTarget_ReturnsSameData()
    {
        var candles = new List<CandleData>
        {
            new CandleData(new DateTime(2026, 1, 5), 100m, 110m, 90m, 105m, 1000L),
            new CandleData(new DateTime(2026, 1, 6), 105m, 115m, 95m, 110m, 2000L),
        };

        var result = TimeFrameAggregator.Aggregate(candles, TimeFrame.D1);

        // Should return same reference (no aggregation needed)
        Assert.Same(candles, result);
    }

    [Fact]
    public void Aggregate_WeeklyTarget_CallsWeeklyAggregation()
    {
        var monday = new DateTime(2026, 1, 5);
        var candles = new List<CandleData>();
        for (int i = 0; i < 5; i++)
        {
            candles.Add(new CandleData(monday.AddDays(i), 100m, 110m, 90m, 105m, 500L));
        }

        var result = TimeFrameAggregator.Aggregate(candles, TimeFrame.W1);

        Assert.Single(result);
    }

    [Fact]
    public void Aggregate_MonthlyTarget_CallsMonthlyAggregation()
    {
        var candles = new List<CandleData>();
        for (int i = 1; i <= 10; i++)
        {
            candles.Add(new CandleData(new DateTime(2026, 1, i), 100m, 110m, 90m, 105m, 500L));
        }

        var result = TimeFrameAggregator.Aggregate(candles, TimeFrame.MN1);

        Assert.Single(result);
    }

    [Fact]
    public void AggregateDailyToWeekly_VolumeIsSum()
    {
        var monday = new DateTime(2026, 1, 5);
        var candles = new List<CandleData>
        {
            new CandleData(monday, 100m, 110m, 90m, 105m, 100L),
            new CandleData(monday.AddDays(1), 100m, 110m, 90m, 105m, 200L),
            new CandleData(monday.AddDays(2), 100m, 110m, 90m, 105m, 300L),
        };

        var result = TimeFrameAggregator.AggregateDailyToWeekly(candles);

        Assert.Single(result);
        Assert.Equal(600L, result[0].Volume);
    }

    [Fact]
    public void AggregateDailyToMonthly_SingleDay_ProducesSingleCandle()
    {
        var candles = new List<CandleData>
        {
            new CandleData(new DateTime(2026, 3, 15), 100m, 110m, 90m, 105m, 1000L),
        };

        var result = TimeFrameAggregator.AggregateDailyToMonthly(candles);

        Assert.Single(result);
        Assert.Equal(100m, result[0].Open);
        Assert.Equal(110m, result[0].High);
        Assert.Equal(90m, result[0].Low);
        Assert.Equal(105m, result[0].Close);
        Assert.Equal(1000L, result[0].Volume);
    }
}
