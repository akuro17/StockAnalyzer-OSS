using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>Application-owned scheduling and transition notifications, independent of settings dialogs.</summary>
public sealed class PredictionOperationsLifetime(IRetrainingScheduler scheduler, IPredictionLogService log,
    IDispatcherService dispatcher, IToastNotificationService toast, ILogger<PredictionOperationsLifetime> logger,
    ICurrentPredictionHealthService? currentHealth = null, TimeProvider? clock = null,
    IMonitoringNotificationOutbox? outbox = null, IMonitoringWorkOwner? work = null,
    IOptions<PredictionMonitoringSettings>? monitoring = null) : IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TimeSpan _healthInterval = HealthIntervalOf(monitoring);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    // Every background task of this lifetime (and, when shared through DI, of the charts) is registered here and drained on stop.
    private readonly IMonitoringWorkOwner _work = work ?? new MonitoringWorkOwner();
    private TaskCompletionSource _outboxWake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _started, _stopped;
    private string? _lastTerminalOccurrence;
    private static TimeSpan HealthIntervalOf(IOptions<PredictionMonitoringSettings>? monitoring)
    {
        var settings = monitoring?.Value ?? new PredictionMonitoringSettings();
        PredictionMonitoringSettingsValidator.ValidateIntervals(settings);
        return settings.HealthEvaluationInterval;
    }

    public void Start()
    {
        if (Volatile.Read(ref _stopped) != 0 || Interlocked.Exchange(ref _started, 1) != 0) return;
        // With a durable outbox, delivery comes from it (at least once); otherwise the in-process event toasts as before.
        if (outbox is { IsActive: true }) log.Decayed += OnOutboxWake; else log.Decayed += OnDecayed;
        scheduler.Changed += OnScheduleChanged;
        _ = _work.Run(async ct =>
        {
            try { await scheduler.RunWithRecoveryAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogError(ex, "Prediction operations could not start."); }
        }, _shutdown.Token);
        // Without the capability the legacy cached-read behavior stays: no loop is started.
        if (currentHealth is not null) _ = _work.Run(ct => RunHealthEvaluationAsync(currentHealth, ct), _shutdown.Token);
        if (outbox is { IsActive: true }) _ = _work.Run(ct => RunOutboxDeliveryAsync(outbox, ct), _shutdown.Token);
    }

    /// <summary>Evaluate immediately, then once per interval; never overlaps itself and never reports a failed evaluation as healthy.</summary>
    private async Task RunHealthEvaluationAsync(ICurrentPredictionHealthService health, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await health.EvaluateCurrentAsync(null, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "Prediction model-health evaluation failed."); }
            try { await Task.Delay(_healthInterval, _clock, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
    }

    /// <summary>Pass at once after a commit, then every poll interval; the sink returning is the acknowledgement point.</summary>
    private async Task RunOutboxDeliveryAsync(IMonitoringNotificationOutbox deliveries, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Wake-ups raised from here on are awaited below; earlier ones are covered by the pass that follows.
            var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref _outboxWake, next);
            try { await deliveries.DispatchPendingAsync(PresentAsync, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "Prediction notification delivery failed; pending events stay queued."); }
            try { await Task.WhenAny(Task.Delay(deliveries.PollInterval, _clock, ct), next.Task).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
    }

    private void OnOutboxWake(object? sender, Core.Models.ModelDecayAlert alert) => Volatile.Read(ref _outboxWake).TrySetResult();

    /// <summary>
    /// Success means ShowNotification returned on the UI dispatcher; it does not prove the user saw the toast. The wait is bounded by the
    /// token: shutdown blocks the UI thread while it drains, so a queued UI action must not be the thing the drain waits for.
    /// </summary>
    private Task PresentAsync(Core.Models.ModelDecayAlert alert, CancellationToken ct) => dispatcher.PostAsync(() =>
    {
        ct.ThrowIfCancellationRequested();
        toast.ShowNotification(DecayMessage(alert));
        return Task.CompletedTask;
    }).WaitAsync(ct);

    private static string DecayMessage(Core.Models.ModelDecayAlert alert) => string.Format(CultureInfo.CurrentCulture,
        LocalizationManager.Instance.Get("Monitoring_DecayAlert"), alert.Snapshot.ModelId,
        alert.Snapshot.Accuracy?.ToString("P2", CultureInfo.CurrentCulture), alert.Snapshot.MaturedCount);

    private void OnDecayed(object? sender, Core.Models.ModelDecayAlert alert) => dispatcher.Post(() =>
    {
        if (_shutdown.IsCancellationRequested) return;
        toast.ShowNotification(DecayMessage(alert));
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
        await _work.StopAsync().ConfigureAwait(false);
        _shutdown.Dispose(); // every task that observed its token has finished
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        log.Decayed -= OnDecayed;
        log.Decayed -= OnOutboxWake;
        scheduler.Changed -= OnScheduleChanged;
        _shutdown.Cancel();
        // Cancellation resources stay alive until StopAsync has drained the owned work (training cleanup included).
        _ = _work.StopAsync();
    }
}
