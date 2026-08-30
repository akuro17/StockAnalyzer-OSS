using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.ViewModels;

public enum TargetSide
{
    Left,
    Right
}

public class TimeFrameItem
{
    public TimeFrame Value { get; set; }
    public string DisplayName { get; set; } = string.Empty;

    public override string ToString() => DisplayName;
}

/// <summary>
/// Display item for the Left Navigation Panel representing either a Category Header or a Selectable Group.
/// </summary>
public class ScreenerGroupDisplayItem
{
    public bool IsHeader { get; set; }
    public string HeaderTitle { get; set; } = string.Empty;
    public ScreenerIndicatorGroup? Group { get; set; }
    public string? CustomDisplayName { get; set; }
    public bool IsAllFilters => Group?.CategoryType == "All";
    public bool IsStandardItem => !IsHeader && !IsAllFilters;
    public string DisplayName => IsHeader ? HeaderTitle : (CustomDisplayName ?? Group?.Name ?? string.Empty);
}

/// <summary>
/// ViewModel for the Indicator Registration tab in the Screener window.
/// Implements the Focus-Bind (Juxtaposition) approach with Left (Source) & Right (Target) containers,
/// comparison operator selection, numeric value vs indicator target mode, and independent timeframes.
/// </summary>
public partial class IndicatorRegistrationViewModel : ViewModelBase, IDisposable
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> _propertyCache = new();
    private readonly IIndicatorFactory? _indicatorFactory;
    private readonly IScreenerCatalogProvider _catalogProvider;
    private readonly ILocalizationService _localizationService;
    private readonly ILogger<IndicatorRegistrationViewModel> _logger;
    private CancellationTokenSource? _searchDebounceTokenSource;
    private bool _isDisposed;

    // ===== Left Navigation Column: Unified Hierarchical Group List =====

    public ObservableCollection<ScreenerGroupDisplayItem> NavGroups { get; } = new();

    [ObservableProperty]
    private ScreenerGroupDisplayItem? _selectedGroupItem;

    // ===== Center Catalog Column =====

    private readonly List<ScreenerCatalogItem> _allCatalogItems = new();

    public BulkObservableCollection<ScreenerCatalogItem> FilteredIndicators { get; } = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private ScreenerCatalogItem? _selectedIndicator;

    // ===== Focus-Bind Container State =====

    [ObservableProperty]
    private TargetSide _activeTargetSide = TargetSide.Left;

    [ObservableProperty]
    private bool _isLeftActive = true;

    [ObservableProperty]
    private bool _isRightActive = false;

    partial void OnActiveTargetSideChanged(TargetSide value)
    {
        IsLeftActive = (value == TargetSide.Left);
        IsRightActive = (value == TargetSide.Right);
    }

    [RelayCommand]
    private void SelectLeftTarget()
    {
        ActiveTargetSide = TargetSide.Left;
    }

    [RelayCommand]
    private void SelectRightTarget()
    {
        ActiveTargetSide = TargetSide.Right;
    }

    public static readonly HashSet<string> StringColumnNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Symbol", "Name", "Sector", "Industry", "Region", "Tag"
    };

    // ===== Right-Hand Target Mode =====

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RegisterIndicatorCommand))]
    private RightHandTargetMode _rightTargetMode = RightHandTargetMode.NumericValue;

    [ObservableProperty]
    private bool _isRightNumericMode = true;

    [ObservableProperty]
    private bool _isRightIndicatorMode = false;

    [ObservableProperty]
    private bool _isRightStringMode = false;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RegisterIndicatorCommand))]
    private string _rightStringValue = string.Empty;

    [ObservableProperty]
    private bool _isLeftTextColumn = false;

    // Visibility toggles for hosting components
    [ObservableProperty]
    private bool _isLogicalJoinVisible = true;

    [ObservableProperty]
    private bool _isHeaderRowVisible = true;

    partial void OnRightTargetModeChanged(RightHandTargetMode value)
    {
        IsRightNumericMode = (value == RightHandTargetMode.NumericValue);
        IsRightIndicatorMode = (value == RightHandTargetMode.Indicator);
        IsRightStringMode = (value == RightHandTargetMode.StringValue);
    }

    [RelayCommand]
    private void SetRightModeNumeric()
    {
        RightTargetMode = RightHandTargetMode.NumericValue;
        RightSelectedIndicator = null;
        RightIndicatorSettings = null;
        RightAvailableOutputs.Clear();
    }

    [RelayCommand]
    private void SetRightModeIndicator()
    {
        RightTargetMode = RightHandTargetMode.Indicator;
        ActiveTargetSide = TargetSide.Right;
    }

    [RelayCommand]
    private void SetRightModeString()
    {
        RightTargetMode = RightHandTargetMode.StringValue;
        RightSelectedIndicator = null;
        RightIndicatorSettings = null;
        RightAvailableOutputs.Clear();
    }

    // ===== Middle Comparison Operator Controls =====

    [ObservableProperty]
    private ComparisonOperator _selectedComparisonOperator = ComparisonOperator.GreaterThan;

    public ObservableCollection<ComparisonOperator> AvailableComparisonOperators { get; } = new(new[]
    {
        ComparisonOperator.GreaterThan,
        ComparisonOperator.GreaterThanOrEqual,
        ComparisonOperator.LessThan,
        ComparisonOperator.LessThanOrEqual,
        ComparisonOperator.Equal,
        ComparisonOperator.NotEqual
    });

    [ObservableProperty]
    private LogicalOperator _logicalOperator = LogicalOperator.And;

    [ObservableProperty]
    private bool _isLogicalAnd = true;

    [ObservableProperty]
    private bool _isLogicalOr = false;

    partial void OnLogicalOperatorChanged(LogicalOperator value)
    {
        IsLogicalAnd = (value == LogicalOperator.And);
        IsLogicalOr = (value == LogicalOperator.Or);

        if (RegisteredEntries != null)
        {
            foreach (var entry in RegisteredEntries)
            {
                entry.LogicalOperator = value;
            }
        }
    }

    [RelayCommand]
    private void SetLogicalAnd()
    {
        LogicalOperator = LogicalOperator.And;
    }

    [RelayCommand]
    private void SetLogicalOr()
    {
        LogicalOperator = LogicalOperator.Or;
    }

    public IReadOnlyList<LogicalOperator> AvailableLogicalOperators { get; } = new[]
    {
        LogicalOperator.And,
        LogicalOperator.Or
    };

    // ===== Left Side (Source) Configuration Properties =====

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RegisterIndicatorCommand))]
    private ScreenerCatalogItem? _leftSelectedIndicator;

    [ObservableProperty]
    private CoreIndicatorSettings? _leftIndicatorSettings;

    public ObservableCollection<string> LeftAvailableOutputs { get; } = new();

    [ObservableProperty]
    private string _leftSelectedOutput = IndicatorResult.MainSeriesName;

    [ObservableProperty]
    private bool _hasMultipleLeftOutputs;

    [ObservableProperty]
    private TimeFrameItem _leftSelectedTimeFrameItem;

    [ObservableProperty]
    private int _leftOffset;

    // ===== Right Side (Target) Configuration Properties =====

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RegisterIndicatorCommand))]
    private ScreenerCatalogItem? _rightSelectedIndicator;

    [ObservableProperty]
    private CoreIndicatorSettings? _rightIndicatorSettings;

    public ObservableCollection<string> RightAvailableOutputs { get; } = new();

    [ObservableProperty]
    private string _rightSelectedOutput = IndicatorResult.MainSeriesName;

    [ObservableProperty]
    private bool _hasMultipleRightOutputs;

    [ObservableProperty]
    private TimeFrameItem _rightSelectedTimeFrameItem;

    [ObservableProperty]
    private int _rightOffset;

    [ObservableProperty]
    private decimal _rightNumericValue = 50m;

    // ===== Registered Entries =====

    public ObservableCollection<ScreenerIndicatorEntry> RegisteredEntries { get; } = new();

    // ===== Common Available TimeFrames =====

    public IReadOnlyList<TimeFrameItem> AvailableTimeFrames { get; } = new[]
    {
        new TimeFrameItem { Value = TimeFrame.D1, DisplayName = "Day" },
        new TimeFrameItem { Value = TimeFrame.W1, DisplayName = "Week" },
        new TimeFrameItem { Value = TimeFrame.MN1, DisplayName = "Month" }
    };

    // ===== Constructor =====

    public IndicatorRegistrationViewModel(
        IIndicatorFactory? indicatorFactory = null,
        IScreenerCatalogProvider? catalogProvider = null,
        ILocalizationService? localizationService = null,
        ILogger<IndicatorRegistrationViewModel>? logger = null)
    {
        _indicatorFactory = indicatorFactory;
        _catalogProvider = catalogProvider ?? new ScreenerCatalogProvider();
        _localizationService = localizationService ?? NullLocalizationService.Instance;
        _logger = logger ?? NullLogger<IndicatorRegistrationViewModel>.Instance;

        _leftSelectedTimeFrameItem = AvailableTimeFrames[0];
        _rightSelectedTimeFrameItem = AvailableTimeFrames[0];

        BuildCatalog();
        LoadNavGroups();

        // Default Left side to first catalog item if available
        if (FilteredIndicators.Count > 0)
        {
            SelectedIndicator = FilteredIndicators[0];
        }
    }

    // ===== Initialization & Catalog =====

    private void LoadNavGroups()
    {
        NavGroups.Clear();
        var defaultGroups = ScreenerIndicatorGroup.GetDefaultGroups();

        // 1. Add "All Filters" at the very top above Indicators header
        var allFiltersGroup = new ScreenerIndicatorGroup("All Filters", System.Array.Empty<IndicatorType>(), "All", "Nav_AllFilters");
        NavGroups.Add(new ScreenerGroupDisplayItem
        {
            IsHeader = false,
            Group = allFiltersGroup,
            CustomDisplayName = "All Filters"
        });

        // 2. Indicators Category Section
        NavGroups.Add(new ScreenerGroupDisplayItem { IsHeader = true, HeaderTitle = "Indicators" });
        foreach (var g in defaultGroups.Where(g => g.CategoryType == "Indicator"))
        {
            NavGroups.Add(new ScreenerGroupDisplayItem { IsHeader = false, Group = g, CustomDisplayName = g.Name });
        }

        // 3. Columns Category Section
        NavGroups.Add(new ScreenerGroupDisplayItem { IsHeader = true, HeaderTitle = "Columns" });
        foreach (var g in defaultGroups.Where(g => g.CategoryType == "Column"))
        {
            NavGroups.Add(new ScreenerGroupDisplayItem { IsHeader = false, Group = g, CustomDisplayName = g.Name });
        }

        // 4. Chart Patterns (Criteria) Category Section
        NavGroups.Add(new ScreenerGroupDisplayItem { IsHeader = true, HeaderTitle = "Chart Patterns" });
        foreach (var g in defaultGroups.Where(g => g.CategoryType == "Criteria"))
        {
            NavGroups.Add(new ScreenerGroupDisplayItem { IsHeader = false, Group = g, CustomDisplayName = g.Name });
        }

        SelectedGroupItem = NavGroups.FirstOrDefault(item => !item.IsHeader);
    }

    private void BuildCatalog()
    {
        _allCatalogItems.Clear();
        _allCatalogItems.AddRange(_catalogProvider.GetCatalogItems(_indicatorFactory));
        ApplyFilters();
    }

    private void ApplyFilters()
    {
        var selectedGroup = SelectedGroupItem?.Group;
        var filtered = _allCatalogItems.AsEnumerable();

        bool isAllFilters = selectedGroup != null && (
            string.Equals(selectedGroup.CategoryType, "All", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(selectedGroup.Name, "All Filters", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(selectedGroup.Name, "All Indicators", StringComparison.OrdinalIgnoreCase));

        if (selectedGroup != null)
        {
            if (isAllFilters)
            {
                // Comprehensively include all items in catalog (Indicators, Columns, Criteria)
                filtered = _allCatalogItems.AsEnumerable();
            }
            else
            {
                string targetGroup = selectedGroup.Name;
                ScreenerItemCategoryType? targetCatEnum = Enum.TryParse<ScreenerItemCategoryType>(selectedGroup.CategoryType, out var parsedCat) ? parsedCat : null;

                filtered = filtered.Where(item =>
                    (targetCatEnum == null || item.CategoryType == targetCatEnum.Value) &&
                    string.Equals(item.GroupName, targetGroup, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var searchText = SearchText.Trim();
            filtered = filtered.Where(item =>
                item.DisplayName.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                item.ShortName.Contains(searchText, StringComparison.OrdinalIgnoreCase));
        }

        List<ScreenerCatalogItem> results;
        if (isAllFilters)
        {
            results = filtered
                .OrderBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.ShortName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        else
        {
            results = filtered
                .OrderBy(i => ScreenerGroupNames.GetGroupSortOrder(i.GroupName))
                .ThenBy(i => GetPatternPairSortKey(i.ShortName), StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.ShortName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        FilteredIndicators.ReplaceRange(results);
    }

    /// <summary>
    /// Sort key that sorts items strictly by their displayed starting character (A to Z),
    /// while keeping "Bearish ..." and "Bullish ..." pairs grouped adjacent under 'B' (e.g. "B_Abandoned Baby").
    /// </summary>
    private static string GetPatternPairSortKey(string shortName)
    {
        // 1. Bullish / Bearish prefix pairs sort together under 'B' (e.g. "B_Abandoned Baby", "B_Breakaway")
        if (shortName.StartsWith("Bullish ", StringComparison.OrdinalIgnoreCase))
        {
            return "B_" + shortName.Substring("Bullish ".Length);
        }
        if (shortName.StartsWith("Bearish ", StringComparison.OrdinalIgnoreCase))
        {
            return "B_" + shortName.Substring("Bearish ".Length);
        }

        // 2. All other items sort strictly by their actual display name / leading letter.
        return shortName;
    }

    partial void OnSelectedGroupItemChanged(ScreenerGroupDisplayItem? value)
    {
        if (value != null && value.IsHeader) return;
        ApplyFilters();
    }

    partial void OnSearchTextChanged(string value)
    {
        if (_searchDebounceTokenSource != null)
        {
            _searchDebounceTokenSource.Cancel();
            _searchDebounceTokenSource.Dispose();
        }
        _searchDebounceTokenSource = new CancellationTokenSource();
        var token = _searchDebounceTokenSource.Token;
        _ = DebounceSearchAsync(token);
    }

    private async Task DebounceSearchAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(300, token);
            if (!token.IsCancellationRequested)
            {
                ApplyFilters();
            }
        }
        catch (TaskCanceledException)
        {
            // Expected on cancellation
        }
    }

    // ===== Selection Handlers & Dynamic Parameters =====

    partial void OnSelectedIndicatorChanged(ScreenerCatalogItem? value)
    {
        if (value == null) return;
        BindToSide(value, ActiveTargetSide == TargetSide.Left);
    }

    private void BindToSide(ScreenerCatalogItem catalogItem, bool isLeft)
    {
        IndicatorType type;
        CoreIndicatorSettings settings;
        IReadOnlyList<string> outputs;

        if (catalogItem.IndicatorType.HasValue)
        {
            type = catalogItem.IndicatorType.Value;
            settings = _catalogProvider.GetDefaultSettings(type, _indicatorFactory)
                ?? new CoreIndicatorSettings
                {
                    TypeEnum = type,
                    DisplayName = catalogItem.DisplayName,
                    // CoreSmaParameter's own default Period (14) is the intended value here; keep it
                    // as the single source rather than repeating the literal.
                    ParameterObject = new CoreSmaParameter()
                };

            outputs = _catalogProvider.GetOutputSeriesNames(type, _indicatorFactory);
        }
        else
        {
            type = IndicatorType.SMA;
            settings = new CoreIndicatorSettings
            {
                TypeEnum = type,
                DisplayName = catalogItem.DisplayName
            };
            outputs = new[] { string.IsNullOrWhiteSpace(catalogItem.ShortName) ? catalogItem.DisplayName : catalogItem.ShortName };
        }

        if (isLeft)
        {
            LeftSelectedIndicator = catalogItem;
            LeftIndicatorSettings = settings;
            LeftAvailableOutputs.Clear();
            foreach (var outName in outputs)
            {
                LeftAvailableOutputs.Add(outName);
            }
            LeftSelectedOutput = LeftAvailableOutputs.FirstOrDefault() ?? catalogItem.ShortName;
            HasMultipleLeftOutputs = LeftAvailableOutputs.Count > 1;

            bool isTextCol = catalogItem.CategoryType == ScreenerItemCategoryType.Column &&
                             StringColumnNames.Contains(catalogItem.ColumnMemberName ?? catalogItem.ShortName);
            IsLeftTextColumn = isTextCol;

            var targetOp = isTextCol ? ComparisonOperator.Contains : ComparisonOperator.GreaterThan;
            var newOps = isTextCol
                ? new[] { ComparisonOperator.Contains, ComparisonOperator.DoesNotContain, ComparisonOperator.Equal, ComparisonOperator.NotEqual }
                : new[] { ComparisonOperator.GreaterThan, ComparisonOperator.GreaterThanOrEqual, ComparisonOperator.LessThan, ComparisonOperator.LessThanOrEqual, ComparisonOperator.Equal, ComparisonOperator.NotEqual };

            if (!AvailableComparisonOperators.SequenceEqual(newOps))
            {
                AvailableComparisonOperators.Clear();
                foreach (var op in newOps)
                {
                    AvailableComparisonOperators.Add(op);
                }
            }
            SelectedComparisonOperator = targetOp;

            if (isTextCol)
            {
                SetRightModeString();
            }
            else
            {
                if (RightTargetMode == RightHandTargetMode.StringValue)
                {
                    SetRightModeNumeric();
                }
            }
        }
        else
        {
            RightSelectedIndicator = catalogItem;
            RightIndicatorSettings = settings;
            RightAvailableOutputs.Clear();
            foreach (var outName in outputs)
            {
                RightAvailableOutputs.Add(outName);
            }
            RightSelectedOutput = RightAvailableOutputs.FirstOrDefault() ?? catalogItem.ShortName;
            HasMultipleRightOutputs = RightAvailableOutputs.Count > 1;
        }
    }

    // ===== Commands =====

    private bool CanRegisterIndicator()
    {
        if (LeftSelectedIndicator == null) return false;
        if (RightTargetMode == RightHandTargetMode.Indicator)
        {
            return RightSelectedIndicator != null;
        }
        if (RightTargetMode == RightHandTargetMode.StringValue)
        {
            return !string.IsNullOrWhiteSpace(RightStringValue);
        }
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanRegisterIndicator))]
    private void RegisterIndicator()
    {
        if (!CanRegisterIndicator()) return;

        var leftConfig = CreateSideConfig(
            LeftSelectedIndicator,
            LeftIndicatorSettings,
            LeftSelectedOutput,
            LeftSelectedTimeFrameItem?.Value ?? TimeFrame.D1,
            LeftOffset);

        ScreenerIndicatorSideConfig rightConfig = new();
        if (RightTargetMode == RightHandTargetMode.Indicator && RightSelectedIndicator != null)
        {
            rightConfig = CreateSideConfig(
                RightSelectedIndicator,
                RightIndicatorSettings,
                RightSelectedOutput,
                RightSelectedTimeFrameItem?.Value ?? TimeFrame.D1,
                RightOffset);
        }

        var entry = new ScreenerIndicatorEntry
        {
            LeftHand = leftConfig,
            Operator = SelectedComparisonOperator,
            TargetMode = RightTargetMode,
            RightNumericValue = RightNumericValue,
            RightStringValue = RightStringValue.Trim(),
            RightHand = rightConfig,
            CategoryType = LeftSelectedIndicator?.CategoryType ?? ScreenerItemCategoryType.Indicator,
            LogicalOperator = LogicalOperator,
            IsEnabled = true
        };

        RegisteredEntries.Add(entry);
    }

    private ScreenerIndicatorSideConfig CreateSideConfig(
        ScreenerCatalogItem? catalogItem,
        CoreIndicatorSettings? settings,
        string outputName,
        TimeFrame timeFrame,
        int offset)
    {
        var category = catalogItem?.CategoryType ?? ScreenerItemCategoryType.Indicator;
        var config = new ScreenerIndicatorSideConfig
        {
            IndicatorType = catalogItem?.IndicatorType ?? IndicatorType.SMA,
            OutputName = outputName ?? "Main",
            TimeFrame = timeFrame,
            Offset = offset,
            CategoryType = category,
            CustomDisplayName = (category == ScreenerItemCategoryType.Column || category == ScreenerItemCategoryType.Criteria)
                ? (catalogItem?.ShortName ?? catalogItem?.DisplayName ?? string.Empty)
                : string.Empty
        };

        if (settings?.ParameterObject != null)
        {
            var paramObj = settings.ParameterObject;
            var props = _propertyCache.GetOrAdd(paramObj.GetType(), t => t.GetProperties());
            foreach (var prop in props)
            {
                var val = prop.GetValue(paramObj);
                if (val != null)
                {
                    config.Parameters[prop.Name] = val;
                }
            }
        }
        return config;
    }

    [RelayCommand]
    private void RemoveEntry(ScreenerIndicatorEntry? entry)
    {
        if (entry != null)
        {
            RegisteredEntries.Remove(entry);
        }
    }

    [RelayCommand]
    private void ClearAllEntries()
    {
        RegisteredEntries.Clear();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (_searchDebounceTokenSource != null)
        {
            _searchDebounceTokenSource.Cancel();
            _searchDebounceTokenSource.Dispose();
            _searchDebounceTokenSource = null;
        }

        GC.SuppressFinalize(this);
    }
}
