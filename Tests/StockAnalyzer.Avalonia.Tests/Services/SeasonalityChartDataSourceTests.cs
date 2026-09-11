using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Services;

public sealed class SeasonalityChartDataSourceTests
{
    [Fact]
    public void SetActiveSymbol_PublishesSymbolAndRaisesChanged()
    {
        var source = new SeasonalityChartDataSource(new FakeDailyDataService(Array.Empty<CandleData>()));
        int changed = 0;
        source.Changed += (_, _) => changed++;

        source.SetActiveSymbol("goog");

        Assert.Equal("goog", source.ActiveSymbol);
        Assert.True(source.HasActiveSymbol);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void SetActiveSymbol_SameSymbolIgnoringCase_DoesNotRaiseChangedAgain()
    {
        var source = new SeasonalityChartDataSource(new FakeDailyDataService(Array.Empty<CandleData>()));
        source.SetActiveSymbol("AAPL");
        int changed = 0;
        source.Changed += (_, _) => changed++;

        source.SetActiveSymbol("aapl");

        Assert.Equal(0, changed);
    }

    [Fact]
    public async Task SetActiveSymbol_ClearsPreviousResult()
    {
        var fake = new FakeDailyDataService(ThreeCalendarYearsOfDailyCandles());
        var source = new SeasonalityChartDataSource(fake);
        source.SetActiveSymbol("TEST");
        await source.AnalyzeAsync(new SeasonalityChartParameters(3));
        Assert.NotNull(source.Current);

        source.SetActiveSymbol("OTHER");

        Assert.Null(source.Current);
    }

    [Fact]
    public async Task AnalyzeAsync_WithoutActiveSymbol_Throws()
    {
        var source = new SeasonalityChartDataSource(new FakeDailyDataService(Array.Empty<CandleData>()));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.AnalyzeAsync(new SeasonalityChartParameters(5)));
    }

    [Fact]
    public async Task AnalyzeAsync_InvalidParameters_ThrowsBeforeFetching()
    {
        var fake = new FakeDailyDataService(ThreeCalendarYearsOfDailyCandles());
        var source = new SeasonalityChartDataSource(fake);
        source.SetActiveSymbol("TEST");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => source.AnalyzeAsync(new SeasonalityChartParameters(0)));
        Assert.Equal(0, fake.CallCount);
    }

    [Fact]
    public async Task AnalyzeAsync_FetchesDailyHistoryAndPublishesResult()
    {
        var fake = new FakeDailyDataService(ThreeCalendarYearsOfDailyCandles());
        var source = new SeasonalityChartDataSource(fake);
        source.SetActiveSymbol("TEST");
        int changed = 0;
        source.Changed += (_, _) => changed++;

        await source.AnalyzeAsync(new SeasonalityChartParameters(3));

        Assert.Equal("TEST", fake.LastSymbol);
        Assert.Equal(TimeFrame.D1, fake.LastTimeFrame);
        Assert.NotNull(source.Current);
        Assert.Equal(new[] { 2021, 2022, 2023 }, source.Current!.CalendarYears.ToArray());
        // One series (the base symbol) over three calendar years => three year traces.
        Assert.Equal(3, source.Current.Traces.Count);
        Assert.True(changed >= 1);
    }

    [AvaloniaFact]
    public async Task AnalyzeAsync_PublishesResultOnTheThreadThatRequestedIt()
    {
        // Regression: the Current write and the Changed fan-out must resume on the caller's thread
        // (the UI thread, in the app). When they ran on the Task.Run continuation's thread-pool
        // thread, a subscriber's UI-thread handler could observe a stale Current == null right after
        // the analysis finished and schedule another analysis, which republished and repeated - a
        // self-feeding ~350 ms loop that flickered the Seasonality panel and, with a second live view
        // (a re-docked tab), saturated the dispatcher and froze the whole app.
        var fake = new FakeDailyDataService(ThreeCalendarYearsOfDailyCandles());
        var source = new SeasonalityChartDataSource(fake);
        source.SetActiveSymbol("TEST");
        int requestingThreadId = Environment.CurrentManagedThreadId;
        int changedThreadId = -1;
        SeasonalityChartResult? currentSeenByHandler = null;
        source.Changed += (_, _) =>
        {
            changedThreadId = Environment.CurrentManagedThreadId;
            currentSeenByHandler = source.Current;
        };

        await source.AnalyzeAsync(new SeasonalityChartParameters(3));

        Assert.Equal(requestingThreadId, changedThreadId);
        Assert.NotNull(currentSeenByHandler);
    }

    [Fact]
    public async Task AnalyzeAsync_DrawsOnlyTheBaseSymbolAsSeriesZero()
    {
        var fake = new FakeDailyDataService(ThreeCalendarYearsOfDailyCandles());
        var source = new SeasonalityChartDataSource(fake);
        source.SetActiveSymbol("TEST");

        await source.AnalyzeAsync(new SeasonalityChartParameters(3));

        int[] seriesIds = source.Current!.Traces.Select(t => t.SeriesId).Distinct().ToArray();
        Assert.Equal(new[] { 0 }, seriesIds);
        Assert.All(source.Current.Traces, t => Assert.Equal("TEST", t.SeriesLabel));
        Assert.All(source.Current.Traces, t => Assert.Equal(SeasonalityRadiusMode.PercentVsYearStart, t.RadiusMode));
    }

    [Fact]
    public async Task AnalyzeAsync_RequestsEnoughBarsForTheRequestedYears()
    {
        var fake = new FakeDailyDataService(ThreeCalendarYearsOfDailyCandles());
        var source = new SeasonalityChartDataSource(fake);
        source.SetActiveSymbol("TEST");

        await source.AnalyzeAsync(new SeasonalityChartParameters(5));

        // 5 years * 260 trading days + 40 bar margin.
        Assert.Equal((5 * 260) + 40, fake.LastCount);
    }

    [Fact]
    public async Task AnalyzeAsync_SecondCallWithSameDepth_ReusesCachedFetch()
    {
        var fake = new FakeDailyDataService(ThreeCalendarYearsOfDailyCandles());
        var source = new SeasonalityChartDataSource(fake);
        source.SetActiveSymbol("TEST");

        await source.AnalyzeAsync(new SeasonalityChartParameters(5));
        await source.AnalyzeAsync(new SeasonalityChartParameters(5));

        Assert.Equal(1, fake.CallCount);
    }

    [Fact]
    public async Task AnalyzeAsync_DeeperDepthThanCached_Refetches()
    {
        var fake = new FakeDailyDataService(ThreeCalendarYearsOfDailyCandles());
        var source = new SeasonalityChartDataSource(fake);
        source.SetActiveSymbol("TEST");

        await source.AnalyzeAsync(new SeasonalityChartParameters(3));
        await source.AnalyzeAsync(new SeasonalityChartParameters(10));

        Assert.Equal(2, fake.CallCount);
    }

    [Fact]
    public async Task AnalyzeAsync_ToleratesUnorderedAndDuplicateProviderTimestamps()
    {
        var messy = new[]
        {
            new CandleData(new DateTime(2022, 3, 2), 10m, 10m, 10m, 10m, 1L),
            new CandleData(new DateTime(2022, 3, 1), 9m, 9m, 9m, 9m, 1L),
            new CandleData(new DateTime(2022, 3, 1, 15, 0, 0), 11m, 11m, 11m, 11m, 1L),
        };
        var source = new SeasonalityChartDataSource(new FakeDailyDataService(messy));
        source.SetActiveSymbol("TEST");

        await source.AnalyzeAsync(new SeasonalityChartParameters(1));

        Assert.NotNull(source.Current);
        SeasonalityYearTrace trace = Assert.Single(source.Current!.Traces);
        Assert.Equal(2, trace.Samples.Count);
        Assert.Equal(new DateTime(2022, 3, 1), trace.Samples[0].Timestamp);
        Assert.Equal(new DateTime(2022, 3, 2), trace.Samples[1].Timestamp);
    }

    [Fact]
    public async Task SetRadiusModeOverrides_DoesNotRaiseChangedAndKeepsCurrentResult()
    {
        var source = new SeasonalityChartDataSource(new FakeDailyDataService(ThreeCalendarYearsOfDailyCandles()));
        source.SetActiveSymbol("TEST");
        await source.AnalyzeAsync(new SeasonalityChartParameters(3));
        Assert.NotNull(source.Current);
        int changed = 0;
        source.Changed += (_, _) => changed++;

        source.SetRadiusModeOverrides(new Dictionary<int, SeasonalityRadiusMode>
        {
            [0] = SeasonalityRadiusMode.ZScoreWithinYear,
        });

        Assert.Equal(0, changed);
        Assert.NotNull(source.Current);
        Assert.Equal(SeasonalityRadiusMode.ZScoreWithinYear, source.RadiusModeOverrides[0]);
    }

    [Fact]
    public void SetRadiusModeOverrides_NullOrEmpty_ClearsStoredOverrides()
    {
        var source = new SeasonalityChartDataSource(new FakeDailyDataService(Array.Empty<CandleData>()));
        source.SetRadiusModeOverrides(new Dictionary<int, SeasonalityRadiusMode>
        {
            [0] = SeasonalityRadiusMode.SignedUnitFromBounds,
        });
        Assert.NotEmpty(source.RadiusModeOverrides);

        source.SetRadiusModeOverrides(null);

        Assert.Empty(source.RadiusModeOverrides);
    }

    [Fact]
    public async Task AnalyzeAsync_RadiusModeOverrideOnActiveSeries_ReplacesTheDefaultMode()
    {
        var source = new SeasonalityChartDataSource(new FakeDailyDataService(ThreeCalendarYearsOfDailyCandles()));
        source.SetActiveSymbol("TEST");
        source.SetRadiusModeOverrides(new Dictionary<int, SeasonalityRadiusMode>
        {
            [0] = SeasonalityRadiusMode.ZScoreWithinYear,
        });

        await source.AnalyzeAsync(new SeasonalityChartParameters(3));

        Assert.All(
            source.Current!.Traces.Where(t => t.SeriesId == 0),
            t => Assert.Equal(SeasonalityRadiusMode.ZScoreWithinYear, t.RadiusMode));
    }

    [Fact]
    public async Task AnalyzeAsync_SignedUnitOverrideOnPriceSeriesWithoutBounds_IsIgnored()
    {
        var source = new SeasonalityChartDataSource(new FakeDailyDataService(ThreeCalendarYearsOfDailyCandles()));
        source.SetActiveSymbol("TEST");
        source.SetRadiusModeOverrides(new Dictionary<int, SeasonalityRadiusMode>
        {
            [0] = SeasonalityRadiusMode.SignedUnitFromBounds,
        });

        await source.AnalyzeAsync(new SeasonalityChartParameters(3));

        Assert.NotNull(source.Current);
        Assert.All(
            source.Current!.Traces.Where(t => t.SeriesId == 0),
            t => Assert.Equal(SeasonalityRadiusMode.PercentVsYearStart, t.RadiusMode));
    }

    private static CandleData[] ThreeCalendarYearsOfDailyCandles()
    {
        var start = new DateTime(2021, 1, 1);
        var end = new DateTime(2023, 12, 31);
        int count = (end - start).Days + 1;
        var candles = new CandleData[count];
        DateTime day = start;
        for (int i = 0; i < count; i++)
        {
            decimal close = 100m + i;
            candles[i] = new CandleData(day, close, close, close, close, 1000L);
            day = day.AddDays(1);
        }

        return candles;
    }

    private sealed class FakeDailyDataService : IDataService
    {
        private readonly IReadOnlyList<CandleData> _candles;

        public FakeDailyDataService(IReadOnlyList<CandleData> candles) => _candles = candles;

        public int CallCount { get; private set; }

        public string? LastSymbol { get; private set; }

        public TimeFrame LastTimeFrame { get; private set; }

        public int LastCount { get; private set; }

        public Task<IReadOnlyList<CandleData>> LoadCandlesAsync(string symbol, TimeFrame timeFrame, int count = 100)
        {
            CallCount++;
            LastSymbol = symbol;
            LastTimeFrame = timeFrame;
            LastCount = count;
            return Task.FromResult(_candles);
        }
    }
}
