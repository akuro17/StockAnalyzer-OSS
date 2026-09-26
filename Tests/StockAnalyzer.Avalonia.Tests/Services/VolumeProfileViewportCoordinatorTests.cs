using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
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

public class VolumeProfileViewportCoordinatorTests
{
    private const string DefaultSettingId = "vp_test";

    /// <summary>Upper bound when waiting for a debounce-timer + background-worker outcome. It is a failure ceiling, not a duration the test relies on: the wait ends as soon as the condition holds, so a loaded machine only slows the test instead of failing it.</summary>
    private const int DebouncedOutcomeTimeoutMs = 15_000;

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

    private static CoreIndicatorSettings CreateProfileSettings(bool isEnabled = true, string id = DefaultSettingId, int period = 10, int rowCount = 10)
    {
        return new CoreIndicatorSettings
        {
            Id = id,
            IsEnabled = isEnabled,
            ParameterObject = new CoreVolumeProfileParameter
            {
                Period = period,
                RowCount = rowCount
            }
        };
    }

    /// <summary>Reflection helper: reaches the private per-key state object for F07 tests that need
    /// to observe internals (Timer identity) no public API exposes.</summary>
    private const BindingFlags AnyInstanceField = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private static object GetKeyState(VolumeProfileViewportCoordinator coordinator, string settingId)
    {
        var keyStatesField = typeof(VolumeProfileViewportCoordinator).GetField("_keyStates", AnyInstanceField)!;
        var dict = (IDictionary)keyStatesField.GetValue(coordinator)!;
        return dict[settingId]!;
    }

    private static Timer? GetDebounceTimer(object keyState)
    {
        var field = keyState.GetType().GetField("DebounceTimer", AnyInstanceField)!;
        return (Timer?)field.GetValue(keyState);
    }

    [Fact]
    public void RequestUpdate_DisabledIndicator_SuspendsState()
    {
        using var coordinator = new VolumeProfileViewportCoordinator(new SynchronousDispatcherService());
        var candles = CreateSampleCandles(20);
        var snapshot = new ChartDataSnapshot(candles: candles, startIndex: 0, count: 20);
        var settings = CreateProfileSettings(isEnabled: false);

        coordinator.RequestUpdate(snapshot, settings);

        Assert.Equal(VolumeProfileCoordinatorState.Suspended, coordinator.GetState(DefaultSettingId));
        Assert.Null(coordinator.GetResult(DefaultSettingId));
    }

    [Fact]
    public void RequestUpdate_NonProfileSetting_SuspendsState()
    {
        using var coordinator = new VolumeProfileViewportCoordinator(new SynchronousDispatcherService());
        var candles = CreateSampleCandles(20);
        var snapshot = new ChartDataSnapshot(candles: candles, startIndex: 0, count: 20);
        var settings = new CoreIndicatorSettings
        {
            Id = "ema_test",
            IsEnabled = true,
            ParameterObject = new CoreEmaParameter() // not CoreVolumeProfileParameter
        };

        coordinator.RequestUpdate(snapshot, settings);

        Assert.Equal(VolumeProfileCoordinatorState.Suspended, coordinator.GetState("ema_test"));
        Assert.Null(coordinator.GetResult("ema_test"));
    }

    [Fact]
    public void RequestUpdate_PointerCaptured_DefersComputationUntilReleased()
    {
        using var coordinator = new VolumeProfileViewportCoordinator(new SynchronousDispatcherService());
        var candles = CreateSampleCandles(20);
        var snapshot = new ChartDataSnapshot(candles: candles, startIndex: 0, count: 20);
        var settings = CreateProfileSettings(isEnabled: true);

        // Simulate mouse down / drag
        coordinator.IsPointerCaptured = true;
        coordinator.RequestUpdate(snapshot, settings);

        // Should not compute immediately while pointer is captured
        Assert.Null(coordinator.GetResult(DefaultSettingId));

        // Release pointer -> triggers computation
        coordinator.NotifyPointerReleased();

        // Wait up to 1 second for async calculation
        var ready = SpinWait.SpinUntil(() => coordinator.GetResult(DefaultSettingId) != null, 2000);
        Assert.True(ready, "Deferred computation should complete after pointer release");
        Assert.NotNull(coordinator.GetResult(DefaultSettingId));
        Assert.Equal(VolumeProfileCoordinatorState.Ready, coordinator.GetState(DefaultSettingId));
    }

    [Fact]
    public async Task RequestUpdate_Debounce_ProducesValidResultAfterDelay()
    {
        // V13 & V14: 150ms debounce
        using var coordinator = new VolumeProfileViewportCoordinator(new SynchronousDispatcherService());
        var candles = CreateSampleCandles(30);
        var settings = CreateProfileSettings(isEnabled: true);

        // Rapid updates within debounce window
        for (int i = 0; i < 5; i++)
        {
            var snapshot = new ChartDataSnapshot(candles: candles, startIndex: i, count: 20);
            coordinator.RequestUpdate(snapshot, settings);
        }

        // Wait for debounce timer (150ms) + calculation
        var completed = await Task.Run(() => SpinWait.SpinUntil(() => coordinator.GetResult(DefaultSettingId) != null, 2000));
        Assert.True(completed, "Coordinator should compute profile after debounce period");
        Assert.NotNull(coordinator.GetResult(DefaultSettingId));
        Assert.Equal(VolumeProfileCoordinatorState.Ready, coordinator.GetState(DefaultSettingId));
    }

    [Fact]
    public void RequestUpdate_PannedAwayFromHistoryStart_ComputesFromVisibleWindowStart()
    {
        // F02/A04 regression: with a large history and the viewport panned well past the beginning,
        // snapshot.Candles is a short slice but snapshot.StartIndex is the GLOBAL index of that slice's
        // first bar. The coordinator must treat the slice's own index 0 as the profile window start
        // (i.e. the first VISIBLE bar), never re-apply the global StartIndex as an offset into the
        // already-sliced Candles array -- doing so either overruns the slice (global 800 >= slice length
        // 100, previously returning an Empty result forever) or double-offsets the window away from the
        // true visible start.
        using var coordinator = new VolumeProfileViewportCoordinator(new SynchronousDispatcherService());
        var fullHistory = CreateSampleCandles(1000);
        var snapshot = new ChartDataSnapshot(candles: fullHistory, startIndex: 800, count: 100);
        var settings = new CoreIndicatorSettings
        {
            Id = "vp_panned_test",
            IsEnabled = true,
            ParameterObject = new CoreVolumeProfileParameter
            {
                Period = 20,
                RowCount = 10
            }
        };

        coordinator.RequestUpdate(snapshot, settings);
        var ready = SpinWait.SpinUntil(() => coordinator.GetState("vp_panned_test") != VolumeProfileCoordinatorState.Waiting
            && coordinator.GetState("vp_panned_test") != VolumeProfileCoordinatorState.Computing, 2000);

        Assert.True(ready, "Computation should complete within the wait window");
        Assert.Equal(VolumeProfileCoordinatorState.Ready, coordinator.GetState("vp_panned_test"));
        var result = coordinator.GetResult("vp_panned_test");
        Assert.NotNull(result);
        Assert.Equal(StockAnalyzer.Core.Analysis.VolumeProfileResultStatus.Success, result!.Status);
        Assert.Single(result.Segments);

        var segment = result.Segments[0];
        Assert.Equal(20, segment.Count);
        // Anchor reversal (2026-09-16, user-approved): Period now counts backward from the end of the
        // visible slice (index 100 of the 100-bar slice = global 900), not forward from its start.
        Assert.Equal(fullHistory[880].Timestamp, segment.StartTime);
        Assert.Equal(fullHistory[899].Timestamp, segment.EndTime);
    }

    [Fact]
    public void RequestUpdate_SmallVisibleStartIndex_DoesNotDoubleOffsetWindow()
    {
        // A05 regression (sa_analysis_report_VolumeProfile_PostImplementation_StrictReview_20260914.md):
        // distinct from the LARGE global-index case already covered by
        // RequestUpdate_PannedAwayFromHistoryStart_ComputesFromVisibleWindowStart (global start 800
        // exceeds the visible slice's own length, so an unfixed coordinator returns Empty and the bug
        // is obvious). A SMALL global StartIndex (here: 20) is the more dangerous case -- if it were
        // mistakenly re-applied as a second offset INTO the already-sliced snapshot.Candles (which is
        // itself already exactly the visible window, indexed from 0), the result would silently shift
        // to the WRONG bars instead of failing loudly, because 20 is still a valid in-bounds index into
        // the 40-bar slice. This full history is 100 bars; the visible window is [20, 60); Period=7
        // must resolve to the slice's own trailing bars [33, 40), i.e. global bars [53, 60) (anchor
        // reversal, 2026-09-16: Period counts backward from the end of the visible slice).
        using var coordinator = new VolumeProfileViewportCoordinator(new SynchronousDispatcherService());
        var fullHistory = CreateSampleCandles(100);
        var snapshot = new ChartDataSnapshot(candles: fullHistory, startIndex: 20, count: 40);
        var settings = new CoreIndicatorSettings
        {
            Id = "vp_small_index_test",
            IsEnabled = true,
            ParameterObject = new CoreVolumeProfileParameter { Period = 7, RowCount = 10 }
        };

        coordinator.RequestUpdate(snapshot, settings);
        var ready = SpinWait.SpinUntil(() => coordinator.GetState("vp_small_index_test") != VolumeProfileCoordinatorState.Waiting
            && coordinator.GetState("vp_small_index_test") != VolumeProfileCoordinatorState.Computing, 2000);

        Assert.True(ready, "Computation should complete within the wait window");
        var result = coordinator.GetResult("vp_small_index_test");
        Assert.NotNull(result);
        Assert.Equal(VolumeProfileResultStatus.Success, result!.Status);
        Assert.Single(result.Segments);

        var segment = result.Segments[0];
        Assert.Equal(7, segment.Count);
        // Anchor reversal (2026-09-16, user-approved): Period now counts backward from the end of the
        // visible slice (local index 40 of the 40-bar slice = global 60), not forward from its start.
        Assert.Equal(fullHistory[53].Timestamp, segment.StartTime);
        Assert.Equal(fullHistory[59].Timestamp, segment.EndTime);
    }

    [Fact]
    public void RequestUpdate_OnlyVerticalPriceRangeOverrideChanged_DoesNotTriggerRecomputation()
    {
        // A14 regression: a purely vertical (price-axis) pan/zoom changes only the Y-axis display
        // range, never the visible candle WINDOW/count/symbol/timeframe/parameters the coordinator's
        // cache key is built from. It must not force a recomputation, same as the Side-only change
        // covered by RequestUpdate_OnlySideChanged_DoesNotTriggerRecomputation. See
        // sa_analysis_report_VolumeProfile_PostImplementation_StrictReview_20260914.md A14 ("色/Side/
        // 上下panで数値再計算0").
        using var coordinator = new VolumeProfileViewportCoordinator(new SynchronousDispatcherService());
        var candles = CreateSampleCandles(20);
        var settings = CreateProfileSettings();

        var snapshot1 = new ChartDataSnapshot(candles: candles, startIndex: 0, count: 20, overrideMinPrice: 5m, overrideMaxPrice: 50m);
        coordinator.RequestUpdate(snapshot1, settings);
        var ready = SpinWait.SpinUntil(() => coordinator.GetResult(DefaultSettingId) != null, 2000);
        Assert.True(ready);
        var resultBefore = coordinator.GetResult(DefaultSettingId);

        // Same symbol/timeframe/window/count/parameters -- only the vertical price-range override
        // changed (simulating a vertical-only pan/zoom).
        var snapshot2 = new ChartDataSnapshot(candles: candles, startIndex: 0, count: 20, overrideMinPrice: 15m, overrideMaxPrice: 80m);
        coordinator.RequestUpdate(snapshot2, settings);

        Thread.Sleep(ChartConstants.IndicatorCalculationDebounceDelay + 300);

        var resultAfter = coordinator.GetResult(DefaultSettingId);
        Assert.Same(resultBefore, resultAfter);
    }

    [Fact]
    public void RequestUpdate_TwoConcurrentSettings_KeepIndependentResultsById()
    {
        // F05 regression: two VolumeProfile indicators registered at the same time (different Id,
        // different Period/RowCount) must each retain their OWN computed result. Previously a single
        // shared CurrentResult field meant the second RequestUpdate's result silently overwrote the
        // first, so every VP instance rendered identical bins regardless of its own parameters
        // (sa_analysis_report_VolumeProfile_PostImplementation_StrictReview_20260914.md F05/A12).
        using var coordinator = new VolumeProfileViewportCoordinator(new SynchronousDispatcherService());
        var candles = CreateSampleCandles(60);
        var snapshot = new ChartDataSnapshot(candles: candles, startIndex: 0, count: 60);

        var settingA = CreateProfileSettings(id: "vp_a", period: 10, rowCount: 10);
        var settingB = CreateProfileSettings(id: "vp_b", period: 50, rowCount: 50);

        coordinator.RequestUpdate(snapshot, settingA);
        coordinator.RequestUpdate(snapshot, settingB);

        var readyA = SpinWait.SpinUntil(() => coordinator.GetResult("vp_a") != null, 2000);
        var readyB = SpinWait.SpinUntil(() => coordinator.GetResult("vp_b") != null, 2000);
        Assert.True(readyA, "vp_a should complete");
        Assert.True(readyB, "vp_b should complete");

        var resultA = coordinator.GetResult("vp_a");
        var resultB = coordinator.GetResult("vp_b");
        Assert.NotNull(resultA);
        Assert.NotNull(resultB);
        Assert.NotEqual(resultA!.RequestKey, resultB!.RequestKey);
        Assert.Equal(10, resultA.Segments[0].Count);
        Assert.Equal(50, resultB.Segments[0].Count);
    }

    [Fact]
    public void RequestUpdate_SameCountDataRevisionChanged_RecomputesInsteadOfReturningStaleCache()
    {
        // F06 regression: the cache key previously omitted any data-revision component, keying only
        // on candle count/visible window/parameters. An explicit reload/correction that replaces
        // every OHLCV value while leaving the candle count and visible window unchanged (e.g. a
        // volume correction) was therefore indistinguishable from an unchanged request, and the
        // coordinator kept returning the stale pre-correction result forever. See
        // sa_analysis_report_VolumeProfile_PostImplementation_StrictReview_20260914.md F06/A11.
        using var coordinator = new VolumeProfileViewportCoordinator(new SynchronousDispatcherService());
        var settings = CreateProfileSettings();

        var originalCandles = CreateSampleCandles(20);
        var snapshot1 = new ChartDataSnapshot(candles: originalCandles, startIndex: 0, count: 20);
        coordinator.RequestUpdate(snapshot1, settings, dataRevision: 1);
        var ready1 = SpinWait.SpinUntil(() => coordinator.GetResult(DefaultSettingId) != null, 2000);
        Assert.True(ready1);
        var result1 = coordinator.GetResult(DefaultSettingId);
        Assert.NotNull(result1);

        // Same symbol/timeframe/count/window/parameters, but a corrected volume (10x) for every bar
        // -- exactly the "same-count data correction" scenario the report reproduces.
        var correctedCandles = originalCandles.ConvertAll(c => new CoreCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume * 10));
        var snapshot2 = new ChartDataSnapshot(candles: correctedCandles, startIndex: 0, count: 20);

        // Without the dataRevision bump, every component of the old cache key (count/window/params)
        // is unchanged, so an unfixed coordinator would short-circuit and never recompute here.
        coordinator.RequestUpdate(snapshot2, settings, dataRevision: 2);
        var ready2 = SpinWait.SpinUntil(() => coordinator.GetResult(DefaultSettingId)?.RequestKey != result1!.RequestKey, 2000);
        Assert.True(ready2, "A data-revision bump must trigger recomputation even with an unchanged candle count/window.");

        var result2 = coordinator.GetResult(DefaultSettingId);
        Assert.NotNull(result2);
        Assert.NotEqual(result1!.RequestKey, result2!.RequestKey);
        Assert.NotEqual(result1.Segments[0].Bins[0].TotalVolume, result2.Segments[0].Bins[0].TotalVolume);
    }

    [Fact]
    public void RequestUpdate_RapidBurstBeforeFirstComputationCompletes_FinalResultReflectsLatestRequestOnly()
    {
        // F07 regression: previously, any RequestUpdate arriving while `key.Result` was still null
        // (i.e. before the very first computation for this key had completed) re-entered the
        // "compute immediately" branch, spawning a new concurrent Task.Run worker on top of any
        // still-running one instead of debouncing/queuing. With multiple concurrent workers racing
        // to publish `key.Result`/fire ResultReady, an EARLIER (stale) request's result could win
        // depending on completion order. This asserts the coordinator instead always converges on
        // the LATEST request only, regardless of how many rapid calls arrived beforehand. See
        // sa_analysis_report_VolumeProfile_PostImplementation_StrictReview_20260914.md F07.
        using var coordinator = new VolumeProfileViewportCoordinator(new SynchronousDispatcherService());
        var candles = CreateSampleCandles(500);

        for (int period = 5; period <= 50; period += 5)
        {
            var snapshot = new ChartDataSnapshot(candles: candles, startIndex: 0, count: 500);
            var settings = CreateProfileSettings(period: period, rowCount: 10);
            coordinator.RequestUpdate(snapshot, settings);
        }

        var ready = SpinWait.SpinUntil(() =>
        {
            var s = coordinator.GetState(DefaultSettingId);
            return s == VolumeProfileCoordinatorState.Ready || s == VolumeProfileCoordinatorState.Failed;
        }, 5000);
        Assert.True(ready, "Computation should settle within the wait window");

        // Give any chained recompute triggered by the burst a moment to fully settle before the
        // final assertion (StartComputation's own short-circuit makes this converge quickly).
        SpinWait.SpinUntil(() => coordinator.GetState(DefaultSettingId) == VolumeProfileCoordinatorState.Ready, 2000);

        var result = coordinator.GetResult(DefaultSettingId);
        Assert.NotNull(result);
        Assert.Equal(VolumeProfileResultStatus.Success, result!.Status);
        Assert.Single(result.Segments);
        Assert.Equal(50, result.Segments[0].Count); // last requested Period wins
    }

    [Fact]
    public void RequestUpdate_MultipleDebounceCycles_ReusesSameTimerInstance()
    {
        // F07 regression: every RequestUpdate call that took the debounce path previously disposed
        // the existing Timer and constructed a brand-new one (plus a new capturing closure) from
        // scratch. This verifies the fix reuses a single Timer object across repeated debounce
        // cycles for the same key instead of reallocating one on every call.
        using var coordinator = new VolumeProfileViewportCoordinator(new SynchronousDispatcherService());
        var candles = CreateSampleCandles(20);
        var settings = CreateProfileSettings();

        // First call takes the immediate-compute path (key.Result is still null); no Timer exists yet.
        var snapshot1 = new ChartDataSnapshot(candles: candles, startIndex: 0, count: 20);
        coordinator.RequestUpdate(snapshot1, settings);
        var ready = SpinWait.SpinUntil(() => coordinator.GetResult(DefaultSettingId) != null, 2000);
        Assert.True(ready);

        // Second call now takes the debounce path (key.Result is non-null), creating the Timer.
        var snapshot2 = new ChartDataSnapshot(candles: candles, startIndex: 0, count: 15);
        coordinator.RequestUpdate(snapshot2, settings);
        var keyState = GetKeyState(coordinator, DefaultSettingId);
        var timerAfterFirstDebounce = GetDebounceTimer(keyState);
        Assert.NotNull(timerAfterFirstDebounce);

        // Third call also takes the debounce path -- must reuse the SAME Timer instance.
        var snapshot3 = new ChartDataSnapshot(candles: candles, startIndex: 0, count: 10);
        coordinator.RequestUpdate(snapshot3, settings);
        var timerAfterSecondDebounce = GetDebounceTimer(keyState);

        Assert.Same(timerAfterFirstDebounce, timerAfterSecondDebounce);
    }

    [Fact]
    public void RequestUpdate_OnlySideChanged_DoesNotTriggerRecomputation()
    {
        // F06/F07: `Side` (Left/Right/Both display placement) affects only rendering
        // (VolumeProfileRenderer.RenderViewport), never the underlying bin computation
        // (VolumeProfileViewportCalculator/VolumeProfileRangeBuilder). Keying the coordinator's
        // computation cache on it forced a full, needless recompute for a change that only ever
        // needed a redraw. See sa_analysis_report_VolumeProfile_PostImplementation_StrictReview_20260914.md.
        using var coordinator = new VolumeProfileViewportCoordinator(new SynchronousDispatcherService());
        var candles = CreateSampleCandles(20);
        var snapshot = new ChartDataSnapshot(candles: candles, startIndex: 0, count: 20);
        var settings = CreateProfileSettings();
        var param = (CoreVolumeProfileParameter)settings.ParameterObject!;
        param.Side = DisplaySide.Left;

        coordinator.RequestUpdate(snapshot, settings);
        var ready = SpinWait.SpinUntil(() => coordinator.GetResult(DefaultSettingId) != null, 2000);
        Assert.True(ready);
        var resultBefore = coordinator.GetResult(DefaultSettingId);

        param.Side = DisplaySide.Right;
        coordinator.RequestUpdate(snapshot, settings);

        // Wait past the debounce delay plus a safety margin -- if Side were still part of the cache
        // key, this would trigger a genuine recomputation (a brand-new result object).
        Thread.Sleep(ChartConstants.IndicatorCalculationDebounceDelay + 300);

        var resultAfter = coordinator.GetResult(DefaultSettingId);
        Assert.Same(resultBefore, resultAfter);
    }

    [Fact]
    public void RequestUpdate_SymbolChangedWhileResultReady_ClearsStaleResultImmediately()
    {
        // F08 regression: a Ready Result computed for the PREVIOUS chart context (symbol/timeframe/
        // chart type) must not go on being returned by GetResult (and therefore rendered) once a
        // RequestUpdate arrives for a DIFFERENT context -- e.g. the user switched symbols and the new
        // symbol's computation has not completed yet. This is distinct from a same-context update
        // (pan/zoom/parameter tweak), where keeping the last valid Result visible during recompute is
        // the desired, flicker-free behavior. See
        // sa_analysis_report_VolumeProfile_PostImplementation_StrictReview_20260914.md F08
        // ("同context旧結果保持と別context旧結果破棄を区別").
        using var coordinator = new VolumeProfileViewportCoordinator(new SynchronousDispatcherService());
        var candlesA = CreateSampleCandles(20);
        var snapshotA = new ChartDataSnapshot(candles: candlesA, startIndex: 0, count: 20, symbol: "AAPL");
        var settings = CreateProfileSettings();

        coordinator.RequestUpdate(snapshotA, settings);
        var ready = SpinWait.SpinUntil(() => coordinator.GetResult(DefaultSettingId) != null, 2000);
        Assert.True(ready);
        Assert.Equal(VolumeProfileCoordinatorState.Ready, coordinator.GetState(DefaultSettingId));

        // Same context (still "AAPL"): a follow-up pan/zoom-style request must NOT clear the existing
        // Result while the (synchronous, near-instant in this test) recompute is in flight.
        var snapshotAPanned = new ChartDataSnapshot(candles: candlesA, startIndex: 0, count: 15, symbol: "AAPL");
        coordinator.IsPointerCaptured = true;
        coordinator.RequestUpdate(snapshotAPanned, settings);
        Assert.NotNull(coordinator.GetResult(DefaultSettingId));
        coordinator.IsPointerCaptured = false;

        // Context switch: a different symbol arrives. The stale "AAPL" Result must be cleared
        // synchronously by this call, before the new symbol's computation has any chance to complete.
        coordinator.IsPointerCaptured = true;
        var candlesB = CreateSampleCandles(20);
        var snapshotB = new ChartDataSnapshot(candles: candlesB, startIndex: 0, count: 20, symbol: "MSFT");
        coordinator.RequestUpdate(snapshotB, settings);

        Assert.Null(coordinator.GetResult(DefaultSettingId));
        Assert.NotEqual(VolumeProfileCoordinatorState.Ready, coordinator.GetState(DefaultSettingId));
    }

    [Fact]
    public void StartComputation_ParameterMutatedAfterDispatch_WorkerObservesFrozenValuesNotLiveMutation()
    {
        // F08 regression: VolumeProfileViewportCalculator.Calculate re-reads `parameter.RowCount`/
        // `parameter.Mode` once per segment on a background thread. Previously the coordinator passed
        // the LIVE, still-mutable CoreVolumeProfileParameter object straight through, so a settings
        // edit landing on the UI thread while the worker was still running could be observed mid-
        // computation. This asserts the worker's result reflects RowCount as it was at dispatch time,
        // even if the live parameter object is mutated immediately afterward. See
        // sa_analysis_report_VolumeProfile_PostImplementation_StrictReview_20260914.md F08
        // ("開始時に計算用primitive値を凍結。worker専有入力とする").
        using var coordinator = new VolumeProfileViewportCoordinator(new SynchronousDispatcherService());
        var candles = CreateSampleCandles(20);
        var snapshot = new ChartDataSnapshot(candles: candles, startIndex: 0, count: 20);
        var settings = CreateProfileSettings(rowCount: 10);
        var param = (CoreVolumeProfileParameter)settings.ParameterObject!;

        coordinator.RequestUpdate(snapshot, settings);
        // Mutate the live parameter object immediately after dispatch, simulating a settings-dialog
        // edit racing with the background worker.
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
        // F12/A17: Period/RowCount range validation previously only ran via the settings dialog's own
        // editor attributes -- nothing re-validated the actual CoreVolumeProfileParameter reaching
        // StartComputation, so an out-of-range value arriving any other way (e.g. a hand-edited or
        // older saved settings file) would flow straight into computation instead of being rejected as
        // an explicit invalid-parameter failure. See
        // sa_analysis_report_VolumeProfile_PostImplementation_StrictReview_20260914.md F12.
        using var coordinator = new VolumeProfileViewportCoordinator(new SynchronousDispatcherService());
        var candles = CreateSampleCandles(20);
        var snapshot = new ChartDataSnapshot(candles: candles, startIndex: 0, count: 20);
        var settings = CreateProfileSettings();
        var param = (CoreVolumeProfileParameter)settings.ParameterObject!;
        param.Period = -5; // out of the documented [0, 10000] range

        coordinator.RequestUpdate(snapshot, settings);

        Assert.Equal(VolumeProfileCoordinatorState.Failed, coordinator.GetState(DefaultSettingId));
        Assert.Null(coordinator.GetResult(DefaultSettingId));
    }

    [Fact]
    public void RequestUpdate_InvalidDataThrownAfterPriorSuccess_ClearsStaleResultInsteadOfKeepingIt()
    {
        // V02/regression fix (sa_analysis_VolumeProfile_V02_V09-V12_Resolution_20260916.md): Calculate
        // now throws InvalidDataException for invalid candle data (negative volume, High<Low, etc.)
        // instead of returning a Failed-status result. The coordinator's generic exception catch
        // previously left the PRIOR successful Result object untouched (only flipping State to Failed),
        // and IndicatorRenderer reads Result.Status alone -- never State -- so a stale, no-longer-valid
        // profile would keep rendering forever after such an exception. This proves the fix: once the
        // exception path runs, GetResult must return null, matching the Failed-status path's own
        // Result-clearing behavior (see RequestUpdate_InvalidPeriodParameter_MarksFailedWithoutComputing
        // above).
        using var coordinator = new VolumeProfileViewportCoordinator(new SynchronousDispatcherService());
        var candles = CreateSampleCandles(20);
        var settings = CreateProfileSettings();

        var snapshot1 = new ChartDataSnapshot(candles: candles, startIndex: 0, count: 20);
        coordinator.RequestUpdate(snapshot1, settings);
        var ready = SpinWait.SpinUntil(() => coordinator.GetResult(DefaultSettingId) != null, 2000);
        Assert.True(ready);
        Assert.Equal(VolumeProfileResultStatus.Success, coordinator.GetResult(DefaultSettingId)!.Status);

        // Anchor reversal (2026-09-16): Period=10 over a 20-candle visible window now resolves to the
        // TRAILING segment [10,20) (backward from the visible end), not the leading segment [0,10) as
        // before -- the corrupted candle must sit inside that trailing window (index 15, not 5) for
        // Calculate to actually observe it and throw.
        var invalidCandles = candles.ConvertAll(c => new CoreCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume));
        invalidCandles[15] = new CoreCandleData(
            invalidCandles[15].Timestamp, invalidCandles[15].Open, invalidCandles[15].High, invalidCandles[15].Low,
            invalidCandles[15].Close, -500); // negative volume -- forces Calculate to throw
        var snapshot2 = new ChartDataSnapshot(candles: invalidCandles, startIndex: 0, count: 20);

        // Same count/window/parameters as snapshot1 -- only the candle DATA changed -- so dataRevision
        // must be bumped to bypass the RequestKey cache short-circuit (same reasoning as F06's
        // RequestUpdate_SameCountDataRevisionChanged_RecomputesInsteadOfReturningStaleCache above).
        coordinator.RequestUpdate(snapshot2, settings, dataRevision: 2);

        // The outcome is produced by the debounce timer and a background worker. Wait for the outcome itself (the stale result being cleared)
        // rather than sleeping a fixed time: a fixed sleep failed whenever the thread pool ran the timer/worker later than the margin.
        var cleared = SpinWait.SpinUntil(() => coordinator.GetResult(DefaultSettingId) == null, DebouncedOutcomeTimeoutMs);

        Assert.True(cleared);
        Assert.Null(coordinator.GetResult(DefaultSettingId));
    }

    [Fact]
    public void ReleaseKey_PendingSettingDisabledSinceQueued_DoesNotResumeStaleComputation()
    {
        // F09 regression: NotifyPointerReleased (and OnDebounceElapsed) previously reached
        // StartComputation using whatever CoreIndicatorSettings was last stored as PendingSetting,
        // without re-checking IsEnabled -- only the immediate branch inside RequestUpdate did. If the
        // indicator was disabled/removed after being queued (e.g. a pointer-release event firing after
        // the VolumeProfile indicator was turned off, per ChartBaseControl's unconditional release
        // notification), a computation for the now-disabled setting could still resume. See
        // sa_analysis_report_VolumeProfile_PostImplementation_StrictReview_20260914.md F09.
        using var coordinator = new VolumeProfileViewportCoordinator(new SynchronousDispatcherService());
        var candles = CreateSampleCandles(20);
        var snapshot = new ChartDataSnapshot(candles: candles, startIndex: 0, count: 20);
        var settings = CreateProfileSettings(isEnabled: true);

        // Queue a request while the pointer is captured so it never auto-computes immediately.
        coordinator.IsPointerCaptured = true;
        coordinator.RequestUpdate(snapshot, settings);
        Assert.Null(coordinator.GetResult(DefaultSettingId));

        // The indicator is disabled while still mid-drag (PendingSetting still references the old,
        // now-stale CoreIndicatorSettings instance since nothing re-queued it).
        settings.IsEnabled = false;

        // Pointer release fires unconditionally regardless of the setting's current enabled state.
        coordinator.NotifyPointerReleased();

        Thread.Sleep(200);
        Assert.Null(coordinator.GetResult(DefaultSettingId));
        Assert.Equal(VolumeProfileCoordinatorState.Suspended, coordinator.GetState(DefaultSettingId));
    }

    [Fact]
    public void OnWorkerDone_KeyResetAndReplacedWhileWorkerInFlight_DoesNotWriteIntoNewKeyState()
    {
        // F09 regression: a worker's completion callback previously looked its PerKeyState up by
        // settingId string alone. If Reset(id) discarded the key while a worker was still running, and
        // a fresh RequestUpdate for the SAME id then created a brand-new PerKeyState object before the
        // old worker's completion callback ran, the old (now-orphaned) worker could still find an entry
        // under that id and, if its captured Generation coincidentally matched the new instance's
        // Generation, overwrite the new key's Result/State with its own stale/discarded outcome. This
        // verifies the completion callback only ever acts on the exact PerKeyState instance it started
        // against. See sa_analysis_report_VolumeProfile_PostImplementation_StrictReview_20260914.md F09
        // ("破棄済み状態へcallbackが書き戻さないことを保証").
        using var coordinator = new VolumeProfileViewportCoordinator(new SynchronousDispatcherService());
        var candles = CreateSampleCandles(500);
        var settings = CreateProfileSettings(period: 5, rowCount: 10);
        var snapshot = new ChartDataSnapshot(candles: candles, startIndex: 0, count: 500);

        // Start a first computation (large candle count keeps it running briefly) then immediately
        // discard its key before it can complete.
        coordinator.RequestUpdate(snapshot, settings);
        coordinator.Reset(DefaultSettingId);

        // Re-request the same id right away -- this creates a brand-new PerKeyState whose Generation
        // starts back at 1, the same value the orphaned worker above captured.
        var settings2 = CreateProfileSettings(period: 20, rowCount: 10);
        coordinator.RequestUpdate(snapshot, settings2);

        var ready = SpinWait.SpinUntil(() => coordinator.GetResult(DefaultSettingId) != null, 3000);
        Assert.True(ready, "The second (current) key's own computation should complete normally");

        var result = coordinator.GetResult(DefaultSettingId);
        Assert.NotNull(result);
        Assert.Equal(20, result!.Segments[0].Count); // must reflect settings2, never the discarded settings' period=5 run
    }

    [Fact]
    public void PruneStaleKeys_RemovesTrackedStateForIdsNoLongerActive()
    {
        using var coordinator = new VolumeProfileViewportCoordinator(new SynchronousDispatcherService());
        var candles = CreateSampleCandles(20);
        var snapshot = new ChartDataSnapshot(candles: candles, startIndex: 0, count: 20);
        var settings = CreateProfileSettings(id: "vp_removed");

        coordinator.RequestUpdate(snapshot, settings);
        SpinWait.SpinUntil(() => coordinator.GetResult("vp_removed") != null, 2000);
        Assert.NotNull(coordinator.GetResult("vp_removed"));

        coordinator.PruneStaleKeys(Array.Empty<string>());

        Assert.Null(coordinator.GetResult("vp_removed"));
        Assert.Equal(VolumeProfileCoordinatorState.Idle, coordinator.GetState("vp_removed"));
    }

    [Fact]
    public void Dispose_CancelsAndDisposesCleanly()
    {
        var coordinator = new VolumeProfileViewportCoordinator(new SynchronousDispatcherService());
        coordinator.Dispose();

        Assert.True(coordinator.IsDisposed);

        // Calling RequestUpdate after dispose should be a no-op
        var candles = CreateSampleCandles(10);
        var snapshot = new ChartDataSnapshot(candles: candles, startIndex: 0, count: 10);
        coordinator.RequestUpdate(snapshot, CreateProfileSettings());

        Assert.True(coordinator.IsDisposed);
        Assert.Null(coordinator.GetResult(DefaultSettingId));
    }
}
