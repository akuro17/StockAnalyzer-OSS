using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs.Common;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>
/// ViewModel for the main Indicator Settings dialog.
/// </summary>
public partial class IndicatorSettingsDialogViewModel : ViewModelBase, IRecipient<SingleIndicatorSettingsChangedMessage>, IDisposable
{
    private readonly ILogger<IndicatorSettingsDialogViewModel> _logger;
    private readonly TemplateCrudHelper<IndicatorTemplate> _templateCrud;
    private bool _isDisposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTemplatesCategory))]
    [NotifyPropertyChangedFor(nameof(IsNotTemplatesCategory))]
    private bool _isTemplatesSelected;

    [ObservableProperty]
    private IndicatorTemplate? _selectedTemplate;

    [ObservableProperty]
    private string _newTemplateName = string.Empty;

    public ObservableCollection<IndicatorTemplate> Templates { get; } = new();
    public ObservableCollection<string> SelectedTemplateIndicatorNames { get; } = new();

    public bool IsTemplatesCategory => IsLibraryMode && IsTemplatesSelected;
    public bool IsNotTemplatesCategory => !IsLibraryMode || !IsTemplatesSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPriceCategory))]
    [NotifyPropertyChangedFor(nameof(IsNotTemplatesCategory))]
    private bool _isPriceSelected;

    public bool IsPriceCategory => IsLibraryMode && IsPriceSelected;

    /// <summary>
    /// The 15 price types, shown in the "Price" category in standard PriceType order.
    /// </summary>
    public static IReadOnlyList<IndicatorCatalogItem> PriceCatalogItems { get; } =
        PriceDataHelper.PriceTypeOptions.Select(t => new IndicatorCatalogItem
        {
            Type = IndicatorType.Price,
            PriceType = t,
            ShortName = t.ToString(),
            DisplayName = PriceDataHelper.FormatPriceTypeLabel(t),
            Category = CoreIndicatorCategory.Other
        }).ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowThicknessAndOffset))]
    [NotifyPropertyChangedFor(nameof(ShowUpDownColors))]
    [NotifyPropertyChangedFor(nameof(ShowPanelSelector))]
    [NotifyPropertyChangedFor(nameof(SelectedOverlayPanelOption))]
    [NotifyPropertyChangedFor(nameof(SelectedSourceIndicatorOption))]
    [NotifyPropertyChangedFor(nameof(SelectedDynamicPeriodDriverOption))]
    [NotifyPropertyChangedFor(nameof(HasDynamicPeriodDriver))]
    [NotifyPropertyChangedFor(nameof(HiddenParameterTags))]
    [NotifyPropertyChangedFor(nameof(SupportsDynamicPeriod))]
    private CoreIndicatorSettings? _selectedIndicator;

    partial void OnSelectedIndicatorChanged(CoreIndicatorSettings? value)
    {
        UpdateReferenceOptions();
    }

    /// <summary>
    /// Available source indicators that can be chained as input series (excluding self).
    /// </summary>
    public ObservableCollection<IndicatorReferenceOption> AvailableSourceIndicators { get; } = new();

    /// <summary>
    /// Available dynamic period drivers (excluding self).
    /// </summary>
    public ObservableCollection<IndicatorReferenceOption> AvailableDynamicPeriodDrivers { get; } = new();

    /// <summary>
    /// Guards SelectedSourceIndicatorOption/SelectedDynamicPeriodDriverOption from being overwritten
    /// by the transient null SelectedItem that SelectingItemsControl reports while UpdateReferenceOptions()
    /// clears and repopulates the bound ObservableCollection (see UpdateReferenceOptions remarks).
    /// </summary>
    private bool _isRefreshingReferenceOptions;

    public IndicatorReferenceOption? SelectedSourceIndicatorOption
    {
        get
        {
            if (SelectedIndicator == null || string.IsNullOrEmpty(SelectedIndicator.SourceIndicatorId))
                return AvailableSourceIndicators.FirstOrDefault();
            return AvailableSourceIndicators.FirstOrDefault(o => o.Id == SelectedIndicator.SourceIndicatorId)
                   ?? AvailableSourceIndicators.FirstOrDefault();
        }
        set
        {
            if (_isRefreshingReferenceOptions) return;
            if (SelectedIndicator != null)
            {
                SelectedIndicator.SourceIndicatorId = value?.Id;

                if (value != null && !string.IsNullOrEmpty(value.Id))
                {
                    if (!value.IsOverlay)
                    {
                        // Source indicator is in sub-window: switch target indicator to sub-window
                        SelectedIndicator.IsOverlay = false;
                        SelectedIndicator.OverlayPanelId = value.OverlayPanelId;
                    }
                    else
                    {
                        // Source indicator is in main window: restore default IsOverlay
                        if (SelectedIndicator.TypeEnum.HasValue)
                        {
                            var defaultSettings = GetEffectiveDefaultSettings(SelectedIndicator.TypeEnum.Value);
                            SelectedIndicator.IsOverlay = defaultSettings.IsOverlay;
                        }
                    }
                }
                else
                {
                    // Reverted to price/default: restore native IsOverlay
                    if (SelectedIndicator.TypeEnum.HasValue)
                    {
                        var defaultSettings = GetEffectiveDefaultSettings(SelectedIndicator.TypeEnum.Value);
                        SelectedIndicator.IsOverlay = defaultSettings.IsOverlay;
                    }
                }

                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedOverlayPanelOption));
                OnPropertyChanged(nameof(ShowPanelSelector));
            }
        }
    }

    public IndicatorReferenceOption? SelectedDynamicPeriodDriverOption
    {
        get
        {
            if (SelectedIndicator == null || string.IsNullOrEmpty(SelectedIndicator.DynamicPeriodIndicatorId))
                return AvailableDynamicPeriodDrivers.FirstOrDefault();
            return AvailableDynamicPeriodDrivers.FirstOrDefault(o => o.Id == SelectedIndicator.DynamicPeriodIndicatorId)
                   ?? AvailableDynamicPeriodDrivers.FirstOrDefault();
        }
        set
        {
            if (_isRefreshingReferenceOptions) return;
            if (SelectedIndicator != null)
            {
                SelectedIndicator.DynamicPeriodIndicatorId = value?.Id;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasDynamicPeriodDriver));
                OnPropertyChanged(nameof(HiddenParameterTags));
            }
        }
    }

    /// <summary>
    /// Computed pass-through over SelectedIndicator.DynamicPeriodIndicatorId, exposed as a checkbox-friendly
    /// boolean. Turning it on auto-selects the first available driver (if any); turning it off clears the
    /// driver id (reverting SelectedDynamicPeriodDriverOption to the "(None / Static Parameter)" sentinel).
    /// No independent state is stored, so this can never desync from the combobox selection.
    /// </summary>
    public bool HasDynamicPeriodDriver
    {
        get => DynamicPeriodDriverHelper.GetHasDynamicPeriodDriver(SelectedIndicator);
        set
        {
            if (SelectedIndicator == null) return;

            bool changed = DynamicPeriodDriverHelper.TrySetHasDynamicPeriodDriver(
                SelectedIndicator, AvailableDynamicPeriodDrivers, value);

            OnPropertyChanged();
            if (changed)
            {
                OnPropertyChanged(nameof(SelectedDynamicPeriodDriverOption));
                OnPropertyChanged(nameof(HiddenParameterTags));
            }
        }
    }

    /// <summary>
    /// Parameter tags to hide in the reflection-based settings panel for the selected indicator,
    /// derived from HasDynamicPeriodDriver. Always empty (see
    /// DynamicPeriodDriverHelper.GetHiddenParameterTags); kept as an extensibility point for
    /// ParameterViewBuilder.
    /// </summary>
    public IReadOnlyCollection<string> HiddenParameterTags =>
        DynamicPeriodDriverHelper.GetHiddenParameterTags(HasDynamicPeriodDriver);

    /// <summary>
    /// Whether the currently selected indicator's parameter object supports dynamic period driver.
    /// Used as capability gate to show/hide the dynamic period driver checkbox and dropdown.
    /// </summary>
    public bool SupportsDynamicPeriod =>
        DynamicPeriodDriverHelper.GetSupportsDynamicPeriod(SelectedIndicator?.ParameterObject);

    /// <summary>
    /// Rebuilds AvailableSourceIndicators/AvailableDynamicPeriodDrivers for the current SelectedIndicator.
    /// PopulateReferenceOptions() clears the ObservableCollections before re-adding items; while cleared,
    /// the ComboBoxes bound to them (SelectedItem, Mode=TwoWay) transiently report a null selection, which
    /// would otherwise flow back through SelectedSourceIndicatorOption/SelectedDynamicPeriodDriverOption and
    /// null out SourceIndicatorId/DynamicPeriodIndicatorId on the indicator now selected. _isRefreshingReferenceOptions
    /// suppresses those setters for the duration of the rebuild so only genuine user selections can write through.
    /// </summary>
    public void UpdateReferenceOptions()
    {
        _isRefreshingReferenceOptions = true;
        try
        {
            var registeredSources = _sourceIndicatorService?.GetSourceIndicators();
            var registeredDrivers = _dynamicPeriodDriverService?.GetDynamicPeriodDrivers();
            IndicatorReferenceHelper.PopulateReferenceOptions(
                AvailableSourceIndicators,
                AvailableDynamicPeriodDrivers,
                Indicators,
                SelectedIndicator?.Id,
                SelectedIndicator?.SourceIndicatorId,
                SelectedIndicator?.DynamicPeriodIndicatorId,
                registeredSources,
                registeredDrivers);
        }
        finally
        {
            _isRefreshingReferenceOptions = false;
        }

        OnPropertyChanged(nameof(SelectedSourceIndicatorOption));
        OnPropertyChanged(nameof(SelectedDynamicPeriodDriverOption));
        OnPropertyChanged(nameof(HasDynamicPeriodDriver));
        OnPropertyChanged(nameof(HiddenParameterTags));
        OnPropertyChanged(nameof(SupportsDynamicPeriod));
    }

    public bool ShowThicknessAndOffset => SelectedIndicator?.TypeEnum != IndicatorType.GranvilleLaw;
    public bool ShowUpDownColors => SelectedIndicator?.TypeEnum != IndicatorType.GranvilleLaw;

    /// <summary>
    /// Panel selector is visible only for non-overlay, non-GranvilleLaw indicators.
    /// </summary>
    public bool ShowPanelSelector => SelectedIndicator != null
        && !SelectedIndicator.IsOverlay
        && SelectedIndicator.TypeEnum != IndicatorType.GranvilleLaw
        && SelectedIndicator.TypeEnum != IndicatorType.VolumeProfile;

    public bool ShowSignals => SelectedIndicator != null &&
        (SelectedIndicator.SeriesColors.Any(c => c.TargetSeries.Contains("Signals")) ||
         SelectedIndicator.TypeEnum == IndicatorType.MACD ||
         SelectedIndicator.TypeEnum == IndicatorType.NATR);

    [ObservableProperty]
    private bool? _dialogResult;

    /// <summary>
    /// True when the Library tab is active; false when Active tab is active.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActiveMode))]
    [NotifyPropertyChangedFor(nameof(IsTemplatesCategory))]
    [NotifyPropertyChangedFor(nameof(IsNotTemplatesCategory))]
    private bool _isLibraryMode;

    /// <summary>
    /// Computed inverse of IsLibraryMode for Active tab visibility.
    /// </summary>
    public bool IsActiveMode => !IsLibraryMode;

    /// <summary>
    /// Search text for filtering the library catalog.
    /// </summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>
    /// Currently selected category filter in the Library tab. Null means "All".
    /// </summary>
    [ObservableProperty]
    private CoreIndicatorCategory? _selectedCategory;

    public Action<bool?>? RequestClose { get; set; }
    public Action<IEnumerable<CoreIndicatorSettings>>? OnApplyCallback { get; set; }

    public IToastNotificationService ToastService { get; }

    public ObservableCollection<CoreIndicatorSettings> Indicators { get; } = new();

    /// <summary>
    /// All available indicator catalog items for the Library grid.
    /// </summary>
    public ObservableCollection<IndicatorCatalogItem> AllCatalogItems { get; } = new();

    /// <summary>
    /// Filtered catalog items based on search text and selected category.
    /// </summary>
    public ObservableCollection<IndicatorCatalogItem> FilteredCatalogItems { get; } = new();

    /// <summary>
    /// Currently selected catalog item in Library mode.
    /// </summary>
    [ObservableProperty]
    private IndicatorCatalogItem? _selectedCatalogItem;

    /// <summary>
    /// Editable default settings for the selected catalog item in Library mode.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLibraryUpDownColors))]
    private CoreIndicatorSettings? _selectedLibraryIndicatorSettings;

    /// <summary>
    /// When true, displays indicator names in short format (e.g. SMA(20)). Default false (Long format).
    /// </summary>
    [ObservableProperty]
    private bool _useShortName;

    /// <summary>
    /// Live preview name displayed in the Library right configuration header.
    /// </summary>
    [ObservableProperty]
    private string _libraryIndicatorPreviewName = string.Empty;

    public bool ShowLibraryUpDownColors => SelectedLibraryIndicatorSettings?.TypeEnum != IndicatorType.GranvilleLaw;

    /// <summary>
    /// All available categories for the accordion filter.
    /// </summary>
    public ObservableCollection<CoreIndicatorCategory> Categories { get; } = new();

    /// <summary>
    /// Panel selection options for the overlay panel ComboBox.
    /// Index 0 = Default (own panel), 1-6 = Panel A through F.
    /// </summary>
    public List<string> PanelOptions { get; } = BuildPanelOptions();

    /// <summary>
    /// Gets/sets the selected overlay panel option for the currently selected indicator.
    /// Maps between the ComboBox display string and CoreIndicatorSettings.OverlayPanelId.
    /// </summary>
    public string SelectedOverlayPanelOption
    {
        get
        {
            var id = SelectedIndicator?.OverlayPanelId;
            if (string.IsNullOrEmpty(id)) return PanelOptions[0]; // Default
            var match = PanelOptions.FirstOrDefault(o => o.EndsWith(id));
            return match ?? PanelOptions[0];
        }
        set
        {
            if (SelectedIndicator == null) return;
            if (value == PanelOptions[0])
            {
                SelectedIndicator.OverlayPanelId = null;
            }
            else
            {
                // Extract the panel letter from "Panel A", "Panel B", etc.
                var parts = value.Split(' ');
                SelectedIndicator.OverlayPanelId = parts.Length > 1 ? parts[^1] : value;
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowSignals));
        }
    }

    private static List<string> BuildPanelOptions()
    {
        var defaultLabel = LocalizationManager.Instance["Panel_Default"];
        var prefix = LocalizationManager.Instance["Panel_GroupPrefix"];
        return new List<string>
        {
            defaultLabel,
            $"{prefix} A",
            $"{prefix} B",
            $"{prefix} C",
            $"{prefix} D",
            $"{prefix} E",
            $"{prefix} F"
        };
    }

    /// <summary>
    /// Available price types for price source selection.
    /// Ordered as Open, High, Low, Close, Median, Typical, Weighted via PriceDataHelper.
    /// </summary>
    public IReadOnlyList<PriceType> PriceTypeOptions { get; } = PriceDataHelper.PriceTypeOptions;

    private readonly IDialogService _dialogService;
    private readonly IIndicatorFactory _indicatorFactory;
    private readonly IIndicatorUserDefaultService _userDefaultService;
    private readonly ISourceIndicatorService? _sourceIndicatorService;
    private readonly IDynamicPeriodDriverService? _dynamicPeriodDriverService;
    private readonly Dictionary<IndicatorType, CoreIndicatorSettings> _systemDefaultSettingsCache = new();
    private readonly Dictionary<IndicatorType, CoreIndicatorSettings> _userDefaultSettingsCache = new();

    public IndicatorSettingsDialogViewModel(
        IDialogService dialogService, 
        IIndicatorFactory indicatorFactory, 
        IToastNotificationService toastService,
        ITemplateService templateService,
        IIndicatorUserDefaultService? userDefaultService = null,
        ISourceIndicatorService? sourceIndicatorService = null,
        ILogger<IndicatorSettingsDialogViewModel>? logger = null,
        IDynamicPeriodDriverService? dynamicPeriodDriverService = null)
    {
        ArgumentNullException.ThrowIfNull(dialogService);
        ArgumentNullException.ThrowIfNull(indicatorFactory);
        ArgumentNullException.ThrowIfNull(toastService);
        ArgumentNullException.ThrowIfNull(templateService);

        _dialogService = dialogService;
        _indicatorFactory = indicatorFactory;
        ToastService = toastService;
        _templateCrud = new TemplateCrudHelper<IndicatorTemplate>(templateService, toastService, TemplateType.Indicator);
        _userDefaultService = userDefaultService ?? new IndicatorUserDefaultService();
        _sourceIndicatorService = sourceIndicatorService;
        _dynamicPeriodDriverService = dynamicPeriodDriverService;
        _logger = logger ?? NullLogger<IndicatorSettingsDialogViewModel>.Instance;
        
        WeakReferenceMessenger.Default.Register<SingleIndicatorSettingsChangedMessage>(this);

        // Build the dynamic system default settings cache
        var staticDefaults = DefaultCoreIndicatorSettings.GetDefault();
        foreach (var type in _indicatorFactory.GetRegisteredTypes())
        {
            var oldDefault = staticDefaults.FirstOrDefault(s => s.TypeEnum == type);
            if (oldDefault != null)
            {
                _systemDefaultSettingsCache[type] = oldDefault;
            }
            else
            {
                var inst = _indicatorFactory.Create(type);
                if (inst != null)
                {
                    var settings = inst.GetDefaultSettings();
                    settings.TypeEnum = type; // ensure TypeEnum is set explicitly
                    _systemDefaultSettingsCache[type] = settings;
                }
            }
        }

        // Load user defaults from persistent storage
        try
        {
            var loadedUserDefaults = _userDefaultService.LoadUserDefaults();
            foreach (var kvp in loadedUserDefaults)
            {
                _userDefaultSettingsCache[kvp.Key] = kvp.Value;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load indicator user defaults on initialization");
        }

        BuildCatalog();

        // Load templates asynchronously
        _ = LoadTemplatesAsync();
    }

    public CoreIndicatorSettings GetEffectiveDefaultSettings(IndicatorType type)
    {
        if (_userDefaultSettingsCache.TryGetValue(type, out var userDefault))
        {
            return userDefault.Clone();
        }
        if (_systemDefaultSettingsCache.TryGetValue(type, out var sysDefault))
        {
            return sysDefault.Clone();
        }
        return new CoreIndicatorSettings { TypeEnum = type, IsEnabled = true };
    }

    public CoreIndicatorSettings GetSystemDefaultSettings(IndicatorType type)
    {
        if (_systemDefaultSettingsCache.TryGetValue(type, out var sysDefault))
        {
            return sysDefault.Clone();
        }
        return new CoreIndicatorSettings { TypeEnum = type, IsEnabled = true };
    }

    public void Initialize(IEnumerable<CoreIndicatorSettings> currentIndicators)
    {
        if (_isDisposed) return;
        Indicators.Clear();
        if (currentIndicators != null)
        {
            foreach (var indicator in currentIndicators.ToList())
            {
                // Deep clone to isolate from main window until Apply/Save
                var clone = indicator.Clone();
                clone.Id = indicator.Id; // Prevent duplicate IDs but match the original for single-indicator apply
                DefaultCoreIndicatorSettings.AutoHeal(clone);
                if (clone.TypeEnum == IndicatorType.Price && (string.IsNullOrEmpty(clone.DisplayName) || clone.DisplayName == "Price"))
                {
                    clone.UpdateDisplayName();
                }
                Indicators.Add(clone);
            }
        }
        
        if (Indicators.Any())
        {
            SelectedIndicator = Indicators.First();
        }

        UpdateReferenceOptions();

        // Start in Active mode
        IsLibraryMode = false;
    }

    /// <summary>
    /// Builds the full catalog of all available indicators from the factory cache,
    /// so new plugins automatically appear.
    /// </summary>
    private void BuildCatalog()
    {
        AllCatalogItems.Clear();
        Categories.Clear();

        var tempItems = new List<IndicatorCatalogItem>();

        foreach (var kvp in _systemDefaultSettingsCache)
        {
            var type = kvp.Key;
            var settings = kvp.Value;
            if (type == IndicatorType.Price) continue;

            tempItems.Add(new IndicatorCatalogItem
            {
                Type = type,
                DisplayName = type.GetDescription(),
                ShortName = type.ToString(),
                Category = settings.Category,
            });
        }

        // Sort alphabetically by ShortName for better searchability
        foreach (var item in tempItems.OrderBy(i => i.ShortName))
        {
            AllCatalogItems.Add(item);
        }

        // Populate unique categories in enum order
        var uniqueCategories = AllCatalogItems
            .Select(item => item.Category)
            .Distinct()
            .OrderBy(c => c);

        foreach (var category in uniqueCategories)
        {
            Categories.Add(category);
        }

        // Show all items initially
        ApplyFilters();
    }

    /// <summary>
    /// Applies both search text and category filters to the catalog.
    /// </summary>
    private void ApplyFilters()
    {
        if (_isDisposed) return;
        FilteredCatalogItems.Clear();

        IEnumerable<IndicatorCatalogItem> filtered;

        if (IsPriceSelected)
        {
            filtered = PriceCatalogItems;
        }
        else
        {
            filtered = AllCatalogItems;
            if (SelectedCategory.HasValue)
            {
                filtered = filtered.Where(item => item.Category == SelectedCategory.Value);
            }
        }

        // Filter by search text
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var searchText = SearchText.Trim();
            filtered = filtered.Where(item =>
                item.DisplayName.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                item.ShortName.Contains(searchText, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var item in filtered)
        {
            FilteredCatalogItems.Add(item);
        }

        if (SelectedCatalogItem == null || !FilteredCatalogItems.Contains(SelectedCatalogItem))
        {
            SelectedCatalogItem = FilteredCatalogItems.FirstOrDefault();
        }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilters();

    partial void OnSelectedCatalogItemChanged(IndicatorCatalogItem? value)
    {
        if (value == null)
        {
            if (SelectedLibraryIndicatorSettings?.ParameterObject != null)
            {
                SelectedLibraryIndicatorSettings.ParameterObject.PropertyChanged -= OnLibraryParameterChanged;
            }
            SelectedLibraryIndicatorSettings = null;
            LibraryIndicatorPreviewName = string.Empty;
            return;
        }

        if (SelectedLibraryIndicatorSettings?.ParameterObject != null)
        {
            SelectedLibraryIndicatorSettings.ParameterObject.PropertyChanged -= OnLibraryParameterChanged;
        }

        SelectedLibraryIndicatorSettings = GetEffectiveDefaultSettings(value.Type);

        if (value.Type == IndicatorType.Price && value.PriceType.HasValue)
        {
            SelectedLibraryIndicatorSettings.PriceSource = value.PriceType.Value;
            SelectedLibraryIndicatorSettings.DisplayName = value.DisplayName;
        }

        if (SelectedLibraryIndicatorSettings.ParameterObject != null)
        {
            SelectedLibraryIndicatorSettings.ParameterObject.PropertyChanged += OnLibraryParameterChanged;
        }

        UpdateLibraryIndicatorPreviewName();
    }

    partial void OnUseShortNameChanged(bool value)
    {
        UpdateLibraryIndicatorPreviewName();
    }

    private void OnLibraryParameterChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        UpdateLibraryIndicatorPreviewName();
    }

    public void UpdateLibraryIndicatorPreviewName()
    {
        if (SelectedCatalogItem == null || SelectedLibraryIndicatorSettings == null)
        {
            LibraryIndicatorPreviewName = string.Empty;
            return;
        }

        if (SelectedCatalogItem.Type == IndicatorType.Price)
        {
            LibraryIndicatorPreviewName = UseShortName
                ? SelectedCatalogItem.ShortName
                : SelectedCatalogItem.DisplayName;
            return;
        }

        string baseName = UseShortName
            ? SelectedCatalogItem.ShortName
            : SelectedCatalogItem.DisplayName;

        if (SelectedLibraryIndicatorSettings.ParameterObject != null)
        {
            string paramDisplayName = SelectedLibraryIndicatorSettings.ParameterObject.GetDisplayName(baseName);
            if (UseShortName)
            {
                LibraryIndicatorPreviewName = paramDisplayName.Replace(" (", "(");
            }
            else
            {
                LibraryIndicatorPreviewName = paramDisplayName;
            }
        }
        else
        {
            LibraryIndicatorPreviewName = baseName;
        }
    }
    
    partial void OnIsLibraryModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsTemplatesCategory));
        OnPropertyChanged(nameof(IsNotTemplatesCategory));
        OnPropertyChanged(nameof(IsPriceCategory));
    }

    partial void OnSelectedCategoryChanged(CoreIndicatorCategory? value)
    {
        if (value != null)
        {
            IsTemplatesSelected = false;
            IsPriceSelected = false;
        }
        ApplyFilters();
        OnPropertyChanged(nameof(IsTemplatesCategory));
        OnPropertyChanged(nameof(IsNotTemplatesCategory));
        OnPropertyChanged(nameof(IsPriceCategory));
    }

    partial void OnIsTemplatesSelectedChanged(bool value)
    {
        if (value)
        {
            SelectedCategory = null;
            IsPriceSelected = false;
        }
        OnPropertyChanged(nameof(IsTemplatesCategory));
        OnPropertyChanged(nameof(IsNotTemplatesCategory));
        OnPropertyChanged(nameof(IsPriceCategory));
    }

    partial void OnSelectedTemplateChanged(IndicatorTemplate? value) => RefreshSelectedTemplatePreview();

    /// <summary>
    /// Rebuilds <see cref="SelectedTemplateIndicatorNames"/> from <see cref="SelectedTemplate"/>.
    /// Called on selection change and again after an in-place overwrite save of the selected
    /// template, whose contents change without the selection reference changing.
    /// </summary>
    private void RefreshSelectedTemplatePreview()
    {
        SelectedTemplateIndicatorNames.Clear();
        if (SelectedTemplate is not { } value)
        {
            return;
        }

        foreach (var ind in value.Indicators)
        {
            string name = ind.DisplayName;
            if (ind.ParameterObject != null)
            {
                string shortName = ind.TypeEnum.HasValue ? ind.TypeEnum.Value.ToString() : "Unknown";
                name = $"{name} ({ind.ParameterObject.GetDisplayName(shortName)})";
            }
            SelectedTemplateIndicatorNames.Add(name);
        }
    }

    [RelayCommand]
    private void SwitchToActive()
    {
        IsLibraryMode = false;
    }

    [RelayCommand]
    private void SwitchToLibrary()
    {
        IsLibraryMode = true;
    }

    [RelayCommand]
    private void SelectPrice()
    {
        IsTemplatesSelected = false;
        SelectedCategory = null;
        IsPriceSelected = true;
        ApplyFilters();
        OnPropertyChanged(nameof(IsTemplatesCategory));
        OnPropertyChanged(nameof(IsNotTemplatesCategory));
        OnPropertyChanged(nameof(IsPriceCategory));
    }

    [RelayCommand]
    private void SelectCategory(CoreIndicatorCategory? category)
    {
        IsPriceSelected = false;
        IsTemplatesSelected = false;
        // Toggle: if same category is clicked again, clear the filter
        SelectedCategory = SelectedCategory == category ? null : category;
    }

    [RelayCommand]
    private void ClearCategoryFilter()
    {
        IsTemplatesSelected = false;
        IsPriceSelected = false;
        SelectedCategory = null;
        ApplyFilters();
        OnPropertyChanged(nameof(IsTemplatesCategory));
        OnPropertyChanged(nameof(IsNotTemplatesCategory));
        OnPropertyChanged(nameof(IsPriceCategory));
    }

    /// <summary>
    /// Adds an indicator from the Library catalog.
    /// </summary>
    [RelayCommand]
    private void AddFromLibrary(IndicatorCatalogItem? catalogItem)
    {
        if (catalogItem == null) return;

        if (catalogItem.Type == IndicatorType.Price)
        {
            AddSelectedLibraryIndicator();
            return;
        }

        // If catalog item matches selected item, add with current configured settings
        if (SelectedCatalogItem != null && SelectedCatalogItem.Type == catalogItem.Type && SelectedLibraryIndicatorSettings != null)
        {
            AddSelectedLibraryIndicator();
        }
        else
        {
            AddIndicator(catalogItem.Type.ToString());
        }
    }

    /// <summary>
    /// Adds the currently configured library indicator to the Active tab with current parameters, colors, and name format.
    /// </summary>
    [RelayCommand]
    private void AddSelectedLibraryIndicator()
    {
        if (SelectedLibraryIndicatorSettings == null || SelectedCatalogItem == null) return;

        var newSettings = SelectedLibraryIndicatorSettings.Clone();
        newSettings.IsEnabled = true;
        if (SelectedCatalogItem.Type == IndicatorType.Price && SelectedCatalogItem.PriceType.HasValue)
        {
            newSettings.PriceSource = SelectedCatalogItem.PriceType.Value;
        }
        newSettings.DisplayName = LibraryIndicatorPreviewName;

        Indicators.Add(newSettings);
        SelectedIndicator = newSettings;

        ToastService.ShowNotification($"{newSettings.DisplayName} {LocalizationManager.Instance["Msg_Added"] ?? "Added"}");
        Apply();
    }

    /// <summary>
    /// Saves the current configuration as the User Default preset for this indicator type.
    /// </summary>
    [RelayCommand]
    private async Task SetAsDefaultAsync()
    {
        if (SelectedLibraryIndicatorSettings == null || SelectedCatalogItem == null) return;

        var userDefault = SelectedLibraryIndicatorSettings.Clone();
        userDefault.DisplayName = LibraryIndicatorPreviewName;
        userDefault.IsEnabled = true;
        if (SelectedCatalogItem.Type == IndicatorType.Price && SelectedCatalogItem.PriceType.HasValue)
        {
            userDefault.PriceSource = SelectedCatalogItem.PriceType.Value;
        }

        _userDefaultSettingsCache[SelectedCatalogItem.Type] = userDefault;
        await _userDefaultService.SaveUserDefaultAsync(userDefault);

        var msg = LocalizationManager.Instance["Msg_SavedAsDefault"] ?? "saved as default setting";
        ToastService.ShowNotification($"{userDefault.DisplayName} {msg}");
    }

    /// <summary>
    /// Resets the current library indicator's settings and colors to effective defaults.
    /// </summary>
    [RelayCommand]
    private void ResetLibraryIndicatorSettings()
    {
        if (SelectedCatalogItem == null) return;

        if (SelectedLibraryIndicatorSettings?.ParameterObject != null)
        {
            SelectedLibraryIndicatorSettings.ParameterObject.PropertyChanged -= OnLibraryParameterChanged;
        }

        SelectedLibraryIndicatorSettings = GetEffectiveDefaultSettings(SelectedCatalogItem.Type);

        if (SelectedLibraryIndicatorSettings.ParameterObject != null)
        {
            SelectedLibraryIndicatorSettings.ParameterObject.PropertyChanged += OnLibraryParameterChanged;
        }

        UpdateLibraryIndicatorPreviewName();
    }

    [RelayCommand]
    private void AddIndicator(string typeName)
    {
        if (Enum.TryParse<IndicatorType>(typeName, out var type))
        {
            var newSettings = GetEffectiveDefaultSettings(type);
            newSettings.IsEnabled = true; // Enable it so it renders
            newSettings.UpdateDisplayName();

            Indicators.Add(newSettings);
            SelectedIndicator = newSettings;
            
            ToastService.ShowNotification($"{newSettings.DisplayName} {LocalizationManager.Instance["Msg_Added"] ?? "Added"}");
            
            // Auto-apply immediately to Main Window when added from library
            Apply();
        }
    }


    [RelayCommand]
    private void RemoveIndicator(CoreIndicatorSettings? settings)
    {
        if (settings != null && Indicators.Contains(settings))
        {
            Indicators.Remove(settings);
            if (SelectedIndicator == settings)
            {
                SelectedIndicator = Indicators.FirstOrDefault();
            }
        }
    }

    [RelayCommand]
    private void TurnOffIndicator(CoreIndicatorSettings? settings)
    {
        if (settings != null)
        {
            settings.IsEnabled = false;
        }
    }

    [RelayCommand]
    private void TurnOffAllIndicators()
    {
        foreach (var ind in Indicators)
        {
            ind.IsEnabled = false;
        }
    }

    [RelayCommand]
    private void RemoveAllIndicators()
    {
        Indicators.Clear();
        SelectedIndicator = null;
    }

    [RelayCommand]
    private void Save()
    {
        if (_isDisposed) return;
        if (OnApplyCallback != null)
        {
            OnApplyCallback.Invoke(Indicators.ToList());
        }
        else
        {
            // Fallback: Send message to update Main Window
            WeakReferenceMessenger.Default.Send(new IndicatorSettingsChangedMessage(Indicators.ToList()));
        }
        
        DialogResult = true;
        RequestClose?.Invoke(true);
    }

    [RelayCommand]
    private void Apply()
    {
        if (_isDisposed) return;
        if (OnApplyCallback != null)
        {
            OnApplyCallback.Invoke(Indicators.ToList());
        }
        else
        {
            // Fallback: Send message to update Main Window WITHOUT closing
            WeakReferenceMessenger.Default.Send(new IndicatorSettingsChangedMessage(Indicators.ToList()));
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        DialogResult = false;
        RequestClose?.Invoke(false);
    }

    [RelayCommand]
    private async Task ResetAsync()
    {
        if (IsActiveMode)
        {
            if (SelectedIndicator == null || SelectedIndicator.TypeEnum == null) return;
            
            var sysDefault = GetSystemDefaultSettings(SelectedIndicator.TypeEnum.Value);
            var index = Indicators.IndexOf(SelectedIndicator);
            if (index >= 0)
            {
                sysDefault.IsEnabled = true;
                sysDefault.UpdateDisplayName();
                
                Indicators[index] = sysDefault;
                SelectedIndicator = sysDefault;

                var msg = LocalizationManager.Instance["Msg_ResetToSystemDefault"] ?? "Reset to system default";
                ToastService.ShowNotification(msg);
            }
        }
        else
        {
            if (SelectedCatalogItem == null) return;

            _userDefaultSettingsCache.Remove(SelectedCatalogItem.Type);
            await _userDefaultService.ResetToSystemDefaultAsync(SelectedCatalogItem.Type);

            if (SelectedLibraryIndicatorSettings?.ParameterObject != null)
            {
                SelectedLibraryIndicatorSettings.ParameterObject.PropertyChanged -= OnLibraryParameterChanged;
            }

            SelectedLibraryIndicatorSettings = GetSystemDefaultSettings(SelectedCatalogItem.Type);

            if (SelectedLibraryIndicatorSettings.ParameterObject != null)
            {
                SelectedLibraryIndicatorSettings.ParameterObject.PropertyChanged += OnLibraryParameterChanged;
            }

            UpdateLibraryIndicatorPreviewName();

            var msg = LocalizationManager.Instance["Msg_ResetToSystemDefault"] ?? "Reset to system default";
            ToastService.ShowNotification(msg);
        }
    }

    public void Receive(SingleIndicatorSettingsChangedMessage message)
    {
        if (message.Value != null)
        {
            var existing = Indicators.FirstOrDefault(i => i.Id == message.Value.Id);
            if (existing != null)
            {
                int index = Indicators.IndexOf(existing);
                if (index >= 0)
                {
                    bool wasSelected = (SelectedIndicator?.Id == message.Value.Id);
                    
                    // Replace the item in the collection
                    Indicators[index] = message.Value;
                    
                    if (wasSelected)
                    {
                        // Explicitly assigning triggers property changed for Avalonia bindings
                        SelectedIndicator = Indicators[index];
                    }
                }
            }
            else
            {
                UpdateReferenceOptions();
            }
        }
    }

    [RelayCommand]
    private async Task OpenSourceIndicatorSettingsAsync()
    {
        await _dialogService.ShowSourceIndicatorRegistrationDialogAsync();
        UpdateReferenceOptions();
    }

    [RelayCommand]
    private async Task OpenDynamicPeriodDriverSettingsAsync()
    {
        await _dialogService.ShowDynamicPeriodDriverRegistrationDialogAsync();
        UpdateReferenceOptions();
    }

    [RelayCommand]
    private void SelectTemplates()
    {
        IsTemplatesSelected = true;
    }

    [RelayCommand]
    private async Task SaveTemplateAsync()
    {
        if (_isDisposed || _templateCrud.IsBusy) return;
        if (string.IsNullOrWhiteSpace(NewTemplateName)) return;
        if (!Indicators.Any()) return;

        var trimmedName = NewTemplateName.Trim();
        await _templateCrud.SaveAsync(
            trimmedName,
            Templates,
            build: existing =>
            {
                var template = existing ?? new IndicatorTemplate
                {
                    Id = Guid.NewGuid(),
                    Name = trimmedName,
                    CreatedAt = DateTime.UtcNow
                };

                template.SetIndicators(Indicators.Select(i => i.Clone()));
                template.UpdatedAt = DateTime.UtcNow;
                return template;
            },
            commit: (saved, existing) =>
            {
                if (existing == null)
                {
                    Templates.Add(saved);
                }
                else if (existing == SelectedTemplate)
                {
                    RefreshSelectedTemplatePreview();
                }

                NewTemplateName = string.Empty;
            },
            onInvalid: validation => _logger.LogWarning(
                "Cannot save invalid indicator template '{Name}': {Errors}", trimmedName, string.Join(", ", validation.Errors)),
            onError: ex => _logger.LogError(ex, "Failed to save indicator template '{Name}'", NewTemplateName));
    }

    [RelayCommand]
    private async Task LoadTemplateAsync(IndicatorTemplate? template)
    {
        if (_isDisposed || _templateCrud.IsBusy || template == null) return;

        await _templateCrud.ApplyAsync(
            template,
            append: false,
            apply: t =>
            {
                // Transactionally construct new clones before touching current state.
                var newClones = new List<CoreIndicatorSettings>();
                foreach (var ind in t.Indicators)
                {
                    var clone = ind.Clone();
                    DefaultCoreIndicatorSettings.AutoHeal(clone);
                    newClones.Add(clone);
                }

                // Atomically replace current indicators.
                Indicators.Clear();
                foreach (var ind in newClones)
                {
                    Indicators.Add(ind);
                }

                SelectedIndicator = Indicators.FirstOrDefault();
                return Task.CompletedTask;
            },
            onInvalid: validation => _logger.LogWarning(
                "Cannot apply invalid indicator template '{Name}': {Errors}", template.Name, string.Join(", ", validation.Errors)),
            onError: ex => _logger.LogError(ex, "Failed to load indicator template '{Name}'", template.Name),
            onApplied: Apply);
    }

    [RelayCommand]
    private async Task AppendTemplateAsync(IndicatorTemplate? template)
    {
        if (_isDisposed || _templateCrud.IsBusy || template == null) return;

        await _templateCrud.ApplyAsync(
            template,
            append: true,
            apply: t =>
            {
                // Clone indicators with fresh IDs to prevent collisions.
                var newClones = new List<CoreIndicatorSettings>();
                foreach (var ind in t.Indicators)
                {
                    var clone = ind.Clone();
                    clone.Id = Guid.NewGuid().ToString();
                    DefaultCoreIndicatorSettings.AutoHeal(clone);
                    newClones.Add(clone);
                }

                // Append to current indicators without clearing.
                foreach (var ind in newClones)
                {
                    Indicators.Add(ind);
                }

                if (newClones.Any())
                {
                    SelectedIndicator = newClones.Last();
                }

                return Task.CompletedTask;
            },
            onInvalid: validation => _logger.LogWarning(
                "Cannot apply invalid indicator template '{Name}': {Errors}", template.Name, string.Join(", ", validation.Errors)),
            onError: ex => _logger.LogError(ex, "Failed to append indicator template '{Name}'", template.Name),
            onApplied: Apply);
    }

    [RelayCommand]
    private async Task DeleteTemplateAsync(IndicatorTemplate? template)
    {
        if (_isDisposed || _templateCrud.IsBusy || template == null) return;

        await _templateCrud.DeleteAsync(
            template,
            Templates,
            afterRemove: removed =>
            {
                if (SelectedTemplate == removed)
                {
                    SelectedTemplate = null;
                }
            },
            onError: ex => _logger.LogError(
                ex, "Failed to delete indicator template '{Name}' ({Id})", template.Name, template.Id));
    }

    private async Task LoadTemplatesAsync()
    {
        if (_isDisposed) return;

        await _templateCrud.LoadAllAsync(
            Templates,
            onError: ex => _logger.LogError(ex, "Failed to load indicator templates."));
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (SelectedLibraryIndicatorSettings?.ParameterObject != null)
        {
            SelectedLibraryIndicatorSettings.ParameterObject.PropertyChanged -= OnLibraryParameterChanged;
        }

        WeakReferenceMessenger.Default.UnregisterAll(this);
        GC.SuppressFinalize(this);
    }
}
