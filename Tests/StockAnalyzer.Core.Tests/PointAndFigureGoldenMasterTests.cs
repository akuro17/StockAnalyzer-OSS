using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Strategies;
using StockAnalyzer.Core.Utilities;
using StockAnalyzer.ZeroAllocation;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.Core.Tests;

/// <summary>
/// Golden Master Tests asserting 100% equivalence between PointAndFigureConverter
/// and ZeroAllocationChartCore.PointAndFigureCalculator across various market conditions.
/// </summary>
public class PointAndFigureGoldenMasterTests
{
    private readonly ITestOutputHelper _output;

    public PointAndFigureGoldenMasterTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static DateTime Day(int o) => new DateTime(2025, 1, 1).AddDays(o);

    [Theory]
    [InlineData(ChartRoundingMode.None)]
    [InlineData(ChartRoundingMode.Floor)]
    [InlineData(ChartRoundingMode.Ceiling)]
    [InlineData(ChartRoundingMode.Round)]
    public void PointAndFigure_ConverterAndCalculator_ProduceIdenticalOutput_AcrossMarketScenarios(ChartRoundingMode roundingMode)
    {
        var testCases = new (string Name, CoreCandleData[] Candles, decimal BoxSize, int Reversal)[]
        {
            ("ElementDates Classic Series", new[] {
                new CoreCandleData(Day(0), 100m, 100m, 100m, 100m, 1000),
                new CoreCandleData(Day(1), 100m, 103m, 99m, 102m, 1000),
                new CoreCandleData(Day(2), 103m, 105m, 103m, 105m, 1000),
                new CoreCandleData(Day(3), 105m, 105m, 101m, 101m, 1000),
                new CoreCandleData(Day(4), 101m, 100m, 99m, 99m, 1000),
                new CoreCandleData(Day(5), 99m, 103m, 99.5m, 103m, 1000),
            }, 1m, 3),

            ("Steady Uptrend Extension", new[] {
                new CoreCandleData(Day(0), 100m, 100m, 100m, 100m, 1000),
                new CoreCandleData(Day(1), 101m, 102m, 100m, 101m, 1000),
                new CoreCandleData(Day(2), 102m, 104m, 101m, 103m, 1000),
                new CoreCandleData(Day(3), 103m, 106m, 102m, 105m, 1000),
            }, 1m, 3),

            ("Steady Downtrend Extension", new[] {
                new CoreCandleData(Day(0), 100m, 100m, 100m, 100m, 1000),
                new CoreCandleData(Day(1), 99m, 100m, 98m, 98m, 1000),
                new CoreCandleData(Day(2), 98m, 99m, 96m, 96m, 1000),
                new CoreCandleData(Day(3), 96m, 97m, 94m, 94m, 1000),
            }, 1m, 3),

            ("Congestion / Sub-Box Fluctuations", new[] {
                new CoreCandleData(Day(0), 100m, 100.2m, 99.8m, 100m, 1000),
                new CoreCandleData(Day(1), 100m, 100.3m, 99.9m, 100.1m, 1000),
                new CoreCandleData(Day(2), 100m, 100.1m, 99.7m, 100m, 1000),
            }, 1m, 3),

            ("Single Candle", new[] {
                new CoreCandleData(Day(0), 100m, 105m, 95m, 100m, 1000),
            }, 1m, 3),

            ("Empty Series", Array.Empty<CoreCandleData>(), 1m, 3),

            ("Extreme Volatility Spike Outliers", new[] {
                new CoreCandleData(Day(0), 100m, 100m, 100m, 100m, 1000),
                new CoreCandleData(Day(1), 100m, 150m, 100m, 150m, 1000),
                new CoreCandleData(Day(2), 150m, 150m, 80m, 80m, 1000),
                new CoreCandleData(Day(3), 80m, 120m, 80m, 120m, 1000),
            }, 1m, 3),
        };

        foreach (var tc in testCases)
        {
            var convCols = new List<CoreCandleData>();
            var convDates = new List<ElementDates>();
            PointAndFigureConverter.Convert(tc.Candles, tc.BoxSize, tc.Reversal, convCols, roundingMode, convDates);

            var zeroAlloc = tc.Candles.Select(c =>
                new ZeroAllocCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, (long)c.Volume)).ToArray();
            var calcAdapter = PointAndFigureCalculator.Calculate(zeroAlloc, new PointAndFigureParameters(tc.BoxSize, tc.Reversal, roundingMode));

            Assert.Equal(convCols.Count, calcAdapter.Count);
            Assert.Equal(convDates.Count, calcAdapter.ElementDates.Length);

            var times = calcAdapter.Timestamps.Span;
            var opens = calcAdapter.Opens.Span;
            var highs = calcAdapter.Highs.Span;
            var lows = calcAdapter.Lows.Span;
            var closes = calcAdapter.Closes.Span;
            var vols = calcAdapter.Volumes.Span;
            var ed = calcAdapter.ElementDates.Span;

            for (int i = 0; i < convCols.Count; i++)
            {
                var col = convCols[i];
                var date = convDates[i];

                Assert.Equal(col.Timestamp, times[i]);
                Assert.Equal(col.Open, opens[i]);
                Assert.Equal(col.High, highs[i]);
                Assert.Equal(col.Low, lows[i]);
                Assert.Equal(col.Close, closes[i]);
                Assert.Equal((long)col.Volume, vols[i]);

                Assert.Equal(date.Start, ed[i].Start);
                Assert.Equal(date.End, ed[i].End);
                Assert.Equal(col.Timestamp, ed[i].End);
            }
        }
    }

    [Theory]
    [InlineData(ChartSizingMode.Fixed, 2.5, 14, 1.0, 1.0, 3)]
    [InlineData(ChartSizingMode.Percentage, 1.0, 14, 1.0, 2.0, 3)]
    [InlineData(ChartSizingMode.AutoAtr, 1.0, 14, 1.5, 1.0, 3)]
    [InlineData(ChartSizingMode.Fixed, 1.0, 14, 1.0, 1.0, 1)]
    public void PointAndFigure_SyncAndAsyncStrategy_ProduceIdenticalColumnsAndDates(
        ChartSizingMode mode, decimal manualSize, int atrPeriod, decimal atrMultiplier, decimal percent, int reversal)
    {
        var candles = new List<CoreCandleData>();
        var baseDate = new DateTime(2025, 1, 1);
        decimal price = 100m;
        var rand = new Random(42);
        for (int i = 0; i < 200; i++)
        {
            decimal change = (decimal)(rand.NextDouble() * 6.0 - 2.8);
            decimal open = price;
            price = Math.Max(10m, price + change);
            decimal high = Math.Max(open, price) + (decimal)(rand.NextDouble() * 2.0);
            decimal low = Math.Min(open, price) - (decimal)(rand.NextDouble() * 2.0);
            candles.Add(new CoreCandleData(baseDate.AddDays(i), open, high, low, price, 1000 + i * 10));
        }

        // 1. Asynchronous path (Strategy used in background Task)
        var strategy = new PointAndFigureChartStrategy();
        var strategyParams = new ChartStrategyParameters(
            mode, manualSize, atrPeriod, atrMultiplier, percent, reversal, 2, (int)ChartRoundingMode.None, (int)AutoFallbackMode.Fixed);
        var strategyResult = strategy.Calculate(candles, strategyParams);
        var asyncAdapter = (ChartSegmentAdapter)strategyResult.Adapter;
        decimal asyncEffectiveSize = strategyResult.EffectiveSize;

        // 2. Synchronous path (Coordinator fallback using AutoBoxSizeCalculator + PointAndFigureConverter)
        decimal syncEffectiveSize = AutoBoxSizeCalculator.Calculate(
            mode, candles, manualSize, atrPeriod, atrMultiplier, ChartRoundingMode.None, AutoFallbackMode.Fixed, candles[^1].Close, percent);
        var syncCols = new List<CoreCandleData>();
        var syncDates = new List<ElementDates>();
        PointAndFigureConverter.Convert(candles, syncEffectiveSize, reversal, syncCols, ChartRoundingMode.None, syncDates);

        // Assert SSoT identity
        Assert.Equal(asyncEffectiveSize, syncEffectiveSize);
        Assert.Equal(asyncAdapter.Count, syncCols.Count);
        Assert.Equal(asyncAdapter.ElementDates.Length, syncDates.Count);

        var asyncTimes = asyncAdapter.Timestamps.Span;
        var asyncOpens = asyncAdapter.Opens.Span;
        var asyncHighs = asyncAdapter.Highs.Span;
        var asyncLows = asyncAdapter.Lows.Span;
        var asyncCloses = asyncAdapter.Closes.Span;
        var asyncDates = asyncAdapter.ElementDates.Span;

        for (int i = 0; i < syncCols.Count; i++)
        {
            Assert.Equal(syncCols[i].Timestamp, asyncTimes[i]);
            Assert.Equal(syncCols[i].Open, asyncOpens[i]);
            Assert.Equal(syncCols[i].High, asyncHighs[i]);
            Assert.Equal(syncCols[i].Low, asyncLows[i]);
            Assert.Equal(syncCols[i].Close, asyncCloses[i]);
            Assert.Equal(syncDates[i].Start, asyncDates[i].Start);
            Assert.Equal(syncDates[i].End, asyncDates[i].End);
        }
    }

    [Fact]
    public void PointAndFigureCalculator_BufferResize_HandlesLargeReversalsWithoutException()
    {
        // Generate 1000 bars with continuous reversals to produce hundreds of columns,
        // which exceeds initial capacity and exercises EnsureCapacity/Resize multiple times.
        var candles = new List<ZeroAllocCandleData>();
        var baseDate = new DateTime(2025, 1, 1);
        decimal price = 100m;
        for (int i = 0; i < 1000; i++)
        {
            // Alternate up and down by 5 boxes to trigger column completions
            price += (i % 2 == 0) ? 5m : -5m;
            candles.Add(new ZeroAllocCandleData(baseDate.AddMinutes(i), price, price + 1m, price - 1m, price, 100));
        }

        var parameters = new PointAndFigureParameters(BoxSize: 1m, ReversalAmount: 3, RoundingMode: ChartRoundingMode.None);
        var adapter = PointAndFigureCalculator.Calculate(candles.ToArray(), parameters);

        Assert.True(adapter.Count > 100, $"Expected many columns to trigger resize, got {adapter.Count}");
        Assert.Equal(adapter.Count, adapter.ElementDates.Length);
    }
}
