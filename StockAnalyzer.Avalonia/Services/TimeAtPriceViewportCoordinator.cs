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
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.Services;

public enum TimeAtPriceCoordinatorState
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
/// Computes and holds one independent debounced Time at Price result per indicator setting Id
/// (<see cref="CoreIndicatorSettings.Id"/>).
/// </summary>
public class TimeAtPriceViewportCoordinator : IDisposable
{
    private sealed class PerKeyState
    {
        public Timer? DebounceTimer;
        public CancellationTokenSource? ActiveCts;
        public int Generation;
        public ChartDataSnapshot? PendingSnapshot;
        public CoreIndicatorSettings? PendingSetting;
        public VolumeProfileViewportResult? Result;
        public TimeAtPriceCoordinatorState State = TimeAtPriceCoordinatorState.Idle;
        public long PendingDataRevision;
        public bool IsWorkerRunning;
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

    public TimeAtPriceViewportCoordinator(IDispatcherService? dispatcherService = null)
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

    public TimeAtPriceCoordinatorState GetState(string settingId)
    {
        lock (_syncLock)
        {
            return _keyStates.TryGetValue(settingId, out var s) ? s.State : TimeAtPriceCoordinatorState.Idle;
        }
    }

    public void RequestUpdate(ChartDataSnapshot snapshot, CoreIndicatorSettings setting, long dataRevision = 0)
    {
        if (_isDisposed) return;
        if (setting == null) return;

        if (!setting.IsEnabled || setting.ParameterObject is not CoreTimeAtPriceParameter)
        {
            lock (_syncLock)
            {
                var key = GetOrCreateKeyState(setting.Id);
                key.DebounceTimer?.Dispose();
                key.DebounceTimer = null;
                key.ActiveCts?.Cancel();
                key.ActiveCts = null;
                key.Result = null;
                key.State = TimeAtPriceCoordinatorState.Suspended;
            }
            return;
        }

        lock (_syncLock)
        {
            var key = GetOrCreateKeyState(setting.Id);

            string newContextKey = $"{snapshot.Symbol}_{snapshot.Timeframe}_{snapshot.ChartType}";
            if (key.LastContextKey != null && key.LastContextKey != newContextKey)
            {
                key.Result = null;
                key.State = TimeAtPriceCoordinatorState.Idle;
            }
            key.LastContextKey = newContextKey;

            key.PendingSnapshot = snapshot;
            key.PendingSetting = setting;
            key.PendingDataRevision = dataRevision;
            key.Generation++;

            if (key.IsWorkerRunning)
            {
                key.State = TimeAtPriceCoordinatorState.Computing;
                return;
            }

            if (key.Result == null && !IsPointerCaptured)
            {
                key.DebounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                StartComputation(setting.Id, key, key.Generation);
            }
            else
            {
                key.State = TimeAtPriceCoordinatorState.Waiting;
                ScheduleDebounce(setting.Id, key);
            }
        }
    }

    private void ScheduleDebounce(string settingId, PerKeyState key)
    {
        key.DebounceTimer ??= new Timer(_ => OnDebounceElapsed(settingId), null, Timeout.Infinite, Timeout.Infinite);
        key.DebounceTimer.Change(ChartConstants.IndicatorCalculationDebounceDelay, Timeout.Infinite);
    }

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

        if (key.IsWorkerRunning)
        {
            key.State = TimeAtPriceCoordinatorState.Computing;
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
            key.State = TimeAtPriceCoordinatorState.Idle;
            return;
        }

        if (!setting.IsEnabled)
        {
            key.State = TimeAtPriceCoordinatorState.Suspended;
            key.Result = null;
            return;
        }

        if (snapshot == null || setting.ParameterObject is not CoreTimeAtPriceParameter param)
        {
            key.State = TimeAtPriceCoordinatorState.Idle;
            return;
        }

        try
        {
            param.Validate();
        }
        catch (ArgumentOutOfRangeException)
        {
            key.State = TimeAtPriceCoordinatorState.Failed;
            key.Result = null;
            return;
        }

        if (snapshot.Candles == null || snapshot.Candles.Count == 0 || snapshot.VisibleCandleCount <= 0)
        {
            key.State = TimeAtPriceCoordinatorState.Idle;
            return;
        }

        var timeframe = Enum.TryParse<TimeframeType>(snapshot.Timeframe, out var tf) ? tf : TimeframeType.Daily;
        int vStart = snapshot.StartIndex;
        int vCount = snapshot.VisibleCandleCount;
        long dataRevision = key.PendingDataRevision;

        // Synchronize param with setting PriceSource
        param.PriceSource = setting.PriceSource;
        param.PriceType = setting.PriceSource;

        // Resolve source indicator series if chained
        IReadOnlyList<decimal?>? sourceSeries = null;
        string sourceIndKey = string.Empty;
        if (!string.IsNullOrEmpty(setting.SourceIndicatorId) && snapshot.IndicatorResults != null)
        {
            if (snapshot.IndicatorResults.TryGetValue(setting.SourceIndicatorId, out var sourceResult) && sourceResult.IsSuccessful)
            {
                string? targetSeriesName = null;
                if (snapshot.IndicatorSettings != null)
                {
                    var srcSetting = snapshot.IndicatorSettings.FirstOrDefault(s => s.Id == setting.SourceIndicatorId);
                    targetSeriesName = srcSetting?.OutputSeriesName;
                    sourceIndKey = $"{setting.SourceIndicatorId}_{srcSetting?.MathematicalVersion}_{srcSetting?.OutputSeriesName}";
                }
                else
                {
                    sourceIndKey = setting.SourceIndicatorId;
                }

                IReadOnlyList<decimal?> series = !string.IsNullOrEmpty(targetSeriesName)
                    ? sourceResult.GetSeries(targetSeriesName)
                    : sourceResult.MainValues;

                if (series.Count == 0 && !string.IsNullOrEmpty(targetSeriesName) && targetSeriesName != IndicatorResult.MainSeriesName)
                {
                    series = sourceResult.MainValues;
                }

                if (series.Count > 0)
                {
                    sourceSeries = series;
                }
            }
        }

        string requestKey = $"{_lifetimeId}_{setting.Id}_{snapshot.Symbol}_{snapshot.Timeframe}_{snapshot.ChartType}_{snapshot.Candles.Count}_{vStart}_{vCount}_{param.Period}_{param.RowCount}_{setting.PriceSource}_{sourceIndKey}_{dataRevision}";

        if (key.Result != null && key.Result.RequestKey == requestKey && key.Result.Status == VolumeProfileResultStatus.Success)
        {
            key.State = TimeAtPriceCoordinatorState.Ready;
            return;
        }

        var cts = new CancellationTokenSource();
        key.ActiveCts = cts;
        key.State = TimeAtPriceCoordinatorState.Computing;
        key.IsWorkerRunning = true;

        var candles = snapshot.Candles;
        const int sliceLocalStart = 0;

        var frozenParam = new CoreTimeAtPriceParameter
        {
            Period = param.Period,
            RowCount = param.RowCount,
            PriceSource = setting.PriceSource,
            PriceType = setting.PriceSource
        };

        _ = Task.Run(() =>
        {
            VolumeProfileViewportResult? result = null;
            bool canceled = false;

            try
            {
                result = TimeAtPriceViewportCalculator.Calculate(
                    requestKey,
                    candles,
                    sliceLocalStart,
                    vCount,
                    timeframe,
                    frozenParam,
                    sourceSeries,
                    setting.PriceSource,
                    cts.Token);
                canceled = cts.Token.IsCancellationRequested;
            }
            catch (OperationCanceledException)
            {
                canceled = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"TimeAtPriceViewportCoordinator calculation error: {ex.Message}");
            }

            void OnWorkerDone()
            {
                lock (_syncLock)
                {
                    if (_isDisposed || !_keyStates.TryGetValue(settingId, out var currentKey) || !ReferenceEquals(currentKey, key))
                    {
                        cts.Dispose();
                        return;
                    }

                    currentKey.IsWorkerRunning = false;
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
                            ? TimeAtPriceCoordinatorState.Ready
                            : TimeAtPriceCoordinatorState.Failed;
                        ResultReady?.Invoke(result);
                        return;
                    }

                    if (result == null && !canceled)
                    {
                        currentKey.Result = null;
                        currentKey.State = TimeAtPriceCoordinatorState.Failed;
                    }

                    if (isStale)
                    {
                        if (IsPointerCaptured)
                        {
                            currentKey.State = TimeAtPriceCoordinatorState.Waiting;
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
