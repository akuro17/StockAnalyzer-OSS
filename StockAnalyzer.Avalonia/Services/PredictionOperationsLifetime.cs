using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>Application-owned scheduling and transition notifications, independent of settings dialogs.</summary>
public sealed class PredictionOperationsLifetime(IRetrainingScheduler scheduler, IPredictionLogService log,
    IDispatcherService dispatcher, IToastNotificationService toast, ILogger<PredictionOperationsLifetime> logger,
    ICurrentPredictionHealthService? currentHealth = null, TimeProvider? clock = null) : IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private Task _runner = Task.CompletedTask;
    private Task _healthLoop = Task.CompletedTask;
    private int _started, _stopped;
    private string? _lastTerminalOccurrence;
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        log.Decayed += OnDecayed;
        scheduler.Changed += OnScheduleChanged;
        _runner = Task.Run(async () =>
        {
            try { await scheduler.RunWithRecoveryAsync(_shutdown.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogError(ex, "Prediction operations could not start."); }
        });
        // Without the capability the legacy cached-read behavior stays: no loop is started.
        if (currentHealth is not null) _healthLoop = Task.Run(() => RunHealthEvaluationAsync(currentHealth, _shutdown.Token));
    }

    /// <summary>Evaluate immediately, then once per interval; never overlaps itself and never reports a failed evaluation as healthy.</summary>
    private async Task RunHealthEvaluationAsync(ICurrentPredictionHealthService health, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await health.EvaluateCurrentAsync(null, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "Prediction model-health evaluation failed."); }
            try { await Task.Delay(PredictionMonitoringPolicy.HealthEvaluationInterval, _clock, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
    }

    private void OnDecayed(object? sender, Core.Models.ModelDecayAlert alert) => dispatcher.Post(() =>
    {
        if (_shutdown.IsCancellationRequested) return;
        toast.ShowNotification(string.Format(CultureInfo.CurrentCulture,
            LocalizationManager.Instance.Get("Monitoring_DecayAlert"), alert.Snapshot.ModelId,
            alert.Snapshot.Accuracy?.ToString("P2", CultureInfo.CurrentCulture), alert.Snapshot.MaturedCount));
    });

    private void OnScheduleChanged(object? sender, EventArgs args)
    {
        if (scheduler.Current is not { } state || state.Status is not (RetrainingStatus.Failed or RetrainingStatus.Succeeded)) return;
        var key = state.JobId + ":" + state.LastOccurrenceUtc.ToString("O") + ":" + state.Status;
        if (Interlocked.Exchange(ref _lastTerminalOccurrence, key) == key) return;
        dispatcher.Post(() =>
        {
            if (_shutdown.IsCancellationRequested) return;
            toast.ShowNotification(LocalizationManager.Instance.Get(state.Status == RetrainingStatus.Succeeded
                ? "Monitoring_CandidateReady" : "Monitoring_RetrainingFailed"));
        });
    }

    public async Task StopAsync()
    {
        Dispose();
        await _runner.ConfigureAwait(false);
        await _healthLoop.ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        log.Decayed -= OnDecayed;
        scheduler.Changed -= OnScheduleChanged;
        _shutdown.Cancel();
        // Cancellation resources remain alive until the runner/process cleanup has completed.
    }
}
