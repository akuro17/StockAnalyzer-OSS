using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>
/// ViewModel managing states and operations for the Column Chooser dialog.
/// </summary>
public partial class ColumnChooserViewModel : ViewModelBase
{
    private readonly IMessenger _messenger;
    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private ColumnCategory _selectedCategory = ColumnCategory.Active;

    [ObservableProperty]
    private ColumnItemViewModel? _selectedColumn;

    public ObservableCollection<ColumnItemViewModel> AllItems { get; } = new();
    public ObservableCollection<ColumnItemViewModel> FilteredItems { get; } = new();

    public ColumnChooserViewModel(IEnumerable<WatchlistColumnMetadata> allColumns, IEnumerable<string> activeColumnNames, IMessenger? messenger = null)
    {
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        var activeList = activeColumnNames.ToList();
        var activeSet = new HashSet<string>(activeList, StringComparer.OrdinalIgnoreCase);
        
        // 1. Add active columns in their current customized order
        foreach (var name in activeList)
        {
            var col = allColumns.FirstOrDefault(c => string.Equals(c.MemberName, name, StringComparison.OrdinalIgnoreCase));
            if (col != null)
            {
                var item = ColumnItemViewModel.Create(col, true);
                BindItemPropertyChange(item);
                AllItems.Add(item);
            }
        }

        // 2. Add remaining inactive columns in their default order
        foreach (var col in allColumns)
        {
            if (!activeSet.Contains(col.MemberName))
            {
                var item = ColumnItemViewModel.Create(col, false);
                BindItemPropertyChange(item);
                AllItems.Add(item);
            }
        }

        AllItems.CollectionChanged += (s, e) => UpdateFilteredItems();

        UpdateFilteredItems();
    }

    private void BindItemPropertyChange(ColumnItemViewModel item)
    {
        item.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ColumnItemViewModel.IsActive))
            {
                if (SelectedCategory == ColumnCategory.Active)
                {
                    UpdateFilteredItems();
                }
            }
        };
    }

    partial void OnSearchQueryChanged(string value) => UpdateFilteredItems();
    partial void OnSelectedCategoryChanged(ColumnCategory value) => UpdateFilteredItems();

    private void UpdateFilteredItems()
    {
        FilteredItems.Clear();
        var query = SearchQuery?.Trim() ?? string.Empty;

        IEnumerable<ColumnItemViewModel> items;
        if (SelectedCategory == ColumnCategory.Active)
        {
            items = AllItems.Where(item => item.IsActive);
        }
        else if (SelectedCategory == ColumnCategory.All)
        {
            items = AllItems
                .OrderBy(item => item.IsSelect ? 0 : (item.IsSymbol ? 1 : 2))
                .ThenBy(item => item.EnglishName, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            items = AllItems
                .Where(item => item.Category == SelectedCategory)
                .OrderBy(item => item.IsSelect ? 0 : (item.IsSymbol ? 1 : 2))
                .ThenBy(item => item.EnglishName, StringComparer.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrEmpty(query))
        {
            items = items.Where(item =>
                item.EnglishName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.MemberName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (item.Description != null && item.Description.Contains(query, StringComparison.OrdinalIgnoreCase)));
        }

        foreach (var item in items)
        {
            FilteredItems.Add(item);
        }

        if (SelectedColumn == null || !FilteredItems.Contains(SelectedColumn))
        {
            SelectedColumn = FilteredItems.FirstOrDefault();
        }
    }

    /// <summary>
    /// Commands to set IsActive to true for all columns in the currently selected category (except Symbol columns).
    /// </summary>
    [RelayCommand]
    private void SelectAll()
    {
        // If in Active or All category, select all in AllItems (except symbol)
        var targetItems = (SelectedCategory == ColumnCategory.Active || SelectedCategory == ColumnCategory.All)
            ? AllItems 
            : AllItems.Where(item => item.Category == SelectedCategory);

        foreach (var item in targetItems)
        {
            if (!item.IsSymbol)
            {
                item.IsActive = true;
            }
        }
    }

    /// <summary>
    /// Commands to set IsActive to false for all columns in the currently selected category (except Symbol columns).
    /// </summary>
    [RelayCommand]
    private void ClearAll()
    {
        var targetItems = (SelectedCategory == ColumnCategory.Active || SelectedCategory == ColumnCategory.All)
            ? AllItems 
            : AllItems.Where(item => item.Category == SelectedCategory);

        foreach (var item in targetItems)
        {
            if (!item.IsSymbol)
            {
                item.IsActive = false;
            }
        }
    }

    /// <summary>
    /// Returns the names of all columns currently selected as active.
    /// </summary>
    public List<string> GetActiveColumnNames()
    {
        return AllItems
            .Where(item => item.IsActive)
            .Select(item => item.MemberName)
            .ToList();
    }

    /// <summary>
    /// Apply changes immediately.
    /// </summary>
    [RelayCommand]
    private void Apply()
    {
        var activeCols = GetActiveColumnNames();
        _messenger.Send(new ColumnChooserAppliedMessage(activeCols));
    }
}
