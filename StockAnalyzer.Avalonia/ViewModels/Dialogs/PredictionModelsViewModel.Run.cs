using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Execution;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>The "Run prediction" action: predicts again on the main chart now and summarises the outcome in the window.</summary>
public partial class PredictionModelsViewModel
{
    private readonly IPredictionRunTarget? _predictionRunTarget;
    private readonly IStockAnalyzerSettings? _settings;   // the confidence threshold the model list measures the held-out confidence against

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunPredictionCommand))]
    [NotifyCanExecuteChangedFor(nameof(TestTempModelFromFileCommand))]
    private bool _isRunningPrediction;

    [ObservableProperty]
    private string? _runPredictionStatus;

    private bool CanRunPrediction() => _predictionRunTarget is not null && !IsRunningPrediction && !_isDisposed;

    [RelayCommand(CanExecute = nameof(CanRunPrediction))]
    private async Task RunPredictionAsync()
    {
        if (!CanRunPrediction()) return;
        IsRunningPrediction = true;
        RunPredictionStatus = LocalizationManager.Instance["PredictionModels_Run_Running"];
        string status;
        try
        {
            status = RunStatusText(await _predictionRunTarget!.RunAsync(default));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Manual prediction run failed.");
            status = LocalizationManager.Instance["PredictionModels_Run_Failed"];
        }
        if (_isDisposed) return;   // the window was closed meanwhile: nothing to show
        RunPredictionStatus = status;
        IsRunningPrediction = false;
    }

    [RelayCommand(CanExecute = nameof(CanRunPrediction))]
    private async Task TestTempModelFromFileAsync()
    {
        if (!CanRunPrediction() || _dialogService is null) return;
        var initialDir = EphemeralSandbox.Root;
        if (!Directory.Exists(initialDir)) Directory.CreateDirectory(initialDir);

        var path = await _dialogService.ShowOpenFileDialogAsync(
            LocalizationManager.Instance["PredictionModels_TestTempModel_Title"],
            new[] { TrainingEnsembleArtifactLayout.ModelExtension },
            initialDir);

        if (string.IsNullOrEmpty(path)) return;

        IsRunningPrediction = true;
        RunPredictionStatus = LocalizationManager.Instance["PredictionModels_Run_Running"];
        string status;
        try
        {
            var report = await _predictionRunTarget!.RunTempModelAsync(path, default);
            var fileName = Path.GetFileName(path);
            status = RunStatusText(report, $"%Temp%: {fileName}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ephemeral model test run failed for {Path}.", path);
            status = LocalizationManager.Instance["PredictionModels_Run_Failed"];
        }
        if (_isDisposed) return;
        RunPredictionStatus = status;
        IsRunningPrediction = false;
    }

    private string RunStatusText(PredictionRunReport report, string? explicitSource = null)
    {
        string Format(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, LocalizationManager.Instance[key], args);
        var ensemble = _ensembleSettings?.Current;
        var active = Generations.FirstOrDefault(generation => generation.IsActive);
        var sourceLabel = explicitSource ?? (ensemble is not null
            ? Format("PredictionModels_Run_Source_Ensemble", ensemble.Members.Count)
            : active?.ModelFile ?? LocalizationManager.Instance["PredictionModels_Run_Source_Default"]);

        return report.Outcome switch
        {
            PredictionRunOutcome.NoData => LocalizationManager.Instance["PredictionModels_Run_NoData"],
            PredictionRunOutcome.ReplayShown => LocalizationManager.Instance["PredictionModels_Run_Replay"],
            PredictionRunOutcome.Unavailable => LocalizationManager.Instance["PredictionModels_Run_Unavailable"],
            _ when !string.IsNullOrEmpty(report.Display) => Format("PredictionModels_Run_Done", report.Symbol, report.Timeframe, report.Display, sourceLabel),
            _ when !string.IsNullOrEmpty(report.Notice) => Format("PredictionModels_Run_None", report.Symbol, report.Timeframe) + " " + report.Notice,
            // No prediction and nothing the chart could say: name the cause the owner can act on.
            _ when ensemble is null && active is null && explicitSource is null => Format("PredictionModels_Run_NoActive", report.Symbol, report.Timeframe),
            _ when report.Bars < ConfiguredWindowSize => Format("PredictionModels_Run_TooFewBars", report.Symbol, report.Timeframe, report.Bars, ConfiguredWindowSize),
            _ when ensemble is null && active is not null && !string.Equals(active.Timeframe, report.Timeframe.ToString(), StringComparison.OrdinalIgnoreCase) =>
                Format("PredictionModels_Run_TimeframeMismatch", report.Symbol, report.Timeframe, active.ModelFile, active.Timeframe),
            _ => Format("PredictionModels_Run_None", report.Symbol, report.Timeframe),
        };
    }
}
