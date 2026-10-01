using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

public partial class TrainingWizardViewModel
{
    private readonly IRetrainingScheduler? _retrainingScheduler;

    [RelayCommand]
    private async Task SaveRetrainingConfigurationAsync()
    {
        if (_retrainingScheduler is null || IsTraining || IsDeploying) return;
        try
        {
            var config = CreateCurrentTrainingConfig();
            if (config is null) return;
            config.Validate();
            await _retrainingScheduler.SaveConfigurationAsync(config);
            StatusMessage = LocalizationManager.Instance.Get("Monitoring_ScheduleSaved");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Training configuration could not be saved for retraining.");
            StatusMessage = LocalizationManager.Instance.Get("Monitoring_ScheduleSaveFailed");
        }
    }
}
