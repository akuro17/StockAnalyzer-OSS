using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.UI;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Models;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Notes;

namespace StockAnalyzer.Avalonia.ViewModels;

/// <summary>
/// Container ViewModel for detached windows, allowing them to host multiple tabs.
/// </summary>
public partial class DetachedWindowViewModel : ViewModelBase, IDetachedWindowContainer, IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DetachedWindowViewModel> _logger;
    private readonly IPanelTabFactory _panelTabFactory;
    private readonly ITearOffService _tearOffService;
    private readonly IContainerRegistry _containerRegistry;
    private readonly IMessenger _messenger;
    private readonly int _maxPanelTabs;
    private readonly int _maxTabReorderDistance;
    private bool _isDisposed;

    [ObservableProperty]
    private string _containerId = Guid.NewGuid().ToString();

    [ObservableProperty]
    private ObservableCollection<WorkspaceViewItem> _items = new();

    [ObservableProperty]
    private WorkspaceViewItem? _selectedItem;

    [ObservableProperty]
    private string _title = "Detached Window";

    public DetachedWindowViewModel(
        IServiceProvider serviceProvider, 
        IPanelTabFactory panelTabFactory, 
        ITearOffService tearOffService,
        IContainerRegistry containerRegistry,
        IMessenger? messenger = null,
        IDispatcherService? dispatcherService = null,
        ILogger<DetachedWindowViewModel>? logger = null,
        LayoutStateStore? stateStore = null)
    {
        _serviceProvider = serviceProvider;
        _panelTabFactory = panelTabFactory;
        _tearOffService = tearOffService;
        _containerRegistry = containerRegistry;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _logger = logger ?? NullLogger<DetachedWindowViewModel>.Instance;

        var defaults = new LayoutSettings();
        _maxPanelTabs = stateStore?.MaxPanelTabs ?? defaults.MaxPanelTabs;
        _maxTabReorderDistance = stateStore?.MaxTabReorderDistance ?? defaults.MaxTabReorderDistance;
    }

    /// <summary>
    /// Adds a view item to the detached window.
    /// </summary>
    public void AddItem(WorkspaceViewItem item)
    {
        if (_isDisposed) return;

        item.ContainerId = ContainerId;
        if (!Items.Contains(item))
        {
            Items.Add(item);
        }
        SelectedItem = item;
        UpdateTitle();
    }

    /// <summary>
    /// Removes a view item from the detached window.
    /// </summary>
    public void RemoveItem(WorkspaceViewItem item)
    {
        if (_isDisposed) return;

        int index = Items.IndexOf(item);
        if (index < 0) return;

        bool wasSelected = SelectedItem == item;
        Items.RemoveAt(index);

        if (wasSelected)
        {
            if (Items.Count > 0)
            {
                // Select adjacent item (prefer same index, fallback to previous)
                int nextIndex = Math.Min(index, Items.Count - 1);
                SelectedItem = Items[nextIndex];
            }
            else
            {
                SelectedItem = null;
            }
        }
        
        UpdateTitle();
    }

    [RelayCommand]
    private void AddTab(string tabId)
    {
        if (_isDisposed || string.IsNullOrEmpty(tabId)) return;

        if (Items.Count >= _maxPanelTabs)
        {
            _logger.LogWarning("Maximum number of tabs reached in detached window.");
            return;
        }

        var item = _panelTabFactory.CreateTab(tabId);
        if (item == null)
        {
            _logger.LogWarning("Failed to create tab of type {Type}", tabId);
            return;
        }

        item.IsDetached = true;
        item.IsIndependent = true; // Dispose when closed/returned to main
        item.CanClose = true;

        AddItem(item);
        
        // Notify main window to include this tab in workspace persistence (FR-70-12)
        _messenger.Send(new Common.RegisterDetachedTabMessage(item));
        
        _logger.LogInformation("Added new Tab [{Type}] to Detached Window.", tabId);
    }

    [RelayCommand]
    private void CloseTab(WorkspaceViewItem? item)
    {
        if (_isDisposed || item == null) return;
        RedockTab(item);
    }

    [RelayCommand]
    private void RedockTab(WorkspaceViewItem? item)
    {
        if (_isDisposed || item == null) return;
        _tearOffService.Redock(item);
    }

    /// <summary>
    /// Moves a tab inside this container (drag reorder), selects the moved tab and
    /// notifies the workspace so the new order is persisted.
    /// </summary>
    [RelayCommand]
    private void MoveTab(TabMoveRequest request)
    {
        if (_isDisposed) return;

        var outcome = TabMoveResolver.Resolve(
            Items.Count, request.SourceIndex, request.TargetIndex, _maxTabReorderDistance, out int targetIndex);

        if (outcome == TabMoveOutcome.SourceOutOfRange)
        {
            _logger.LogWarning("MoveTab sourceIndex '{SourceIndex}' is out of bounds. Tabs count = {Count}.", request.SourceIndex, Items.Count);
            return;
        }

        if (outcome == TabMoveOutcome.NoChange) return;

        var moved = Items[request.SourceIndex];
        Items.Move(request.SourceIndex, targetIndex);

        // Items.Move can reset the bound TabControl's selection; restore it to the moved tab.
        SelectedItem = moved;

        _messenger.Send(new DetachedTabOrderChangedMessage(ContainerId, Items.ToArray()));

        _logger.LogInformation("Reordered tab [{Id}] {From} -> {To} in Detached Window {ContainerId}.", moved.Id, request.SourceIndex, targetIndex, ContainerId);
    }

    private void UpdateTitle()
    {
        if (Items.Count == 0)
        {
            Title = LocalizationManager.Instance["Window_Detached_Empty"];
        }
        else if (Items.Count == 1)
        {
            Title = Items[0].Title;
        }
        else
        {
            Title = string.Format(LocalizationManager.Instance["Window_Detached_MultiTabs"], Items.Count);
        }
    }

    /// <summary>
    /// Finds an existing NoteTimeline tab in this container and selects it.
    /// Does not create a new tab if absent.
    /// </summary>
    public WorkspaceViewItem? FindExistingNoteTimelineTab()
    {
        if (_isDisposed) return null;

        var existing = Items.FirstOrDefault(item => item.ViewModel is NoteTimelineViewModel);
        if (existing != null)
        {
            SelectedItem = existing;
            return existing;
        }

        return null;
    }

    /// <summary>
    /// Creates and adds a new NoteTimeline tab to this container.
    /// </summary>
    public WorkspaceViewItem? OpenNewNoteTimelineTab()
    {
        if (_isDisposed) return null;

        if (Items.Count >= _maxPanelTabs)
        {
            _logger.LogWarning("Maximum number of tabs reached in detached window. Cannot add NoteTimeline tab.");
            return null;
        }

        var newTab = _panelTabFactory.CreateTab("NoteTimeline");
        if (newTab == null)
        {
            _logger.LogWarning("Failed to create NoteTimeline tab in detached window.");
            return null;
        }

        newTab.IsDetached = true;
        newTab.IsIndependent = true; // Dispose when closed/returned to main
        newTab.CanClose = true;

        AddItem(newTab);
        
        // Notify main window to include this tab in workspace persistence (FR-70-12)
        _messenger.Send(new Common.RegisterDetachedTabMessage(newTab));
        SelectedItem = newTab;

        _logger.LogInformation("Added new Tab [NoteTimeline] to Detached Window {ContainerId}.", ContainerId);
        return newTab;
    }

    /// <summary>
    /// Finds an existing NoteTimeline tab in this container or creates and adds a new one.
    /// </summary>
    public WorkspaceViewItem? FindOrOpenNoteTimelineTab()
    {
        return FindExistingNoteTimelineTab() ?? OpenNewNoteTimelineTab();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _logger.LogInformation("Disposing DetachedWindowViewModel {ContainerId}. Cleaning up {Count} items.", ContainerId, Items.Count);
        
        _messenger.UnregisterAll(this);

        // 1. Unregister from global registry to prevent leaks
        _containerRegistry.Unregister(ContainerId);

        // 2. Dispose ONLY view models that are still detached (not yet restored to main panel).
        // When the user closes this window manually, WindowPersistenceBehavior.OnWindowClosing
        // sends RestoreRequestMessage for each item BEFORE OnClosed fires.
        // MainWindowViewModel.Receive sets item.IsDetached = false upon restoration.
        // Disposing already-restored ViewModels causes ObjectDisposedException in the main panel.
        foreach (var item in Items.ToList())
        {
            if (item.IsDetached && item.IsIndependent && item.ViewModel is IDisposable disposable)
            {
                disposable.Dispose();
                _logger.LogDebug("Disposed child ViewModel [{Type}] for Item {Id}.", item.ViewModel.GetType().Name, item.Id);
            }
        }
        
        Items.Clear();
    }
}
