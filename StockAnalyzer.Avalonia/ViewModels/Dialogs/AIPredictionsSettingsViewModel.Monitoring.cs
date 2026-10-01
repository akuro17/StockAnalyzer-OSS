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
    private long _monitoringRevision;
    internal Task MonitoringRefreshTask { get; private set; } = Task.CompletedTask;
    [ObservableProperty] private string? _monitoringStatus;
    [ObservableProperty] private string? _scheduleStatus;
    [ObservableProperty] private string? _scheduleConfiguration;
    [ObservableProperty] private bool _isScheduleEnabled;
    [ObservableProperty] private bool _hasScheduleConfiguration;

    private void InitializeMonitoring()
    {
        if (_predictionLog is not null) _predictionLog.Changed += MonitoringChanged;
        if (_retrainingScheduler is not null) _retrainingScheduler.Changed += MonitoringChanged;
        MonitoringRefreshTask = RefreshMonitoringAsync();
    }

    private void MonitoringChanged(object? sender, EventArgs args)
    {
        void Refresh() { if (!_isDisposed) MonitoringRefreshTask = RefreshMonitoringAsync(); }
        if (_analysisDispatcher is null) Refresh(); else _analysisDispatcher.Post(Refresh);
    }

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
                    : string.Join(Environment.NewLine, snapshots.Select(s => string.Format(CultureInfo.CurrentCulture,
                        LocalizationManager.Instance.Get("Monitoring_Accuracy"),
                        LocalizationManager.Instance.Get("Monitoring_Health_" + s.State),
                        s.Accuracy is { } accuracy ? accuracy.ToString("P2", CultureInfo.CurrentCulture) : "—", s.MaturedCount)));
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
        void Publish()
        {
            if (_isDisposed || revision != _monitoringRevision || SelectedGeneration?.ModelId != modelId) return;
            MonitoringStatus = status; ScheduleStatus = scheduleStatus; ScheduleConfiguration = configuration;
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
