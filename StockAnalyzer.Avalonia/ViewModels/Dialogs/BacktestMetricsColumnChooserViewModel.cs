using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Core.Interfaces;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

public enum BacktestMetricsCategory
{
    Active = 0,
    All = 1,
    Performance = 2,
    Drawdown = 3,
    RiskAdjusted = 4,
    TradeStatistics = 5,
    TradeBehavior = 6,
    RiskEfficiency = 7
}

/// <summary>
/// ViewModel managing states and operations for the Backtest Metrics Column Chooser dialog.
/// </summary>
public partial class BacktestMetricsColumnChooserViewModel : ViewModelBase, IDisposable
{
    private const string MetricLabelPrefix = "Backtest_Metric_";
    private const string MetricTooltipPrefix = "Backtest_Metric_Tooltip_";

    [ObservableProperty]
    private BacktestMetricsCategory _selectedCategory = BacktestMetricsCategory.Active;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private BacktestMetricItemViewModel? _selectedMetric;

    public ObservableCollection<BacktestMetricItemViewModel> AllItems { get; } = new();
    public ObservableCollection<BacktestMetricItemViewModel> FilteredItems { get; } = new();

    private readonly PropertyChangedEventHandler _itemPropertyChangedHandler;
    private readonly System.Collections.Specialized.NotifyCollectionChangedEventHandler _collectionChangedHandler;

    public Action<List<string>>? OnApplyAction { get; set; }

    public BacktestMetricsColumnChooserViewModel(
        IEnumerable<string>? activeMetricNames = null,
        ILocalizationService? localization = null)
    {
        _itemPropertyChangedHandler = OnItemPropertyChanged;
        _collectionChangedHandler = (s, e) => UpdateFilteredItems();

        var activeList = activeMetricNames?.ToList();
        var activeSet = activeList != null && activeList.Count > 0
            ? new HashSet<string>(activeList, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(BacktestMetricCatalog.Definitions.Select(d => d.Name), StringComparer.OrdinalIgnoreCase);

        // Fallback: If activeSet is empty, default to all catalog definitions
        if (activeSet.Count == 0)
        {
            foreach (var def in BacktestMetricCatalog.Definitions)
            {
                activeSet.Add(def.Name);
            }
        }

        // Build a lookup for catalog definitions by name
        var catalogLookup = BacktestMetricCatalog.Definitions.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);
        var seenActive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Phase 1: Add active items (sorted by display name if default sequence, or preserving caller's custom order)
        bool isDefaultSequence = activeList == null ||
            (activeList.Count == BacktestMetricCatalog.Definitions.Length &&
             activeList.SequenceEqual(BacktestMetricCatalog.Definitions.Select(d => d.Name)));

        IEnumerable<string> orderedActiveNames;
        if (isDefaultSequence)
        {
            orderedActiveNames = BacktestMetricCatalog.Definitions
                .OrderBy(d => (localization != null && localization.TryGetString(MetricLabelPrefix + d.Name, out var dn)) ? dn : d.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Select(d => d.Name);
        }
        else
        {
            orderedActiveNames = activeList!.Where(n => catalogLookup.ContainsKey(n));
        }

        foreach (var name in orderedActiveNames)
        {
            if (!activeSet.Contains(name) || !seenActive.Add(name)) continue;
            AddItem(catalogLookup[name], isActive: true, localization);
        }

        // Phase 2: Add remaining inactive items sorted by display name
        var inactiveDefs = BacktestMetricCatalog.Definitions
            .Where(d => !activeSet.Contains(d.Name))
            .OrderBy(d => (localization != null && localization.TryGetString(MetricLabelPrefix + d.Name, out var dn)) ? dn : d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var def in inactiveDefs)
        {
            AddItem(def, isActive: false, localization);
        }

        AllItems.CollectionChanged += _collectionChangedHandler;

        SelectedMetric = AllItems.FirstOrDefault();
        UpdateFilteredItems();
    }

    private void AddItem(BacktestMetricDefinition def, bool isActive, ILocalizationService? localization)
    {
        string name = def.Name;
        string displayName = (localization != null && localization.TryGetString(MetricLabelPrefix + name, out var dn)) ? dn : name;
        string groupDisplayName = (localization != null && localization.TryGetString(BacktestMetricCatalog.GroupKey(def.Group), out var gdn)) ? gdn : def.Group.ToString();
        string description = string.Empty;
        if (localization != null)
        {
            if (localization.TryGetString("Backtest_Metric_Description_" + name, out var desc) ||
                localization.TryGetString(MetricTooltipPrefix + name, out desc))
            {
                description = desc;
            }
        }

        var item = new BacktestMetricItemViewModel(
            name,
            def.Group,
            displayName,
            groupDisplayName,
            description,
            isActive,
            CanChangeActive);

        item.PropertyChanged += _itemPropertyChangedHandler;
        AllItems.Add(item);
    }

    private bool CanChangeActive(BacktestMetricItemViewModel item, bool targetValue)
    {
        if (targetValue) return true;
        // Invariant: At least 1 active metric must remain (ActiveCount >= 1).
        return AllItems.Count(x => x.IsActive && x != item) >= 1;
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BacktestMetricItemViewModel.IsActive))
        {
            if (SelectedCategory == BacktestMetricsCategory.Active)
            {
                UpdateFilteredItems();
            }
        }
    }

    partial void OnSelectedCategoryChanged(BacktestMetricsCategory value)
    {
        UpdateFilteredItems();
    }

    partial void OnSearchQueryChanged(string value)
    {
        UpdateFilteredItems();
    }

    public void Apply()
    {
        OnApplyAction?.Invoke(GetActiveMetricNames());
    }

    public List<string> GetActiveMetricNames()
    {
        return AllItems.Where(x => x.IsActive).Select(x => x.MetricName).ToList();
    }

    private void UpdateFilteredItems()
    {
        FilteredItems.Clear();
        string query = SearchQuery?.Trim() ?? string.Empty;

        BacktestMetricGroup? targetGroup = SelectedCategory switch
        {
            BacktestMetricsCategory.Performance => BacktestMetricGroup.Performance,
            BacktestMetricsCategory.Drawdown => BacktestMetricGroup.Drawdown,
            BacktestMetricsCategory.RiskAdjusted => BacktestMetricGroup.RiskAdjusted,
            BacktestMetricsCategory.TradeStatistics => BacktestMetricGroup.TradeStatistics,
            BacktestMetricsCategory.TradeBehavior => BacktestMetricGroup.TradeBehavior,
            BacktestMetricsCategory.RiskEfficiency => BacktestMetricGroup.RiskEfficiency,
            _ => null
        };

        IEnumerable<BacktestMetricItemViewModel> items;
        if (SelectedCategory == BacktestMetricsCategory.Active)
        {
            items = AllItems.Where(item => item.IsActive);
        }
        else if (targetGroup.HasValue)
        {
            items = AllItems
                .Where(item => item.Group == targetGroup.Value)
                .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.MetricName, StringComparer.OrdinalIgnoreCase);
        }
        else // SelectedCategory == BacktestMetricsCategory.All
        {
            items = AllItems
                .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.MetricName, StringComparer.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrEmpty(query))
        {
            items = items.Where(item =>
                item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.MetricName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.Description.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var item in items)
        {
            FilteredItems.Add(item);
        }

        if (SelectedMetric == null || !FilteredItems.Contains(SelectedMetric))
        {
            SelectedMetric = FilteredItems.FirstOrDefault();
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
