using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

public partial class AIPredictionsSettingsViewModel
{
    private readonly IPredictionLogService? _predictionLog;
    private readonly IRetrainingScheduler? _retrainingScheduler;
    private readonly ICurrentPredictionHealthService? _currentHealth;
    private readonly Core.Models.IAttestedEvidenceDiagnostics? _evidenceDiagnostics;
    private long _monitoringRevision;
    internal Task MonitoringRefreshTask { get; private set; } = Task.CompletedTask;
    [ObservableProperty] private string? _monitoringStatus;
    /// <summary>Health of the saved ensemble (its own monitoring group); null while no ensemble is saved or no health service exists.</summary>
    [ObservableProperty] private string? _ensembleMonitoringStatus;
    [ObservableProperty] private string? _scheduleStatus;
    [ObservableProperty] private string? _scheduleConfiguration;
    [ObservableProperty] private bool _isScheduleEnabled;
    [ObservableProperty] private bool _hasScheduleConfiguration;

    private volatile bool _monitoringStarted;

    private void InitializeMonitoring()
    {
        _monitoringStarted = true;
        if (_predictionLog is not null) _predictionLog.Changed += MonitoringChanged;
        if (_retrainingScheduler is not null) _retrainingScheduler.Changed += MonitoringChanged;
        MonitoringRefreshTask = RefreshMonitoringAsync();
    }

    private void MonitoringChanged(object? sender, EventArgs args)
    {
        void Refresh() { if (!_isDisposed) MonitoringRefreshTask = RefreshMonitoringAsync(); }
        if (_analysisDispatcher is null) Refresh(); else _analysisDispatcher.Post(Refresh);
    }

    /// <summary>
    /// The saved ensemble changed (loaded, saved or cleared): its health line has to be read again. Before <see cref="InitializeMonitoring"/> has run
    /// (the constructor loads the ensemble first) nothing is requested, because the first refresh reads the ensemble that is current then.
    /// </summary>
    private void RequestMonitoringRefresh()
    {
        if (!_isDisposed && _monitoringStarted) MonitoringRefreshTask = RefreshMonitoringAsync();
    }

    /// <summary>One health line: the localized state name, the accuracy as a culture percentage (a dash when null) and the matured count.</summary>
    private static string FormatSnapshot(Core.Models.ModelMonitoringSnapshot s) => string.Format(CultureInfo.CurrentCulture,
        LocalizationManager.Instance.Get("Monitoring_Accuracy"),
        LocalizationManager.Instance.Get("Monitoring_Health_" + s.State),
        s.Accuracy is { } accuracy ? accuracy.ToString("P2", CultureInfo.CurrentCulture) : "—", s.MaturedCount);

    private static string EnsembleLine(string text) => string.Format(CultureInfo.CurrentCulture,
        LocalizationManager.Instance.Get("Monitoring_EnsembleState"), text);

    private async Task RefreshMonitoringAsync()
    {
        if (_isDisposed) return;
        long revision = ++_monitoringRevision;
        string? modelId = SelectedGeneration?.ModelId;
        bool isRegression = SelectedGeneration?.TargetType == Core.Models.PredictionModelMetadata.TargetTypeRegression;
        string? status = null;
        string? scheduleStatus = null, configuration = null;
        bool enabled = false, hasConfiguration = false;
        try
        {
            if (isRegression) status = LocalizationManager.Instance.Get("Monitoring_RegressionUnavailable");
            else if ((_currentHealth is not null || _predictionLog is not null) && modelId is not null)
            {
                // With the current-health capability a failure surfaces as LoadFailed, never as a cached (possibly stale) Healthy card.
                var snapshots = _currentHealth is not null
                    ? await _currentHealth.EvaluateCurrentAsync(modelId).ConfigureAwait(false)
                    : await _predictionLog!.GetSnapshotsAsync(modelId).ConfigureAwait(false);
                status = snapshots.IsEmpty ? LocalizationManager.Instance.Get("Monitoring_InsufficientData")
                    : string.Join(Environment.NewLine, snapshots.Select(FormatSnapshot));
                // A data-integrity fault of the producer evidence is its own line, never folded into the performance state.
                if (_evidenceDiagnostics is not null && await _evidenceDiagnostics.GetConflictedStreamsAsync().ConfigureAwait(false) is { IsEmpty: false } faults)
                    status += Environment.NewLine + string.Format(CultureInfo.CurrentCulture, LocalizationManager.Instance.Get("Monitoring_EvidenceConflict"), faults.Length);
            }
            if (_retrainingScheduler is not null)
            {
                await _retrainingScheduler.InitializeAsync().ConfigureAwait(false);
                if (_retrainingScheduler.Current is { } schedule)
                {
                    enabled = schedule.Enabled; hasConfiguration = true;
                    scheduleStatus = string.Format(CultureInfo.CurrentCulture,
                        LocalizationManager.Instance.Get("Monitoring_ScheduleStatus"),
                        LocalizationManager.Instance.Get("Monitoring_Retraining_" + schedule.Status),
                        schedule.LastOccurrenceUtc.AddDays(Core.Models.Training.PredictionMonitoringPolicy.SchedulePeriodDays).ToLocalTime());
                    var cfg = schedule.Config;
                    configuration = string.Format(CultureInfo.CurrentCulture,
                        LocalizationManager.Instance.Get("Monitoring_ScheduleConfiguration"), cfg.Symbols.Length,
                        cfg.Timeframe, cfg.Architecture, cfg.StartDate?.ToString() ?? "—", cfg.EndDate?.ToString() ?? "—");
                    if (schedule.CandidateModelId is { } candidate)
                        scheduleStatus += Environment.NewLine + string.Format(CultureInfo.CurrentCulture,
                            LocalizationManager.Instance.Get("Monitoring_Candidate"), candidate);
                }
                else scheduleStatus = LocalizationManager.Instance.Get("Monitoring_NoConfiguration");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Prediction monitoring settings or evidence could not be loaded.");
            status = LocalizationManager.Instance.Get("Monitoring_LoadFailed");
        }
        // The saved ensemble is its own monitoring group; its failure never changes the generation status above.
        string? ensembleStatus = null;
        if (_ensembleSettings?.Current is { } ensemble && (_currentHealth is not null || _predictionLog is not null))
        {
            try
            {
                var ensembleId = ensemble.MonitoringId();
                var ensembleSnapshots = _currentHealth is not null
                    ? await _currentHealth.EvaluateCurrentAsync(ensembleId).ConfigureAwait(false)
                    : await _predictionLog!.GetSnapshotsAsync(ensembleId).ConfigureAwait(false);
                ensembleStatus = ensembleSnapshots.IsEmpty
                    ? EnsembleLine(LocalizationManager.Instance.Get("Monitoring_InsufficientData"))
                    : string.Join(Environment.NewLine, ensembleSnapshots.Select(s => EnsembleLine(FormatSnapshot(s))));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ensemble prediction monitoring could not be loaded.");
                ensembleStatus = EnsembleLine(LocalizationManager.Instance.Get("Monitoring_LoadFailed"));
            }
        }
        void Publish()
        {
            if (_isDisposed || revision != _monitoringRevision || SelectedGeneration?.ModelId != modelId) return;
            MonitoringStatus = status; EnsembleMonitoringStatus = ensembleStatus;
            ScheduleStatus = scheduleStatus; ScheduleConfiguration = configuration;
            IsScheduleEnabled = enabled; HasScheduleConfiguration = hasConfiguration;
        }
        if (_analysisDispatcher is null) Publish(); else _analysisDispatcher.Post(Publish);
    }

    [RelayCommand]
    private Task EnableRetrainingAsync() => SetRetrainingEnabledAsync(true);
    [RelayCommand]
    private Task DisableRetrainingAsync() => SetRetrainingEnabledAsync(false);
    private async Task SetRetrainingEnabledAsync(bool enabled)
    {
        if (_isDisposed || _retrainingScheduler is null) return;
        try { await _retrainingScheduler.SetEnabledAsync(enabled); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Retraining schedule enable state could not be saved.");
            if (!_isDisposed) ScheduleStatus = LocalizationManager.Instance.Get("Monitoring_ScheduleSaveFailed");
        }
    }

    [RelayCommand]
    private void CancelRetraining() { if (!_isDisposed) _retrainingScheduler?.CancelCurrent(); }

    private void DisposeMonitoring()
    {
        _monitoringRevision++;
        if (_predictionLog is not null) _predictionLog.Changed -= MonitoringChanged;
        if (_retrainingScheduler is not null) _retrainingScheduler.Changed -= MonitoringChanged;
    }
}
