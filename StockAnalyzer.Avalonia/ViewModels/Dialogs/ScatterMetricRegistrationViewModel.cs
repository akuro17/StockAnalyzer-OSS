using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.Core.Services.Backtest.Engine;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>What a row of the left navigation of the scatter metric registration window is.</summary>
public enum ScatterMetricNavKind
{
    /// <summary>The metrics registered so far (the draft).</summary>
    Active,
    Divider,
    /// <summary>Every offered catalog item.</summary>
    AllFilters,
    /// <summary>A section title ("Indicators", "Columns"); not selectable.</summary>
    Header,
    /// <summary>One group of the catalog.</summary>
    Group,
}

/// <summary>A row of the left navigation.</summary>
public sealed class ScatterMetricNavItem
{
    public ScatterMetricNavKind Kind { get; init; }

    public string Title { get; init; } = string.Empty;

    public ScreenerIndicatorGroup? Group { get; init; }

    public bool IsActive => Kind == ScatterMetricNavKind.Active;
    public bool IsDivider => Kind == ScatterMetricNavKind.Divider;
    public bool IsHeader => Kind == ScatterMetricNavKind.Header;
    public bool IsAllFilters => Kind == ScatterMetricNavKind.AllFilters;
    public bool IsGroup => Kind == ScatterMetricNavKind.Group;
    public bool IsSelectable => Kind is ScatterMetricNavKind.Active or ScatterMetricNavKind.AllFilters or ScatterMetricNavKind.Group;
}

/// <summary>
/// The scatter metric registration window: pick indicators and columns from the catalog, edit them, order them, and confirm the whole list with OK.
/// Everything the user does is applied to a draft; the confirmed list (<see cref="IScatterMetricRegistry"/>) changes only when OK succeeds, and
/// Cancel throws the draft away. A window of its own: it shares the Core catalog, rules and registry with the Screener, none of the Screener's screens.
/// </summary>
public sealed partial class ScatterMetricRegistrationViewModel : ViewModelBase, IDisposable
{
    private readonly IScatterMetricRegistry _registry;
    private readonly IScreenerCatalogProvider _catalogProvider;
    private readonly IIndicatorFactory _indicatorFactory;
    private readonly ILocalizationService _localization;
    private readonly IDialogService _dialogService;
    private readonly ILogger<ScatterMetricRegistrationViewModel> _logger;
    private readonly int _maxMetrics;
    private readonly int _maxOffset;
    private readonly int _debounceMilliseconds;
    private readonly string _timeFrameFixedReason;
    private readonly List<ScreenerCatalogItem> _offeredItems;
    private IReadOnlyList<ScatterMetricDefinition> _baseline = Array.Empty<ScatterMetricDefinition>();
    private CancellationTokenSource? _searchDebounce;
    private ScatterMetricNavItem? _lastSelectableNav;
    private bool _isCancelling;
    private bool _isDisposed;

    public ScatterMetricRegistrationViewModel(
        IScatterMetricRegistry registry,
        IScreenerCatalogProvider catalogProvider,
        ILocalizationService localization,
        IToastNotificationService toastService,
        IDialogService dialogService,
        IStockAnalyzerSettings settings,
        IIndicatorFactory? indicatorFactory = null,
        ILogger<ScatterMetricRegistrationViewModel>? logger = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _catalogProvider = catalogProvider ?? throw new ArgumentNullException(nameof(catalogProvider));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        ToastService = toastService ?? throw new ArgumentNullException(nameof(toastService));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        ArgumentNullException.ThrowIfNull(settings);
        _indicatorFactory = indicatorFactory ?? IndicatorFactory.Default;
        _logger = logger ?? NullLogger<ScatterMetricRegistrationViewModel>.Instance;
        _maxMetrics = settings.ScatterMaxRegisteredMetrics;
        _maxOffset = settings.ScatterMaxMetricOffset;
        _debounceMilliseconds = settings.ScatterInputDebounceMilliseconds;
        MaxOffset = _maxOffset;
        TimeFrameOptions = ScatterMetricTimeFrames.Supported
            .Select(frame => new ScatterTimeFrameOption(frame, TimeFrameName(frame)))
            .ToList();
        _timeFrameFixedReason = _localization.GetString("ScatterMetric_Tip_TimeFrameFixedDay");

        _offeredItems = _catalogProvider.GetCatalogItems(_indicatorFactory).Where(ScatterMetricEligibility.IsEligible).ToList();
        Draft.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasDraft));
            RefreshModified();
        };

        BuildNavigation();

        IsBusy = true;
        InitializationTask = InitializeAsync();
    }

    // ===== State =====

    /// <summary>The toast service the window's overlay shows.</summary>
    public IToastNotificationService ToastService { get; }

    /// <summary>Completes when the confirmed list has been read into the draft and the controls are enabled again.</summary>
    public Task InitializationTask { get; }

    /// <summary>Completes when the search text typed last has been applied to the catalog list (an already completed task when none is pending).</summary>
    public Task PendingSearch { get; private set; } = Task.CompletedTask;

    /// <summary>Set by the window: closes it. The argument is whether the draft was confirmed with OK.</summary>
    public Action<bool>? RequestClose { get; set; }

    /// <summary>True once OK saved the draft.</summary>
    public bool Committed { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OkCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    /// <summary>The window's controls are usable.</summary>
    public bool IsIdle => !IsBusy;

    /// <summary>The draft differs from the confirmed list in the order of the metrics or in the content of one.</summary>
    [ObservableProperty]
    private bool _isModified;

    public decimal MinOffset => BacktestConditionOffsetRule.MinOffset;

    public decimal MaxOffset { get; }

    /// <summary>The time frames of a scatter metric (Day, Week, Month) with their localized names, as the combo box lists them.</summary>
    public IReadOnlyList<ScatterTimeFrameOption> TimeFrameOptions { get; }

    // One literal key per time frame, so the localization key scan sees each of them.
    private string TimeFrameName(TimeFrame frame) => frame switch
    {
        TimeFrame.D1 => _localization.GetString("Enum_TimeFrame_D1"),
        TimeFrame.W1 => _localization.GetString("Enum_TimeFrame_W1"),
        TimeFrame.MN1 => _localization.GetString("Enum_TimeFrame_MN1"),
        _ => throw new ArgumentOutOfRangeException(nameof(frame), frame, "A supported time frame has no display name."),
    };

    // ===== Navigation =====

    public ObservableCollection<ScatterMetricNavItem> NavItems { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActiveMode))]
    [NotifyPropertyChangedFor(nameof(IsCatalogMode))]
    private ScatterMetricNavItem? _selectedNavItem;

    public bool IsActiveMode => SelectedNavItem?.Kind == ScatterMetricNavKind.Active;

    public bool IsCatalogMode => SelectedNavItem?.Kind is ScatterMetricNavKind.AllFilters or ScatterMetricNavKind.Group;

    // ===== Catalog (center, catalog modes) =====

    public BulkObservableCollection<ScreenerCatalogItem> CatalogItems { get; } = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private ScreenerCatalogItem? _selectedCatalogItem;

    /// <summary>The catalog item chosen in a catalog mode, set up for adding; null until one is chosen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentEditor))]
    [NotifyPropertyChangedFor(nameof(ShowCatalogHint))]
    private ScatterMetricDraftItem? _pendingTarget;

    // ===== Draft (center, Active mode) =====

    public ObservableCollection<ScatterMetricDraftItem> Draft { get; } = new();

    public bool HasDraft => Draft.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentEditor))]
    [NotifyPropertyChangedFor(nameof(ShowActiveHint))]
    private ScatterMetricDraftItem? _selectedDraftItem;

    // ===== Detail (right) =====

    /// <summary>What the detail column edits: the selected draft row in Active mode, the chosen catalog item in a catalog mode.</summary>
    public ScatterMetricDraftItem? CurrentEditor => IsActiveMode ? SelectedDraftItem : IsCatalogMode ? PendingTarget : null;

    public bool HasEditor => CurrentEditor != null;

    public bool ShowActiveHint => IsActiveMode && SelectedDraftItem == null;

    public bool ShowCatalogHint => IsCatalogMode && PendingTarget == null;

    // ===== Initialization =====

    private async Task InitializeAsync()
    {
        try
        {
            await _registry.Loaded;
            _baseline = _registry.Snapshot();
            foreach (ScatterMetricDefinition definition in _baseline)
            {
                ScatterMetricDraftItem item = CreateItem(definition.Clone());
                item.Edited += OnItemEdited;
                Draft.Add(item);
            }

            if (_registry.TakeLoadDroppedCount() is int dropped and > 0)
            {
                ToastService.ShowNotification(Format("ScatterMetric_Msg_LoadDropped", dropped));
            }

            ScatterMetricNavKind initialKind = Draft.Count > 0 ? ScatterMetricNavKind.Active : ScatterMetricNavKind.AllFilters;
            SelectedNavItem = NavItems.First(n => n.Kind == initialKind);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ScatterMetricRegistration] The registered metrics could not be read into the draft");
        }
        finally
        {
            IsBusy = false;
            RefreshModified();
        }
    }

    private void BuildNavigation()
    {
        NavItems.Add(new ScatterMetricNavItem { Kind = ScatterMetricNavKind.Active, Title = _localization.GetString("ScatterMetric_Nav_Active") });
        NavItems.Add(new ScatterMetricNavItem { Kind = ScatterMetricNavKind.Divider });
        NavItems.Add(new ScatterMetricNavItem
        {
            Kind = ScatterMetricNavKind.AllFilters,
            Title = _localization.GetString("Nav_AllFilters"),
            Group = new ScreenerIndicatorGroup("All Filters", Array.Empty<IndicatorType>(), "All", "Nav_AllFilters"),
        });

        IReadOnlyList<ScreenerIndicatorGroup> groups = ScreenerIndicatorGroup.GetDefaultGroups();
        AddSection("IndicatorRegistration_NavHeader_Indicators", groups.Where(g => g.CategoryType == "Indicator"));
        AddSection("IndicatorRegistration_NavHeader_Columns", groups.Where(g => g.CategoryType == "Column"));

        void AddSection(string headerKey, IEnumerable<ScreenerIndicatorGroup> sectionGroups)
        {
            NavItems.Add(new ScatterMetricNavItem { Kind = ScatterMetricNavKind.Header, Title = _localization.GetString(headerKey) });
            foreach (ScreenerIndicatorGroup group in sectionGroups)
            {
                NavItems.Add(new ScatterMetricNavItem
                {
                    Kind = ScatterMetricNavKind.Group,
                    Group = group,
                    Title = group.LocalizationKey is { } key ? _localization.GetString(key) : group.Name,
                });
            }
        }
    }

    // ===== Navigation, search and catalog =====

    partial void OnSelectedNavItemChanged(ScatterMetricNavItem? value)
    {
        if (value is { IsSelectable: false })
        {
            // A section title or the divider cannot be chosen: keep the previous row.
            SelectedNavItem = _lastSelectableNav;
            return;
        }

        _lastSelectableNav = value;
        OnPropertyChanged(nameof(CurrentEditor));
        OnPropertyChanged(nameof(HasEditor));
        OnPropertyChanged(nameof(ShowActiveHint));
        OnPropertyChanged(nameof(ShowCatalogHint));
        if (IsCatalogMode)
        {
            ApplyFilters();
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
        _searchDebounce = new CancellationTokenSource();
        PendingSearch = DebounceSearchAsync(_searchDebounce.Token);
    }

    private async Task DebounceSearchAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(_debounceMilliseconds, token);
            if (!token.IsCancellationRequested && !_isDisposed)
            {
                ApplyFilters();
            }
        }
        catch (TaskCanceledException)
        {
            // A newer keystroke replaced this search.
        }
    }

    private void ApplyFilters()
    {
        IEnumerable<ScreenerCatalogItem> items = _offeredItems;
        ScatterMetricNavItem? nav = SelectedNavItem;

        if (nav is { Kind: ScatterMetricNavKind.Group, Group: { } group })
        {
            ScreenerItemCategoryType? category = Enum.TryParse(group.CategoryType, out ScreenerItemCategoryType parsed) ? parsed : null;
            items = items.Where(item =>
                (category == null || item.CategoryType == category.Value)
                && string.Equals(item.GroupName, group.Name, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            string text = SearchText.Trim();
            items = items.Where(item =>
                item.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase)
                || item.ShortName.Contains(text, StringComparison.OrdinalIgnoreCase));
        }

        List<ScreenerCatalogItem> ordered;
        if (nav?.Kind == ScatterMetricNavKind.AllFilters)
        {
            ordered = items.OrderBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.ShortName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        else if (nav?.Group is { } selected && string.Equals(selected.Name, ScreenerGroupNames.Price, StringComparison.OrdinalIgnoreCase))
        {
            Dictionary<string, int> priceOrder = PriceDataHelper.PriceTypeOptions
                .Select((type, index) => (Name: type.ToString(), index))
                .ToDictionary(x => x.Name, x => x.index, StringComparer.OrdinalIgnoreCase);
            ordered = items.OrderBy(i => priceOrder.TryGetValue(i.ShortName, out int order) ? order : int.MaxValue).ToList();
        }
        else
        {
            ordered = items.OrderBy(i => ScreenerGroupNames.GetGroupSortOrder(i.GroupName))
                .ThenBy(i => i.ShortName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        CatalogItems.ReplaceRange(ordered);
    }

    partial void OnSelectedCatalogItemChanged(ScreenerCatalogItem? value)
    {
        PendingTarget?.Dispose();
        PendingTarget = value is null ? null : CreateItem(value);
        OnPropertyChanged(nameof(CurrentEditor));
        OnPropertyChanged(nameof(HasEditor));
        AddCommand.NotifyCanExecuteChanged();
    }

    partial void OnPendingTargetChanged(ScatterMetricDraftItem? value) => OnPropertyChanged(nameof(HasEditor));

    partial void OnSelectedDraftItemChanged(ScatterMetricDraftItem? oldValue, ScatterMetricDraftItem? newValue)
    {
        if (oldValue != null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue != null)
        {
            newValue.IsSelected = true;
        }

        OnPropertyChanged(nameof(HasEditor));
    }

    // ===== Building draft items =====

    /// <summary>The editor for a catalog item, with the indicator's default parameters; null when the item has no usable default settings.</summary>
    private ScatterMetricDraftItem? CreateItem(ScreenerCatalogItem catalogItem)
    {
        bool isColumn = catalogItem.CategoryType == ScreenerItemCategoryType.Column;
        bool isPrice = !isColumn && catalogItem.IndicatorType == IndicatorType.Price;
        // The Screener stores a Column on the SMA type (the type is not read for a Column).
        IndicatorType type = catalogItem.IndicatorType ?? IndicatorType.SMA;

        CoreIndicatorSettings? settings = null;
        IReadOnlyList<string> outputs;
        if (isColumn || isPrice)
        {
            outputs = new[] { catalogItem.ShortName };
        }
        else
        {
            settings = _catalogProvider.GetDefaultSettings(type, _indicatorFactory);
            outputs = _catalogProvider.GetOutputSeriesNames(type, _indicatorFactory);
            if (settings is null || outputs.Count == 0)
            {
                return null;
            }
        }

        var source = new ScreenerIndicatorSideConfig
        {
            IndicatorType = type,
            OutputName = outputs[0],
            TimeFrame = TimeFrame.D1,
            Offset = 0,
            CategoryType = isColumn ? ScreenerItemCategoryType.Column : ScreenerItemCategoryType.Indicator,
            // The Screener names a Column or a price by its catalog description; the extractor looks the Column up by that name.
            CustomDisplayName = isColumn || isPrice ? catalogItem.DisplayName ?? catalogItem.ShortName ?? string.Empty : string.Empty,
        };
        if (settings?.ParameterObject != null)
        {
            foreach (KeyValuePair<string, object> pair in IndicatorRegistrationViewModel.BuildParameterDictionary(settings.ParameterObject))
            {
                source.Parameters[pair.Key] = pair.Value;
            }
        }

        return new ScatterMetricDraftItem(new ScatterMetricDefinition { Source = source }, settings, outputs, _maxOffset, catalogItem.DisplayName)
        {
            FixedTimeFrameReason = _timeFrameFixedReason,
        };
    }

    private ScatterMetricDraftItem CreateItem(ScatterMetricDefinition definition)
    {
        ScreenerIndicatorSideConfig source = definition.Source;
        CoreIndicatorSettings? settings = null;
        IReadOnlyList<string> outputs = new[] { source.OutputName };

        if (source.CategoryType == ScreenerItemCategoryType.Indicator && source.IndicatorType != IndicatorType.Price)
        {
            settings = _catalogProvider.GetDefaultSettings(source.IndicatorType, _indicatorFactory);
            IReadOnlyList<string> known = _catalogProvider.GetOutputSeriesNames(source.IndicatorType, _indicatorFactory);
            if (known.Count > 0)
            {
                outputs = known;
            }

            if (settings?.ParameterObject is { } parameters)
            {
                ScatterMetricDraftItem.ApplyParameters(parameters, source.Parameters);
            }
        }

        return new ScatterMetricDraftItem(definition, settings, outputs, _maxOffset)
        {
            FixedTimeFrameReason = _timeFrameFixedReason,
        };
    }

    private void OnItemEdited(object? sender, EventArgs e) => RefreshModified();

    private void RefreshModified()
        => IsModified = ScatterMetricRules.DiffersInIdOrContent(_baseline, Draft.Select(d => d.Definition).ToList());

    // ===== Commands: draft =====

    private bool CanAdd() => IsIdle && PendingTarget != null;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        if (PendingTarget is null)
        {
            return;
        }

        ScatterMetricDefinition definition = PendingTarget.Definition.Clone();
        definition.Id = Guid.NewGuid();
        string label = definition.Label;

        if (ScatterMetricRules.Validate(definition, _maxOffset, _indicatorFactory) != ScatterMetricInvalidReason.None)
        {
            ToastService.ShowNotification(Format("ScatterMetric_Msg_Invalid", label));
            return;
        }

        if (Draft.Count >= _maxMetrics)
        {
            ToastService.ShowNotification(Format("ScatterMetric_Msg_LimitReached", _maxMetrics));
            return;
        }

        if (Draft.Any(d => string.Equals(d.Definition.ContentKey, definition.ContentKey, StringComparison.Ordinal)))
        {
            ToastService.ShowNotification(Format("ScatterMetric_Msg_Duplicate", label));
            return;
        }

        ScatterMetricDraftItem item = CreateItem(definition);
        item.Edited += OnItemEdited;
        Draft.Add(item);
        ToastService.ShowNotification($"{label} {_localization.GetString("Msg_Added")}");
    }

    [RelayCommand]
    private void SelectDraftItem(ScatterMetricDraftItem? item)
    {
        if (item is null || Draft.Contains(item))
        {
            SelectedDraftItem = item;
        }
    }

    [RelayCommand]
    private void MoveUp(ScatterMetricDraftItem? item) => Move(item, -1);

    [RelayCommand]
    private void MoveDown(ScatterMetricDraftItem? item) => Move(item, +1);

    private void Move(ScatterMetricDraftItem? item, int direction)
    {
        if (item is null || IsBusy)
        {
            return;
        }

        int index = Draft.IndexOf(item);
        int target = index + direction;
        if (index < 0 || target < 0 || target >= Draft.Count)
        {
            return;
        }

        Draft.Move(index, target);
    }

    [RelayCommand]
    private void Remove(ScatterMetricDraftItem? item)
    {
        if (item is null || IsBusy || !Draft.Remove(item))
        {
            return;
        }

        if (SelectedDraftItem == item)
        {
            SelectedDraftItem = null;
        }

        item.Edited -= OnItemEdited;
        item.Dispose();
    }

    // ===== Commands: OK / Cancel =====

    private bool CanClose() => IsIdle;

    /// <summary>OK: saves the draft as the confirmed list and closes; a draft that equals the confirmed list closes without writing.</summary>
    [RelayCommand(CanExecute = nameof(CanClose))]
    private async Task OkAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (!IsModified)
        {
            Close(false);
            return;
        }

        IsBusy = true;
        bool closing = false;
        try
        {
            ScatterMetricRegistryResult result = await _registry.ReplaceAllAsync(Draft.Select(d => d.Definition.Clone()).ToList());
            switch (result)
            {
                case ScatterMetricRegistryResult.Ok:
                    Committed = true;
                    closing = true;
                    Close(true);
                    break;
                case ScatterMetricRegistryResult.PersistFailed:
                    ToastService.ShowNotification(_localization.GetString("ScatterMetric_Msg_PersistFailed"));
                    break;
                case ScatterMetricRegistryResult.LimitReached:
                    ToastService.ShowNotification(Format("ScatterMetric_Msg_LimitReached", _maxMetrics));
                    break;
                default:
                    ScatterMetricDraftItem? invalid = Draft.FirstOrDefault(d =>
                        ScatterMetricRules.Validate(d.Definition, _maxOffset, _indicatorFactory) != ScatterMetricInvalidReason.None);
                    ToastService.ShowNotification(Format("ScatterMetric_Msg_Invalid", invalid?.Label ?? string.Empty));
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ScatterMetricRegistration] Saving the draft failed");
            ToastService.ShowNotification(_localization.GetString("ScatterMetric_Msg_PersistFailed"));
        }
        finally
        {
            if (!closing)
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>Cancel (Esc, the window's close button): closes at once when nothing changed, otherwise after the user agrees to discard the draft.</summary>
    [RelayCommand(CanExecute = nameof(CanClose))]
    private async Task CancelAsync()
    {
        if (IsBusy || _isCancelling)
        {
            return;
        }

        if (!IsModified)
        {
            Close(false);
            return;
        }

        _isCancelling = true;
        try
        {
            bool discard = await _dialogService.ShowConfirmationAsync(
                _localization.GetString("Settings_DiscardConfirm_Title"),
                _localization.GetString("Settings_DiscardConfirm_Message"));
            if (discard)
            {
                Close(false);
            }
        }
        finally
        {
            _isCancelling = false;
        }
    }

    private void Close(bool committed) => RequestClose?.Invoke(committed);

    private string Format(string key, object argument) => string.Format(System.Globalization.CultureInfo.CurrentCulture, _localization.GetString(key), argument);

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
        PendingTarget?.Dispose();
        foreach (ScatterMetricDraftItem item in Draft)
        {
            item.Edited -= OnItemEdited;
            item.Dispose();
        }
    }
}
