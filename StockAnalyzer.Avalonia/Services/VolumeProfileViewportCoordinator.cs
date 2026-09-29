using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.Services;

public enum VolumeProfileCoordinatorState
{
    Idle,
    Waiting,
    Computing,
    Ready,
    Failed,
    Suspended,
    Disposed
}

/// <summary>
/// Computes and holds one independent debounced Volume Profile result per indicator setting Id
/// (<see cref="CoreIndicatorSettings.Id"/>), so that multiple concurrently-registered VolumeProfile
/// indicators each keep their own Period/RowCount/Mode/Side and computed bins instead of sharing a
/// single result (F05: sa_analysis_report_VolumeProfile_PostImplementation_StrictReview_20260914.md).
/// </summary>
public class VolumeProfileViewportCoordinator : IDisposable
{
    private sealed class PerKeyState
    {
        public Timer? DebounceTimer;
        public CancellationTokenSource? ActiveCts;
        public int Generation;
        public ChartDataSnapshot? PendingSnapshot;
        public CoreIndicatorSettings? PendingSetting;
        public VolumeProfileViewportResult? Result;
        public VolumeProfileCoordinatorState State = VolumeProfileCoordinatorState.Idle;
        public long PendingDataRevision;

        /// <summary>
        /// True from the moment a worker's Task.Run body starts until its completion callback runs.
        /// F07 fix: while true, a new RequestUpdate/ReleaseKey must NOT cancel-and-immediately-start a
        /// second concurrent worker for this key -- it only updates Pending*/Generation and lets the
        /// in-flight worker's own completion callback notice the generation change and immediately
        /// chain into the next computation using whatever became the latest Pending* state by then.
        /// This guarantees at most one worker per key at any time (
        /// sa_analysis_report_VolumeProfile_PostImplementation_StrictReview_20260914.md F07).
        /// </summary>
        public bool IsWorkerRunning;

        /// <summary>
        /// Symbol/Timeframe/ChartType identity of whatever context <see cref="Result"/> was computed
        /// for (or the most recent RequestUpdate's context, if no Result exists yet). Lets RequestUpdate
        /// tell a same-context update (pan/zoom/parameter tweak -- the last valid Result should keep
        /// rendering while the next one computes, to avoid flicker) apart from a context switch (symbol/
        /// timeframe/chart-type change), for which a Result computed for the OLD context must not go on
        /// rendering while the new one is still in flight (F08:
        /// sa_analysis_report_VolumeProfile_PostImplementation_StrictReview_20260914.md, "同context旧結
        /// 果保持と別context旧結果破棄を区別").
        /// </summary>
        public string? LastContextKey;
    }

    private readonly Guid _lifetimeId = Guid.NewGuid();
    private readonly IDispatcherService? _dispatcherService;
    private readonly object _syncLock = new();
    private readonly Dictionary<string, PerKeyState> _keyStates = new();
    private bool _isDisposed = false;

    public bool IsPointerCaptured { get; set; }
    public bool IsDisposed => _isDisposed;

    /// <summary>Fired whenever any tracked setting Id's computation completes (Success or Failed).</summary>
    public event Action<VolumeProfileViewportResult>? ResultReady;

    public VolumeProfileViewportCoordinator(IDispatcherService? dispatcherService = null)
    {
        _dispatcherService = dispatcherService;
    }

    public VolumeProfileViewportResult? GetResult(string settingId)
    {
        lock (_syncLock)
        {
            return _keyStates.TryGetValue(settingId, out var s) ? s.Result : null;
        }
    }

    public VolumeProfileCoordinatorState GetState(string settingId)
    {
        lock (_syncLock)
        {
            return _keyStates.TryGetValue(settingId, out var s) ? s.State : VolumeProfileCoordinatorState.Idle;
        }
    }

    /// <summary>
    /// <paramref name="dataRevision"/> identifies the underlying candle DATA generation (e.g.
    /// <c>ChartViewModel.DataRevision</c>), distinct from the visible window/count captured by
    /// <paramref name="snapshot"/>. It must be included in the computation cache key so that an
    /// explicit reload/correction that replaces OHLCV values while leaving the candle count, visible
    /// window, and indicator parameters all unchanged is not silently treated as an identical,
    /// already-computed request (F06:
    /// sa_analysis_report_VolumeProfile_PostImplementation_StrictReview_20260914.md).
    /// </summary>
    public void RequestUpdate(ChartDataSnapshot snapshot, CoreIndicatorSettings setting, long dataRevision = 0)
    {
        if (_isDisposed) return;
        if (setting == null) return;

        if (!setting.IsEnabled || setting.ParameterObject is not CoreVolumeProfileParameter)
        {
            lock (_syncLock)
            {
                var key = GetOrCreateKeyState(setting.Id);
                key.DebounceTimer?.Dispose();
                key.DebounceTimer = null;
                key.ActiveCts?.Cancel();
                key.ActiveCts = null;
                key.Result = null;
                key.State = VolumeProfileCoordinatorState.Suspended;
            }
            return;
        }

        lock (_syncLock)
        {
            var key = GetOrCreateKeyState(setting.Id);

            // F08 fix: if this request's context (symbol/timeframe/chart type) differs from whatever
            // context the currently-held Result belongs to, that Result is for a now-irrelevant context
            // (e.g. the chart just switched symbols) and must not keep being returned by GetResult/
            // rendered while the new context's computation is still in flight -- unlike a same-context
            // update (pan/zoom/parameter tweak), where keeping the last valid Result visible during
            // recompute is the desired, flicker-free behavior (sa_analysis_report_..._20260914.md F08).
            string newContextKey = $"{snapshot.Symbol}_{snapshot.Timeframe}_{snapshot.ChartType}";
            if (key.LastContextKey != null && key.LastContextKey != newContextKey)
            {
                key.Result = null;
                key.State = VolumeProfileCoordinatorState.Idle;
            }
            key.LastContextKey = newContextKey;

            key.PendingSnapshot = snapshot;
            key.PendingSetting = setting;
            key.PendingDataRevision = dataRevision;
            key.Generation++;

            // F07 fix: previously this always cancelled any ActiveCts and either restarted a worker
            // immediately (whenever key.Result was still null) or reset the debounce timer -- a burst
            // of rapid requests arriving before the FIRST-ever computation for a key completed each
            // re-entered the `key.Result == null` immediate-compute branch, piling up concurrent
            // Task.Run workers instead of debouncing. A worker already in flight is now left alone:
            // its own completion callback re-checks Generation and chains into the next computation
            // using the latest Pending* state once it finishes.
            if (key.IsWorkerRunning)
            {
                key.State = VolumeProfileCoordinatorState.Computing;
                return;
            }

            if (key.Result == null && !IsPointerCaptured)
            {
                key.DebounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                StartComputation(setting.Id, key, key.Generation);
            }
            else
            {
                key.State = VolumeProfileCoordinatorState.Waiting;
                ScheduleDebounce(setting.Id, key);
            }
        }
    }

    /// <summary>
    /// F07 fix: reuses a single Timer instance (and its one capturing closure) per key across every
    /// debounce cycle via <see cref="Timer.Change(long,long)"/>, instead of disposing and constructing
    /// a brand-new Timer (plus a new closure capturing the expected generation) on every RequestUpdate
    /// call. The callback re-reads PerKeyState live at fire time rather than a captured generation, so
    /// it always acts on whatever is currently pending.
    /// </summary>
    private void ScheduleDebounce(string settingId, PerKeyState key)
    {
        key.DebounceTimer ??= new Timer(_ => OnDebounceElapsed(settingId), null, Timeout.Infinite, Timeout.Infinite);
        key.DebounceTimer.Change(ChartConstants.IndicatorCalculationDebounceDelay, Timeout.Infinite);
    }

    /// <summary>
    /// Chart-wide pointer release: immediately (re)computes every tracked setting Id using the latest
    /// snapshot, since panning/dragging is a single chart-level gesture shared by all registered
    /// VolumeProfile indicators. Passing an explicit <paramref name="setting"/> instead targets only
    /// that one Id.
    /// </summary>
    public void NotifyPointerReleased(ChartDataSnapshot? snapshot = null, CoreIndicatorSettings? setting = null)
    {
        if (_isDisposed) return;

        lock (_syncLock)
        {
            IsPointerCaptured = false;

            if (setting != null)
            {
                var key = GetOrCreateKeyState(setting.Id);
                if (snapshot != null) key.PendingSnapshot = snapshot;
                key.PendingSetting = setting;
                ReleaseKey(setting.Id, key, snapshot);
                return;
            }

            foreach (var (id, key) in _keyStates)
            {
                if (snapshot != null) key.PendingSnapshot = snapshot;
                ReleaseKey(id, key, snapshot);
            }
        }
    }

    private void ReleaseKey(string settingId, PerKeyState key, ChartDataSnapshot? snapshot)
    {
        key.DebounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        key.Generation++;

        // F07 fix: do not cancel-and-restart a worker that is already in flight -- bump Generation
        // and let its completion callback chain into the next computation with the latest Pending*
        // state once it finishes, preserving the "at most one worker per key" invariant.
        if (key.IsWorkerRunning)
        {
            key.State = VolumeProfileCoordinatorState.Computing;
            return;
        }

        StartComputation(settingId, key, key.Generation);
    }

    private void OnDebounceElapsed(string settingId)
    {
        if (_isDisposed) return;

        lock (_syncLock)
        {
            if (!_keyStates.TryGetValue(settingId, out var key)) return;
            if (IsPointerCaptured) return;
            // A worker may already be running if it started via a different path (e.g. an immediate
            // first-computation or a pointer-release) between this timer being scheduled and firing;
            // its own completion callback will pick up whatever is latest, so this is a safe no-op.
            if (key.IsWorkerRunning) return;

            StartComputation(settingId, key, key.Generation);
        }
    }

    private void StartComputation(string settingId, PerKeyState key, int generation)
    {
        var snapshot = key.PendingSnapshot;
        var setting = key.PendingSetting;

        if (setting == null)
        {
            key.State = VolumeProfileCoordinatorState.Idle;
            return;
        }

        // F09 fix: OnDebounceElapsed and ReleaseKey both reach this method using whatever setting was
        // last pending, without re-checking IsEnabled themselves -- only the immediate branch inside
        // RequestUpdate did. A setting disabled/removed after being queued (e.g. a pointer-release
        // firing after its VolumeProfile indicator was turned off) must not resume a stale computation
        // for it (sa_analysis_report_VolumeProfile_PostImplementation_StrictReview_20260914.md F09:
        // "Start/Release時にも有効setting/context/capabilityを検証").
        if (!setting.IsEnabled)
        {
            key.State = VolumeProfileCoordinatorState.Suspended;
            key.Result = null;
            return;
        }

        if (snapshot == null || setting.ParameterObject is not CoreVolumeProfileParameter param)
        {
            key.State = VolumeProfileCoordinatorState.Idle;
            return;
        }

        // F12 fix: Period/RowCount range validation previously only ever ran via the settings-dialog
        // UI's own editor range attributes -- nothing re-checked it on this, the actual computation
        // entry point, so a corrupt/out-of-range value reaching here any other way (e.g. a hand-edited
        // or older saved settings file) would flow straight into VolumeAnalysis.CalculateProfile
        // (RowCount<=0 silently yields an empty bin list with no explicit signal why) instead of being
        // rejected as an explicit invalid-parameter failure. See
        // sa_analysis_report_VolumeProfile_PostImplementation_StrictReview_20260914.md F12/A17.
        try
        {
            param.Validate();
        }
        catch (ArgumentOutOfRangeException)
        {
            key.State = VolumeProfileCoordinatorState.Failed;
            key.Result = null;
            return;
        }

        if (snapshot.Candles == null || snapshot.Candles.Count == 0 || snapshot.VisibleCandleCount <= 0)
        {
            key.State = VolumeProfileCoordinatorState.Idle;
            return;
        }

        var timeframe = Enum.TryParse<TimeframeType>(snapshot.Timeframe, out var tf) ? tf : TimeframeType.Daily;
        int vStart = snapshot.StartIndex;
        int vCount = snapshot.VisibleCandleCount;
        long dataRevision = key.PendingDataRevision;

        // F06 fix: dataRevision (the underlying candle DATA generation) must be part of the cache
        // key alongside the count/window/parameter identity below -- an explicit reload/correction
        // can replace every OHLCV value while the candle count, visible window, and indicator
        // parameters all stay the same, which the previous key (missing any data-revision component)
        // could not distinguish from an already-computed, still-valid request.
        // F07 fix: `Side` (Left/Right/Both display placement) is intentionally excluded -- it is a
        // pure rendering concern read only by VolumeProfileRenderer.RenderViewport, never by
        // VolumeProfileViewportCalculator/VolumeProfileRangeBuilder's bin computation, so keying on
        // it forced a full recompute for a change that only ever needed a redraw.
        string requestKey = $"{_lifetimeId}_{setting.Id}_{snapshot.Symbol}_{snapshot.Timeframe}_{snapshot.ChartType}_{snapshot.Candles.Count}_{vStart}_{vCount}_{param.Period}_{param.RowCount}_{param.Mode}_{dataRevision}";

        if (key.Result != null && key.Result.RequestKey == requestKey && key.Result.Status == VolumeProfileResultStatus.Success)
        {
            key.State = VolumeProfileCoordinatorState.Ready;
            return;
        }

        var cts = new CancellationTokenSource();
        key.ActiveCts = cts;
        key.State = VolumeProfileCoordinatorState.Computing;
        key.IsWorkerRunning = true;

        var candles = snapshot.Candles;

        // F02 fix: `candles` (snapshot.Candles) is already the visible-window slice, so the correct
        // start offset INTO that slice is always 0 -- the slice's own first element is the first
        // visible bar. `vStart` (snapshot.StartIndex) is the GLOBAL index of that same bar within the
        // full unsliced history and must only be used for request identity (requestKey above), never
        // as an index into `candles` itself. Passing vStart here previously either overran the slice
        // bounds (VolumeProfileRangeBuilder's `visibleStartIndex >= candles.Count` guard, returning an
        // Empty result for any global start beyond the slice length) or, when vStart happened to be
        // smaller than the slice length, silently double-offset the window away from the visible start
        // (see sa_analysis_report_VolumeProfile_PostImplementation_StrictReview_20260914.md F02).
        const int sliceLocalStart = 0;

        // F08 fix: `param` is the live, still-mutable CoreVolumeProfileParameter instance shared with
        // the settings UI/dialog. VolumeProfileViewportCalculator.Calculate re-reads `parameter.RowCount`
        // and `parameter.Mode` once PER SEGMENT inside its loop, on a background thread that can run
        // for the duration of a multi-segment computation -- if a settings edit lands on the UI thread
        // mid-computation, different segments of the SAME result could observe different RowCount/Mode
        // values, all silently labeled as one consistent result by the single `requestKey` string built
        // from the pre-mutation values above. Freezing the exact primitive values the calculator reads
        // into a worker-private copy before dispatching removes any live reference to the mutable
        // settings object from the background computation (sa_analysis_report_..._20260914.md F08:
        // "開始時に計算用primitive値を凍結。worker専有入力とする").
        var frozenParam = new CoreVolumeProfileParameter
        {
            Period = param.Period,
            RowCount = param.RowCount,
            Mode = param.Mode
        };

        _ = Task.Run(() =>
        {
            VolumeProfileViewportResult? result = null;
            bool canceled = false;

            try
            {
                result = VolumeProfileViewportCalculator.Calculate(
                    requestKey,
                    candles,
                    sliceLocalStart,
                    vCount,
                    timeframe,
                    frozenParam,
                    cts.Token);
                canceled = cts.Token.IsCancellationRequested;
            }
            catch (OperationCanceledException)
            {
                canceled = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"VolumeProfileViewportCoordinator calculation error: {ex.Message}");
            }

            // F07 fix: this single completion path always clears IsWorkerRunning (releasing the "at
            // most one worker per key" gate for the next RequestUpdate/debounce/release) and, if a
            // newer request arrived while this worker ran (generation mismatch), discards this
            // worker's result -- even if it completed successfully -- and immediately chains into
            // computing the LATEST Pending* state instead of publishing a result already known to be
            // superseded, or silently dropping the newer request until some external trigger happens
            // to arrive later (sa_analysis_report_..._20260914.md F07: "worker終了までは次workerを
            // 起動しない。pendingは最新1件。旧結果公開数=0を保証").
            void OnWorkerDone()
            {
                lock (_syncLock)
                {
                    // F09 fix: a stale worker's completion must never write into a PerKeyState that
                    // Reset/PruneStaleKeys already discarded and possibly replaced with a brand-new
                    // instance under the same settingId (e.g. remove-then-re-add the same indicator
                    // while this worker was still running). Looking the id up by string alone is not
                    // enough -- a coincidentally-equal Generation counter on the new instance could
                    // otherwise pass the staleness check below and let a discarded worker publish into
                    // it. Only proceed if the dictionary still holds this EXACT PerKeyState instance
                    // (sa_analysis_report_VolumeProfile_PostImplementation_StrictReview_20260914.md F09:
                    // "破棄済み状態へcallbackが書き戻さないことを保証").
                    if (_isDisposed || !_keyStates.TryGetValue(settingId, out var currentKey) || !ReferenceEquals(currentKey, key))
                    {
                        cts.Dispose();
                        return;
                    }

                    currentKey.IsWorkerRunning = false;
                    // F09 fix: dispose this worker's own CTS once it is done with it. Only null out
                    // ActiveCts first if it still points at this same instance -- a chained recompute
                    // below (StartComputation) always creates its own fresh CTS, so this never races
                    // with it, and nothing later calls .Cancel() on the now-disposed instance.
                    if (ReferenceEquals(currentKey.ActiveCts, cts))
                    {
                        currentKey.ActiveCts = null;
                    }
                    cts.Dispose();

                    bool isStale = generation != currentKey.Generation;
                    if (!canceled && result != null && !isStale)
                    {
                        currentKey.Result = result;
                        currentKey.State = result.Status == VolumeProfileResultStatus.Success
                            ? VolumeProfileCoordinatorState.Ready
                            : VolumeProfileCoordinatorState.Failed;
                        ResultReady?.Invoke(result);
                        return;
                    }

                    if (result == null && !canceled)
                    {
                        // V02 fix (sa_analysis_VolumeProfile_V02_V09-V12_Resolution_20260916.md): a
                        // thrown exception (OverflowException, InvalidDataException) reaches only this
                        // branch, never the Result-replacing branch above -- leaving a stale prior
                        // Success Result in place while only State flips to Failed. IndicatorRenderer
                        // reads Result.Status alone (never State), so without this line a stale profile
                        // kept rendering after an exception-based rejection, silently reintroducing the
                        // F03/A10 "no stale profile on Failed" defect for this one pathway.
                        currentKey.Result = null;
                        currentKey.State = VolumeProfileCoordinatorState.Failed;
                    }

                    if (isStale)
                    {
                        if (IsPointerCaptured)
                        {
                            // Defer the chained recompute until release, same as any other pending
                            // request captured mid-drag; NotifyPointerReleased will start it once
                            // IsWorkerRunning is (already) false.
                            currentKey.State = VolumeProfileCoordinatorState.Waiting;
                        }
                        else
                        {
                            StartComputation(settingId, currentKey, currentKey.Generation);
                        }
                    }
                }
            }

            if (_dispatcherService != null)
            {
                _dispatcherService.Post(OnWorkerDone);
            }
            else
            {
                Dispatcher.UIThread.Post(OnWorkerDone);
            }
        });
    }

    private PerKeyState GetOrCreateKeyState(string settingId)
    {
        if (!_keyStates.TryGetValue(settingId, out var key))
        {
            key = new PerKeyState();
            _keyStates[settingId] = key;
        }
        return key;
    }

    /// <summary>Resets and discards a single setting Id's pending/current computation state.</summary>
    public void Reset(string settingId)
    {
        lock (_syncLock)
        {
            if (_keyStates.Remove(settingId, out var key))
            {
                key.DebounceTimer?.Dispose();
                key.ActiveCts?.Cancel();
            }
        }
    }

    /// <summary>Resets and discards every tracked setting Id's pending/current computation state.</summary>
    public void Reset()
    {
        lock (_syncLock)
        {
            foreach (var key in _keyStates.Values)
            {
                key.DebounceTimer?.Dispose();
                key.ActiveCts?.Cancel();
            }
            _keyStates.Clear();
        }
    }

    /// <summary>
    /// Discards tracked state for any setting Id not present in <paramref name="activeSettingIds"/>.
    /// Required so removing/disabling a VolumeProfile indicator does not leak its per-key timer/CTS
    /// forever now that state is tracked per Id instead of a single shared instance (F05 follow-on of
    /// moving from one shared result to a keyed dictionary).
    /// </summary>
    public void PruneStaleKeys(IEnumerable<string> activeSettingIds)
    {
        var activeSet = new HashSet<string>(activeSettingIds);
        lock (_syncLock)
        {
            var staleIds = _keyStates.Keys.Where(id => !activeSet.Contains(id)).ToList();
            foreach (var id in staleIds)
            {
                if (_keyStates.Remove(id, out var key))
                {
                    key.DebounceTimer?.Dispose();
                    key.ActiveCts?.Cancel();
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            foreach (var key in _keyStates.Values)
            {
                key.DebounceTimer?.Dispose();
                key.ActiveCts?.Cancel();
            }
            _keyStates.Clear();
        }
        GC.SuppressFinalize(this);
    }
}
