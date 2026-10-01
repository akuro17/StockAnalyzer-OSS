using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace StockAnalyzer.Avalonia.Services;

public sealed class WindowManagementService : IWindowManagementService
{
    public ITearOffService TearOff { get; }
    public IPanelTabFactory TabFactory { get; }
    public IDetachedWindowFactory WindowFactory { get; }
    public IWindowBoundaryService BoundaryService { get; }

    public WindowManagementService(
        ITearOffService tearOff,
        IPanelTabFactory tabFactory,
        IDetachedWindowFactory windowFactory,
        IWindowBoundaryService boundaryService)
    {
        TearOff = tearOff ?? throw new ArgumentNullException(nameof(tearOff));
        TabFactory = tabFactory ?? throw new ArgumentNullException(nameof(tabFactory));
        WindowFactory = windowFactory ?? throw new ArgumentNullException(nameof(windowFactory));
        BoundaryService = boundaryService ?? throw new ArgumentNullException(nameof(boundaryService));
    }

    public void BringMainWindowToFront()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow != null)
        {
            if (desktop.MainWindow.WindowState == WindowState.Minimized)
            {
                desktop.MainWindow.WindowState = WindowState.Normal;
            }
            desktop.MainWindow.Activate();
        }
    }

    public void BringWindowToFront(object dataContext)
    {
        if (dataContext == null) return;

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = desktop.Windows.FirstOrDefault(w => w.DataContext == dataContext);
            if (window != null)
            {
                if (window.WindowState == WindowState.Minimized)
                {
                    window.WindowState = WindowState.Normal;
                }
                window.Activate();
            }
        }
    }
}
