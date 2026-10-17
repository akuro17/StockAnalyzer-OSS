using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Views;

namespace StockAnalyzer.Avalonia.Services;

public partial class DialogService
{
    /// <summary>The open Prediction Models window; read and written on the UI thread only.</summary>
    private PredictionModelsWindow? _predictionModelsWindow;

    public async Task ShowPredictionModelsWindowAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null || _serviceProvider is null) return;
        await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (_predictionModelsWindow is { IsVisible: true } open)
            {
                if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
                open.Activate();
                return;
            }
            _predictionModelsWindow = null;
            try
            {
                var window = new PredictionModelsWindow
                {
                    DataContext = _serviceProvider.GetRequiredService<PredictionModelsViewModel>(),
                    Icon = desktop.MainWindow.Icon,
                };
                _predictionModelsWindow = window;
                // A window replaced by a newer one must not clear the newer one's slot.
                window.Closed += (_, _) => { if (ReferenceEquals(_predictionModelsWindow, window)) _predictionModelsWindow = null; };
                window.Show();
            }
            catch (Exception ex)
            {
                _predictionModelsWindow = null;
                await ShowAlertAsync(LocalizationManager.Instance["Dialog_Error"] ?? "Error", ex.Message);
            }
        });
    }
}
