using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Models.UI;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>
/// Factory interface for creating workspace panel tabs.
/// Adheres to OCP by delegating resolution to ITabRegistry.
/// </summary>
public interface IPanelTabFactory
{
    /// <summary>
    /// Creates a WorkspaceViewItem based on the provided ID.
    /// </summary>
    WorkspaceViewItem? CreateTab(string id);
}

/// <summary>
/// Implementation of IPanelTabFactory that resolves ViewModels via ITabRegistry.
/// Lifecycle management (Singleton/Transient) is handled by the DI container.
/// </summary>
public sealed class PanelTabFactory : IPanelTabFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ITabRegistry _tabRegistry;
    private readonly ILogger<PanelTabFactory> _logger;

    public PanelTabFactory(
        IServiceProvider serviceProvider, 
        ITabRegistry tabRegistry,
        ILogger<PanelTabFactory>? logger = null)
    {
        _serviceProvider = serviceProvider;
        _tabRegistry = tabRegistry;
        _logger = logger ?? NullLogger<PanelTabFactory>.Instance;
    }

    public WorkspaceViewItem? CreateTab(string id)
    {
        try
        {
            var metadata = _tabRegistry.GetMetadata(id);
            
            // Resolving from DI is the SSoT for lifecycle management.
            // If the ViewModel is registered as Singleton, we get the same instance.
            // If Transient, we get a new instance.
            var viewModel = (ViewModelBase)_tabRegistry.CreateViewModel(id, _serviceProvider);

            var item = new WorkspaceViewItem 
            { 
                Id = id, 
                Title = metadata.DisplayName, 
                ViewModel = viewModel 
            };

            // FR-70-12: Synchronize Tab title for Chart tabs
            // Initial sync. Subsequent updates are handled by ChartSymbolChangedMessage in MainWindowViewModel.
            if (viewModel is ChartViewModel cvm)
            {
                item.Title = cvm.TabHeaderText;
            }

            return item;
        }
        catch (Exception ex)
        {
            // Fallback for cases where registration or creation fails
            _logger.LogError(ex, "Failed to create tab '{TabId}'", id);
            return null;
        }
    }
}
