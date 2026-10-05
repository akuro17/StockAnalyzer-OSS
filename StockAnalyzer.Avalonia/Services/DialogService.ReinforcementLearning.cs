using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Services.Backtest.Configuration;
using StockAnalyzer.Core.Services.ReinforcementLearning;

namespace StockAnalyzer.Avalonia.Services;

public partial class DialogService
{
    public async Task ShowRlTrainingDialogAsync(ChartViewModel chart)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null || _serviceProvider is null) return;
        await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var localization = _serviceProvider.GetRequiredService<ILocalizationService>();
            try
            {
                using var vm = new RlTrainingDialogViewModel(
                    _serviceProvider.GetRequiredService<IRlTrainingService>(),
                    _serviceProvider.GetRequiredService<IRlModelGenerationRegistry>(),
                    _serviceProvider.GetRequiredService<IQValueService>(),
                    _serviceProvider.GetRequiredService<IBacktestDefaultSettingsManager>(), chart, localization);
                await vm.InitializeAsync();
                var window = new RlTrainingWindow { DataContext = vm, Icon = desktop.MainWindow.Icon };
                window.Closing += (_, _) => vm.Dispose();
                await window.ShowDialog(desktop.MainWindow);
            }
            catch (Exception ex)
            {
                await ShowAlertAsync(localization.GetString("Rl.Title"), ex.Message);
            }
        });
    }
}
