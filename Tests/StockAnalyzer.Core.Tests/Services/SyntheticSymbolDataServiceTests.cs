using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using StockAnalyzer.Core.Helpers;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public class SyntheticSymbolDataServiceTests
{
    #region 1. SyntheticSymbolParser Tests

    [Theory]
    [InlineData("7203 / 7267", "7203-T", "7267-T", SeriesOperation.Divide, '/', true, "7203-T / 7267-T")]
    [InlineData("7203.T / 7267.T", "7203-T", "7267-T", SeriesOperation.Divide, '/', true, "7203-T / 7267-T")]
    [InlineData("7203-T/7267-T", "7203-T", "7267-T", SeriesOperation.Divide, '/', true, "7203-T / 7267-T")]
    [InlineData("AAPL + MSFT", "AAPL", "MSFT", SeriesOperation.Add, '+', true, "AAPL + MSFT")]
    [InlineData("AAPL+MSFT", "AAPL", "MSFT", SeriesOperation.Add, '+', true, "AAPL + MSFT")]
    [InlineData("2681-T+3036-T", "2681-T", "3036-T", SeriesOperation.Add, '+', true, "2681-T + 3036-T")]
    [InlineData("2681-T + 3036-T", "2681-T", "3036-T", SeriesOperation.Add, '+', true, "2681-T + 3036-T")]
    [InlineData("AAPL+2681-T", "AAPL", "2681-T", SeriesOperation.Add, '+', true, "AAPL + 2681-T")]
    [InlineData("2681-T+AAPL", "2681-T", "AAPL", SeriesOperation.Add, '+', true, "2681-T + AAPL")]
    [InlineData("7203 - 7267", "7203-T", "7267-T", SeriesOperation.Subtract, '-', true, "7203-T - 7267-T")]
    [InlineData("7203.T - 7267.T", "7203-T", "7267-T", SeriesOperation.Subtract, '-', true, "7203-T - 7267-T")]
    [InlineData("7203-T - 7267-T", "7203-T", "7267-T", SeriesOperation.Subtract, '-', true, "7203-T - 7267-T")]
    [InlineData("7203-7267", "7203-T", "7267-T", SeriesOperation.Subtract, '-', true, "7203-T - 7267-T")]
    [InlineData("7203-T-7267-T", "7203-T", "7267-T", SeriesOperation.Subtract, '-', true, "7203-T - 7267-T")]
    [InlineData("AAPL - MSFT", "AAPL", "MSFT", SeriesOperation.Subtract, '-', true, "AAPL - MSFT")]
    [InlineData("7203 * 7267", "7203-T", "7267-T", SeriesOperation.Multiply, '*', false, "7203-T * 7267-T")]
    [InlineData("AAPL * MSFT", "AAPL", "MSFT", SeriesOperation.Multiply, '*', false, "AAPL * MSFT")]
    public void TryParse_ValidExpressions_ReturnsExpected(
        string input,
        string expectedLeft,
        string expectedRight,
        SeriesOperation expectedOp,
        char expectedOpChar,
        bool expectedSupported,
        string expectedCanonical)
    {
        bool success = SyntheticSymbolParser.TryParse(input, out var expr);

        Assert.True(success);
        Assert.Equal(expectedLeft, expr.LeftSymbol);
        Assert.Equal(expectedRight, expr.RightSymbol);
        Assert.Equal(expectedOp, expr.Operation);
        Assert.Equal(expectedOpChar, expr.OperatorChar);
        Assert.Equal(expectedSupported, expr.IsOperatorSupported);
        Assert.Equal(expectedCanonical, expr.CanonicalExpression);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("7203-T")]
    [InlineData("7203.T")]
    [InlineData("7203")]
    [InlineData("AAPL")]
    [InlineData("BRK-B")]
    [InlineData("BRK.B")]
    [InlineData("ZZZ-EMPTY")]
    [InlineData("AAPL +")]
    [InlineData("+ MSFT")]
    [InlineData("AAPL + MSFT + GOOG")]
    [InlineData("AAPL / MSFT / GOOG")]
    [InlineData("AAPL * MSFT * GOOG")]
    [InlineData("2681-T 3036-T")]
    [InlineData("AAPL MSFT")]
    [InlineData("AAPL 2681-T")]
    public void TryParse_SingleTickersOrInvalidExpressions_ReturnsFalse(string? input)
    {
        bool success = SyntheticSymbolParser.TryParse(input, out var expr);
        Assert.False(success);
        Assert.Equal(default, expr);
    }

    [Fact]
    public void TryParse_ExceedsMaxExpressionLength_ReturnsFalse()
    {
        string tooLong = new string('A', 35) + " + " + new string('B', 35);
        bool success = SyntheticSymbolParser.TryParse(tooLong, out _);
        Assert.False(success);
    }

    #endregion

    #region 2. SyntheticCandleCalculator Tests

    [Fact]
    public void ComputeCandles_Addition_CalculatesOhlcvAndPreservesInvariants()
    {
        var ts1 = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var ts2 = new DateTime(2024, 1, 16, 0, 0, 0, DateTimeKind.Utc);
        var timestamps = new[] { ts1, ts2 };

        var seriesA = new CandleData?[]
        {
            new CandleData(ts1, 100m, 110m, 95m, 105m, 1000L),
            new CandleData(ts2, 106m, 115m, 102m, 112m, 1500L),
        };

        var seriesB = new CandleData?[]
        {
            new CandleData(ts1, 200m, 215m, 190m, 205m, 2000L),
            new CandleData(ts2, 204m, 210m, 198m, 200m, 2500L),
        };

        var seriesDict = new Dictionary<string, CandleData?[]>
        {
            ["AAPL"] = seriesA,
            ["MSFT"] = seriesB,
        };

        var aligned = new ComparisonAlignedData("AAPL", timestamps, seriesDict, Array.Empty<string>());
        var expr = new SyntheticSymbolExpression("AAPL", "MSFT", SeriesOperation.Add, '+', true, "AAPL + MSFT");

        var candles = SyntheticCandleCalculator.ComputeCandles(aligned, expr);

        Assert.Equal(2, candles.Count);

        // Bar 1
        var c1 = candles[0];
        Assert.Equal(ts1, c1.Timestamp);
        Assert.Equal(300m, c1.Open);
        Assert.Equal(310m, c1.Close);
        Assert.True(c1.High >= Math.Max(c1.Open, c1.Close));
        Assert.True(c1.Low <= Math.Min(c1.Open, c1.Close));
        Assert.Equal(3000L, c1.Volume); // 1000 + 2000
        Assert.True(c1.IsValid());

        // Bar 2
        var c2 = candles[1];
        Assert.Equal(ts2, c2.Timestamp);
        Assert.Equal(310m, c2.Open);
        Assert.Equal(312m, c2.Close);
        Assert.Equal(4000L, c2.Volume); // 1500 + 2500
        Assert.True(c2.IsValid());
    }

    [Fact]
    public void ComputeCandles_Subtraction_MinimumLegLiquidityVolumeAndOhlcInvariants()
    {
        var ts = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var timestamps = new[] { ts };

        var seriesA = new CandleData?[] { new CandleData(ts, 150m, 160m, 140m, 155m, 5000L) };
        var seriesB = new CandleData?[] { new CandleData(ts, 100m, 110m, 95m, 105m, 3000L) };

        var seriesDict = new Dictionary<string, CandleData?[]>
        {
            ["7203-T"] = seriesA,
            ["7267-T"] = seriesB,
        };

        var aligned = new ComparisonAlignedData("7203-T", timestamps, seriesDict, Array.Empty<string>());
        var expr = new SyntheticSymbolExpression("7203-T", "7267-T", SeriesOperation.Subtract, '-', true, "7203-T - 7267-T");

        var candles = SyntheticCandleCalculator.ComputeCandles(aligned, expr);

        Assert.Single(candles);
        var c = candles[0];
        Assert.Equal(50m, c.Open); // 150 - 100
        Assert.Equal(50m, c.Close); // 155 - 105
        // Interval bounds: High = left.High - right.Low = 160 - 95 = 65, Low = left.Low - right.High = 140 - 110 = 30
        Assert.Equal(65m, c.High);
        Assert.Equal(30m, c.Low);
        Assert.True(c.High >= Math.Max(c.Open, c.Close));
        Assert.True(c.Low <= Math.Min(c.Open, c.Close));
        Assert.Equal(3000L, c.Volume); // Min(5000, 3000)
        Assert.True(c.IsValid());
    }

    [Fact]
    public void ComputeCandles_Division_HandlesSpreadRatioAndDivZero()
    {
        var ts1 = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var ts2 = new DateTime(2024, 1, 16, 0, 0, 0, DateTimeKind.Utc);
        var timestamps = new[] { ts1, ts2 };

        // Bar 2 has right Close = 0 (div zero condition)
        var seriesA = new CandleData?[]
        {
            new CandleData(ts1, 100m, 120m, 80m, 110m, 1000L),
            new CandleData(ts2, 100m, 120m, 80m, 110m, 1000L),
        };

        var seriesB = new CandleData?[]
        {
            new CandleData(ts1, 50m, 60m, 40m, 55m, 2500L),
            new CandleData(ts2, 50m, 60m, 0m, 55m, 2500L), // Low = 0 triggers exclusion for division
        };

        var seriesDict = new Dictionary<string, CandleData?[]>
        {
            ["7203-T"] = seriesA,
            ["7267-T"] = seriesB,
        };

        var aligned = new ComparisonAlignedData("7203-T", timestamps, seriesDict, Array.Empty<string>());
        var expr = new SyntheticSymbolExpression("7203-T", "7267-T", SeriesOperation.Divide, '/', true, "7203-T / 7267-T");

        var candles = SyntheticCandleCalculator.ComputeCandles(aligned, expr);

        // Bar 2 must be excluded because right leg has 0 price
        Assert.Single(candles);
        var c = candles[0];
        Assert.Equal(ts1, c.Timestamp);
        Assert.Equal(2.0m, c.Open); // 100 / 50
        Assert.Equal(2.0m, c.Close); // 110 / 55
        // Interval bounds: High = left.High / right.Low = 120 / 40 = 3.0, Low = left.Low / right.High = 80 / 60 = 1.33333333
        Assert.Equal(3.0m, c.High);
        Assert.Equal(1.33333333m, c.Low);
        Assert.True(c.High >= Math.Max(c.Open, c.Close));
        Assert.True(c.Low <= Math.Min(c.Open, c.Close));
        Assert.Equal(1000L, c.Volume); // Min(1000, 2500)
        Assert.True(c.IsValid());
    }

    [Fact]
    public void ComputeCandles_UnsupportedMultiplyOperator_ReturnsEmpty()
    {
        var ts = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var aligned = new ComparisonAlignedData(
            "AAPL",
            new[] { ts },
            new Dictionary<string, CandleData?[]>
            {
                ["AAPL"] = new CandleData?[] { new CandleData(ts, 100m, 110m, 90m, 105m, 100L) },
                ["MSFT"] = new CandleData?[] { new CandleData(ts, 200m, 210m, 190m, 205m, 200L) },
            },
            Array.Empty<string>());

        var expr = new SyntheticSymbolExpression("AAPL", "MSFT", SeriesOperation.Multiply, '*', false, "AAPL * MSFT");
        var candles = SyntheticCandleCalculator.ComputeCandles(aligned, expr);

        Assert.Empty(candles);
    }

    [Fact]
    public void ComputeCandles_IntervalArithmeticBounds_PreventsWickCollapse()
    {
        // When both legs have Open == Close (e.g. 1000m), but significant intraday swings:
        // Leg A: O=1000, H=1050, L=950, C=1000
        // Leg B: O=1000, H=1050, L=950, C=1000
        // Under naive subtraction (H_A - H_B, L_A - L_B), High would be 0, Low would be 0, collapsing candle to a flat line.
        // Under interval arithmetic:
        // High = left.High - right.Low = 1050 - 950 = 100
        // Low = left.Low - right.High = 950 - 1050 = -100
        var ts = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var aligned = new ComparisonAlignedData(
            "AAPL",
            new[] { ts },
            new Dictionary<string, CandleData?[]>
            {
                ["AAPL"] = new CandleData?[] { new CandleData(ts, 1000m, 1050m, 950m, 1000m, 1000L) },
                ["MSFT"] = new CandleData?[] { new CandleData(ts, 1000m, 1050m, 950m, 1000m, 2000L) },
            },
            Array.Empty<string>());

        var expr = new SyntheticSymbolExpression("AAPL", "MSFT", SeriesOperation.Subtract, '-', true, "AAPL - MSFT");
        var candles = SyntheticCandleCalculator.ComputeCandles(aligned, expr);

        Assert.Single(candles);
        var c = candles[0];
        Assert.Equal(0m, c.Open);
        Assert.Equal(0m, c.Close);
        Assert.Equal(100m, c.High);
        Assert.Equal(-100m, c.Low);
        Assert.True(c.High > c.Low); // Wick is preserved, no collapse to flat line
        Assert.True(c.IsValid());
    }

    #endregion

    #region 3. SyntheticSymbolDataService Tests

    [Fact]
    public async Task LoadCandlesAsync_RegularSymbol_DelegatesToInner()
    {
        var mockInner = new Mock<IDataService>();
        var regularCandles = new List<CandleData>
        {
            new CandleData(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), 100m, 105m, 95m, 102m, 1000L)
        };

        mockInner.Setup(s => s.LoadCandlesAsync("7203-T", TimeFrame.D1, 100, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(regularCandles);

        var service = new SyntheticSymbolDataService(mockInner.Object);
        var result = await service.LoadCandlesAsync("7203-T", TimeFrame.D1, 100);

        Assert.Same(regularCandles, result);
        mockInner.Verify(s => s.LoadCandlesAsync("7203-T", TimeFrame.D1, 100, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LoadCandlesAsync_SyntheticD1_ComputesFromAlignedData()
    {
        var mockInner = new Mock<IDataService>();
        var mockAligner = new Mock<IComparisonDataAligner>();

        var ts = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var aligned = new ComparisonAlignedData(
            "7203-T",
            new[] { ts },
            new Dictionary<string, CandleData?[]>
            {
                ["7203-T"] = new CandleData?[] { new CandleData(ts, 3000m, 3050m, 2980m, 3020m, 10000L) },
                ["7267-T"] = new CandleData?[] { new CandleData(ts, 1500m, 1520m, 1490m, 1510m, 5000L) },
            },
            Array.Empty<string>());

        mockAligner.Setup(a => a.AlignAsync("7203-T", It.Is<IReadOnlyList<string>>(l => l.Contains("7267-T")), TimeFrame.D1, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(aligned);

        var service = new SyntheticSymbolDataService(mockInner.Object, mockAligner.Object);
        var result = await service.LoadCandlesAsync("7203 / 7267", TimeFrame.D1, 100);

        Assert.Single(result);
        var c = result[0];
        Assert.Equal(2.0m, c.Open); // 3000 / 1500
        Assert.Equal(2.0m, c.Close); // 3020 / 1510
        Assert.Equal(5000L, c.Volume); // Min(10000, 5000)
        Assert.True(c.IsValid());
    }

    [Fact]
    public async Task LoadCandlesAsync_SyntheticWeekly_AggregatesToPeriodStart()
    {
        var mockInner = new Mock<IDataService>();
        var mockAligner = new Mock<IComparisonDataAligner>();

        // Week of 2024-01-08 (Monday Jan 8 holiday, trading begins Tuesday Jan 9 to Friday Jan 12)
        var d1 = new DateTime(2024, 1, 9, 0, 0, 0, DateTimeKind.Utc);
        var d2 = new DateTime(2024, 1, 10, 0, 0, 0, DateTimeKind.Utc);
        var d3 = new DateTime(2024, 1, 11, 0, 0, 0, DateTimeKind.Utc);
        var d4 = new DateTime(2024, 1, 12, 0, 0, 0, DateTimeKind.Utc);
        var dailyTimestamps = new[] { d1, d2, d3, d4 };

        var leftDaily = dailyTimestamps.Select(t => (CandleData?)new CandleData(t, 200m, 210m, 195m, 205m, 1000L)).ToArray();
        var rightDaily = dailyTimestamps.Select(t => (CandleData?)new CandleData(t, 100m, 105m, 95m, 100m, 500L)).ToArray();

        var dailyAligned = new ComparisonAlignedData(
            "AAPL",
            dailyTimestamps,
            new Dictionary<string, CandleData?[]>
            {
                ["AAPL"] = leftDaily,
                ["MSFT"] = rightDaily,
            },
            Array.Empty<string>());

        mockAligner.Setup(a => a.AlignAsync("AAPL", It.Is<IReadOnlyList<string>>(l => l.Contains("MSFT")), TimeFrame.D1, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(dailyAligned);

        var service = new SyntheticSymbolDataService(mockInner.Object, mockAligner.Object);
        var result = await service.LoadCandlesAsync("AAPL - MSFT", TimeFrame.W1, 10);

        Assert.Single(result);
        var wBar = result[0];
        // Weekly bar timestamp must be PeriodStart (Monday 2024-01-08)
        Assert.Equal(new DateTime(2024, 1, 8, 0, 0, 0, DateTimeKind.Utc), wBar.Timestamp);
        Assert.Equal(100m, wBar.Open); // 200 - 100
        Assert.Equal(105m, wBar.Close); // 205 - 100
        Assert.Equal(2000L, wBar.Volume); // 4 days * 500
        Assert.True(wBar.IsValid());
    }

    [Fact]
    public async Task LoadCandlesAsync_SyntheticSymbol_IndicatorsCanBeCalculatedDirectly()
    {
        var mockInner = new Mock<IDataService>();
        var mockAligner = new Mock<IComparisonDataAligner>();

        // Generate 30 daily bars
        var startDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var timestamps = Enumerable.Range(0, 30).Select(i => startDate.AddDays(i)).ToArray();

        var leftDaily = timestamps.Select((t, i) => (CandleData?)new CandleData(t, 2000m + i * 10m, 2050m + i * 10m, 1980m + i * 10m, 2010m + i * 10m, 10000L)).ToArray();
        var rightDaily = timestamps.Select((t, i) => (CandleData?)new CandleData(t, 1000m + i * 5m, 1020m + i * 5m, 990m + i * 5m, 1005m + i * 5m, 5000L)).ToArray();

        var dailyAligned = new ComparisonAlignedData(
            "7203-T",
            timestamps,
            new Dictionary<string, CandleData?[]>
            {
                ["7203-T"] = leftDaily,
                ["7267-T"] = rightDaily,
            },
            Array.Empty<string>());

        mockAligner.Setup(a => a.AlignAsync("7203-T", It.Is<IReadOnlyList<string>>(l => l.Contains("7267-T")), TimeFrame.D1, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(dailyAligned);

        var service = new SyntheticSymbolDataService(mockInner.Object, mockAligner.Object);
        var candles = await service.LoadCandlesAsync("7203.T / 7267.T", TimeFrame.D1, 30);

        Assert.Equal(30, candles.Count);
        Assert.All(candles, c => Assert.True(c.IsValid()));

        // Verify that real indicators (RSI) compute without errors on the synthetic candles
        var indicator = IndicatorFactory.Default.Create(IndicatorType.RSI);
        Assert.NotNull(indicator);

        var coreCandles = candles.Select(c => new CoreCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume)).ToList();
        var indicatorResult = indicator.Calculate(coreCandles);
        Assert.NotNull(indicatorResult);
        Assert.Equal(30, indicatorResult.Count);
        // Valid RSI values exist
        Assert.Contains(indicatorResult, v => v.HasValue);
    }

    [Fact]
    public async Task LoadMonitoringObservationAsync_SyntheticSymbol_ReturnsNull()
    {
        var mockInner = new Mock<IDataService>();
        var service = new SyntheticSymbolDataService(mockInner.Object);

        var observation = await service.LoadMonitoringObservationAsync("7203 / 7267", TimeFrame.D1);
        Assert.Null(observation);

        mockInner.Verify(s => s.LoadMonitoringObservationAsync(It.IsAny<string>(), It.IsAny<TimeFrame>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LoadCandlesAsync_WhenAlignerThrows_ReturnsEmptyArrayGracefully()
    {
        var mockInner = new Mock<IDataService>();
        var mockAligner = new Mock<IComparisonDataAligner>();

        mockAligner.Setup(a => a.AlignAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<TimeFrame>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("No data found for primary symbol: AAPL"));

        var service = new SyntheticSymbolDataService(mockInner.Object, mockAligner.Object);

        var result = await service.LoadCandlesAsync("AAPL + MSFT", TimeFrame.D1, 100);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Theory]
    [InlineData(10, 100)] // Math.Max(10 * 7 + 14, 100) = 100
    [InlineData(20, 154)] // 20 * 7 + 14 = 154
    [InlineData(100, 714)] // 100 * 7 + 14 = 714
    [InlineData(int.MaxValue, 350014)] // Clamped to 50000 -> 50000 * 7 + 14 = 350014, prevents overflow
    public async Task LoadCandlesAsync_SyntheticWeekly_UsesOptimizedFetchMultiplierAndClampsMax(int requestedCount, int expectedDailyFetch)
    {
        var mockInner = new Mock<IDataService>();
        var mockAligner = new Mock<IComparisonDataAligner>();

        int capturedFetchCount = -1;
        mockAligner.Setup(a => a.AlignAsync(
                "AAPL",
                It.Is<IReadOnlyList<string>>(l => l.Contains("MSFT")),
                TimeFrame.D1,
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<string>, TimeFrame, int, CancellationToken>((_, _, _, count, _) =>
            {
                capturedFetchCount = count;
            })
            .ReturnsAsync(new ComparisonAlignedData("AAPL", Array.Empty<DateTime>(), new Dictionary<string, CandleData?[]>(), Array.Empty<string>()));

        var service = new SyntheticSymbolDataService(mockInner.Object, mockAligner.Object);
        await service.LoadCandlesAsync("AAPL - MSFT", TimeFrame.W1, requestedCount);

        Assert.Equal(expectedDailyFetch, capturedFetchCount);
    }

    [Theory]
    [InlineData(2, 100)] // Math.Max(2 * 25 + 35, 100) = 100
    [InlineData(10, 285)] // 10 * 25 + 35 = 285
    [InlineData(100, 2535)] // 100 * 25 + 35 = 2535
    [InlineData(int.MaxValue, 500035)] // Clamped to 20000 -> 20000 * 25 + 35 = 500035, prevents overflow
    public async Task LoadCandlesAsync_SyntheticMonthly_UsesOptimizedFetchMultiplierAndClampsMax(int requestedCount, int expectedDailyFetch)
    {
        var mockInner = new Mock<IDataService>();
        var mockAligner = new Mock<IComparisonDataAligner>();

        int capturedFetchCount = -1;
        mockAligner.Setup(a => a.AlignAsync(
                "AAPL",
                It.Is<IReadOnlyList<string>>(l => l.Contains("MSFT")),
                TimeFrame.D1,
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<string>, TimeFrame, int, CancellationToken>((_, _, _, count, _) =>
            {
                capturedFetchCount = count;
            })
            .ReturnsAsync(new ComparisonAlignedData("AAPL", Array.Empty<DateTime>(), new Dictionary<string, CandleData?[]>(), Array.Empty<string>()));

        var service = new SyntheticSymbolDataService(mockInner.Object, mockAligner.Object);
        await service.LoadCandlesAsync("AAPL - MSFT", TimeFrame.MN1, requestedCount);

        Assert.Equal(expectedDailyFetch, capturedFetchCount);
    }

    [Fact]
    public async Task LoadCandlesAsync_SyntheticPair_WhenRightLegExcludedByPrimaryTimeline_FallsBackToReverseAlignment()
    {
        var mockInner = new Mock<IDataService>();
        var mockAligner = new Mock<IComparisonDataAligner>();

        var ts = new DateTime(2025, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var timestamps = new[] { ts };

        var candles2681 = new CandleData?[] { new CandleData(ts, 100m, 110m, 95m, 105m, 1000L) };
        var candles5016 = new CandleData?[] { new CandleData(ts, 50m, 55m, 45m, 50m, 500L) };

        // 1. Primary alignment: "2681-T" is primary, "5016-T" is excluded (missing from Series dictionary due to fragmentation)
        var primaryExcludedAligned = new ComparisonAlignedData(
            "2681-T",
            timestamps,
            new Dictionary<string, CandleData?[]>
            {
                ["2681-T"] = candles2681,
            },
            new[] { "5016-T excluded due to high data fragmentation (53.6% missing)." });

        mockAligner.Setup(a => a.AlignAsync(
                "2681-T",
                It.Is<IReadOnlyList<string>>(l => l.Contains("5016-T")),
                TimeFrame.D1,
                840,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(primaryExcludedAligned);

        // 2. Reverse alignment: "5016-T" is primary, both legs are present
        var reverseSuccessAligned = new ComparisonAlignedData(
            "5016-T",
            timestamps,
            new Dictionary<string, CandleData?[]>
            {
                ["5016-T"] = candles5016,
                ["2681-T"] = candles2681,
            },
            Array.Empty<string>());

        mockAligner.Setup(a => a.AlignAsync(
                "5016-T",
                It.Is<IReadOnlyList<string>>(l => l.Contains("2681-T")),
                TimeFrame.D1,
                840,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(reverseSuccessAligned);

        var service = new SyntheticSymbolDataService(mockInner.Object, mockAligner.Object);
        var candles = await service.LoadCandlesAsync("2681-T / 5016-T", TimeFrame.D1, 840);

        Assert.Single(candles);
        var candle = candles[0];
        Assert.Equal(ts, candle.Timestamp);
        // 105 / 50 = 2.1
        Assert.Equal(2.1m, candle.Close);
        Assert.True(candle.IsValid());

        mockAligner.Verify(a => a.AlignAsync("2681-T", It.IsAny<IReadOnlyList<string>>(), TimeFrame.D1, 840, It.IsAny<CancellationToken>()), Times.Once);
        mockAligner.Verify(a => a.AlignAsync("5016-T", It.IsAny<IReadOnlyList<string>>(), TimeFrame.D1, 840, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LoadCandlesAsync_SyntheticPair_PeriodFrame_WhenDailyRightLegExcluded_FallsBackToReverseDailyAlignment()
    {
        var mockInner = new Mock<IDataService>();
        var mockAligner = new Mock<IComparisonDataAligner>();

        var ts = new DateTime(2025, 4, 1, 0, 0, 0, DateTimeKind.Utc); // Tuesday
        var timestamps = new[] { ts };

        var candles2681 = new CandleData?[] { new CandleData(ts, 100m, 110m, 95m, 105m, 1000L) };
        var candles5016 = new CandleData?[] { new CandleData(ts, 50m, 55m, 45m, 50m, 500L) };

        // 1. Primary daily alignment excludes 5016-T
        var primaryExcludedAligned = new ComparisonAlignedData(
            "2681-T",
            timestamps,
            new Dictionary<string, CandleData?[]>
            {
                ["2681-T"] = candles2681,
            },
            new[] { "5016-T excluded due to high data fragmentation." });

        mockAligner.Setup(a => a.AlignAsync(
                "2681-T",
                It.Is<IReadOnlyList<string>>(l => l.Contains("5016-T")),
                TimeFrame.D1,
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(primaryExcludedAligned);

        // 2. Reverse daily alignment succeeds
        var reverseSuccessAligned = new ComparisonAlignedData(
            "5016-T",
            timestamps,
            new Dictionary<string, CandleData?[]>
            {
                ["5016-T"] = candles5016,
                ["2681-T"] = candles2681,
            },
            Array.Empty<string>());

        mockAligner.Setup(a => a.AlignAsync(
                "5016-T",
                It.Is<IReadOnlyList<string>>(l => l.Contains("2681-T")),
                TimeFrame.D1,
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(reverseSuccessAligned);

        var service = new SyntheticSymbolDataService(mockInner.Object, mockAligner.Object);
        var candles = await service.LoadCandlesAsync("2681-T / 5016-T", TimeFrame.W1, 10);

        Assert.Single(candles);
        var candle = candles[0];
        // Period start Monday of 2025-04-01 is 2025-03-31
        Assert.Equal(new DateTime(2025, 3, 31, 0, 0, 0, DateTimeKind.Utc), candle.Timestamp);
        Assert.Equal(2.1m, candle.Close);
        Assert.True(candle.IsValid());
    }

    [Fact]
    public async Task LoadCandlesAsync_SyntheticPair_WithRealAligner_SymmetricBarCountAcrossOrientations()
    {
        var mockInner = new Mock<IDataService>();

        var baseDate = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        // Symbol A has 1000 daily bars
        var candlesA = Enumerable.Range(0, 1000)
            .Select(i => new CandleData(baseDate.AddDays(i), 100m, 110m, 90m, 100m, 1000L))
            .ToList();

        // Symbol B has only 300 daily bars (the latest 300 days: from day 700 to 999)
        var candlesB = Enumerable.Range(700, 300)
            .Select(i => new CandleData(baseDate.AddDays(i), 50m, 55m, 45m, 50m, 500L))
            .ToList();

        mockInner.Setup(d => d.LoadCandlesAsync("TICKER-A", TimeFrame.D1, It.IsAny<int>()))
            .ReturnsAsync((string _, TimeFrame _, int count) =>
                count > 0 && candlesA.Count > count ? candlesA.TakeLast(count).ToList() : candlesA);

        mockInner.Setup(d => d.LoadCandlesAsync("TICKER-B", TimeFrame.D1, It.IsAny<int>()))
            .ReturnsAsync((string _, TimeFrame _, int count) =>
                count > 0 && candlesB.Count > count ? candlesB.TakeLast(count).ToList() : candlesB);

        mockInner.Setup(d => d.LoadCandlesAsync("TICKER-A", TimeFrame.D1, It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string s, TimeFrame tf, int count, bool _, CancellationToken _) =>
                mockInner.Object.LoadCandlesAsync(s, tf, count).Result);

        mockInner.Setup(d => d.LoadCandlesAsync("TICKER-B", TimeFrame.D1, It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string s, TimeFrame tf, int count, bool _, CancellationToken _) =>
                mockInner.Object.LoadCandlesAsync(s, tf, count).Result);

        var realAligner = new ComparisonDataAligner(mockInner.Object);
        var service = new SyntheticSymbolDataService(mockInner.Object, realAligner);

        // 1. Shorter leg as left leg: TICKER-B / TICKER-A
        var candlesBOverA = await service.LoadCandlesAsync("TICKER-B / TICKER-A", TimeFrame.D1, 1000);
        Assert.Equal(300, candlesBOverA.Count);

        // 2. Longer leg as left leg: TICKER-A / TICKER-B
        // With the fix, reverse alignment activates when TICKER-B is excluded by 1000-bar timeline, yielding 300 bars
        var candlesAOverB = await service.LoadCandlesAsync("TICKER-A / TICKER-B", TimeFrame.D1, 1000);
        Assert.Equal(300, candlesAOverB.Count);

        // Verify timestamps match and prices are reciprocal: 100 / 50 = 2.0, 50 / 100 = 0.5 -> 2.0 * 0.5 = 1.0
        for (int i = 0; i < 300; i++)
        {
            Assert.Equal(candlesBOverA[i].Timestamp, candlesAOverB[i].Timestamp);
            Assert.Equal(2.0m, candlesAOverB[i].Close);
            Assert.Equal(0.5m, candlesBOverA[i].Close);
            Assert.Equal(1.0m, candlesAOverB[i].Close * candlesBOverA[i].Close);
        }
    }

    [Fact]
    public async Task LoadCandlesAsync_SyntheticPair_Real2681And5016_BothOrientationsProduce390Candles()
    {
        string dailyPath = @"Data\Daily";
        if (!System.IO.File.Exists(System.IO.Path.Combine(dailyPath, "2681-T.parquet")) || !System.IO.File.Exists(System.IO.Path.Combine(dailyPath, "5016-T.parquet")))
        {
            return;
        }

        using var dbManager = new DuckDBConnectionManager();
        var settings = Microsoft.Extensions.Options.Options.Create(new MarketDataSettings { DailyDataPath = dailyPath });
        var parquetService = new ParquetDataService(dbManager, settings);
        var aligner = new ComparisonDataAligner(parquetService);
        var service = new SyntheticSymbolDataService(parquetService, aligner);

        // 1. Shorter leg as left leg: 5016-T / 2681-T
        var candles5016Over2681 = await service.LoadCandlesAsync("5016-T / 2681-T", TimeFrame.D1, 840);
        Assert.Equal(390, candles5016Over2681.Count);

        // 2. Longer leg as left leg: 2681-T / 5016-T (previously returned 0 candles)
        var candles2681Over5016 = await service.LoadCandlesAsync("2681-T / 5016-T", TimeFrame.D1, 840);
        Assert.Equal(390, candles2681Over5016.Count);

        // Verify timestamps match and prices are reciprocal
        for (int i = 0; i < 390; i++)
        {
            Assert.Equal(candles5016Over2681[i].Timestamp, candles2681Over5016[i].Timestamp);
            decimal product = candles5016Over2681[i].Close * candles2681Over5016[i].Close;
            Assert.True(Math.Abs(product - 1.0m) < 0.0001m, $"Expected product close to 1.0 at index {i}, got {product}");
        }
    }

    #endregion
}
