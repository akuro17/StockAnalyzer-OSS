using System;
using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Avalonia.Views;
using StockAnalyzer.Core.Models.UI;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Services;

public class DetachedWindowFactory : IDetachedWindowFactory
{
    private readonly ILogger<DetachedWindowFactory> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IContainerRegistry _containerRegistry;

    public DetachedWindowFactory(
        IServiceProvider serviceProvider, 
        IContainerRegistry containerRegistry,
        ILogger<DetachedWindowFactory>? logger = null)
    {
        _serviceProvider = serviceProvider;
        _containerRegistry = containerRegistry;
        _logger = logger ?? NullLogger<DetachedWindowFactory>.Instance;
    }

    public object CreateWindow(object owner)
    {
        _logger.LogDebug("Creating new empty DetachedWindow instance.");
        
        var viewModel = ActivatorUtilities.CreateInstance<ViewModels.DetachedWindowViewModel>(_serviceProvider);
        _containerRegistry.Register(viewModel.ContainerId, viewModel);

        var window = new DetachedWindow
        {
            DataContext = viewModel
        };
        return window;
    }

    public object CreateWindow(object owner, WorkspaceViewItem item)
    {
        _logger.LogDebug("Creating new DetachedWindow instance for {Title}.", item.Title);
        
        var viewModel = ActivatorUtilities.CreateInstance<ViewModels.DetachedWindowViewModel>(_serviceProvider);
        _containerRegistry.Register(viewModel.ContainerId, viewModel);
        
        viewModel.AddItem(item);

        var window = new DetachedWindow
        {
            DataContext = viewModel
        };
        return window;
    }

    public void SetPosition(object window, double x, double y)
    {
        if (window is Window win)
        {
            win.Position = new PixelPoint((int)x, (int)y);
        }
    }

    public void ApplyGeometry(object window, double x, double y, double width, double height)
    {
        if (window is Window win)
        {
            win.Position = new PixelPoint((int)x, (int)y);
            win.Width = width;
            win.Height = height;
        }
    }

    public void ShowWindow(object window, object? owner = null)
    {
        if (window is Window win)
        {
            if (owner is Window ownerWin) win.Show(ownerWin);
            else win.Show();
        }
    }

    public void CloseWindow(object window)
    {
        if (window is Window win)
        {
            win.Close();
        }
    }
}
