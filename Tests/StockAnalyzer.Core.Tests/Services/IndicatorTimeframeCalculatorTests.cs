using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// Indicator Timeframe, Sprint 2 / T3: aggregation, per-call cache, causal alignment and guards of the pipeline.
/// Real <see cref="AnalysisPipelineService"/>, indicators and candles only (no mocks); expected values are computed independently of the production aggregation.
/// </summary>
public sealed class IndicatorTimeframeCalculatorTests
{
    // 398 weekdays from Monday 2023-01-02: 79 complete ISO weeks (groups 0..78) and a running week of three days (group 79, Mon-Wed).
    private const int DailyCount = 398;
    private const int DaysPerGroup = 5;
    private static readonly DateTime FirstMonday = new(2023, 1, 2);

    private static readonly AnalysisPipelineService Service = new(pythonService: null, indicatorFactory: IndicatorFactory.Default);

    private static decimal CloseOf(int dayIndex) => 100m + (dayIndex * 7 % 23) + (dayIndex * 0.1m);

    private static List<CoreCandleData> DailyCandles(int count = DailyCount)
    {
        var list = new List<CoreCandleData>(count);
        var date = FirstMonday;
        for (int i = 0; i < count; i++)
        {
            while (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                date = date.AddDays(1);
            }

            decimal close = CloseOf(i);
            list.Add(new CoreCandleData(date, close - 0.5m, close + 1m, close - 1m, close, 1000 + i));
            date = date.AddDays(1);
        }

        return list;
    }

    private static CoreIndicatorSettings Sma(string id, int period, IndicatorTimeframe timeframe = IndicatorTimeframe.SameAsChart)
    {
        var setting = new CoreIndicatorSettings { Id = id, TypeEnum = IndicatorType.SMA, ParameterObject = new CoreSmaParameter { Period = period }, IsEnabled = true };
        setting.Timeframe = timeframe;
        return setting;
    }

    private static CoreIndicatorSettings FromDefault(string id, IndicatorType type, IndicatorTimeframe timeframe = IndicatorTimeframe.SameAsChart)
    {
        var setting = IndicatorFactory.CreateStatic(type)!.GetDefaultSettings();
        setting.Id = id;
        setting.TypeEnum = type;
        setting.Timeframe = timeframe;
        return setting;
    }

    private static async Task<Dictionary<string, IIndicatorResult>> RunAsync(IReadOnlyList<CoreCandleData> candles, TimeFrame chartFrame, params CoreIndicatorSettings[] settings)
        => await Service.CalculateIndicatorsAsync(candles, settings, null, chartFrame, CancellationToken.None);

    private static void AssertSeriesEqual(IReadOnlyList<decimal?> expected, IReadOnlyList<decimal?> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.True(expected[i] == actual[i], $"index {i}: expected {expected[i]} but was {actual[i]}");
        }
    }

    // Independent reference: close of the last trading day of every ISO week group, in chronological order (group 0 is the first, complete, week).
    private static List<decimal> WeeklyLastCloses(IReadOnlyList<CoreCandleData> candles)
    {
        var closes = new List<decimal>();
        (int year, int week)? current = null;
        foreach (var c in candles)
        {
            var key = (ISOWeek.GetYear(c.Timestamp), ISOWeek.GetWeekOfYear(c.Timestamp));
            if (current != key)
            {
                closes.Add(c.Close);
                current = key;
            }
            else
            {
                closes[^1] = c.Close;
            }
        }

        return closes;
    }

    private static int GroupOf(IReadOnlyList<CoreCandleData> candles, int index)
    {
        int group = 0;
        var firstKey = (ISOWeek.GetYear(candles[0].Timestamp), ISOWeek.GetWeekOfYear(candles[0].Timestamp));
        var previous = firstKey;
        for (int i = 1; i <= index; i++)
        {
            var key = (ISOWeek.GetYear(candles[i].Timestamp), ISOWeek.GetWeekOfYear(candles[i].Timestamp));
            if (key != previous)
            {
                group++;
                previous = key;
            }
        }

        return group;
    }

    // ---- value and causality ---------------------------------------------------------------------------------------

    [Fact]
    public async Task WeeklySma3_OnDailyChart_EqualsIndependentReference_AndIsNullUntilTheFirstCompletedPeriods()
    {
        var candles = DailyCandles();
        var weeklyCloses = WeeklyLastCloses(candles);
        var weekly = Sma("w", 3, IndicatorTimeframe.Weekly);

        var result = await RunAsync(candles, TimeFrame.D1, weekly);

        var series = result["w"].MainValues;
        Assert.True(result["w"].IsSuccessful);
        Assert.Equal(candles.Count, series.Count);
        int nullCount = 0;
        for (int i = 0; i < candles.Count; i++)
        {
            int g = GroupOf(candles, i);
            // Group 0 is dropped; A'[k] = group k+1; the value at a bar of group g is SMA3 at A' index g-2, which needs g-2 >= 2.
            decimal? expected = g >= 4 ? (weeklyCloses[g - 3] + weeklyCloses[g - 2] + weeklyCloses[g - 1]) / 3m : null;
            Assert.True(expected is null ? series[i] is null : series[i] is { } v && Math.Abs(v - expected.Value) < 0.0000001m,
                $"bar {i} (group {g}): expected {expected} but was {series[i]}");
            if (expected is null) nullCount++;
        }

        Assert.Equal(4 * DaysPerGroup, nullCount); // groups 0..3 are null
    }

    [Fact]
    public async Task Causality_MutatingTheRunningWeek_ChangesNothingWeekly_ButChangesDaily()
    {
        var candles = DailyCandles();
        var mutated = candles.ToList();
        for (int i = 395; i < DailyCount; i++) // the running week: Mon-Wed of group 79
        {
            var c = candles[i];
            mutated[i] = c with { Close = c.Close * 10m, High = c.High * 10m };
        }

        var before = await RunAsync(candles, TimeFrame.D1, Sma("w", 3, IndicatorTimeframe.Weekly), Sma("d", 3));
        var after = await RunAsync(mutated, TimeFrame.D1, Sma("w", 3, IndicatorTimeframe.Weekly), Sma("d", 3));

        AssertSeriesEqual(before["w"].MainValues, after["w"].MainValues);
        Assert.NotEqual(before["d"].MainValues[DailyCount - 1], after["d"].MainValues[DailyCount - 1]);
    }

    [Fact]
    public async Task Causality_MutatingTheLastClosedWeek_ChangesWeeklyOnlyFromTheNextWeekOnward()
    {
        var candles = DailyCandles();
        var mutated = candles.ToList();
        for (int i = 390; i < 395; i++) // the last closed week: group 78
        {
            var c = candles[i];
            mutated[i] = c with { Close = c.Close * 10m, High = c.High * 10m };
        }

        var before = (await RunAsync(candles, TimeFrame.D1, Sma("w", 3, IndicatorTimeframe.Weekly)))["w"].MainValues;
        var after = (await RunAsync(mutated, TimeFrame.D1, Sma("w", 3, IndicatorTimeframe.Weekly)))["w"].MainValues;

        for (int i = 0; i < 395; i++)
        {
            Assert.True(before[i] == after[i], $"bar {i} must not change");
        }

        for (int i = 395; i < DailyCount; i++)
        {
            Assert.NotEqual(before[i], after[i]);
        }
    }

    [Fact]
    public async Task WeeklyIndicator_OnWeeklyChart_IsIdenticalToSameAsChart()
    {
        var weeklyCandles = TimeFrameAggregator.AggregateToPeriodStart(DailyCandles(), TimeFrame.W1).ToList();

        var result = await RunAsync(weeklyCandles, TimeFrame.W1, Sma("w", 3, IndicatorTimeframe.Weekly), Sma("s", 3));

        AssertSeriesEqual(result["s"].MainValues, result["w"].MainValues);
        Assert.IsNotType<TimeframeAlignedIndicatorResult>(result["w"]);
    }

    // ---- refusals --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WeeklyIndicator_OnMonthlyChart_Fails_OnAsyncAndSyncPipeline_WithTheSharedMessage()
    {
        var monthly = TimeFrameAggregator.AggregateToPeriodStart(DailyCandles(), TimeFrame.MN1).ToList();
        string expected = IndicatorTimeframeMessages.FinerThanChart(TimeFrame.W1, TimeFrame.MN1);

        var asyncSetting = Sma("w", 3, IndicatorTimeframe.Weekly);
        var asyncResult = await RunAsync(monthly, TimeFrame.MN1, asyncSetting);
        var syncSetting = Sma("w", 3, IndicatorTimeframe.Weekly);
        var syncResult = Service.CalculateIndicators(monthly, new[] { syncSetting });

        Assert.False(asyncResult["w"].IsSuccessful);
        Assert.Equal(expected, asyncResult["w"].ErrorMessage);
        Assert.Equal(expected, asyncSetting.ErrorMessage);
        Assert.False(syncResult["w"].IsSuccessful);
        Assert.Equal(expected, syncResult["w"].ErrorMessage);
        Assert.Equal(expected, syncSetting.ErrorMessage);
    }

    [Fact]
    public async Task MonthlyIndicator_OnWeeklyChart_Fails_OnAsyncAndSyncPipeline_WithTheFixedMessage()
    {
        var weekly = TimeFrameAggregator.AggregateToPeriodStart(DailyCandles(), TimeFrame.W1).ToList();
        const string expected = "Monthly indicator timeframe requires a Daily chart and cannot be calculated from Weekly bars.";

        var asyncSetting = Sma("m", 3, IndicatorTimeframe.Monthly);
        var asyncResult = await RunAsync(weekly, TimeFrame.W1, asyncSetting);
        var syncSetting = Sma("m", 3, IndicatorTimeframe.Monthly);
        var syncResult = Service.CalculateIndicators(weekly, new[] { syncSetting });

        Assert.Equal(expected, IndicatorTimeframeMessages.MonthlyRequiresDailyChart);
        Assert.False(asyncResult["m"].IsSuccessful);
        Assert.Equal(expected, asyncResult["m"].ErrorMessage);
        Assert.Equal(expected, asyncSetting.ErrorMessage);
        Assert.False(syncResult["m"].IsSuccessful);
        Assert.Equal(expected, syncResult["m"].ErrorMessage);
        Assert.Equal(expected, syncSetting.ErrorMessage);
    }

    [Fact]
    public async Task MonthlyIndicator_OnMonthlyChart_EqualsSameAsChart_AndOnDailyChartIsStillCalculated()
    {
        var monthlyCandles = TimeFrameAggregator.AggregateToPeriodStart(DailyCandles(), TimeFrame.MN1).ToList();
        var onMonthly = await RunAsync(monthlyCandles, TimeFrame.MN1, Sma("m", 3, IndicatorTimeframe.Monthly), Sma("s", 3));
        AssertSeriesEqual(onMonthly["s"].MainValues, onMonthly["m"].MainValues);

        var onDaily = await RunAsync(DailyCandles(), TimeFrame.D1, Sma("m", 3, IndicatorTimeframe.Monthly));
        Assert.True(onDaily["m"].IsSuccessful);
        Assert.IsType<TimeframeAlignedIndicatorResult>(onDaily["m"]);
    }

    [Fact]
    public async Task PivotWithMonthlySourcePeriod_OnWeeklyChart_IsStillCalculated_AsItsOwnLogic()
    {
        var weekly = TimeFrameAggregator.AggregateToPeriodStart(DailyCandles(), TimeFrame.W1).ToList();
        var setting = FromDefault("p", IndicatorType.ClassicPivotPoints);
        ((CoreClassicPivotPointsParameter)setting.ParameterObject!).SourcePeriod = PivotSourcePeriod.Month;

        var result = await RunAsync(weekly, TimeFrame.W1, setting);

        Assert.True(result["p"].IsSuccessful, result["p"].ErrorMessage);
    }

    [Theory]
    [InlineData(TimeFrame.M1)]
    [InlineData(TimeFrame.M15)]
    [InlineData(TimeFrame.H1)]
    [InlineData(TimeFrame.H4)]
    public async Task NonSameIndicator_OnAnIntradayChartFrame_Fails_AndSameAsChartIsNotRefused(TimeFrame intraday)
    {
        const string expected = "Indicator timeframe is supported on Daily, Weekly and Monthly charts only.";
        var candles = DailyCandles(60);

        var setting = Sma("w", 3, IndicatorTimeframe.Weekly);
        var result = await RunAsync(candles, intraday, setting, Sma("s", 3));

        Assert.Equal(expected, IndicatorTimeframeMessages.UnsupportedChartFrame);
        Assert.False(result["w"].IsSuccessful);
        Assert.Equal(expected, result["w"].ErrorMessage);
        Assert.Equal(expected, setting.ErrorMessage);
        Assert.True(result["s"].IsSuccessful);
    }

    [Fact]
    public void DataServiceTimeFrames_AreExactlyDailyWeeklyMonthly()
    {
        // The refusal of an unsupported chart frame is derived from this list; a change to it must be a conscious one.
        Assert.Equal(new[] { TimeFrame.D1, TimeFrame.W1, TimeFrame.MN1 }, DataServiceTimeFrames.Supported.ToArray());
        // The fixed text of the refusal names exactly these frames; when the list changes, this test and the text change together.
        Assert.Contains("Daily, Weekly and Monthly", IndicatorTimeframeMessages.UnsupportedChartFrame);
    }

    [Theory]
    [InlineData(IndicatorType.PivotPointsAdvanced)]
    [InlineData(IndicatorType.ClassicPivotPoints)]
    public async Task PivotWithWeeklySourcePeriod_OnMonthlyChart_Fails_OnAsyncAndSyncPipeline(IndicatorType type)
    {
        var monthly = TimeFrameAggregator.AggregateToPeriodStart(DailyCandles(), TimeFrame.MN1).ToList();
        string expected = IndicatorTimeframeMessages.FinerThanChart(TimeFrame.W1, TimeFrame.MN1);

        CoreIndicatorSettings Make()
        {
            var setting = FromDefault("p", type);
            switch (setting.ParameterObject)
            {
                case CorePivotPointsAdvancedParameter advanced: advanced.SourcePeriod = PivotSourcePeriod.Week; break;
                case CoreClassicPivotPointsParameter classic: classic.SourcePeriod = PivotSourcePeriod.Week; break;
                default: throw new InvalidOperationException("Pivot parameter expected.");
            }

            return setting;
        }

        var asyncSetting = Make();
        var asyncResult = await RunAsync(monthly, TimeFrame.MN1, asyncSetting);
        var syncSetting = Make();
        var syncResult = Service.CalculateIndicators(monthly, new[] { syncSetting });

        Assert.False(asyncResult["p"].IsSuccessful);
        Assert.Equal(expected, asyncResult["p"].ErrorMessage);
        Assert.Equal(expected, asyncSetting.ErrorMessage);
        Assert.False(syncResult["p"].IsSuccessful);
        Assert.Equal(expected, syncSetting.ErrorMessage);
    }

    [Fact]
    public async Task MonthlySourcePeriodPivot_OnMonthlyChart_IsNotRefused()
    {
        var monthly = TimeFrameAggregator.AggregateToPeriodStart(DailyCandles(), TimeFrame.MN1).ToList();
        var setting = FromDefault("p", IndicatorType.ClassicPivotPoints);
        ((CoreClassicPivotPointsParameter)setting.ParameterObject!).SourcePeriod = PivotSourcePeriod.Month;

        var result = await RunAsync(monthly, TimeFrame.MN1, setting);

        Assert.True(result["p"].IsSuccessful, result["p"].ErrorMessage);
    }

    [Fact]
    public async Task FewerThanTwoPeriods_FailsWithTheSharedMessage_AndDoesNotThrow()
    {
        var oneWeek = DailyCandles(DaysPerGroup); // Mon-Fri: a single aggregated period, which is dropped
        var setting = Sma("w", 3, IndicatorTimeframe.Weekly);

        var result = await RunAsync(oneWeek, TimeFrame.D1, setting);

        Assert.False(result["w"].IsSuccessful);
        Assert.Equal(IndicatorTimeframeMessages.InsufficientAggregatedBars, result["w"].ErrorMessage);
        Assert.Equal(IndicatorTimeframeMessages.InsufficientAggregatedBars, setting.ErrorMessage);
    }

    // ---- series shape ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Ichimoku_Weekly_AllSeriesHaveTheChartBarCount_WithoutAnException()
    {
        var candles = DailyCandles();
        var sameResult = (await RunAsync(candles, TimeFrame.D1, FromDefault("i", IndicatorType.Ichimoku)))["i"];

        var result = (await RunAsync(candles, TimeFrame.D1, FromDefault("i", IndicatorType.Ichimoku, IndicatorTimeframe.Weekly)))["i"];

        Assert.True(result.IsSuccessful, result.ErrorMessage);
        Assert.Equal(sameResult.SeriesNamesList.ToArray(), result.SeriesNamesList.ToArray());
        foreach (var name in result.SeriesNamesList)
        {
            Assert.Equal(candles.Count, result.GetSeries(name).Count);
        }
    }

    [Theory]
    [InlineData(IndicatorType.MACD)]
    [InlineData(IndicatorType.BB)]
    public async Task MultiSeriesIndicators_Weekly_AlignEveryNamedSeries(IndicatorType type)
    {
        var candles = DailyCandles();
        var sameResult = (await RunAsync(candles, TimeFrame.D1, FromDefault("m", type)))["m"];

        var result = (await RunAsync(candles, TimeFrame.D1, FromDefault("m", type, IndicatorTimeframe.Weekly)))["m"];

        Assert.True(result.IsSuccessful, result.ErrorMessage);
        Assert.True(sameResult.SeriesNamesList.Count > 1);
        Assert.Equal(sameResult.SeriesNamesList.ToArray(), result.SeriesNamesList.ToArray());
        foreach (var name in result.SeriesNamesList)
        {
            Assert.Equal(candles.Count, result.GetSeries(name).Count);
            Assert.Contains(result.GetSeries(name), v => v.HasValue);
        }

        Assert.Null(result.CustomData);
        Assert.Null(result.SignalProvider);
    }

    [Theory]
    [InlineData(IndicatorType.MACD)]
    [InlineData(IndicatorType.BB)]
    [InlineData(IndicatorType.Ichimoku)]
    [InlineData(IndicatorType.Stoch)]
    public async Task WeeklyResult_KeepsTheIndicatorsOwnSeriesNamesAndLabels(IndicatorType type)
    {
        var candles = DailyCandles();
        var sameResult = (await RunAsync(candles, TimeFrame.D1, FromDefault("m", type)))["m"];

        var result = (await RunAsync(candles, TimeFrame.D1, FromDefault("m", type, IndicatorTimeframe.Weekly)))["m"];

        Assert.True(sameResult.SeriesNamesList.Count > 1);
        Assert.Equal(sameResult.SeriesNamesList.ToArray(), result.SeriesNamesList.ToArray()); // the legend suffix belongs to the display name only (U-9)
        Assert.Equal(sameResult.SeriesLabels.OrderBy(p => p.Key).ToArray(), result.SeriesLabels.OrderBy(p => p.Key).ToArray());
    }

    // ---- chain, applicability, parity ------------------------------------------------------------------------------

    [Fact]
    public async Task ChainedUpstreamAndDownstream_AreForcedToTheChartFrame()
    {
        var candles = DailyCandles();
        CoreIndicatorSettings Upstream(IndicatorTimeframe tf) => Sma("up", 5, tf);
        CoreIndicatorSettings Downstream(IndicatorTimeframe tf)
        {
            var s = new CoreIndicatorSettings { Id = "down", TypeEnum = IndicatorType.EMA, ParameterObject = new CoreEmaParameter { Period = 4 }, IsEnabled = true, SourceIndicatorId = "up" };
            s.Timeframe = tf;
            return s;
        }

        var baseline = await RunAsync(candles, TimeFrame.D1, Upstream(IndicatorTimeframe.SameAsChart), Downstream(IndicatorTimeframe.SameAsChart));
        var withTimeframe = await RunAsync(candles, TimeFrame.D1, Upstream(IndicatorTimeframe.Weekly), Downstream(IndicatorTimeframe.Monthly));

        AssertSeriesEqual(baseline["up"].MainValues, withTimeframe["up"].MainValues);
        AssertSeriesEqual(baseline["down"].MainValues, withTimeframe["down"].MainValues);
    }

    [Fact]
    public async Task NotApplicableIndicator_WithATimeframe_IsCalculatedOnTheChartBars()
    {
        var candles = DailyCandles();
        var baseline = await RunAsync(candles, TimeFrame.D1, FromDefault("z", IndicatorType.ZigZag));

        var withTimeframe = await RunAsync(candles, TimeFrame.D1, FromDefault("z", IndicatorType.ZigZag, IndicatorTimeframe.Monthly));

        Assert.True(withTimeframe["z"].IsSuccessful);
        AssertSeriesEqual(baseline["z"].MainValues, withTimeframe["z"].MainValues);
    }

    [Fact]
    public async Task WeeklySync_And_Async_ProduceTheSameSeries()
    {
        var candles = DailyCandles();
        // The sync pipeline infers the chart frame from the bar spacing; daily weekday data is D1.
        var syncResult = Service.CalculateIndicators(candles, new[] { Sma("w", 3, IndicatorTimeframe.Weekly) });
        var asyncResult = await RunAsync(candles, TimeFrame.D1, Sma("w", 3, IndicatorTimeframe.Weekly));

        AssertSeriesEqual(asyncResult["w"].MainValues, syncResult["w"].MainValues);
    }

    [Fact]
    public async Task MonthlyOnDailyChart_UsesMonthlyBars()
    {
        var candles = DailyCandles();
        var result = (await RunAsync(candles, TimeFrame.D1, Sma("m", 2, IndicatorTimeframe.Monthly)))["m"];

        Assert.True(result.IsSuccessful, result.ErrorMessage);
        Assert.Equal(candles.Count, result.MainValues.Count);
        Assert.Null(result.MainValues[0]);
        Assert.NotNull(result.MainValues[^1]);
    }

    // ---- aggregator adapter, cache, plan ---------------------------------------------------------------------------

    [Theory]
    [InlineData(TimeFrame.W1)]
    [InlineData(TimeFrame.MN1)]
    public void AggregateToPeriodStart_CoreCandleOverload_IsBitIdenticalToTheCandleDataOverload(TimeFrame frame)
    {
        var core = DailyCandles();
        var plain = core.Select(c => new CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume)).ToList();

        var viaCore = TimeFrameAggregator.AggregateToPeriodStart(core, frame);
        var viaPlain = TimeFrameAggregator.AggregateToPeriodStart(plain, frame);

        Assert.Equal(viaPlain.Count, viaCore.Count);
        for (int i = 0; i < viaPlain.Count; i++)
        {
            var p = viaPlain[i];
            Assert.Equal(new CoreCandleData(p.Timestamp, p.Open, p.High, p.Low, p.Close, p.Volume), viaCore[i]);
        }
    }

    [Fact]
    public void AggregateToPeriodStart_CoreCandleOverload_ReturnsTheInputForNonPeriodFramesAndEmptyForNoInput()
    {
        var core = DailyCandles(10);

        Assert.Same(core, TimeFrameAggregator.AggregateToPeriodStart(core, TimeFrame.D1));
        Assert.Empty(TimeFrameAggregator.AggregateToPeriodStart(new List<CoreCandleData>(), TimeFrame.W1));
    }

    [Fact]
    public void FrameCache_AggregatesEachFrameOnce_AndDropsTheLeadingPeriod()
    {
        var candles = DailyCandles();
        var cache = new IndicatorTimeframeFrameCache(candles);

        var first = cache.Get(TimeFrame.W1);
        var second = cache.Get(TimeFrame.W1);

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Equal(80 - 1, first!.Candles.Length); // groups 0..79 minus the leading one
        Assert.Equal(first.Candles.Length, first.Bars.Length);
        Assert.Equal(candles[DaysPerGroup].Timestamp, first.Candles[0].Timestamp); // group 1 starts on the second Monday
        Assert.Equal(candles.Count, cache.ChartBars.Length);
        Assert.Null(new IndicatorTimeframeFrameCache(DailyCandles(DaysPerGroup)).Get(TimeFrame.W1));
    }

    [Fact]
    public void Plan_ClassifiesEveryMode()
    {
        var none = new HashSet<string>();
        var chain = new HashSet<string> { "c" };

        Assert.Equal(IndicatorTimeframeMode.None, IndicatorTimeframePlan.Classify(Sma("a", 3), TimeFrame.D1, none).Mode);
        Assert.Equal(IndicatorTimeframeMode.Aligned, IndicatorTimeframePlan.Classify(Sma("a", 3, IndicatorTimeframe.Weekly), TimeFrame.D1, none).Mode);
        Assert.Equal(IndicatorTimeframeMode.MonthlyRequiresDailyChart, IndicatorTimeframePlan.Classify(Sma("a", 3, IndicatorTimeframe.Monthly), TimeFrame.W1, none).Mode);
        Assert.Equal(IndicatorTimeframeMode.Aligned, IndicatorTimeframePlan.Classify(Sma("a", 3, IndicatorTimeframe.Monthly), TimeFrame.D1, none).Mode);
        Assert.Equal(IndicatorTimeframeMode.None, IndicatorTimeframePlan.Classify(Sma("a", 3, IndicatorTimeframe.Monthly), TimeFrame.MN1, none).Mode);
        Assert.Equal(IndicatorTimeframeMode.UnsupportedChartFrame, IndicatorTimeframePlan.Classify(Sma("a", 3, IndicatorTimeframe.Weekly), TimeFrame.H1, none).Mode);
        Assert.Equal(IndicatorTimeframeMode.None, IndicatorTimeframePlan.Classify(Sma("a", 3), TimeFrame.H1, none).Mode); // Same as Chart is never refused
        Assert.Equal(IndicatorTimeframeMode.InChainForcedSame, IndicatorTimeframePlan.Classify(Sma("c", 3, IndicatorTimeframe.Weekly), TimeFrame.H1, chain).Mode); // a chain is forced to Same before the frame is judged
        Assert.Equal(IndicatorTimeframeMode.None, IndicatorTimeframePlan.Classify(Sma("a", 3, IndicatorTimeframe.Weekly), TimeFrame.W1, none).Mode);
        Assert.Equal(IndicatorTimeframeMode.FinerThanChart, IndicatorTimeframePlan.Classify(Sma("a", 3, IndicatorTimeframe.Weekly), TimeFrame.MN1, none).Mode);
        Assert.Equal(IndicatorTimeframeMode.InChainForcedSame, IndicatorTimeframePlan.Classify(Sma("c", 3, IndicatorTimeframe.Weekly), TimeFrame.D1, chain).Mode);
        Assert.Equal(IndicatorTimeframeMode.NotApplicable, IndicatorTimeframePlan.Classify(FromDefault("z", IndicatorType.ZigZag, IndicatorTimeframe.Weekly), TimeFrame.D1, none).Mode);

        var pivot = FromDefault("p", IndicatorType.ClassicPivotPoints);
        ((CoreClassicPivotPointsParameter)pivot.ParameterObject!).SourcePeriod = PivotSourcePeriod.Week;
        Assert.Equal(IndicatorTimeframeMode.SelfHandledFiner, IndicatorTimeframePlan.Classify(pivot, TimeFrame.MN1, none).Mode);
        Assert.Equal(IndicatorTimeframeMode.None, IndicatorTimeframePlan.Classify(pivot, TimeFrame.D1, none).Mode);
    }

    [Fact]
    public void Plan_NeedsPlan_IsFalseOnlyForSameAsChartWithoutSelfOwnedFrame()
    {
        Assert.False(IndicatorTimeframePlan.NeedsPlan(Sma("a", 3)));
        Assert.True(IndicatorTimeframePlan.NeedsPlan(Sma("a", 3, IndicatorTimeframe.Weekly)));
        Assert.True(IndicatorTimeframePlan.NeedsPlan(FromDefault("p", IndicatorType.ClassicPivotPoints)));
    }

    [Fact]
    public void Plan_CollectChainParticipants_ContainsBothEndsOfEveryReference()
    {
        var up = Sma("up", 3);
        var down = Sma("down", 3);
        down.SourceIndicatorId = "up";
        var driven = Sma("driven", 3);
        driven.DynamicPeriodIndicatorId = "driver";
        var free = Sma("free", 3);

        var participants = IndicatorTimeframePlan.CollectChainParticipants(new[] { up, down, driven, free });

        Assert.Equal(new[] { "down", "driven", "driver", "up" }, participants.OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }
}
