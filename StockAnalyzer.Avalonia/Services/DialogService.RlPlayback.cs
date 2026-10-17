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
    /// <summary>The open RL Playback window; read and written on the UI thread only.</summary>
    private RlPlaybackWindow? _rlPlaybackWindow;

    public async Task ShowRlPlaybackWindowAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null || _serviceProvider is null) return;
        await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (_rlPlaybackWindow is { IsVisible: true } open)
            {
                if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
                open.Activate();
                return;
            }
            _rlPlaybackWindow = null;
            try
            {
                var window = new RlPlaybackWindow
                {
                    DataContext = _serviceProvider.GetRequiredService<RlPlaybackViewModel>(),
                    Icon = desktop.MainWindow.Icon,
                };
                _rlPlaybackWindow = window;
                // A window replaced by a newer one must not clear the newer one's slot.
                window.Closed += (_, _) => { if (ReferenceEquals(_rlPlaybackWindow, window)) _rlPlaybackWindow = null; };
                window.Show();
            }
            catch (Exception ex)
            {
                _rlPlaybackWindow = null;
                await ShowAlertAsync(LocalizationManager.Instance["Dialog_Error"] ?? "Error", ex.Message);
            }
        });
    }
}
