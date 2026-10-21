#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.Core.Utilities;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>The evaluator reads each ticker's metric values through the Screener's extractor and history sizing, unchanged.</summary>
public class ScatterMetricEvaluatorTests
{
    private const int BarCount = 600;

    private static readonly ScreenerValueExtractor Extractor = ScreenerValueExtractor.Default;

    private static ScatterMetricDefinition Rsi(int offset = 0) => ScatterMetricTestKit.Indicator(IndicatorType.RSI, offset, null, ("Period", 14));

    private static ScatterMetricDefinition Sma(int period = 20) => ScatterMetricTestKit.Indicator(IndicatorType.SMA, 0, null, ("Period", period));

    private static ScatterMetricDefinition ColumnNamed(string name) => new()
    {
        Source = new ScreenerIndicatorSideConfig
        {
            IndicatorType = IndicatorType.SMA,
            OutputName = name,
            CategoryType = ScreenerItemCategoryType.Column,
            TimeFrame = TimeFrame.D1,
        },
    };

    private static IReadOnlyDictionary<string, IReadOnlyList<CandleData>> Series(params string[] tickers)
        => tickers.Select((ticker, i) => (ticker, bars: (IReadOnlyList<CandleData>)ScatterMetricTestKit.Bars(BarCount, seed: 1000 + i)))
            .ToDictionary(pair => pair.ticker, pair => pair.bars);

    private static ScatterMetricEvaluator Create(
        FakeDataService data,
        FakeMarketDataProvider? provider = null,
        int parallelism = ScatterMetricLimits_DefaultParallelism,
        IScreenerValueExtractor? extractor = null)
        => new(
            data,
            provider is null ? Array.Empty<IMarketDataProvider>() : new IMarketDataProvider[] { provider },
            extractor ?? Extractor,
            new ScatterMetricTestSettings { ScatterMaxParallelism = parallelism },
            new ScreenerCatalogProvider());

    private const int ScatterMetricLimits_DefaultParallelism = StockAnalyzer.Core.Models.Settings.ScatterMetricLimits.DefaultMaxParallelism;

    private static double Direct(ScatterMetricDefinition metric, IReadOnlyList<CandleData> bars, TickerMetadata metadata = default)
        => (double)Extractor.ExtractValueNullable(metric.Source, bars, metadata)!.Value;

    [Fact]
    public async Task IndicatorValues_EqualTheScreenerExtractorsValue_AsDouble()
    {
        var series = Series("AAA", "BBB");
        var evaluator = Create(new FakeDataService(series));

        var result = await evaluator.ComputeAsync(new[] { "AAA", "BBB" }, Rsi(), Sma(), CancellationToken.None);

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(2, result.ValidCount);
        Assert.Equal(0, result.FailedCount);
        foreach (ScatterEvaluationItem item in result.Items)
        {
            Assert.Equal(Direct(Rsi(), series[item.Ticker]), item.X);
            Assert.Equal(Direct(Sma(), series[item.Ticker]), item.Y);
        }
    }

    [Fact]
    public async Task Offset_ReadsTheBarsBeforeTheTickersLatestBar()
    {
        var series = Series("AAA");
        var evaluator = Create(new FakeDataService(series));
        var close = ScatterMetricTestKit.Indicator(IndicatorType.Price, 0, "Close");
        var closeTwoBarsBack = ScatterMetricTestKit.Indicator(IndicatorType.Price, 2, "Close");

        var result = await evaluator.ComputeAsync(new[] { "AAA" }, close, closeTwoBarsBack, CancellationToken.None);

        IReadOnlyList<CandleData> bars = series["AAA"];
        Assert.Equal((double)bars[^1].Close, result.Items[0].X);
        Assert.Equal((double)bars[^3].Close, result.Items[0].Y);
    }

    [Fact]
    public async Task IndicatorOffset_EqualsTheValueOnTheSeriesWithoutTheNewestBars()
    {
        var series = Series("AAA");
        var evaluator = Create(new FakeDataService(series));

        var result = await evaluator.ComputeAsync(new[] { "AAA" }, Rsi(offset: 1), Rsi(), CancellationToken.None);

        IReadOnlyList<CandleData> older = series["AAA"].Take(BarCount - 1).ToList();
        Assert.Equal(Direct(Rsi(), older), result.Items[0].X);
    }

    [Fact]
    public async Task Columns_ReadTheBarSeriesAndTheMetadata()
    {
        var series = Series("AAA");
        var metadata = new Dictionary<string, TickerMetadata>
        {
            ["AAA"] = new TickerMetadata("A", "A Inc", "US", "Tech", "Software", "USD") { ReturnOnEquity = 0.25m },
        };
        var provider = new FakeMarketDataProvider(series, metadata);
        var evaluator = Create(new FakeDataService(series), provider);

        var result = await evaluator.ComputeAsync(new[] { "AAA" }, ColumnNamed("Close"), ColumnNamed("ReturnOnEquity"), CancellationToken.None);

        Assert.Equal((double)series["AAA"][^1].Close, result.Items[0].X);
        Assert.Equal(0.25, result.Items[0].Y);
        Assert.Equal(1, provider.MetadataRequests);
    }

    [Fact]
    public async Task MetadataIsOnlyRead_WhenAColumnIsPlotted()
    {
        var series = Series("AAA", "BBB");
        var provider = new FakeMarketDataProvider(series, new Dictionary<string, TickerMetadata>());
        var evaluator = Create(new FakeDataService(series), provider);

        await evaluator.ComputeAsync(new[] { "AAA", "BBB" }, Rsi(), Sma(), CancellationToken.None);

        Assert.Equal(0, provider.MetadataRequests);
    }

    [Fact]
    public async Task AThrowingMetadataProvider_LeavesTheColumnWithoutAValue_AndIsNotAFailure()
    {
        var series = Series("AAA");
        var provider = new FakeMarketDataProvider(series); // GetMetadataAsync throws
        var evaluator = Create(new FakeDataService(series), provider);

        var result = await evaluator.ComputeAsync(new[] { "AAA" }, Rsi(), ColumnNamed("ReturnOnEquity"), CancellationToken.None);

        Assert.Equal(0, result.FailedCount);
        Assert.Equal(0, result.ValidCount);
        Assert.False(double.IsNaN(result.Items[0].X));
        Assert.True(double.IsNaN(result.Items[0].Y));
    }

    [Fact]
    public async Task AnEmptyHistory_GivesNaN_AndIsNotAFailure()
    {
        var series = Series("AAA");
        var evaluator = Create(new FakeDataService(series));

        var result = await evaluator.ComputeAsync(new[] { "AAA", "NO_DATA" }, Rsi(), Sma(), CancellationToken.None);

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(1, result.ValidCount);
        Assert.Equal(0, result.FailedCount);
        Assert.True(double.IsNaN(result.Items[1].X));
        Assert.True(double.IsNaN(result.Items[1].Y));
    }

    [Fact]
    public async Task ADataServiceException_GivesNaN_CountsAFailure_AndKeepsTheOthers()
    {
        var series = Series("AAA", "BAD", "CCC");
        var evaluator = Create(new FakeDataService(series, failing: new HashSet<string> { "BAD" }));

        var result = await evaluator.ComputeAsync(new[] { "AAA", "BAD", "CCC" }, Rsi(), Sma(), CancellationToken.None);

        Assert.Equal(3, result.TotalCount);
        Assert.Equal(2, result.ValidCount);
        Assert.Equal(1, result.FailedCount);
        Assert.True(double.IsNaN(result.Items[1].X));
        Assert.True(double.IsNaN(result.Items[1].Y));
    }

    [Fact]
    public async Task TheResultKeepsTheOrderOfTheInput_EvenWhenTheTickersFinishInAnotherOrder()
    {
        string[] tickers = Enumerable.Range(0, 50).Select(i => $"T{i:D2}").ToArray();
        var series = Series(tickers);
        // The first tickers wait longest, so the completion order is the reverse of the input order.
        var data = new FakeDataService(series, beforeLoad: symbol => Task.Delay(int.Parse(symbol[1..]) < 25 ? 30 : 0));
        var evaluator = Create(data, parallelism: 10);

        var result = await evaluator.ComputeAsync(tickers, Rsi(), Sma(), CancellationToken.None);

        Assert.Equal(tickers, result.Items.Select(item => item.Ticker).ToArray());
        foreach (ScatterEvaluationItem item in result.Items)
        {
            Assert.Equal(Direct(Rsi(), series[item.Ticker]), item.X);
            Assert.Equal(Direct(Sma(), series[item.Ticker]), item.Y);
        }
    }

    [Fact]
    public async Task AParallelismOfOne_GivesTheSameResultAsTheDefault()
    {
        string[] tickers = Enumerable.Range(0, 20).Select(i => $"T{i:D2}").ToArray();
        var series = Series(tickers);

        var sequential = await Create(new FakeDataService(series), parallelism: 1).ComputeAsync(tickers, Rsi(), Sma(), CancellationToken.None);
        var parallel = await Create(new FakeDataService(series)).ComputeAsync(tickers, Rsi(), Sma(), CancellationToken.None);

        Assert.Equal(parallel.Items.ToArray(), sequential.Items.ToArray());
        Assert.Equal((parallel.TotalCount, parallel.ValidCount, parallel.FailedCount), (sequential.TotalCount, sequential.ValidCount, sequential.FailedCount));
    }

    [Fact]
    public async Task ACancelledToken_PropagatesTheCancellation()
    {
        var series = Series("AAA", "BBB");
        var evaluator = Create(new FakeDataService(series));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => evaluator.ComputeAsync(new[] { "AAA", "BBB" }, Rsi(), Sma(), cts.Token));
    }

    [Fact]
    public async Task ACancellationDuringTheWork_IsNotCountedAsAFailure()
    {
        string[] tickers = Enumerable.Range(0, 30).Select(i => $"T{i:D2}").ToArray();
        var series = Series(tickers);
        using var cts = new CancellationTokenSource();
        var data = new FakeDataService(series, beforeLoad: async _ =>
        {
            cts.Cancel();
            await Task.Delay(5);
            cts.Token.ThrowIfCancellationRequested();
        });
        var evaluator = Create(data, parallelism: 2);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => evaluator.ComputeAsync(tickers, Rsi(), Sma(), cts.Token));
    }

    [Fact]
    public async Task AMetricThatBreaksTheSharedRules_IsRefusedWithAnArgumentException()
    {
        var evaluator = Create(new FakeDataService(Series("AAA")));
        // Week became a supported time frame (the 2026-10-22 extension); an hourly metric is still refused, so this case moved from W1 to H1.
        ScatterMetricDefinition notDaily = Rsi();
        notDaily.Source.TimeFrame = TimeFrame.H1;
        ScatterMetricDefinition tooFar = Rsi(offset: new ScatterMetricTestSettings().ScatterMaxMetricOffset + 1);

        var x = await Assert.ThrowsAsync<ArgumentException>(() => evaluator.ComputeAsync(new[] { "AAA" }, notDaily, Rsi(), CancellationToken.None));
        var y = await Assert.ThrowsAsync<ArgumentException>(() => evaluator.ComputeAsync(new[] { "AAA" }, Rsi(), tooFar, CancellationToken.None));

        Assert.Equal("x", x.ParamName);
        Assert.Equal("y", y.ParamName);
    }

    [Fact]
    public async Task TheSameMetricOnBothAxes_IsAllowed()
    {
        var series = Series("AAA");
        var evaluator = Create(new FakeDataService(series));

        var result = await evaluator.ComputeAsync(new[] { "AAA" }, Rsi(), Rsi(), CancellationToken.None);

        Assert.Equal(result.Items[0].X, result.Items[0].Y);
        Assert.Equal(1, result.ValidCount);
    }

    [Fact]
    public async Task NoTickers_GiveAnEmptyResult_WithTheFixedRangesStillResolved()
    {
        var evaluator = Create(new FakeDataService(Series("AAA")));

        var empty = await evaluator.ComputeAsync(Array.Empty<string>(), Rsi(), Sma(), CancellationToken.None);
        var missing = await evaluator.ComputeAsync(null!, Rsi(), Sma(), CancellationToken.None);

        foreach (var result in new[] { empty, missing })
        {
            Assert.Empty(result.Items);
            Assert.Equal(0, result.TotalCount);
            Assert.Equal(0, result.ValidCount);
            Assert.Equal(new AxisRange(0, 100), result.XFixedRange);
            Assert.Null(result.YFixedRange);
        }
    }

    [Fact]
    public async Task FixedRanges_AreTheIndicatorsRange_ForEachAxisSeparately()
    {
        var evaluator = Create(new FakeDataService(Series("AAA")));

        var result = await evaluator.ComputeAsync(new[] { "AAA" }, Sma(), Rsi(), CancellationToken.None);

        Assert.Null(result.XFixedRange);
        Assert.Equal(new AxisRange(0, 100), result.YFixedRange);
    }

    [Fact]
    public async Task TheHistoryRequested_IsTheLargerOfTheTwoMetricsScreenerRequirements()
    {
        var series = Series("AAA");
        var data = new FakeDataService(series);
        var evaluator = Create(data);
        ScatterMetricDefinition longSma = Sma(period: 400);

        await evaluator.ComputeAsync(new[] { "AAA" }, Rsi(), longSma, CancellationToken.None);

        int forLongSma = ScreenerHistoryRequirements.GetRequiredCandleCount(
            new ScreenerIndicatorEntry { LeftHand = longSma.Source.Clone(), TargetMode = RightHandTargetMode.NumericValue });
        int forRsi = ScreenerHistoryRequirements.GetRequiredCandleCount(
            new ScreenerIndicatorEntry { LeftHand = Rsi().Source.Clone(), TargetMode = RightHandTargetMode.NumericValue });
        Assert.True(forLongSma > forRsi, "the test needs the two requirements to differ");
        Assert.Equal(forLongSma, data.RequestedCounts["AAA"]);
    }

    [Fact]
    public async Task AFullHistoryIndicator_RequestsAllHistory_ForBothAxes()
    {
        var series = Series("AAA");
        var data = new FakeDataService(series);
        var evaluator = Create(data);

        await evaluator.ComputeAsync(new[] { "AAA" }, Rsi(), ScatterMetricTestKit.Indicator(IndicatorType.OBV), CancellationToken.None);

        Assert.Equal(0, data.RequestedCounts["AAA"]);
    }

    // ===== Week and Month =====

    /// <summary>Weekly bars as the Weekly files hold them: one bar per week, dated on the Monday that opens it.</summary>
    private static List<CandleData> Weekly(int count, int seed)
        => ScatterMetricTestKit.Bars(count, seed).Select((bar, i) => bar with { Timestamp = new DateTime(2018, 1, 1).AddDays(7 * i) }).ToList();

    /// <summary>Monthly bars as the Monthly files hold them: one bar per month, dated on the 1st.</summary>
    private static List<CandleData> Monthly(int count, int seed)
        => ScatterMetricTestKit.Bars(count, seed).Select((bar, i) => bar with { Timestamp = new DateTime(2000, 1, 1).AddMonths(i) }).ToList();

    // Lengths of the Week / Month fixtures: enough rows for the default RSI / SMA of the tests, and one history shorter than any Offset used.
    private const int WeekBars = 40;
    private const int LongWeekBars = 60;
    private const int MonthBars = 30;
    private const int LongMonthBars = 40;
    private const int ShortHistoryBars = 3;
    private const int DaySeed = 1000;
    private const int WeekSeed = 2000;
    private const int MonthSeed = 3000;

    private static IReadOnlyDictionary<(string Symbol, TimeFrame Frame), IReadOnlyList<CandleData>> Frames(
        params (string Ticker, TimeFrame Frame, IReadOnlyList<CandleData> Bars)[] entries)
        => entries.ToDictionary(entry => (entry.Ticker, entry.Frame), entry => entry.Bars);

    /// <summary>The value the extractor gives for <paramref name="metric"/> over the bars of its own time frame (the extractor itself reads a Day source).</summary>
    private static double DirectOnFrame(ScatterMetricDefinition metric, IReadOnlyList<CandleData> bars, TickerMetadata metadata = default)
        => (double)Extractor.ExtractValueNullable(ScatterMetricTestKit.DaySourceOf(metric), bars, metadata)!.Value;

    private static TimeFrame[] Sorted(IReadOnlyCollection<TimeFrame> frames) => frames.OrderBy(frame => (int)frame).ToArray();

    [Fact]
    public async Task WeekColumnClose_IsTheLatestWeeklyBar_AsItIs()
    {
        List<CandleData> weekly = Weekly(WeekBars, WeekSeed);
        // The last weekly bar stands for a week that is not over; it is used as the file holds it.
        weekly[^1] = weekly[^1] with { Volume = 1 };
        IReadOnlyList<CandleData> daily = Series("AAA")["AAA"];
        var data = new FakeDataService(Frames(("AAA", TimeFrame.D1, daily), ("AAA", TimeFrame.W1, weekly)));

        var result = await Create(data).ComputeAsync(new[] { "AAA" }, ScatterMetricTestKit.OnFrame(ColumnNamed("Close"), TimeFrame.W1), ScatterMetricTestKit.OnFrame(ColumnNamed("Volume"), TimeFrame.W1), CancellationToken.None);

        Assert.Equal((double)weekly[^1].Close, result.Items[0].X);
        Assert.Equal(1d, result.Items[0].Y);
        Assert.NotEqual((double)daily[^1].Close, result.Items[0].X);
    }

    [Fact]
    public async Task WeekOffset_ReadsTheWeeklyBarsBeforeTheLatestWeek()
    {
        List<CandleData> weekly = Weekly(WeekBars, WeekSeed);
        var data = new FakeDataService(Frames(("AAA", TimeFrame.W1, weekly)));
        ScatterMetricDefinition oneWeekBack = ScatterMetricTestKit.OnFrame(ColumnNamed("Close"), TimeFrame.W1);
        oneWeekBack.Source.Offset = 1;
        ScatterMetricDefinition threeWeeksBack = ScatterMetricTestKit.OnFrame(ColumnNamed("Close"), TimeFrame.W1);
        threeWeeksBack.Source.Offset = 3;

        var result = await Create(data).ComputeAsync(new[] { "AAA" }, oneWeekBack, threeWeeksBack, CancellationToken.None);

        Assert.Equal((double)weekly[^2].Close, result.Items[0].X);
        Assert.Equal((double)weekly[^4].Close, result.Items[0].Y);
    }

    [Fact]
    public async Task MonthColumnClose_IsTheLatestMonthlyBar_AndOffsetOneIsThePreviousMonth()
    {
        List<CandleData> monthly = Monthly(MonthBars, MonthSeed);
        var data = new FakeDataService(Frames(("AAA", TimeFrame.MN1, monthly)));
        ScatterMetricDefinition previous = ScatterMetricTestKit.OnFrame(ColumnNamed("Close"), TimeFrame.MN1);
        previous.Source.Offset = 1;

        var result = await Create(data).ComputeAsync(new[] { "AAA" }, ScatterMetricTestKit.OnFrame(ColumnNamed("Close"), TimeFrame.MN1), previous, CancellationToken.None);

        Assert.Equal((double)monthly[^1].Close, result.Items[0].X);
        Assert.Equal((double)monthly[^2].Close, result.Items[0].Y);
    }

    [Fact]
    public async Task WeekAndMonthIndicators_AreComputedOnTheBarsOfTheirOwnTimeFrame()
    {
        List<CandleData> weekly = Weekly(LongWeekBars, WeekSeed);
        List<CandleData> monthly = Monthly(LongMonthBars, MonthSeed);
        var data = new FakeDataService(Frames(("AAA", TimeFrame.W1, weekly), ("AAA", TimeFrame.MN1, monthly)));
        ScatterMetricDefinition weeklySma = ScatterMetricTestKit.OnFrame(Sma(3), TimeFrame.W1);
        ScatterMetricDefinition monthlyRsi = ScatterMetricTestKit.OnFrame(Rsi(), TimeFrame.MN1);

        var result = await Create(data).ComputeAsync(new[] { "AAA" }, weeklySma, monthlyRsi, CancellationToken.None);

        Assert.Equal(DirectOnFrame(weeklySma, weekly), result.Items[0].X);
        Assert.Equal(DirectOnFrame(monthlyRsi, monthly), result.Items[0].Y);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(1, result.ValidCount);
        Assert.Equal(new[] { TimeFrame.W1, TimeFrame.MN1 }, Sorted(data.RequestedTimeFrames));
    }

    [Fact]
    public async Task WeekColumnChangePercent_IsTheChangeFromThePreviousWeeklyClose()
    {
        List<CandleData> weekly = Weekly(WeekBars, WeekSeed);
        var data = new FakeDataService(Frames(("AAA", TimeFrame.W1, weekly)));

        var result = await Create(data).ComputeAsync(new[] { "AAA" }, ScatterMetricTestKit.OnFrame(ColumnNamed("Change %"), TimeFrame.W1), ScatterMetricTestKit.OnFrame(ColumnNamed("Close"), TimeFrame.W1), CancellationToken.None);

        Assert.Equal((double)PriceChange.FromPreviousClose(weekly, weekly.Count - 1).Percent, result.Items[0].X);
        Assert.NotEqual(0d, result.Items[0].X);
    }

    [Fact]
    public async Task AHistoryShorterThanTheOffset_GivesNaN_AndIsNotAFailure()
    {
        List<CandleData> weekly = Weekly(ShortHistoryBars, WeekSeed);
        var data = new FakeDataService(Frames(("AAA", TimeFrame.W1, weekly)));
        ScatterMetricDefinition tooFarBack = ScatterMetricTestKit.OnFrame(ColumnNamed("Close"), TimeFrame.W1);
        tooFarBack.Source.Offset = 5;

        var result = await Create(data).ComputeAsync(new[] { "AAA" }, tooFarBack, ScatterMetricTestKit.OnFrame(ColumnNamed("Close"), TimeFrame.W1), CancellationToken.None);

        Assert.True(double.IsNaN(result.Items[0].X));
        Assert.Equal((double)weekly[^1].Close, result.Items[0].Y);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(0, result.ValidCount);
    }

    [Fact]
    public async Task ATickerWithNoWeeklyFile_GivesNaN_AndIsNotAFailure()
    {
        List<CandleData> weekly = Weekly(WeekBars, WeekSeed);
        IReadOnlyList<CandleData> daily = Series("AAA")["AAA"];
        var data = new FakeDataService(Frames(
            ("AAA", TimeFrame.D1, daily), ("AAA", TimeFrame.W1, weekly),
            ("NOWEEKLY", TimeFrame.D1, daily)));

        var result = await Create(data).ComputeAsync(new[] { "AAA", "NOWEEKLY" }, ScatterMetricTestKit.OnFrame(ColumnNamed("Close"), TimeFrame.W1), Rsi(), CancellationToken.None);

        Assert.Equal(0, result.FailedCount);
        Assert.Equal(1, result.ValidCount);
        Assert.True(double.IsNaN(result.Items[1].X));
        Assert.Equal(DirectOnFrame(Rsi(), daily), result.Items[1].Y);
    }

    [Fact]
    public async Task AWeeklyMetric_NextToADayFundamental_ReadsTheMetadataOnce()
    {
        List<CandleData> weekly = Weekly(WeekBars, WeekSeed);
        IReadOnlyList<CandleData> daily = Series("AAA")["AAA"];
        var metadata = new Dictionary<string, TickerMetadata>
        {
            ["AAA"] = new TickerMetadata("A", "A Inc", "US", "Tech", "Software", "USD") { ReturnOnEquity = 0.25m },
        };
        var provider = new FakeMarketDataProvider(Series("AAA"), metadata);
        var data = new FakeDataService(Frames(("AAA", TimeFrame.D1, daily), ("AAA", TimeFrame.W1, weekly)));

        var result = await Create(data, provider).ComputeAsync(new[] { "AAA" }, ScatterMetricTestKit.OnFrame(ColumnNamed("Close"), TimeFrame.W1), ColumnNamed("ReturnOnEquity"), CancellationToken.None);

        Assert.Equal((double)weekly[^1].Close, result.Items[0].X);
        Assert.Equal(0.25, result.Items[0].Y);
        Assert.Equal(1, provider.MetadataRequests);
    }

    [Theory]
    [InlineData(TimeFrame.W1)]
    [InlineData(TimeFrame.MN1)]
    public async Task TheFixedRangeOfAnIndicator_IsTheSameOnWeekAndMonth(TimeFrame frame)
    {
        var data = new FakeDataService(Frames(("AAA", frame, Weekly(WeekBars, WeekSeed))));

        var result = await Create(data).ComputeAsync(new[] { "AAA" }, ScatterMetricTestKit.OnFrame(Rsi(), frame), ScatterMetricTestKit.OnFrame(Sma(), frame), CancellationToken.None);

        Assert.Equal(new AxisRange(0, 100), result.XFixedRange);
        Assert.Null(result.YFixedRange);
    }

    [Fact]
    public async Task ADayMetricsValue_IsNotChangedByAWeekPartner()
    {
        IReadOnlyList<CandleData> daily = Series("AAA")["AAA"];
        List<CandleData> weekly = Weekly(LongWeekBars, WeekSeed);
        Assert.Equal(Required(Rsi()), Required(Sma()));

        var dayPartner = new FakeDataService(Frames(("AAA", TimeFrame.D1, daily), ("AAA", TimeFrame.W1, weekly)));
        var weekPartner = new FakeDataService(Frames(("AAA", TimeFrame.D1, daily), ("AAA", TimeFrame.W1, weekly)));
        var withDay = await Create(dayPartner).ComputeAsync(new[] { "AAA" }, Rsi(), Sma(), CancellationToken.None);
        var withWeek = await Create(weekPartner).ComputeAsync(new[] { "AAA" }, Rsi(), ScatterMetricTestKit.OnFrame(Sma(), TimeFrame.W1), CancellationToken.None);

        Assert.Equal(withDay.Items[0].X, withWeek.Items[0].X);
        Assert.Equal(Direct(Rsi(), daily), withWeek.Items[0].X);
        Assert.Equal(dayPartner.RequestedCountsByFrame[("AAA", TimeFrame.D1)], weekPartner.RequestedCountsByFrame[("AAA", TimeFrame.D1)]);
    }

    public static IEnumerable<object[]> Combinations()
    {
        yield return new object[] { TimeFrame.D1, TimeFrame.D1 };
        yield return new object[] { TimeFrame.D1, TimeFrame.W1 };
        yield return new object[] { TimeFrame.W1, TimeFrame.MN1 };
        yield return new object[] { TimeFrame.MN1, TimeFrame.MN1 };
        yield return new object[] { TimeFrame.W1, TimeFrame.W1 };
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public async Task EveryCombinationOfTimeFrames_GivesEachMetricItsOwnValue_AndLoadsPerTimeFrame(TimeFrame xFrame, TimeFrame yFrame)
    {
        // Long enough for SMA(400) on every time frame.
        IReadOnlyList<CandleData> daily = Series("AAA")["AAA"];
        List<CandleData> weekly = Weekly(BarCount, WeekSeed);
        List<CandleData> monthly = Monthly(BarCount, MonthSeed);
        IReadOnlyList<CandleData> OfFrame(TimeFrame frame) => frame switch { TimeFrame.D1 => daily, TimeFrame.W1 => weekly, _ => monthly };
        ScatterMetricDefinition X() => ScatterMetricTestKit.OnFrame(Rsi(), xFrame);
        ScatterMetricDefinition Y() => ScatterMetricTestKit.OnFrame(Sma(period: 400), yFrame);
        Assert.True(Required(Y()) > Required(X()), "the test needs the two requirements to differ");

        FakeDataService NewData() => new(Frames(
            ("AAA", TimeFrame.D1, daily), ("AAA", TimeFrame.W1, weekly), ("AAA", TimeFrame.MN1, monthly)));
        FakeDataService data = NewData();
        var result = await Create(data).ComputeAsync(new[] { "AAA" }, X(), Y(), CancellationToken.None);

        // Each metric reads the bars of its own time frame.
        Assert.Equal(DirectOnFrame(X(), OfFrame(xFrame)), result.Items[0].X);
        Assert.Equal(DirectOnFrame(Y(), OfFrame(yFrame)), result.Items[0].Y);

        // One load per time frame; metrics on the same time frame share the larger requirement, others keep their own.
        bool same = xFrame == yFrame;
        Assert.Equal(same ? Required(Y()) : Required(X()), data.RequestedCountsByFrame[("AAA", xFrame)]);
        Assert.Equal(Required(Y()), data.RequestedCountsByFrame[("AAA", yFrame)]);
        Assert.Equal(same ? 1 : 2, data.RequestedCountsByFrame.Count);
        Assert.Equal(Sorted(new[] { xFrame, yFrame }.Distinct().ToArray()), Sorted(data.RequestedTimeFrames));

        // Swapping the axes does not change the value of either metric.
        FakeDataService swappedData = NewData();
        var swapped = await Create(swappedData).ComputeAsync(new[] { "AAA" }, Y(), X(), CancellationToken.None);
        Assert.Equal(result.Items[0].X, swapped.Items[0].Y);
        Assert.Equal(result.Items[0].Y, swapped.Items[0].X);
    }

    [Fact]
    public async Task AFullHistoryIndicator_AsksForAllHistoryOnItsOwnTimeFrameOnly()
    {
        IReadOnlyList<CandleData> daily = Series("AAA")["AAA"];
        List<CandleData> weekly = Weekly(LongWeekBars, WeekSeed);
        ScatterMetricDefinition Obv() => ScatterMetricTestKit.Indicator(IndicatorType.OBV);
        FakeDataService NewData() => new(Frames(("AAA", TimeFrame.D1, daily), ("AAA", TimeFrame.W1, weekly)));

        FakeDataService obvDay = NewData();
        await Create(obvDay).ComputeAsync(new[] { "AAA" }, Obv(), ScatterMetricTestKit.OnFrame(Rsi(), TimeFrame.W1), CancellationToken.None);
        Assert.Equal(0, obvDay.RequestedCountsByFrame[("AAA", TimeFrame.D1)]);
        Assert.Equal(Required(Rsi()), obvDay.RequestedCountsByFrame[("AAA", TimeFrame.W1)]);

        FakeDataService obvWeek = NewData();
        await Create(obvWeek).ComputeAsync(new[] { "AAA" }, ScatterMetricTestKit.OnFrame(Obv(), TimeFrame.W1), ScatterMetricTestKit.OnFrame(Rsi(), TimeFrame.W1), CancellationToken.None);
        Assert.Equal(0, obvWeek.RequestedCountsByFrame[("AAA", TimeFrame.W1)]);
        Assert.False(obvWeek.RequestedCountsByFrame.ContainsKey(("AAA", TimeFrame.D1)));
    }

    [Fact]
    public async Task TwoWeekMetrics_ReadTheSameLoadedList_ThroughACopyOfTheirSourceThatSaysDay()
    {
        List<CandleData> weekly = Weekly(LongWeekBars, WeekSeed);
        IReadOnlyList<CandleData> daily = Series("AAA")["AAA"];
        int loads = 0;
        var data = new FakeDataService(Frames(("AAA", TimeFrame.D1, daily), ("AAA", TimeFrame.W1, weekly)), beforeLoad: _ =>
        {
            Interlocked.Increment(ref loads);
            return Task.CompletedTask;
        });
        var recorder = new RecordingExtractor();
        ScatterMetricDefinition x = ScatterMetricTestKit.OnFrame(Rsi(), TimeFrame.W1);
        ScatterMetricDefinition y = ScatterMetricTestKit.OnFrame(Sma(period: 400), TimeFrame.W1);

        var result = await Create(data, extractor: recorder).ComputeAsync(new[] { "AAA" }, x, y, CancellationToken.None);

        Assert.Equal(1, loads);
        var calls = recorder.Calls.ToArray();
        Assert.Equal(2, calls.Length);
        Assert.Same(calls[0].Candles, calls[1].Candles);
        Assert.Same(weekly, calls[0].Candles);
        Assert.All(calls, call => Assert.Equal(TimeFrame.D1, call.Config!.TimeFrame));
        Assert.NotSame(x.Source, calls[0].Config);
        Assert.NotSame(y.Source, calls[1].Config);
        Assert.Equal(DirectOnFrame(x, weekly), result.Items[0].X);

        // The metrics themselves keep their time frame (they are saved and shown from the same objects).
        Assert.Equal(TimeFrame.W1, x.Source.TimeFrame);
        Assert.Equal(TimeFrame.W1, y.Source.TimeFrame);
    }

    [Fact]
    public async Task ADayMetricNextToAWeekMetric_ReadsItsOwnSourceOnTheDailyList_AndTheWeekMetricTheWeeklyList()
    {
        List<CandleData> weekly = Weekly(LongWeekBars, WeekSeed);
        IReadOnlyList<CandleData> daily = Series("AAA")["AAA"];
        var data = new FakeDataService(Frames(("AAA", TimeFrame.D1, daily), ("AAA", TimeFrame.W1, weekly)));
        var recorder = new RecordingExtractor();
        ScatterMetricDefinition x = Rsi();
        ScatterMetricDefinition y = ScatterMetricTestKit.OnFrame(Rsi(), TimeFrame.W1);

        await Create(data, extractor: recorder).ComputeAsync(new[] { "AAA" }, x, y, CancellationToken.None);

        var calls = recorder.Calls.ToArray();
        Assert.Same(x.Source, calls[0].Config);
        Assert.Same(daily, calls[0].Candles);
        Assert.NotSame(y.Source, calls[1].Config);
        Assert.Same(weekly, calls[1].Candles);
    }

    // ===== Day characterization (written before the Week/Month change: these facts hold before and after it) =====

    /// <summary>Hands every call to the real extractor and keeps the config and the very list instance it was given.</summary>
    private sealed class RecordingExtractor : IScreenerValueExtractor
    {
        public ConcurrentQueue<(ScreenerIndicatorSideConfig? Config, IReadOnlyList<CandleData> Candles)> Calls { get; } = new();

        public decimal ExtractValue(ScreenerIndicatorSideConfig? config, IReadOnlyList<CoreCandleData> candles) => Extractor.ExtractValue(config, candles);

        public decimal ExtractValue(ScreenerIndicatorSideConfig? config, IReadOnlyList<CandleData> candles, TickerMetadata metadata = default)
            => Extractor.ExtractValue(config, candles, metadata);

        public decimal? ExtractValueNullable(ScreenerIndicatorSideConfig? config, IReadOnlyList<CandleData> candles, TickerMetadata metadata = default)
        {
            Calls.Enqueue((config, candles));
            return Extractor.ExtractValueNullable(config, candles, metadata);
        }
    }

    private static int Required(ScatterMetricDefinition metric)
        => ScreenerHistoryRequirements.GetRequiredCandleCount(new ScreenerIndicatorEntry { LeftHand = metric.Source.Clone(), TargetMode = RightHandTargetMode.NumericValue });

    [Fact]
    public async Task TwoDayMetrics_ReadOneLoadedList_WithTheirOwnSources()
    {
        var series = Series("AAA");
        var recorder = new RecordingExtractor();
        int loads = 0;
        var data = new FakeDataService(series, beforeLoad: _ =>
        {
            Interlocked.Increment(ref loads);
            return Task.CompletedTask;
        });
        ScatterMetricDefinition x = Rsi();
        ScatterMetricDefinition y = Sma();

        await Create(data, extractor: recorder).ComputeAsync(new[] { "AAA" }, x, y, CancellationToken.None);

        Assert.Equal(1, loads);
        var calls = recorder.Calls.ToArray();
        Assert.Equal(2, calls.Length);
        Assert.Same(calls[0].Candles, calls[1].Candles);
        Assert.Same(x.Source, calls[0].Config);
        Assert.Same(y.Source, calls[1].Config);
    }

    [Fact]
    public async Task ADayMetricsValue_IsTheSame_WhateverTheDayPartnerIs_WhenBothRequestTheSameHistory()
    {
        var series = Series("AAA");
        ScatterMetricDefinition close = ScatterMetricTestKit.Indicator(IndicatorType.Price, 0, "Close");
        Assert.Equal(Required(Rsi()), Required(close));
        Assert.Equal(Required(Rsi()), Required(Sma()));

        var withSma = await Create(new FakeDataService(series)).ComputeAsync(new[] { "AAA" }, Rsi(), Sma(), CancellationToken.None);
        var withClose = await Create(new FakeDataService(series)).ComputeAsync(new[] { "AAA" }, Rsi(), close, CancellationToken.None);
        var withRsi = await Create(new FakeDataService(series)).ComputeAsync(new[] { "AAA" }, Rsi(), Rsi(), CancellationToken.None);

        Assert.Equal(Direct(Rsi(), series["AAA"]), withSma.Items[0].X);
        Assert.Equal(withSma.Items[0].X, withClose.Items[0].X);
        Assert.Equal(withSma.Items[0].X, withRsi.Items[0].X);
    }

    [Fact]
    public async Task TwoDayMetricsOfDifferentRequirements_LoadTheLargerCountOnce_AndShareIt()
    {
        var series = Series("AAA");
        ScatterMetricDefinition longSma = Sma(period: 400);
        int loads = 0;
        var data = new FakeDataService(series, beforeLoad: _ =>
        {
            Interlocked.Increment(ref loads);
            return Task.CompletedTask;
        });
        var recorder = new RecordingExtractor();

        await Create(data, extractor: recorder).ComputeAsync(new[] { "AAA" }, Rsi(), longSma, CancellationToken.None);

        Assert.True(Required(longSma) > Required(Rsi()), "the test needs the two requirements to differ");
        Assert.Equal(1, loads);
        Assert.Equal(Required(longSma), data.RequestedCounts["AAA"]);
        Assert.Same(recorder.Calls.ElementAt(0).Candles, recorder.Calls.ElementAt(1).Candles);
    }
}
