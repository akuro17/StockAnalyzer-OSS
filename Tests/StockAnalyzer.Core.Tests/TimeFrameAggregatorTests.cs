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

    [Theory]
    [InlineData("2024-01-01", "2024-01-07")] // Monday
    [InlineData("2024-01-03", "2024-01-07")] // Wednesday
    [InlineData("2024-01-07", "2024-01-07")] // Sunday is the last day of its own ISO week
    [InlineData("2024-12-30", "2025-01-05")] // an ISO week across the year boundary
    [InlineData("2025-01-02", "2025-01-05")] // first trading day after a holiday-shifted start
    [InlineData("2020-12-31", "2021-01-03")] // week 53 of a 53-week ISO year
    [InlineData("2021-01-01", "2021-01-03")] // January 1 that still belongs to the previous ISO year
    public void PeriodEnd_Weekly_IsTheSundayOfTheIsoWeek(string barDate, string expectedEnd)
    {
        Assert.Equal(DateTime.Parse(expectedEnd), TimeFrameAggregator.PeriodEnd(DateTime.Parse(barDate), TimeFrame.W1));
    }

    [Theory]
    [InlineData("2024-01-02", "2024-01-31")]
    [InlineData("2024-02-01", "2024-02-29")] // leap year
    [InlineData("2023-02-01", "2023-02-28")]
    [InlineData("2024-12-31", "2024-12-31")]
    public void PeriodEnd_Monthly_IsTheLastDayOfTheMonth(string barDate, string expectedEnd)
    {
        Assert.Equal(DateTime.Parse(expectedEnd), TimeFrameAggregator.PeriodEnd(DateTime.Parse(barDate), TimeFrame.MN1));
    }

    [Fact]
    public void PeriodEnd_DailyAndTimeOfDay_AreTheDateItself()
    {
        Assert.Equal(new DateTime(2024, 1, 3), TimeFrameAggregator.PeriodEnd(new DateTime(2024, 1, 3, 15, 30, 0), TimeFrame.D1));
        Assert.Equal(new DateTime(2024, 1, 7), TimeFrameAggregator.PeriodEnd(new DateTime(2024, 1, 3, 15, 30, 0), TimeFrame.W1));
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

    [Theory]
    [InlineData("2024-01-16", "2024-01-15")] // Tuesday -> Monday of week
    [InlineData("2024-01-15", "2024-01-15")] // Monday
    [InlineData("2024-01-21", "2024-01-15")] // Sunday
    [InlineData("2024-12-30", "2024-12-30")] // Monday cross year
    [InlineData("2025-01-02", "2024-12-30")] // Thursday cross year -> Monday 2024-12-30
    public void PeriodStart_Weekly_IsTheMondayOfTheIsoWeek(string barDate, string expectedStart)
    {
        Assert.Equal(DateTime.Parse(expectedStart), TimeFrameAggregator.PeriodStart(DateTime.Parse(barDate), TimeFrame.W1));
    }

    [Theory]
    [InlineData("2024-06-03", "2024-06-01")]
    [InlineData("2024-02-29", "2024-02-01")]
    [InlineData("2024-12-31", "2024-12-01")]
    public void PeriodStart_Monthly_IsTheFirstOfTheMonth(string barDate, string expectedStart)
    {
        Assert.Equal(DateTime.Parse(expectedStart), TimeFrameAggregator.PeriodStart(DateTime.Parse(barDate), TimeFrame.MN1));
    }

    [Theory]
    [InlineData("2024-01-16", 202403)] // Week 3 of 2024
    [InlineData("2024-12-30", 202501)] // ISO week 1 of 2025
    [InlineData("2025-01-02", 202501)] // ISO week 1 of 2025
    [InlineData("2020-12-31", 202053)] // ISO week 53 of 2020
    public void GetPeriodKey_Weekly_MatchesIsoYearAndWeek(string barDate, int expectedKey)
    {
        Assert.Equal(expectedKey, TimeFrameAggregator.GetPeriodKey(DateTime.Parse(barDate), TimeFrame.W1));
    }

    [Theory]
    [InlineData("2024-06-03", 202406)]
    [InlineData("2024-12-31", 202412)]
    public void GetPeriodKey_Monthly_MatchesYearAndMonth(string barDate, int expectedKey)
    {
        Assert.Equal(expectedKey, TimeFrameAggregator.GetPeriodKey(DateTime.Parse(barDate), TimeFrame.MN1));
    }

    [Theory]
    [InlineData("2024-10-23", "2024-10-23", true)]  // Mid-week Wednesday with ref Wednesday -> incomplete (end is Sunday)
    [InlineData("2024-10-23", "2024-10-27", false)] // Sunday reached -> complete
    [InlineData("2024-10-23", "2024-10-28", false)] // Monday next week -> complete
    public void IsPartialPeriod_Weekly_ReturnsExpected(string barDate, string refDate, bool expected)
    {
        Assert.Equal(expected, TimeFrameAggregator.IsPartialPeriod(DateTime.Parse(barDate), TimeFrame.W1, DateTime.Parse(refDate)));
    }

    [Theory]
    [InlineData("2024-10-23", "2024-10-23", true)]  // Mid-month with ref Oct 23 -> incomplete (end is Oct 31)
    [InlineData("2024-10-23", "2024-10-31", false)] // End of month reached -> complete
    public void IsPartialPeriod_Monthly_ReturnsExpected(string barDate, string refDate, bool expected)
    {
        Assert.Equal(expected, TimeFrameAggregator.IsPartialPeriod(DateTime.Parse(barDate), TimeFrame.MN1, DateTime.Parse(refDate)));
    }

    [Fact]
    public void GoldenVectors_AllSevenBoundaryCases_MatchAggregatorAndRules()
    {
        IReadOnlyList<GoldenVector> vectors = TimeFrameGoldenVectors.Load();
        Assert.Equal(TimeFrameGoldenVectors.RequiredVectorCount, vectors.Count);

        foreach (GoldenVector vector in vectors)
        {
            AssertFrameMatches(vector, TimeFrameAggregator.AggregateDailyToWeekly(vector.DailyBars), vector.Weekly, TimeFrame.W1);
            AssertFrameMatches(vector, TimeFrameAggregator.AggregateDailyToMonthly(vector.DailyBars), vector.Monthly, TimeFrame.MN1);
        }
    }

    private static void AssertFrameMatches(GoldenVector vector, IReadOnlyList<CandleData> actual, IReadOnlyList<GoldenBar> expected, TimeFrame frame)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < actual.Count; i++)
        {
            GoldenBar exp = expected[i];
            Assert.Equal(exp.ChartTimestamp, actual[i].Timestamp.Date);
            Assert.Equal(exp.ParquetTimestamp, TimeFrameAggregator.PeriodStart(actual[i].Timestamp, frame));
            Assert.Equal(exp.PeriodKey, TimeFrameAggregator.GetPeriodKey(actual[i].Timestamp, frame));
            Assert.Equal(exp.Open, actual[i].Open);
            Assert.Equal(exp.High, actual[i].High);
            Assert.Equal(exp.Low, actual[i].Low);
            Assert.Equal(exp.Close, actual[i].Close);
            Assert.Equal(exp.Volume, actual[i].Volume);
            Assert.Equal(exp.IsPartial, TimeFrameAggregator.IsPartialPeriod(actual[i].Timestamp, frame, vector.ReferenceDate));
        }
    }

    [Fact]
    public void PeriodStart_And_GetPeriodKey_AreStrictlyEquivelentAcross35Years()
    {
        // Tests 1995-01-01 through 2030-12-31 (13,149 days), verifying:
        // GetPeriodKey(d1, tf) == GetPeriodKey(d2, tf) <=> PeriodStart(d1, tf) == PeriodStart(d2, tf)
        var startDate = new DateTime(1995, 1, 1);
        var endDate = new DateTime(2030, 12, 31);

        DateTime? prevW1Start = null;
        int? prevW1Key = null;

        DateTime? prevMn1Start = null;
        int? prevMn1Key = null;

        for (var date = startDate; date <= endDate; date = date.AddDays(1))
        {
            // D1 checks
            Assert.Equal(date, TimeFrameAggregator.PeriodStart(date, TimeFrame.D1));
            int expectedD1Key = date.Year * 10000 + date.Month * 100 + date.Day;
            Assert.Equal(expectedD1Key, TimeFrameAggregator.GetPeriodKey(date, TimeFrame.D1));

            // W1 checks
            var w1Start = TimeFrameAggregator.PeriodStart(date, TimeFrame.W1);
            int w1Key = TimeFrameAggregator.GetPeriodKey(date, TimeFrame.W1);
            Assert.Equal(DayOfWeek.Monday, w1Start.DayOfWeek);

            if (prevW1Start.HasValue)
            {
                if (w1Start == prevW1Start.Value)
                {
                    Assert.Equal(prevW1Key!.Value, w1Key);
                }
                else
                {
                    Assert.NotEqual(prevW1Key!.Value, w1Key);
                }
            }
            prevW1Start = w1Start;
            prevW1Key = w1Key;

            // MN1 checks
            var mn1Start = TimeFrameAggregator.PeriodStart(date, TimeFrame.MN1);
            int mn1Key = TimeFrameAggregator.GetPeriodKey(date, TimeFrame.MN1);
            Assert.Equal(1, mn1Start.Day);

            if (prevMn1Start.HasValue)
            {
                if (mn1Start == prevMn1Start.Value)
                {
                    Assert.Equal(prevMn1Key!.Value, mn1Key);
                }
                else
                {
                    Assert.NotEqual(prevMn1Key!.Value, mn1Key);
                }
            }
            prevMn1Start = mn1Start;
            prevMn1Key = mn1Key;
        }
    }

    [Fact]
    public void AggregateDailyToWeekly_WithDuplicateDailyDates_KeepsLatestAndDoesNotDoubleCountVolume()
    {
        var monday = new DateTime(2026, 1, 5);
        var candles = new List<CandleData>
        {
            new CandleData(monday, 100m, 105m, 95m, 102m, 1000L),
            // Duplicate monday with corrected close and volume
            new CandleData(monday, 100m, 106m, 95m, 103m, 1200L),
            new CandleData(monday.AddDays(1), 103m, 108m, 101m, 107m, 1500L)
        };

        var weekly = TimeFrameAggregator.AggregateDailyToWeekly(candles);

        Assert.Single(weekly);
        Assert.Equal(monday, weekly[0].Timestamp);
        Assert.Equal(100m, weekly[0].Open);
        Assert.Equal(108m, weekly[0].High);
        Assert.Equal(95m, weekly[0].Low);
        Assert.Equal(107m, weekly[0].Close);
        // Deduplicated volume: 1200 + 1500 = 2700 (not 1000 + 1200 + 1500 = 3700)
        Assert.Equal(2700L, weekly[0].Volume);
    }

    // A chronological series with gaps (weekends, holidays, a few missing weeks), calendar days that appear twice (a corrected candle, sometimes at another time of day),
    // across ISO year boundaries (week 53 of 2020 and 2026) and month ends.
    private static List<CandleData> ChronologicalSeriesWithDuplicates(int seed)
    {
        var random = new Random(seed);
        var candles = new List<CandleData>();
        DateTime day = new DateTime(2019, 11, 25);
        decimal price = 100m;
        while (day < new DateTime(2027, 2, 1))
        {
            bool skip = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || random.Next(40) == 0;
            if (!skip)
            {
                int copies = random.Next(25) == 0 ? 2 : 1;
                for (int copy = 0; copy < copies; copy++)
                {
                    price = Math.Max(1m, price + random.Next(-300, 301) / 100m);
                    candles.Add(new CandleData(day.AddHours(copy * random.Next(0, 3)), price, price + random.Next(0, 200) / 100m, price - random.Next(0, 100) / 100m, price + random.Next(-50, 51) / 100m, random.Next(1, 5_000_000)));
                }
            }

            day = day.AddDays(random.Next(30) == 0 ? 9 : 1);
        }

        return candles;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TheSinglePassAggregationEqualsTheGroupingForms(int seed)
    {
        List<CandleData> daily = ChronologicalSeriesWithDuplicates(seed);
        Assert.Contains(daily.Zip(daily.Skip(1)), pair => pair.First.Timestamp.Date == pair.Second.Timestamp.Date);

        Assert.Equal(TimeFrameAggregator.AggregateDailyToWeeklyByGrouping(daily), TimeFrameAggregator.AggregateDailyToWeekly(daily));
        Assert.Equal(TimeFrameAggregator.AggregateDailyToMonthlyByGrouping(daily), TimeFrameAggregator.AggregateDailyToMonthly(daily));

        // every prefix of the series ends a period at a different place: a lone day, a period of one day, a duplicated last day
        for (int length = 1; length <= 60; length++)
        {
            List<CandleData> prefix = daily.Take(length).ToList();
            Assert.Equal(TimeFrameAggregator.AggregateDailyToWeeklyByGrouping(prefix), TimeFrameAggregator.AggregateDailyToWeekly(prefix));
            Assert.Equal(TimeFrameAggregator.AggregateDailyToMonthlyByGrouping(prefix), TimeFrameAggregator.AggregateDailyToMonthly(prefix));
        }
    }

    [Fact]
    public void ASeriesThatIsNotChronologicalIsStillGroupedByPeriod()
    {
        List<CandleData> daily = ChronologicalSeriesWithDuplicates(4).Take(400).ToList();
        var shuffled = daily.OrderBy(candle => candle.Close).ThenBy(candle => candle.Volume).ToList();
        Assert.NotEqual(daily, shuffled);

        Assert.Equal(TimeFrameAggregator.AggregateDailyToWeeklyByGrouping(shuffled), TimeFrameAggregator.AggregateDailyToWeekly(shuffled));
        Assert.Equal(TimeFrameAggregator.AggregateDailyToMonthlyByGrouping(shuffled), TimeFrameAggregator.AggregateDailyToMonthly(shuffled));
    }

    [Fact]
    public void MarketDataSettings_Validate_RejectsDuplicatePaths()
    {
        var settings = new MarketDataSettings
        {
            DailyDataPath = "Data/Parquet",
            WeeklyDataPath = "Data/Parquet",
            MonthlyDataPath = "Data/Monthly"
        };

        var ex = Assert.Throws<ArgumentException>(() => settings.Validate());
        Assert.Contains("must not resolve to the same directory", ex.Message);
    }
}
