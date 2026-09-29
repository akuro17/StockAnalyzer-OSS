using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Services;

public class TimeAtPriceViewportCoordinatorTests
{
    private const string DefaultSettingId = "tap_test";

    private static List<CoreCandleData> CreateSampleCandles(int count)
    {
        var list = new List<CoreCandleData>(count);
        var baseDate = new DateTime(2026, 1, 1);
        for (int i = 0; i < count; i++)
        {
            list.Add(new CoreCandleData(
                baseDate.AddDays(i),
                10m + i,
                15m + i,
                8m + i,
                12m + i,
                1000 + i * 10));
        }
        return list;
    }

    private static CoreIndicatorSettings CreateTimeAtPriceSettings(
        bool isEnabled = true,
        string id = DefaultSettingId,
        int period = 10,
        int rowCount = 10,
        PriceType priceType = PriceType.Close)
    {
        return new CoreIndicatorSettings
        {
            Id = id,
            IsEnabled = isEnabled,
            ParameterObject = new CoreTimeAtPriceParameter
            {
                Period = period,
                RowCount = rowCount,
                PriceType = priceType
            }
        };
    }

    [Fact]
    public void RequestUpdate_DisabledIndicator_SuspendsState()
    {
        using var coordinator = new TimeAtPriceViewportCoordinator(new SynchronousDispatcherService());
        var candles = CreateSampleCandles(20);
        var snapshot = new ChartDataSnapshot(candles: candles, startIndex: 0, count: 20);
        var settings = CreateTimeAtPriceSettings(isEnabled: false);

        coordinator.RequestUpdate(snapshot, settings);

        Assert.Equal(TimeAtPriceCoordinatorState.Suspended, coordinator.GetState(DefaultSettingId));
        Assert.Null(coordinator.GetResult(DefaultSettingId));
    }

    [Fact]
    public void RequestUpdate_NonTimeAtPriceSetting_SuspendsState()
    {
        using var coordinator = new TimeAtPriceViewportCoordinator(new SynchronousDispatcherService());
        var candles = CreateSampleCandles(20);
        var snapshot = new ChartDataSnapshot(candles: candles, startIndex: 0, count: 20);
        var settings = new CoreIndicatorSettings
        {
            Id = "ema_test",
            IsEnabled = true,
            ParameterObject = new CoreEmaParameter()
        };

        coordinator.RequestUpdate(snapshot, settings);

        Assert.Equal(TimeAtPriceCoordinatorState.Suspended, coordinator.GetState("ema_test"));
        Assert.Null(coordinator.GetResult("ema_test"));
    }

    [Fact]
    public void RequestUpdate_PointerCaptured_DefersComputationUntilReleased()
    {
        using var coordinator = new TimeAtPriceViewportCoordinator(new SynchronousDispatcherService());
        var candles = CreateSampleCandles(20);
        var snapshot = new ChartDataSnapshot(candles: candles, startIndex: 0, count: 20);
        var settings = CreateTimeAtPriceSettings(isEnabled: true);

        // Simulate mouse down / drag
        coordinator.IsPointerCaptured = true;
        coordinator.RequestUpdate(snapshot, settings);

        // Should not compute immediately while pointer is captured
        Assert.Null(coordinator.GetResult(DefaultSettingId));

        // Release pointer -> triggers computation
        coordinator.NotifyPointerReleased(snapshot);

        // Wait up to 2 seconds for async calculation
        var ready = SpinWait.SpinUntil(() => coordinator.GetResult(DefaultSettingId) != null, 2000);
        Assert.True(ready, "Deferred computation should complete after pointer release");
        Assert.NotNull(coordinator.GetResult(DefaultSettingId));
        Assert.Equal(TimeAtPriceCoordinatorState.Ready, coordinator.GetState(DefaultSettingId));
    }

    [Fact]
    public async Task RequestUpdate_Debounce_ProducesValidResultAfterDelay()
    {
        using var coordinator = new TimeAtPriceViewportCoordinator(new SynchronousDispatcherService());
        var candles = CreateSampleCandles(30);
        var settings = CreateTimeAtPriceSettings(isEnabled: true);

        // Rapid updates within debounce window
        for (int i = 0; i < 5; i++)
        {
            var snapshot = new ChartDataSnapshot(candles: candles, startIndex: i, count: 20);
            coordinator.RequestUpdate(snapshot, settings);
        }

        // A result from an earlier request in the burst can already be present while the coordinator is
        // still Waiting for the debounce of the latest one, so wait for the settled Ready state too.
        var completed = await Task.Run(() => SpinWait.SpinUntil(
            () => coordinator.GetResult(DefaultSettingId) != null
                  && coordinator.GetState(DefaultSettingId) == TimeAtPriceCoordinatorState.Ready, 2000));
        Assert.True(completed, "Coordinator should compute profile after debounce period");
        Assert.NotNull(coordinator.GetResult(DefaultSettingId));
        Assert.Equal(TimeAtPriceCoordinatorState.Ready, coordinator.GetState(DefaultSettingId));
    }

    [Fact]
    public void RequestUpdate_PannedAwayFromHistoryStart_ComputesFromVisibleWindowStart()
    {
        using var coordinator = new TimeAtPriceViewportCoordinator(new SynchronousDispatcherService());
        var fullHistory = CreateSampleCandles(1000);
        var snapshot = new ChartDataSnapshot(candles: fullHistory, startIndex: 800, count: 100);
        var settings = new CoreIndicatorSettings
        {
            Id = "tap_panned_test",
            IsEnabled = true,
            ParameterObject = new CoreTimeAtPriceParameter
            {
                Period = 20,
                RowCount = 10
            }
        };

        coordinator.RequestUpdate(snapshot, settings);
        var ready = SpinWait.SpinUntil(() => coordinator.GetState("tap_panned_test") != TimeAtPriceCoordinatorState.Waiting
            && coordinator.GetState("tap_panned_test") != TimeAtPriceCoordinatorState.Computing, 2000);

        Assert.True(ready, "Computation should complete within the wait window");
        Assert.Equal(TimeAtPriceCoordinatorState.Ready, coordinator.GetState("tap_panned_test"));
        var result = coordinator.GetResult("tap_panned_test");
        Assert.NotNull(result);
        Assert.Equal(VolumeProfileResultStatus.Success, result!.Status);
        Assert.Single(result.Segments);

        var segment = result.Segments[0];
        Assert.Equal(20, segment.Count);
        Assert.Equal(fullHistory[880].Timestamp, segment.StartTime);
        Assert.Equal(fullHistory[899].Timestamp, segment.EndTime);
    }

    [Fact]
    public void RequestUpdate_TwoConcurrentSettings_KeepIndependentResultsById()
    {
        using var coordinator = new TimeAtPriceViewportCoordinator(new SynchronousDispatcherService());
        var candles = CreateSampleCandles(60);
        var snapshot = new ChartDataSnapshot(candles: candles, startIndex: 0, count: 60);

        var settingA = CreateTimeAtPriceSettings(id: "tap_a", period: 10, rowCount: 10);
        var settingB = CreateTimeAtPriceSettings(id: "tap_b", period: 50, rowCount: 20);

        coordinator.RequestUpdate(snapshot, settingA);
        coordinator.RequestUpdate(snapshot, settingB);

        var readyA = SpinWait.SpinUntil(() => coordinator.GetResult("tap_a") != null, 2000);
        var readyB = SpinWait.SpinUntil(() => coordinator.GetResult("tap_b") != null, 2000);
        Assert.True(readyA, "tap_a should complete");
        Assert.True(readyB, "tap_b should complete");

        var resultA = coordinator.GetResult("tap_a");
        var resultB = coordinator.GetResult("tap_b");
        Assert.NotNull(resultA);
        Assert.NotNull(resultB);
        Assert.NotEqual(resultA!.RequestKey, resultB!.RequestKey);
        Assert.Equal(10, resultA.Segments[0].Count);
        Assert.Equal(50, resultB.Segments[0].Count);
    }

    [Fact]
    public void RequestUpdate_SameCountDataRevisionChanged_RecomputesInsteadOfReturningStaleCache()
    {
        using var coordinator = new TimeAtPriceViewportCoordinator(new SynchronousDispatcherService());
        var settings = CreateTimeAtPriceSettings();

        var originalCandles = CreateSampleCandles(20);
        var snapshot1 = new ChartDataSnapshot(candles: originalCandles, startIndex: 0, count: 20);
        coordinator.RequestUpdate(snapshot1, settings, dataRevision: 1);
        var ready1 = SpinWait.SpinUntil(() => coordinator.GetResult(DefaultSettingId) != null, 2000);
        Assert.True(ready1);
        var result1 = coordinator.GetResult(DefaultSettingId);
        Assert.NotNull(result1);

        // Price correction
        var correctedCandles = originalCandles.ConvertAll(c => new CoreCandleData(c.Timestamp, c.Open, c.High * 2, c.Low, c.Close * 2, c.Volume));
        var snapshot2 = new ChartDataSnapshot(candles: correctedCandles, startIndex: 0, count: 20);

        coordinator.RequestUpdate(snapshot2, settings, dataRevision: 2);
        var ready2 = SpinWait.SpinUntil(() => coordinator.GetResult(DefaultSettingId)?.RequestKey != result1!.RequestKey, 2000);
        Assert.True(ready2, "A data-revision bump must trigger recomputation.");

        var result2 = coordinator.GetResult(DefaultSettingId);
        Assert.NotNull(result2);
        Assert.NotEqual(result1!.RequestKey, result2!.RequestKey);
    }

    [Fact]
    public void RequestUpdate_OnlySideChanged_DoesNotTriggerRecomputation()
    {
        using var coordinator = new TimeAtPriceViewportCoordinator(new SynchronousDispatcherService());
        var candles = CreateSampleCandles(20);
        var snapshot = new ChartDataSnapshot(candles: candles, startIndex: 0, count: 20);
        var settings = CreateTimeAtPriceSettings();
        var param = (CoreTimeAtPriceParameter)settings.ParameterObject!;
        param.Side = DisplaySide.Left;

        coordinator.RequestUpdate(snapshot, settings);
        var ready = SpinWait.SpinUntil(() => coordinator.GetResult(DefaultSettingId) != null, 2000);
        Assert.True(ready);
        var resultBefore = coordinator.GetResult(DefaultSettingId);

        param.Side = DisplaySide.Right;
        coordinator.RequestUpdate(snapshot, settings);

        Thread.Sleep(ChartConstants.IndicatorCalculationDebounceDelay + 300);

        var resultAfter = coordinator.GetResult(DefaultSettingId);
        Assert.Same(resultBefore, resultAfter);
    }

    [Fact]
    public void RequestUpdate_PriceTypeChanged_TriggersRecomputation()
    {
        using var coordinator = new TimeAtPriceViewportCoordinator(new SynchronousDispatcherService());
        var candles = CreateSampleCandles(20);
        var snapshot = new ChartDataSnapshot(candles: candles, startIndex: 0, count: 20);
        var settings = CreateTimeAtPriceSettings(priceType: PriceType.Close);

        coordinator.RequestUpdate(snapshot, settings);
        var ready = SpinWait.SpinUntil(() => coordinator.GetResult(DefaultSettingId) != null, 2000);
        Assert.True(ready);
        var resultBefore = coordinator.GetResult(DefaultSettingId);

        var param = (CoreTimeAtPriceParameter)settings.ParameterObject!;
        param.PriceType = PriceType.Open;
        coordinator.RequestUpdate(snapshot, settings);

        var readyAfter = SpinWait.SpinUntil(() => coordinator.GetResult(DefaultSettingId)?.RequestKey != resultBefore!.RequestKey, 2000);
        Assert.True(readyAfter, "Changing PriceType should trigger recomputation");

        var resultAfter = coordinator.GetResult(DefaultSettingId);
        Assert.NotSame(resultBefore, resultAfter);
    }

    [Fact]
    public void RequestUpdate_SymbolChangedWhileResultReady_ClearsStaleResultImmediately()
    {
        using var coordinator = new TimeAtPriceViewportCoordinator(new SynchronousDispatcherService());
        var candlesA = CreateSampleCandles(20);
        var snapshotA = new ChartDataSnapshot(candles: candlesA, startIndex: 0, count: 20, symbol: "AAPL");
        var settings = CreateTimeAtPriceSettings();

        coordinator.RequestUpdate(snapshotA, settings);
        var ready = SpinWait.SpinUntil(() => coordinator.GetResult(DefaultSettingId) != null, 2000);
        Assert.True(ready);
        Assert.Equal(TimeAtPriceCoordinatorState.Ready, coordinator.GetState(DefaultSettingId));

        // Context switch: symbol MSFT
        coordinator.IsPointerCaptured = true;
        var candlesB = CreateSampleCandles(20);
        var snapshotB = new ChartDataSnapshot(candles: candlesB, startIndex: 0, count: 20, symbol: "MSFT");
        coordinator.RequestUpdate(snapshotB, settings);

        Assert.Null(coordinator.GetResult(DefaultSettingId));
        Assert.NotEqual(TimeAtPriceCoordinatorState.Ready, coordinator.GetState(DefaultSettingId));
    }

    [Fact]
    public void StartComputation_ParameterMutatedAfterDispatch_WorkerObservesFrozenValuesNotLiveMutation()
    {
        using var coordinator = new TimeAtPriceViewportCoordinator(new SynchronousDispatcherService());
        var candles = CreateSampleCandles(20);
        var snapshot = new ChartDataSnapshot(candles: candles, startIndex: 0, count: 20);
        var settings = CreateTimeAtPriceSettings(rowCount: 10);
        var param = (CoreTimeAtPriceParameter)settings.ParameterObject!;

        coordinator.RequestUpdate(snapshot, settings);
        param.RowCount = 100;

        var ready = SpinWait.SpinUntil(() => coordinator.GetResult(DefaultSettingId) != null, 2000);
        Assert.True(ready);
        var result = coordinator.GetResult(DefaultSettingId);
        Assert.NotNull(result);
        Assert.Equal(10, result!.Segments[0].Bins.Count);
    }

    [Fact]
    public void RequestUpdate_InvalidPeriodParameter_MarksFailedWithoutComputing()
    {
        using var coordinator = new TimeAtPriceViewportCoordinator(new SynchronousDispatcherService());
        var candles = CreateSampleCandles(20);
        var snapshot = new ChartDataSnapshot(candles: candles, startIndex: 0, count: 20);
        var settings = CreateTimeAtPriceSettings();
        var param = (CoreTimeAtPriceParameter)settings.ParameterObject!;
        param.Period = -5; // invalid

        coordinator.RequestUpdate(snapshot, settings);

        Assert.Equal(TimeAtPriceCoordinatorState.Failed, coordinator.GetState(DefaultSettingId));
        Assert.Null(coordinator.GetResult(DefaultSettingId));
    }

    [Fact]
    public void PruneStaleKeys_RemovesTrackedStateForIdsNoLongerActive()
    {
        using var coordinator = new TimeAtPriceViewportCoordinator(new SynchronousDispatcherService());
        var candles = CreateSampleCandles(20);
        var snapshot = new ChartDataSnapshot(candles: candles, startIndex: 0, count: 20);
        var settings = CreateTimeAtPriceSettings(id: "tap_removed");

        coordinator.RequestUpdate(snapshot, settings);
        SpinWait.SpinUntil(() => coordinator.GetResult("tap_removed") != null, 2000);
        Assert.NotNull(coordinator.GetResult("tap_removed"));

        coordinator.PruneStaleKeys(Array.Empty<string>());

        Assert.Null(coordinator.GetResult("tap_removed"));
        Assert.Equal(TimeAtPriceCoordinatorState.Idle, coordinator.GetState("tap_removed"));
    }

    [Fact]
    public void Dispose_CancelsAndDisposesCleanly()
    {
        var coordinator = new TimeAtPriceViewportCoordinator(new SynchronousDispatcherService());
        coordinator.Dispose();

        Assert.True(coordinator.IsDisposed);

        var candles = CreateSampleCandles(10);
        var snapshot = new ChartDataSnapshot(candles: candles, startIndex: 0, count: 10);
        coordinator.RequestUpdate(snapshot, CreateTimeAtPriceSettings());

        Assert.True(coordinator.IsDisposed);
        Assert.Null(coordinator.GetResult(DefaultSettingId));
    }

    [Fact]
    public void RequestUpdate_PriceSourceChange_InvalidatesCacheAndRecomputes()
    {
        using var coordinator = new TimeAtPriceViewportCoordinator(new SynchronousDispatcherService());
        var candles = CreateSampleCandles(20);
        var snapshot = new ChartDataSnapshot(candles: candles, startIndex: 0, count: 20);
        var settings = CreateTimeAtPriceSettings(priceType: PriceType.Close);

        coordinator.RequestUpdate(snapshot, settings);
        var ready = SpinWait.SpinUntil(() => coordinator.GetResult(DefaultSettingId) != null, 2000);
        Assert.True(ready);
        var result1 = coordinator.GetResult(DefaultSettingId);
        Assert.NotNull(result1);

        // Change PriceSource to High
        settings.PriceSource = PriceType.High;
        coordinator.RequestUpdate(snapshot, settings);
        var ready2 = SpinWait.SpinUntil(() => coordinator.GetResult(DefaultSettingId) != null && coordinator.GetResult(DefaultSettingId)!.RequestKey != result1!.RequestKey, 2000);
        Assert.True(ready2);
        var result2 = coordinator.GetResult(DefaultSettingId);
        Assert.NotNull(result2);
        Assert.NotEqual(result1!.RequestKey, result2!.RequestKey);
    }

    [Fact]
    public void RequestUpdate_SourceIndicatorId_ResolvesChainedSeriesAndComputes()
    {
        using var coordinator = new TimeAtPriceViewportCoordinator(new SynchronousDispatcherService());
        var candles = CreateSampleCandles(20);

        var rsiValues = new List<decimal?>();
        for (int i = 0; i < 20; i++)
        {
            rsiValues.Add(30m + (i * 2m)); // Values range from 30 to 68
        }
        var rsiResult = StockAnalyzer.Core.Models.Indicators.IndicatorResult.Success(rsiValues);
        var indicatorResults = new Dictionary<string, StockAnalyzer.Core.Models.Indicators.IIndicatorResult>
        {
            { "rsi_1", rsiResult }
        };

        var rsiSetting = new CoreIndicatorSettings
        {
            Id = "rsi_1",
            IsEnabled = true,
            DisplayName = "RSI (14)"
        };

        var settings = CreateTimeAtPriceSettings(period: 0);
        settings.SourceIndicatorId = "rsi_1";

        var snapshot = new ChartDataSnapshot(
            candles: candles,
            indicatorResults: indicatorResults,
            indicatorSettings: new[] { rsiSetting, settings },
            startIndex: 0,
            count: 20);

        coordinator.RequestUpdate(snapshot, settings);
        var ready = SpinWait.SpinUntil(() => coordinator.GetResult(DefaultSettingId) != null, 2000);
        Assert.True(ready);
        var result = coordinator.GetResult(DefaultSettingId);
        Assert.NotNull(result);
        Assert.Equal(VolumeProfileResultStatus.Success, result!.Status);
        Assert.NotEmpty(result.Segments);
        Assert.NotEmpty(result.Segments[0].Bins);

        // Bins should span RSI range ~30 to 68, not candle prices
        var firstBin = result.Segments[0].Bins[0];
        var lastBin = result.Segments[0].Bins[^1];
        Assert.True(firstBin.LowerBound >= 29m && firstBin.LowerBound <= 31m);
        Assert.True(lastBin.UpperBound >= 67m && lastBin.UpperBound <= 69m);
    }
}
