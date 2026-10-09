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
    public async Task ShowPortfolioRlDialogAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null || _serviceProvider is null) return;
        await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var localization = _serviceProvider.GetRequiredService<ILocalizationService>();
            try
            {
                using var vm = new PortfolioRlDialogViewModel(
                    _serviceProvider.GetRequiredService<IRlPortfolioService>(),
                    _serviceProvider.GetRequiredService<ITickerSourceResolver>(),
                    _serviceProvider.GetRequiredService<ITickerStateStore>(),
                    _serviceProvider.GetRequiredService<IBacktestDefaultSettingsManager>(), localization,
                    transfer: _serviceProvider.GetRequiredService<IRlPortfolioTransferService>());
                var window = new PortfolioRlWindow { DataContext = vm, Icon = desktop.MainWindow.Icon };
                window.Opened += async (_, _) => await vm.InitializeAsync();
                window.Closing += (_, _) => vm.Dispose();
                await window.ShowDialog(desktop.MainWindow);
            }
            catch (Exception ex)
            {
                await ShowAlertAsync(localization.GetString("Prl.Title"), ex.Message);
            }
        });
    }
}
