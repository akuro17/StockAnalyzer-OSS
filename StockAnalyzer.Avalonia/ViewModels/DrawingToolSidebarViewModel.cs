namespace StockAnalyzer.Avalonia.ViewModels;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Models;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Services.Drawing;

/// <summary>
/// ViewModel for a single category in the sidebar.
/// </summary>
public partial class DrawingToolCategoryViewModel : ObservableObject
{
    public string NameKey { get; }
    public string DisplayName => LocalizationManager.Instance[NameKey];
    public string Icon { get; }
    public ObservableCollection<DrawingToolItemViewModel> Tools { get; }
    public bool IsIconCategory => NameKey == "DrawCat_Icon";

    [ObservableProperty]
    private bool _isExpanded;

    public DrawingToolCategoryViewModel(string nameKey, string icon, IEnumerable<DrawingToolItemViewModel> tools)
    {
        NameKey = nameKey;
        Icon = icon;
        var comparer = StringComparer.Create(
            CultureInfo.GetCultureInfo(LocalizationManager.Instance.CurrentLanguage), ignoreCase: true);
        Tools = new ObservableCollection<DrawingToolItemViewModel>(
            tools.OrderBy(tool => tool.DisplayName, comparer));
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
    private readonly IIconDrawingService? _iconDrawingService;

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

    [ObservableProperty]
    private ObservableCollection<IconGroupInfo> _iconGroups = new();

    [ObservableProperty]
    private IconGroupInfo? _selectedIconGroup;

    [ObservableProperty]
    private bool _isIconDropDownOpen;

    // Flat lookup for updating selection highlight
    private readonly Dictionary<DrawingTool, DrawingToolItemViewModel> _toolLookup = new();

    public DrawingToolSidebarViewModel(
        ChartViewModel chartViewModel,
        DrawingObjectsViewModel drawingObjectsViewModel,
        IIconDrawingService? iconDrawingService = null)
    {
        _chartViewModel = chartViewModel ?? throw new ArgumentNullException(nameof(chartViewModel));
        DrawingObjectsViewModel = drawingObjectsViewModel ?? throw new ArgumentNullException(nameof(drawingObjectsViewModel));
        _iconDrawingService = iconDrawingService;
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
        : this(chartViewModel, new DrawingObjectsViewModel(chartViewModel, new StockAnalyzer.Avalonia.Services.DispatcherService()), null)
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

        if (category.IsIconCategory)
        {
            RefreshIconGroups();
        }
    }

    public void RefreshIconGroups()
    {
        if (_iconDrawingService == null) return;
        _iconDrawingService.Refresh();
        IconGroups.Clear();
        var groups = _iconDrawingService.GetGroups();
        foreach (var g in groups)
        {
            IconGroups.Add(g);
        }
        if (IconGroups.Count > 0)
        {
            var match = SelectedIconGroup != null
                ? IconGroups.FirstOrDefault(g => string.Equals(g.Key, SelectedIconGroup.Key, StringComparison.OrdinalIgnoreCase))
                : null;
            SelectedIconGroup = match ?? IconGroups[0];
            LoadIconTools(SelectedIconGroup.Key);
        }
    }

    partial void OnSelectedIconGroupChanged(IconGroupInfo? value)
    {
        if (ExpandedCategory != null && ExpandedCategory.IsIconCategory && value != null)
        {
            LoadIconTools(value.Key);
        }
    }

    private void LoadIconTools(string? groupKey)
    {
        if (_iconDrawingService == null || ExpandedCategory == null || !ExpandedCategory.IsIconCategory) return;

        ExpandedCategory.Tools.Clear();
        var icons = _iconDrawingService.GetIcons(groupKey);
        foreach (var icon in icons)
        {
            var bitmap = _iconDrawingService.GetAvaloniaBitmap(icon.RelativePath);
            var itemVm = new DrawingToolItemViewModel(
                DrawingTool.Icon,
                icon: string.Empty,
                nameKey: icon.Name,
                parent: this,
                customDisplayName: icon.Name,
                image: bitmap,
                iconInfo: icon);

            if (_chartViewModel.CurrentTool == DrawingTool.Icon &&
                _iconDrawingService.ActiveIcon != null &&
                _iconDrawingService.ActiveIcon.RelativePath == icon.RelativePath)
            {
                itemVm.IsSelected = true;
            }

            ExpandedCategory.Tools.Add(itemVm);
        }
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

        if (ExpandedCategory != null && ExpandedCategory.IsIconCategory)
        {
            foreach (var t in ExpandedCategory.Tools)
            {
                t.IsSelected = false;
            }
        }

        foreach (var fav in Favorites)
        {
            fav.IsSelected = false;
        }

        item.IsSelected = true;
        foreach (var fav in Favorites)
        {
            if (fav == item || (fav.Tool == item.Tool && fav.IconInfo?.RelativePath == item.IconInfo?.RelativePath && fav.Tool == DrawingTool.Icon) || (fav.Tool != DrawingTool.Icon && fav.Tool == item.Tool))
            {
                fav.IsSelected = true;
            }
        }
        if (item.IconInfo != null && _iconDrawingService != null)
        {
            _iconDrawingService.ActiveIcon = item.IconInfo;
        }

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
