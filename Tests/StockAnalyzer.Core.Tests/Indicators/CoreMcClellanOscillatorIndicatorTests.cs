#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Statistics;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

public sealed class CoreMcClellanOscillatorIndicatorTests
{
    private static List<CoreCandleData> BuildSeries(DateTime start, int count, decimal startPrice, decimal step, long baseVolume = 100_000)
    {
        var list = new List<CoreCandleData>(count);
        DateTime d = start;
        while (list.Count < count)
        {
            if (d.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday)
            {
                int i = list.Count;
                decimal close = startPrice + step * i + ((i % 4) - 1.5m) * 0.6m;
                decimal open = close - 0.3m;
                decimal high = Math.Max(open, close) + 1.0m;
                decimal low = Math.Min(open, close) - 1.0m;
                list.Add(new CoreCandleData(d, open, high, low, close, baseVolume + i * 500));
            }
            d = d.AddDays(1);
        }

        return list;
    }

    private static (Dictionary<string, IReadOnlyList<CandleData>> Universe, MarketBreadthResult Breadth) BuildBreadthFixture(
        DateTime start,
        int days,
        TimeFrame timeFrame,
        int fast = 3,
        int slow = 6)
    {
        var rawDaily = new Dictionary<string, IReadOnlyList<CandleData>>(StringComparer.OrdinalIgnoreCase)
        {
            ["SYM1"] = BuildSeries(start, days, 100m, 0.7m).Select(c => new CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume)).ToList(),
            ["SYM2"] = BuildSeries(start, days, 200m, -0.2m).Select(c => new CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume)).ToList(),
            ["SYM3"] = BuildSeries(start, days, 80m, 0.4m).Select(c => new CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume)).ToList()
        };

        IReadOnlyDictionary<string, IReadOnlyList<CandleData>> universe = TimeFrameAggregator.IsPeriodFrame(timeFrame)
            ? rawDaily.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<CandleData>)TimeFrameAggregator.AggregateToPeriodStart(kv.Value, timeFrame),
                StringComparer.OrdinalIgnoreCase)
            : rawDaily;

        MarketBreadthResult result = MarketBreadthCalculator.Calculate(
            universe,
            new MarketBreadthParameters
            {
                McClellanFast = fast,
                McClellanSlow = slow,
                TimeFrame = timeFrame
            });

        return (rawDaily, result);
    }

    [Fact]
    public void Calculate_WithoutInjectionOrWithBlankGroup_ReturnsAllNullsForEverySeries()
    {
        List<CoreCandleData> candles = BuildSeries(new DateTime(2025, 1, 2), 20, 100m, 0.5m);
        var indicator = new CoreMcClellanOscillatorIndicator();

        IIndicatorResult uninjected = indicator.Calculate(candles);
        Assert.True(uninjected.IsSuccessful);
        Assert.Equal(candles.Count, uninjected.MainValues.Count);
        Assert.All(uninjected.MainValues, v => Assert.Null(v));
        Assert.Equal(candles.Count, uninjected.GetSeries(CoreMcClellanOscillatorIndicator.SummationIndexSeriesName).Count);
        Assert.All(uninjected.GetSeries(CoreMcClellanOscillatorIndicator.SummationIndexSeriesName), v => Assert.Null(v));
        Assert.Equal(candles.Count, uninjected.GetSeries(CoreMcClellanOscillatorIndicator.AdLineSeriesName).Count);
        Assert.All(uninjected.GetSeries(CoreMcClellanOscillatorIndicator.AdLineSeriesName), v => Assert.Null(v));

        // Even when a breadth result is injected, an empty UniverseGroupId returns all nulls.
        var (_, breadth) = BuildBreadthFixture(new DateTime(2025, 1, 2), 20, TimeFrame.D1);
        indicator.UniverseGroupId = string.Empty;
        indicator.SetUniverseBreadth(breadth, TimeFrame.D1);
        IIndicatorResult blankGroup = indicator.Calculate(candles);
        Assert.True(blankGroup.IsSuccessful);
        Assert.All(blankGroup.MainValues, v => Assert.Null(v));
    }

    [Fact]
    public void Calculate_WithDailyInjection_MatchesDirectBreadthAndLeavesUnmatchedDatesNull()
    {
        var start = new DateTime(2025, 1, 2);
        var (rawDaily, breadth) = BuildBreadthFixture(start, 25, TimeFrame.D1, fast: 3, slow: 6);

        // Chart candles include two leading dates before universe history and one trailing date after universe history.
        var chartCandles = new List<CoreCandleData>
        {
            new(new DateTime(2024, 12, 30), 99m, 100m, 98m, 99.5m, 10_000),
            new(new DateTime(2024, 12, 31), 99.5m, 101m, 99m, 100m, 10_000)
        };
        chartCandles.AddRange(rawDaily["SYM1"].Select(c => new CoreCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume)));
        chartCandles.Add(new CoreCandleData(chartCandles[^1].Timestamp.AddDays(5), 120m, 121m, 119m, 120m, 10_000));

        var indicator = new CoreMcClellanOscillatorIndicator
        {
            UniverseGroupId = Guid.NewGuid().ToString(),
            FastPeriod = 3,
            SlowPeriod = 6
        };
        indicator.SetUniverseBreadth(breadth, TimeFrame.D1);

        IIndicatorResult result = indicator.Calculate(chartCandles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(chartCandles.Count, result.MainValues.Count);
        Assert.Null(result.MainValues[0]);
        Assert.Null(result.MainValues[1]);
        Assert.Null(result.MainValues[^1]);

        IReadOnlyList<decimal?> summation = result.GetSeries(CoreMcClellanOscillatorIndicator.SummationIndexSeriesName);
        IReadOnlyList<decimal?> adLine = result.GetSeries(CoreMcClellanOscillatorIndicator.AdLineSeriesName);

        for (int i = 0; i < breadth.DateCount; i++)
        {
            int barIdx = i + 2;
            Assert.Equal(IndicatorCalculationHelper.ToDecimalOrNull(breadth.Series.McClellan[i] ?? double.NaN), result.MainValues[barIdx]);
            Assert.Equal(IndicatorCalculationHelper.ToDecimalOrNull(breadth.Series.SummationIndex[i] ?? double.NaN), summation[barIdx]);
            Assert.Equal((decimal?)breadth.Series.AdLine[i], adLine[barIdx]);
        }
    }

    [Fact]
    public void Calculate_WithWeeklyInjection_MatchesPeriodStartDate()
    {
        var start = new DateTime(2025, 1, 6);
        var (_, breadth) = BuildBreadthFixture(start, 65, TimeFrame.W1, fast: 3, slow: 6);

        // Give weekly chart bars mid-week (Wednesday) timestamps; aligner must map via PeriodMatchDate (Monday).
        var wednesdayBars = Enumerable.Range(0, breadth.DateCount)
            .Select(i =>
            {
                DateTime periodStart = DateTime.Parse(breadth.Series.Dates[i]);
                DateTime wednesday = periodStart.AddDays(2);
                return new CoreCandleData(wednesday, 100m, 102m, 99m, 101m, 50_000);
            })
            .ToList();

        var indicator = new CoreMcClellanOscillatorIndicator
        {
            UniverseGroupId = Guid.NewGuid().ToString(),
            FastPeriod = 3,
            SlowPeriod = 6
        };
        indicator.SetUniverseBreadth(breadth, TimeFrame.W1);

        IIndicatorResult result = indicator.Calculate(wednesdayBars);

        Assert.True(result.IsSuccessful);
        Assert.Equal(breadth.DateCount, result.MainValues.Count);
        for (int i = 0; i < breadth.DateCount; i++)
        {
            decimal? expected = breadth.Series.McClellan[i] is double d ? IndicatorCalculationHelper.ToDecimalOrNull(d) : null;
            Assert.Equal(expected, result.MainValues[i]);
        }
    }

    [Fact]
    public void ParameterValidationAndRegistrations_EnforceContracts()
    {
        var valid = new CoreMcClellanOscillatorParameter
        {
            UniverseGroupId = Guid.NewGuid().ToString(),
            FastPeriod = MarketBreadthCalculator.DefaultMcClellanFast,
            SlowPeriod = MarketBreadthCalculator.DefaultMcClellanSlow
        };
        valid.Validate();

        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreMcClellanOscillatorParameter { UniverseGroupId = "not-a-guid" }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreMcClellanOscillatorParameter { FastPeriod = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreMcClellanOscillatorParameter { FastPeriod = 501 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreMcClellanOscillatorParameter { SlowPeriod = 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreMcClellanOscillatorParameter { SlowPeriod = 1001 }.Validate());

        Assert.Equal(
            BacktestIndicatorViolationReason.SynchronousCalculationUnsupported,
            BacktestIndicatorEligibility.Check(IndicatorType.McClellanOscillator, null));

        // Chart suppression: SummationIndex and AdLine are suppressed from chart rendering, kept in DataWindow.
        Assert.False(IndicatorChartSuppression.IsSuppressed(IndicatorType.McClellanOscillator, IndicatorResult.MainSeriesName));
        Assert.True(IndicatorChartSuppression.IsSuppressed(IndicatorType.McClellanOscillator, CoreMcClellanOscillatorIndicator.SummationIndexSeriesName));
        Assert.True(IndicatorChartSuppression.IsSuppressed(IndicatorType.McClellanOscillator, CoreMcClellanOscillatorIndicator.AdLineSeriesName));
        Assert.False(IndicatorChartSuppression.IsDataWindowSuppressed(IndicatorType.McClellanOscillator, CoreMcClellanOscillatorIndicator.SummationIndexSeriesName));
        Assert.False(IndicatorChartSuppression.IsDataWindowSuppressed(IndicatorType.McClellanOscillator, CoreMcClellanOscillatorIndicator.AdLineSeriesName));

        CoreIndicatorSettings defaults = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == IndicatorType.McClellanOscillator);
        Assert.False(defaults.IsOverlay);
        Assert.True(defaults.IncludeZeroBaseline);
        Assert.Single(defaults.SeriesColors);
        Assert.Equal(IndicatorResult.MainSeriesName, defaults.SeriesColors[0].Name);
    }
}
