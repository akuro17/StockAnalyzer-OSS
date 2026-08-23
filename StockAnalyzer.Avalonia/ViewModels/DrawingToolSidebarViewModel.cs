namespace StockAnalyzer.Avalonia.ViewModels;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Models;
using StockAnalyzer.Avalonia.Services;

/// <summary>
/// ViewModel for a single category in the sidebar.
/// </summary>
public partial class DrawingToolCategoryViewModel : ObservableObject
{
    public string NameKey { get; }
    public string DisplayName => LocalizationManager.Instance[NameKey];
    public string Icon { get; }
    public ObservableCollection<DrawingToolItemViewModel> Tools { get; }

    [ObservableProperty]
    private bool _isExpanded;

    public DrawingToolCategoryViewModel(string nameKey, string icon, IEnumerable<DrawingToolItemViewModel> tools)
    {
        NameKey = nameKey;
        Icon = icon;
        Tools = new ObservableCollection<DrawingToolItemViewModel>(tools);
    }
}

/// <summary>
/// ViewModel for the drawing tool sidebar. Manages categories, selection,
/// favorites, and layer/object management panel integration.
/// </summary>
public partial class DrawingToolSidebarViewModel : ViewModelBase, IDisposable
{
    private bool _isDisposed;
    private ChartViewModel _chartViewModel;

    public DrawingObjectsViewModel DrawingObjectsViewModel { get; }

    public void SetChartViewModel(ChartViewModel chartViewModel)
    {
        _chartViewModel = chartViewModel;
        DrawingObjectsViewModel?.SetChartViewModel(chartViewModel);
    }

    public ObservableCollection<DrawingToolCategoryViewModel> Categories { get; } = new();
    public ObservableCollection<DrawingToolItemViewModel> Favorites { get; } = new();

    [ObservableProperty]
    private DrawingToolCategoryViewModel? _expandedCategory;

    [ObservableProperty]
    private bool _isFlyoutOpen;

    [ObservableProperty]
    private bool _isObjectsPanelOpen;

    [ObservableProperty]
    private bool _showFavoritesPalette;

    // Flat lookup for updating selection highlight
    private readonly Dictionary<DrawingTool, DrawingToolItemViewModel> _toolLookup = new();

    public DrawingToolSidebarViewModel(ChartViewModel chartViewModel, DrawingObjectsViewModel drawingObjectsViewModel)
    {
        _chartViewModel = chartViewModel ?? throw new ArgumentNullException(nameof(chartViewModel));
        DrawingObjectsViewModel = drawingObjectsViewModel ?? throw new ArgumentNullException(nameof(drawingObjectsViewModel));
        DrawingObjectsViewModel.SetChartViewModel(_chartViewModel);

        var categories = DrawingToolCategoryService.GetCategories();
        foreach (var cat in categories)
        {
            var toolVMs = cat.Tools.Select(t =>
            {
                var vm = new DrawingToolItemViewModel(t.Tool, t.Icon, t.NameKey, this);
                _toolLookup[t.Tool] = vm;
                return vm;
            }).ToArray();

            Categories.Add(new DrawingToolCategoryViewModel(cat.NameKey, cat.Icon, toolVMs));
        }

        // Mark the initial tool as selected
        if (_toolLookup.TryGetValue(_chartViewModel.CurrentTool, out var initial))
        {
            initial.IsSelected = true;
        }
    }

    public DrawingToolSidebarViewModel(ChartViewModel chartViewModel)
        : this(chartViewModel, new DrawingObjectsViewModel(chartViewModel, new StockAnalyzer.Avalonia.Services.DispatcherService()))
    {
    }

    public void OpenCategory(DrawingToolCategoryViewModel category)
    {
        // Mirrors ToggleObjectsPanel()'s reverse direction: opening a category flyout must close
        // the Layer Management panel too, since both occupy the same sidebar slot and the Objects
        // panel would otherwise stay visible on top of it, blocking the flyout from being seen.
        IsObjectsPanelOpen = false;

        if (ExpandedCategory != null && ExpandedCategory != category)
        {
            ExpandedCategory.IsExpanded = false;
        }

        ExpandedCategory = category;
        category.IsExpanded = true;
        IsFlyoutOpen = true;
    }

    /// <summary>
    /// Called by DrawingToolItemViewModel when a tool is selected.
    /// </summary>
    public void SelectTool(DrawingToolItemViewModel item)
    {
        // Deselect previous
        foreach (var kvp in _toolLookup)
        {
            kvp.Value.IsSelected = false;
        }

        item.IsSelected = true;
        _chartViewModel.CurrentTool = item.Tool;

        // Close flyout after selection
        IsFlyoutOpen = false;
        if (ExpandedCategory != null)
        {
            ExpandedCategory.IsExpanded = false;
            ExpandedCategory = null;
        }
    }

    /// <summary>
    /// Called by DrawingToolItemViewModel when favorite is toggled.
    /// </summary>
    public void UpdateFavorites(DrawingToolItemViewModel item)
    {
        if (item.IsFavorite)
        {
            if (!Favorites.Contains(item))
            {
                Favorites.Add(item);
            }
        }
        else
        {
            Favorites.Remove(item);
        }

        ShowFavoritesPalette = Favorites.Count > 0;
    }

    /// <summary>
    /// Selects a tool directly (for favorites palette clicks).
    /// </summary>
    [RelayCommand]
    private void SelectToolDirect(DrawingToolItemViewModel item)
    {
        SelectTool(item);
    }

    /// <summary>
    /// Closes the flyout (e.g., when clicking outside).
    /// </summary>
    [RelayCommand]
    private void CloseFlyout()
    {
        IsFlyoutOpen = false;
        if (ExpandedCategory != null)
        {
            ExpandedCategory.IsExpanded = false;
            ExpandedCategory = null;
        }
    }

    /// <summary>
    /// Toggles visibility of the drawing objects / layer management panel.
    /// </summary>
    [RelayCommand]
    private void ToggleObjectsPanel()
    {
        IsObjectsPanelOpen = !IsObjectsPanelOpen;
        if (IsObjectsPanelOpen)
        {
            CloseFlyout();
            DrawingObjectsViewModel?.SyncFromManager();
        }
    }

    /// <summary>
    /// Explicitly closes the drawing objects panel.
    /// </summary>
    [RelayCommand]
    private void CloseObjectsPanel()
    {
        IsObjectsPanelOpen = false;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        DrawingObjectsViewModel?.Dispose();
        GC.SuppressFinalize(this);
    }
}
