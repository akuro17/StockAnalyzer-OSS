using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>The health line of the saved ensemble, shown in the Ensemble card.</summary>
public partial class PredictionModelsViewModel
{
    private readonly IPredictionLogService? _predictionLog;
    private readonly ICurrentPredictionHealthService? _currentHealth;
    private long _monitoringRevision;
    internal Task MonitoringRefreshTask { get; private set; } = Task.CompletedTask;
    /// <summary>Health of the saved ensemble (its own monitoring group); null while no ensemble is saved or no health service exists.</summary>
    [ObservableProperty] private string? _ensembleMonitoringStatus;

    private volatile bool _monitoringStarted;

    private void InitializeMonitoring()
    {
        _monitoringStarted = true;
        if (_predictionLog is not null) _predictionLog.Changed += MonitoringChanged;
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
        // The saved ensemble is its own monitoring group.
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
            if (_isDisposed || revision != _monitoringRevision) return;
            EnsembleMonitoringStatus = ensembleStatus;
        }
        if (_analysisDispatcher is null) Publish(); else _analysisDispatcher.Post(Publish);
    }

    private void DisposeMonitoring()
    {
        _monitoringRevision++;
        if (_predictionLog is not null) _predictionLog.Changed -= MonitoringChanged;
    }
}
