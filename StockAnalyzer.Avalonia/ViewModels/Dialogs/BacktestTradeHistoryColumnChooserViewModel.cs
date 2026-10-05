using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockAnalyzer.Core.Interfaces;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

public enum BacktestTradeColumnCategory
{
    Active = 0,
    All = 1
}

/// <summary>
/// ViewModel managing states and operations for the Backtest Trade History Column Chooser dialog.
/// </summary>
public partial class BacktestTradeHistoryColumnChooserViewModel : ViewModelBase, IDisposable
{
    public static readonly IReadOnlyList<(string Id, string HeaderKey, string DescKey, double MinWidth)> KnownColumns = new[]
    {
        ("TradeId", "Backtest_TradeList_Header_TradeId", "Backtest_TradeHistory_Col_TradeId_Desc", 50.0),
        ("Side", "Backtest_TradeList_Header_Side", "Backtest_TradeHistory_Col_Side_Desc", 60.0),
        ("EntryTime", "Backtest_TradeList_Header_EntryTime", "Backtest_TradeHistory_Col_EntryTime_Desc", 150.0),
        ("EntryPrice", "Backtest_TradeList_Header_EntryPrice", "Backtest_TradeHistory_Col_EntryPrice_Desc", 90.0),
        ("ExitTime", "Backtest_TradeList_Header_ExitTime", "Backtest_TradeHistory_Col_ExitTime_Desc", 150.0),
        ("ExitPrice", "Backtest_TradeList_Header_ExitPrice", "Backtest_TradeHistory_Col_ExitPrice_Desc", 90.0),
        ("Quantity", "Backtest_TradeList_Header_Quantity", "Backtest_TradeHistory_Col_Quantity_Desc", 80.0),
        ("EntryValue", "Backtest_TradeList_Header_EntryValue", "Backtest_TradeHistory_Col_EntryValue_Desc", 100.0),
        ("ExitValue", "Backtest_TradeList_Header_ExitValue", "Backtest_TradeHistory_Col_ExitValue_Desc", 100.0),
        ("Commission", "Backtest_TradeList_Header_Commission", "Backtest_TradeHistory_Col_Commission_Desc", 85.0),
        ("Slippage", "Backtest_TradeList_Header_Slippage", "Backtest_TradeHistory_Col_Slippage_Desc", 85.0),
        ("ClosedNet", "Backtest_TradeList_Header_ClosedNet", "Backtest_TradeHistory_Col_ClosedNet_Desc", 110.0),
        ("PnLPercent", "Backtest_TradeList_Header_PnLPercent", "Backtest_TradeHistory_Col_PnLPercent_Desc", 80.0),
        ("MfePrice", "Backtest_TradeList_Header_MfePrice", "Backtest_TradeHistory_Col_MfePrice_Desc", 85.0),
        ("MfePercent", "Backtest_TradeList_Header_MfePercent", "Backtest_TradeHistory_Col_MfePercent_Desc", 80.0),
        ("MaePrice", "Backtest_TradeList_Header_MaePrice", "Backtest_TradeHistory_Col_MaePrice_Desc", 85.0),
        ("MaePercent", "Backtest_TradeList_Header_MaePercent", "Backtest_TradeHistory_Col_MaePercent_Desc", 80.0),
        ("HoldingBars", "Backtest_TradeList_Header_HoldingBars", "Backtest_TradeHistory_Col_HoldingBars_Desc", 75.0),
        ("Duration", "Backtest_TradeList_Header_Duration", "Backtest_TradeHistory_Col_Duration_Desc", 95.0),
        ("EntrySignal", "Backtest_TradeList_Header_EntrySignal", "Backtest_TradeHistory_Col_EntrySignal_Desc", 130.0),
        ("ExitReason", "Backtest_TradeList_Header_ExitReason", "Backtest_TradeHistory_Col_ExitReason_Desc", 130.0),
        ("ForcedLiquidation", "Backtest_TradeList_Header_ForcedLiquidation", "Backtest_TradeHistory_Col_ForcedLiquidation_Desc", 110.0),
    };

    private static readonly Dictionary<string, double> MinWidthLookup =
        KnownColumns.ToDictionary(c => c.Id, c => c.MinWidth, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> HeaderKeyLookup =
        KnownColumns.ToDictionary(c => c.Id, c => c.HeaderKey, StringComparer.OrdinalIgnoreCase);

    public static double GetColumnMinWidth(string colId) =>
        MinWidthLookup.TryGetValue(colId, out double width) ? width : 50.0;

    public static string GetColumnHeaderKey(string colId) =>
        HeaderKeyLookup.TryGetValue(colId, out string? key) ? key : colId;

    [ObservableProperty]
    private BacktestTradeColumnCategory _selectedCategory = BacktestTradeColumnCategory.Active;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private BacktestTradeColumnItemViewModel? _selectedColumn;

    public ObservableCollection<BacktestTradeColumnItemViewModel> AllItems { get; } = new();
    public ObservableCollection<BacktestTradeColumnItemViewModel> FilteredItems { get; } = new();

    private readonly PropertyChangedEventHandler _itemPropertyChangedHandler;
    private readonly System.Collections.Specialized.NotifyCollectionChangedEventHandler _collectionChangedHandler;

    public Action<List<string>>? OnApplyAction { get; set; }

    public BacktestTradeHistoryColumnChooserViewModel(
        IEnumerable<string>? activeColumns = null,
        ILocalizationService? localization = null)
    {
        _itemPropertyChangedHandler = OnItemPropertyChanged;
        _collectionChangedHandler = (s, e) => UpdateFilteredItems();

        var activeList = activeColumns?.ToList() ?? KnownColumns.Select(c => c.Id).ToList();
        var activeSet = new HashSet<string>(activeList, StringComparer.OrdinalIgnoreCase);

        // Fallback: If activeSet is empty, default to all columns
        if (activeSet.Count == 0)
        {
            activeList = KnownColumns.Select(c => c.Id).ToList();
            activeSet = new HashSet<string>(activeList, StringComparer.OrdinalIgnoreCase);
        }

        var knownDict = KnownColumns.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        var seenActive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Add active columns in their current customized order
        foreach (var id in activeList)
        {
            if (seenActive.Add(id) && knownDict.TryGetValue(id, out var col))
            {
                string name = (localization != null && localization.TryGetString(col.HeaderKey, out var h)) ? h : col.Id;
                string desc = (localization != null && localization.TryGetString(col.DescKey, out var d)) ? d : string.Empty;
                var item = new BacktestTradeColumnItemViewModel(col.Id, name, desc, true, CanChangeActive);
                item.PropertyChanged += _itemPropertyChangedHandler;
                AllItems.Add(item);
            }
        }

        // 2. Add remaining inactive columns in their default order
        foreach (var col in KnownColumns)
        {
            if (!activeSet.Contains(col.Id))
            {
                string name = (localization != null && localization.TryGetString(col.HeaderKey, out var h)) ? h : col.Id;
                string desc = (localization != null && localization.TryGetString(col.DescKey, out var d)) ? d : string.Empty;
                var item = new BacktestTradeColumnItemViewModel(col.Id, name, desc, false, CanChangeActive);
                item.PropertyChanged += _itemPropertyChangedHandler;
                AllItems.Add(item);
            }
        }

        AllItems.CollectionChanged += _collectionChangedHandler;

        SelectedColumn = AllItems.FirstOrDefault();
        UpdateFilteredItems();
    }

    private bool CanChangeActive(BacktestTradeColumnItemViewModel item, bool targetValue)
    {
        if (targetValue) return true;
        // Invariant: At least 1 active column must remain (ActiveCount >= 1).
        return AllItems.Count(x => x.IsActive && x != item) >= 1;
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BacktestTradeColumnItemViewModel.IsActive))
        {
            if (SelectedCategory == BacktestTradeColumnCategory.Active)
            {
                UpdateFilteredItems();
            }
        }
    }

    partial void OnSelectedCategoryChanged(BacktestTradeColumnCategory value)
    {
        UpdateFilteredItems();
    }

    partial void OnSearchQueryChanged(string value)
    {
        UpdateFilteredItems();
    }

    public void Apply()
    {
        OnApplyAction?.Invoke(GetActiveColumnIds());
    }

    public List<string> GetActiveColumnIds()
    {
        return AllItems.Where(x => x.IsActive).Select(x => x.ColumnId).ToList();
    }

    private void UpdateFilteredItems()
    {
        FilteredItems.Clear();
        string query = SearchQuery?.Trim() ?? string.Empty;

        foreach (var item in AllItems)
        {
            if (SelectedCategory == BacktestTradeColumnCategory.Active && !item.IsActive)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(query))
            {
                bool matchesName = item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase);
                bool matchesDesc = item.Description.Contains(query, StringComparison.OrdinalIgnoreCase);
                if (!matchesName && !matchesDesc)
                {
                    continue;
                }
            }

            FilteredItems.Add(item);
        }

        if (SelectedColumn == null || !FilteredItems.Contains(SelectedColumn))
        {
            SelectedColumn = FilteredItems.FirstOrDefault();
        }
    }

    public void Dispose()
    {
        AllItems.CollectionChanged -= _collectionChangedHandler;
        foreach (var item in AllItems)
        {
            item.PropertyChanged -= _itemPropertyChangedHandler;
        }
    }
}
