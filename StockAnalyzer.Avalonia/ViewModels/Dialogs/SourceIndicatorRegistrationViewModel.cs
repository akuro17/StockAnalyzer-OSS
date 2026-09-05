using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>
/// ViewModel for the independent Base Indicator (Source Indicator) registration window.
/// Allows registering, updating, and removing base indicators (inputs for chaining / indicator-on-indicator)
/// with a 3-column layout mirroring the Indicator Manager Library tab.
/// </summary>
public partial class SourceIndicatorRegistrationViewModel : ViewModelBase, IDisposable
{
    private readonly ISourceIndicatorService _sourceIndicatorService;
    private readonly IIndicatorFactory _indicatorFactory;
    private readonly IToastNotificationService? _toastService;
    private readonly ITemplateService? _templateService;
    private readonly TemplateCrudHelper<SourceIndicatorTemplate>? _templateCrud;
    private readonly IDispatcherService? _dispatcherService;
    private readonly IScreenerCatalogProvider _catalogProvider;
    private readonly ILogger<SourceIndicatorRegistrationViewModel> _logger;
    private bool _isDisposed;
    private bool _isSyncingPriceSource;
    private bool _isSyncingParameterChange;
    private bool _isLoadingSettings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotSelectedMode))]
    [NotifyPropertyChangedFor(nameof(IsCatalogMode))]
    private bool _isSelectedMode = true;

    public bool IsNotSelectedMode => !IsSelectedMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotTemplatesSelected))]
    [NotifyPropertyChangedFor(nameof(IsCatalogMode))]
    private bool _isTemplatesSelected;

    public bool IsNotTemplatesSelected => !IsTemplatesSelected;
    public bool IsCatalogMode => !IsSelectedMode && !IsTemplatesSelected;

    public IToastNotificationService? ToastService => _toastService;

    [ObservableProperty]
    private SourceIndicatorTemplate? _selectedTemplate;

    [ObservableProperty]
    private string _newTemplateName = string.Empty;

    public ObservableCollection<SourceIndicatorTemplate> Templates { get; } = new();
    public ObservableCollection<string> SelectedTemplateIndicatorNames { get; } = new();

    [ObservableProperty]
    private bool _isPriceSelected;

    [ObservableProperty]
    private CoreIndicatorCategory? _selectedCategory;

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>
    /// List of available price types for price source selection, mirroring
    /// IndicatorPropertiesViewModel/DynamicPeriodDriverRegistrationViewModel so the currently-edited
    /// indicator's input price series can be changed here too (previously only available in
    /// Indicator Manager / Properties, not in this registration window).
    /// Ordered as Open, High, Low, Close, Median, Typical, Weighted via PriceDataHelper.
    /// </summary>
    public IReadOnlyList<PriceType> PriceTypeOptions { get; } = PriceDataHelper.PriceTypeOptions;

    public ObservableCollection<CoreIndicatorCategory> Categories { get; } = new();
    public ObservableCollection<IndicatorCatalogItem> FilteredCatalogItems { get; } = new();
    public ObservableCollection<CoreIndicatorSettings> RegisteredIndicators { get; } = new();

    private readonly List<IndicatorCatalogItem> _allCatalogItems = new();

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
    private IndicatorCatalogItem? _selectedCatalogItem;

    [ObservableProperty]
    private CoreIndicatorSettings? _selectedRegisteredIndicator;

    [ObservableProperty]
    private CoreIndicatorSettings? _editingSettings;

    [ObservableProperty]
    private string _previewName = string.Empty;

    [ObservableProperty]
    private bool _useShortName;

    public ObservableCollection<string> AvailableOutputs { get; } = new();

    [ObservableProperty]
    private string _selectedOutput = IndicatorResult.MainSeriesName;

    [ObservableProperty]
    private bool _hasMultipleOutputs;

    /// <summary>
    /// Panel selection options for the overlay panel ComboBox.
    /// Index 0 = Default (own panel), 1-6 = Panel A through F.
    /// </summary>
    public List<string> PanelOptions { get; } = BuildPanelOptions();

    /// <summary>
    /// Panel selector is visible only for non-overlay, non-GranvilleLaw indicators.
    /// </summary>
    public bool ShowPanelSelector => EditingSettings != null
        && !EditingSettings.IsOverlay
        && EditingSettings.TypeEnum != IndicatorType.GranvilleLaw
        && EditingSettings.TypeEnum != IndicatorType.VolumeProfile;

    /// <summary>
    /// Gets/sets the selected overlay panel option for the currently edited indicator.
    /// Maps between the ComboBox display string and CoreIndicatorSettings.OverlayPanelId.
    /// </summary>
    public string SelectedOverlayPanelOption
    {
        get
        {
            var id = EditingSettings?.OverlayPanelId;
            if (string.IsNullOrEmpty(id)) return PanelOptions[0];
            var match = PanelOptions.FirstOrDefault(o => o.EndsWith(id));
            return match ?? PanelOptions[0];
        }
        set
        {
            if (EditingSettings == null) return;
            if (value == PanelOptions[0])
            {
                EditingSettings.OverlayPanelId = null;
            }
            else
            {
                var parts = value.Split(' ');
                EditingSettings.OverlayPanelId = parts.Length > 1 ? parts[^1] : value;
            }
            OnPropertyChanged();

            if (IsSelectedMode && SelectedRegisteredIndicator != null)
            {
                SelectedRegisteredIndicator.OverlayPanelId = EditingSettings.OverlayPanelId;
                SelectedRegisteredIndicator.MathematicalVersion++;
                var snapshot = SelectedRegisteredIndicator.Snapshot();
                FireAndForgetSave(snapshot);
                WeakReferenceMessenger.Default.Send(new SingleIndicatorSettingsChangedMessage(snapshot));
            }
        }
    }

    private static List<string> BuildPanelOptions()
    {
        var defaultLabel = LocalizationManager.Instance["Panel_Default"] ?? "Default (New Panel)";
        var prefix = LocalizationManager.Instance["Panel_GroupPrefix"] ?? "Panel";
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

    public Action? RequestClose { get; set; }
    public Task InitializationTask { get; }

    public SourceIndicatorRegistrationViewModel(
        ISourceIndicatorService sourceIndicatorService,
        IIndicatorFactory indicatorFactory,
        IToastNotificationService? toastService = null,
        ITemplateService? templateService = null,
        IDispatcherService? dispatcherService = null,
        ILogger<SourceIndicatorRegistrationViewModel>? logger = null,
        IScreenerCatalogProvider? catalogProvider = null)
    {
        _sourceIndicatorService = sourceIndicatorService ?? throw new ArgumentNullException(nameof(sourceIndicatorService));
        _indicatorFactory = indicatorFactory ?? throw new ArgumentNullException(nameof(indicatorFactory));
        _toastService = toastService;
        _templateService = templateService;
        _dispatcherService = dispatcherService;
        _logger = logger ?? NullLogger<SourceIndicatorRegistrationViewModel>.Instance;
        _catalogProvider = catalogProvider ?? new ScreenerCatalogProvider();

        if (_templateService != null)
        {
            _templateCrud = new TemplateCrudHelper<SourceIndicatorTemplate>(_templateService, _toastService, TemplateType.SourceIndicator);
            _ = _templateCrud.LoadAllAsync(Templates, ex => _logger.LogError(ex, "Failed to load source indicator templates"));
        }

        BuildCatalog();
        InitializationTask = LoadRegisteredIndicatorsAsync();
    }

    private void BuildCatalog()
    {
        Categories.Clear();
        FilteredCatalogItems.Clear();
        _allCatalogItems.Clear();

        var staticDefaults = DefaultCoreIndicatorSettings.GetDefault();
        var tempItems = new List<IndicatorCatalogItem>();

        foreach (var type in _indicatorFactory.GetRegisteredTypes())
        {
            if (type == IndicatorType.Price) continue;

            var defaultSetting = staticDefaults.FirstOrDefault(s => s.TypeEnum == type);
            var category = defaultSetting?.Category ?? CoreIndicatorCategory.Other;

            tempItems.Add(new IndicatorCatalogItem
            {
                Type = type,
                DisplayName = type.GetDescription(),
                ShortName = type.ToString(),
                Category = category
            });
        }

        foreach (var item in tempItems.OrderBy(i => i.ShortName))
        {
            _allCatalogItems.Add(item);
        }

        var uniqueCategories = _allCatalogItems
            .Select(i => i.Category)
            .Distinct()
            .OrderBy(c => c);

        foreach (var cat in uniqueCategories)
        {
            Categories.Add(cat);
        }

        ApplyFilters();
    }

    public async Task LoadRegisteredIndicatorsAsync()
    {
        try
        {
            var items = await _sourceIndicatorService.GetSourceIndicatorsAsync().ConfigureAwait(false);
            void Apply()
            {
                RegisteredIndicators.Clear();
                foreach (var item in items)
                {
                    item.DisplayName = item.GetFormattedDisplayName();
                    RegisteredIndicators.Add(item);
                }

                if (IsSelectedMode && SelectedRegisteredIndicator == null)
                {
                    SelectedRegisteredIndicator = RegisteredIndicators.FirstOrDefault();
                }
            }

            if (_dispatcherService != null)
            {
                _dispatcherService.Post(Apply);
            }
            else
            {
                Apply();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load registered source indicators");
        }
    }

    [RelayCommand]
    private void SelectSelected()
    {
        IsSelectedMode = true;
        IsTemplatesSelected = false;
        IsPriceSelected = false;
        SelectedCategory = null;

        if (SelectedRegisteredIndicator != null)
        {
            LoadEditingSettings(SelectedRegisteredIndicator.Snapshot());
        }
        else if (RegisteredIndicators.Any())
        {
            SelectedRegisteredIndicator = RegisteredIndicators.First();
        }
        else
        {
            EditingSettings = null;
            PreviewName = string.Empty;
            AvailableOutputs.Clear();
            HasMultipleOutputs = false;
            OnPropertyChanged(nameof(ShowPanelSelector));
            OnPropertyChanged(nameof(SelectedOverlayPanelOption));
        }
    }

    [RelayCommand]
    private void ClearCategoryFilter()
    {
        IsSelectedMode = false;
        IsTemplatesSelected = false;
        IsPriceSelected = false;
        SelectedCategory = null;
        ApplyFilters();
    }

    [RelayCommand]
    private void SelectPrice()
    {
        IsSelectedMode = false;
        IsTemplatesSelected = false;
        IsPriceSelected = true;
        SelectedCategory = null;
        ApplyFilters();
    }

    [RelayCommand]
    private void SelectTemplates()
    {
        IsTemplatesSelected = true;
        IsSelectedMode = false;
        IsPriceSelected = false;
        SelectedCategory = null;

        if (SelectedTemplate != null)
        {
            RefreshSelectedTemplatePreview();
        }
        else
        {
            SelectedTemplate = Templates.FirstOrDefault();
        }
    }

    partial void OnSelectedCategoryChanged(CoreIndicatorCategory? value)
    {
        if (value.HasValue)
        {
            IsSelectedMode = false;
            IsTemplatesSelected = false;
            IsPriceSelected = false;
            ApplyFilters();
        }
    }

    partial void OnSelectedTemplateChanged(SourceIndicatorTemplate? value) => RefreshSelectedTemplatePreview();

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
            if (string.IsNullOrEmpty(name))
            {
                name = ind.TypeEnum?.ToString() ?? "Unknown";
            }
            SelectedTemplateIndicatorNames.Add(name);
        }
    }

    [RelayCommand]
    private async Task SaveTemplateAsync()
    {
        if (_isDisposed || _templateCrud == null || _templateCrud.IsBusy) return;
        if (string.IsNullOrWhiteSpace(NewTemplateName)) return;
        if (!RegisteredIndicators.Any()) return;

        var trimmedName = NewTemplateName.Trim();
        await _templateCrud.SaveAsync(
            trimmedName,
            Templates,
            build: existing =>
            {
                var template = existing ?? new SourceIndicatorTemplate
                {
                    Id = Guid.NewGuid(),
                    Name = trimmedName,
                    CreatedAt = DateTime.UtcNow
                };

                template.SetIndicators(RegisteredIndicators.Select(i => i.Snapshot()));
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
                "Cannot save invalid source indicator template '{Name}': {Errors}", trimmedName, string.Join(", ", validation.Errors)),
            onError: ex => _logger.LogError(ex, "Failed to save source indicator template '{Name}'", NewTemplateName));
    }

    [RelayCommand]
    private async Task LoadTemplateAsync(SourceIndicatorTemplate? template)
    {
        if (_isDisposed || _templateCrud == null || _templateCrud.IsBusy || template == null) return;

        await _templateCrud.ApplyAsync(
            template,
            append: false,
            apply: async t =>
            {
                var existing = await _sourceIndicatorService.GetSourceIndicatorsAsync().ConfigureAwait(false);
                foreach (var ind in existing)
                {
                    await _sourceIndicatorService.DeleteSourceIndicatorAsync(ind.Id).ConfigureAwait(false);
                }

                var newClones = new List<CoreIndicatorSettings>();
                foreach (var ind in t.Indicators)
                {
                    var clone = ind.Clone();
                    await _sourceIndicatorService.SaveSourceIndicatorAsync(clone).ConfigureAwait(false);
                    newClones.Add(clone);
                }

                void Apply()
                {
                    RegisteredIndicators.Clear();
                    foreach (var ind in newClones)
                    {
                        RegisteredIndicators.Add(ind);
                    }
                    SelectedRegisteredIndicator = RegisteredIndicators.FirstOrDefault();
                }

                if (_dispatcherService != null)
                {
                    _dispatcherService.Post(Apply);
                }
                else
                {
                    Apply();
                }
            });
    }

    [RelayCommand]
    private async Task AppendTemplateAsync(SourceIndicatorTemplate? template)
    {
        if (_isDisposed || _templateCrud == null || _templateCrud.IsBusy || template == null) return;

        await _templateCrud.ApplyAsync(
            template,
            append: true,
            apply: async t =>
            {
                var newClones = new List<CoreIndicatorSettings>();
                foreach (var ind in t.Indicators)
                {
                    var clone = ind.Clone();
                    clone.Id = Guid.NewGuid().ToString();
                    await _sourceIndicatorService.SaveSourceIndicatorAsync(clone).ConfigureAwait(false);
                    newClones.Add(clone);
                }

                void Apply()
                {
                    foreach (var ind in newClones)
                    {
                        RegisteredIndicators.Add(ind);
                    }
                    if (SelectedRegisteredIndicator == null)
                    {
                        SelectedRegisteredIndicator = RegisteredIndicators.FirstOrDefault();
                    }
                }

                if (_dispatcherService != null)
                {
                    _dispatcherService.Post(Apply);
                }
                else
                {
                    Apply();
                }
            });
    }

    [RelayCommand]
    private async Task DeleteTemplateAsync(SourceIndicatorTemplate? template)
    {
        if (_isDisposed || _templateCrud == null || _templateCrud.IsBusy || template == null) return;

        await _templateCrud.DeleteAsync(
            template,
            Templates,
            afterRemove: removed =>
            {
                if (SelectedTemplate == removed)
                {
                    SelectedTemplate = Templates.FirstOrDefault();
                }
            });
    }

    partial void OnSearchTextChanged(string value) => ApplyFilters();

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
            filtered = _allCatalogItems;
            if (SelectedCategory.HasValue)
            {
                filtered = filtered.Where(item => item.Category == SelectedCategory.Value);
            }
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var text = SearchText.Trim();
            filtered = filtered.Where(item =>
                item.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                item.ShortName.Contains(text, StringComparison.OrdinalIgnoreCase));
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

    partial void OnSelectedCatalogItemChanged(IndicatorCatalogItem? value)
    {
        if (IsSelectedMode) return;

        if (value == null)
        {
            EditingSettings = null;
            PreviewName = string.Empty;
            AvailableOutputs.Clear();
            HasMultipleOutputs = false;
            OnPropertyChanged(nameof(ShowPanelSelector));
            OnPropertyChanged(nameof(SelectedOverlayPanelOption));
            return;
        }

        var instance = _indicatorFactory.Create(value.Type);
        var settings = instance?.GetDefaultSettings() ?? new CoreIndicatorSettings { TypeEnum = value.Type, IsEnabled = true };
        settings.TypeEnum = value.Type;
        settings.IsEnabled = true;

        if (value.Type == IndicatorType.Price && value.PriceType.HasValue)
        {
            settings.PriceSource = value.PriceType.Value;
            settings.DisplayName = value.DisplayName;
            settings.IsOverlay = true;
        }

        LoadEditingSettings(settings);
    }

    partial void OnSelectedRegisteredIndicatorChanged(CoreIndicatorSettings? value)
    {
        if (!IsSelectedMode) return;

        if (value != null)
        {
            bool useShort = value.UseShortName || (value.DisplayName == value.ShortDisplayName && !string.IsNullOrEmpty(value.ShortDisplayName));
            _useShortName = useShort;
            OnPropertyChanged(nameof(UseShortName));
            LoadEditingSettings(value.Snapshot());
        }
        else
        {
            EditingSettings = null;
            PreviewName = string.Empty;
            AvailableOutputs.Clear();
            HasMultipleOutputs = false;
            OnPropertyChanged(nameof(ShowPanelSelector));
            OnPropertyChanged(nameof(SelectedOverlayPanelOption));
        }
    }

    private void LoadEditingSettings(CoreIndicatorSettings settings)
    {
        // Guards the entire load sequence, not just the PriceSource-specific _isSyncingPriceSource
        // flag below: any property assignment against EditingSettings/settings inside this method
        // (e.g. the OutputSeriesName/DisplayName assignments further down) fires PropertyChanged, and
        // OnEditingSettingsPropertyChanged must never react to a change caused by loading, only to a
        // real user edit. Mirrors DynamicPeriodDriverRegistrationViewModel.LoadEditingSettings.
        if (_isLoadingSettings) return;
        _isLoadingSettings = true;
        try
        {
            if (EditingSettings?.ParameterObject != null)
            {
                EditingSettings.ParameterObject.PropertyChanged -= OnParameterChanged;
            }
            if (EditingSettings != null)
            {
                EditingSettings.PropertyChanged -= OnEditingSettingsPropertyChanged;
            }

            EditingSettings = settings;
            // CoreIndicatorSettings.Snapshot()/Clone() are MemberwiseClone()-based, which shallow-copies the
            // PropertyChanged event's backing delegate field - a freshly cloned settings object can therefore
            // silently already carry this handler if it (or an object it was cloned from) was ever subscribed.
            // Unconditionally strip before re-adding so we never end up double-subscribed to one instance.
            EditingSettings.PropertyChanged -= OnEditingSettingsPropertyChanged;
            EditingSettings.PropertyChanged += OnEditingSettingsPropertyChanged;
            _useShortName = settings.UseShortName || (settings.DisplayName == settings.ShortDisplayName && !string.IsNullOrEmpty(settings.ShortDisplayName));
            OnPropertyChanged(nameof(UseShortName));

            // Load available output series
            AvailableOutputs.Clear();
            if (settings.TypeEnum.HasValue && settings.TypeEnum.Value != IndicatorType.Price)
            {
                var outputs = _catalogProvider.GetOutputSeriesNames(settings.TypeEnum.Value, _indicatorFactory);
                foreach (var outName in outputs)
                {
                    AvailableOutputs.Add(outName);
                }
            }
            HasMultipleOutputs = AvailableOutputs.Count > 1;

            if (!string.IsNullOrEmpty(settings.OutputSeriesName) && AvailableOutputs.Contains(settings.OutputSeriesName))
            {
                _selectedOutput = settings.OutputSeriesName;
            }
            else
            {
                _selectedOutput = AvailableOutputs.FirstOrDefault() ?? IndicatorResult.MainSeriesName;
                settings.OutputSeriesName = _selectedOutput;
            }
            OnPropertyChanged(nameof(SelectedOutput));

            if (EditingSettings.ParameterObject != null)
            {
                // Same MemberwiseClone()-inherited-subscription hazard as EditingSettings above applies to
                // ParameterObject.Clone() (CoreIndicatorParameterBase.Clone() is also MemberwiseClone()-based).
                EditingSettings.ParameterObject.PropertyChanged -= OnParameterChanged;
                EditingSettings.ParameterObject.PropertyChanged += OnParameterChanged;
            }

            UpdatePreviewName();
            EditingSettings.DisplayName = PreviewName;
            OnPropertyChanged(nameof(ShowPanelSelector));
            OnPropertyChanged(nameof(SelectedOverlayPanelOption));
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    partial void OnSelectedOutputChanged(string value)
    {
        if (EditingSettings != null)
        {
            EditingSettings.OutputSeriesName = value;
        }
        UpdatePreviewName();
        if (EditingSettings != null)
        {
            EditingSettings.DisplayName = PreviewName;
        }

        if (IsSelectedMode && SelectedRegisteredIndicator != null)
        {
            SelectedRegisteredIndicator.OutputSeriesName = value;
            SelectedRegisteredIndicator.DisplayName = PreviewName;
            SelectedRegisteredIndicator.MathematicalVersion++;
            var snapshot = SelectedRegisteredIndicator.Snapshot();
            FireAndForgetSave(snapshot);
            WeakReferenceMessenger.Default.Send(new SingleIndicatorSettingsChangedMessage(snapshot));
        }
    }

    partial void OnUseShortNameChanged(bool value)
    {
        if (EditingSettings != null)
        {
            EditingSettings.UseShortName = value;
        }
        UpdatePreviewName();
        if (EditingSettings != null)
        {
            EditingSettings.DisplayName = PreviewName;
        }

        if (IsSelectedMode && SelectedRegisteredIndicator != null)
        {
            SelectedRegisteredIndicator.UseShortName = value;
            SelectedRegisteredIndicator.DisplayName = PreviewName;
            var snapshot = SelectedRegisteredIndicator.Snapshot();
            FireAndForgetSave(snapshot);
            WeakReferenceMessenger.Default.Send(new SingleIndicatorSettingsChangedMessage(snapshot));
        }
    }

    private void OnParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Guard against re-entrancy: this handler assigns SelectedRegisteredIndicator.ParameterObject =
        // EditingSettings.ParameterObject.Clone() below, and CoreIndicatorParameterBase.Clone() is
        // MemberwiseClone()-based - the clone can silently inherit this very subscription, so mutating
        // it later (e.g. via the ParameterObject setter's own PropertyChanged) can re-enter this method.
        if (_isSyncingParameterChange) return;

        UpdatePreviewName();
        if (EditingSettings != null)
        {
            EditingSettings.DisplayName = PreviewName;
        }
        if (IsSelectedMode && SelectedRegisteredIndicator != null)
        {
            _isSyncingParameterChange = true;
            try
            {
                if (EditingSettings?.ParameterObject != null)
                {
                    SelectedRegisteredIndicator.ParameterObject = EditingSettings.ParameterObject.Clone();
                }
                SelectedRegisteredIndicator.UseShortName = UseShortName;
                SelectedRegisteredIndicator.DisplayName = PreviewName;
                SelectedRegisteredIndicator.MathematicalVersion++;
                var snapshot = SelectedRegisteredIndicator.Snapshot();
                FireAndForgetSave(snapshot);
                WeakReferenceMessenger.Default.Send(new SingleIndicatorSettingsChangedMessage(snapshot));
            }
            finally
            {
                _isSyncingParameterChange = false;
            }
        }
    }

    /// <summary>
    /// Persists a change to EditingSettings.PriceSource (the "Price Type" selector) back onto the
    /// already-registered indicator being edited, mirroring OnParameterChanged/OnUseShortNameChanged/
    /// OnSelectedOutputChanged above - without this, editing Price Type on a Selected-mode indicator
    /// would update the bound value visibly but silently fail to persist, since this window has no
    /// "Add Indicator" button in Selected mode (it's Catalog-mode only) to trigger a save otherwise.
    /// </summary>
    private void OnEditingSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isLoadingSettings || _isSyncingPriceSource) return;
        if (e.PropertyName != nameof(CoreIndicatorSettings.PriceSource)) return;
        if (EditingSettings == null || !IsSelectedMode || SelectedRegisteredIndicator == null) return;

        // Guard against re-entrancy: SelectedRegisteredIndicator can silently carry an inherited copy of
        // this handler (via the same MemberwiseClone()-shallow-copies-PropertyChanged-subscribers hazard),
        // so the assignment below can synchronously re-enter this method.
        _isSyncingPriceSource = true;
        try
        {
            SelectedRegisteredIndicator.PriceSource = EditingSettings.PriceSource;
            var snapshot = SelectedRegisteredIndicator.Snapshot();
            FireAndForgetSave(snapshot);
            WeakReferenceMessenger.Default.Send(new SingleIndicatorSettingsChangedMessage(snapshot));
        }
        finally
        {
            _isSyncingPriceSource = false;
        }
    }

    private void FireAndForgetSave(CoreIndicatorSettings snapshot)
    {
        _ = SaveInternalAsync(snapshot);

        async Task SaveInternalAsync(CoreIndicatorSettings item)
        {
            try
            {
                await _sourceIndicatorService.SaveSourceIndicatorAsync(item).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to background save source indicator {Id}", item.Id);
            }
        }
    }

    private void UpdatePreviewName()
    {
        if (EditingSettings == null)
        {
            PreviewName = string.Empty;
            return;
        }

        if (EditingSettings.TypeEnum == IndicatorType.Price)
        {
            PreviewName = UseShortName
                ? EditingSettings.PriceSource.ToString()
                : PriceDataHelper.FormatPriceTypeLabel(EditingSettings.PriceSource);
            return;
        }

        string baseName = UseShortName
            ? (EditingSettings.TypeEnum?.ToString() ?? "Unknown")
            : (EditingSettings.TypeEnum?.GetDescription() ?? "Unknown");

        string formattedName;
        if (EditingSettings.ParameterObject != null)
        {
            string paramName = EditingSettings.ParameterObject.GetDisplayName(baseName);
            formattedName = UseShortName ? paramName.Replace(" (", "(") : paramName;
        }
        else
        {
            formattedName = baseName;
        }

        if (HasMultipleOutputs &&
            !string.IsNullOrEmpty(SelectedOutput) &&
            SelectedOutput != IndicatorResult.MainSeriesName &&
            (AvailableOutputs.Count == 0 || SelectedOutput != AvailableOutputs.FirstOrDefault()))
        {
            PreviewName = $"{formattedName} ({SelectedOutput})";
        }
        else
        {
            PreviewName = formattedName;
        }
    }

    [RelayCommand]
    private async Task RegisterIndicatorAsync()
    {
        if (EditingSettings == null || _isDisposed) return;

        try
        {
            var toSave = EditingSettings.Snapshot();
            toSave.IsEnabled = true;
            toSave.UseShortName = UseShortName;
            toSave.DisplayName = PreviewName;
            toSave.OutputSeriesName = SelectedOutput;
            if (!IsSelectedMode || string.IsNullOrEmpty(toSave.Id))
            {
                toSave.Id = Guid.NewGuid().ToString();
            }

            await _sourceIndicatorService.SaveSourceIndicatorAsync(toSave);

            var existing = RegisteredIndicators.FirstOrDefault(i => i.Id == toSave.Id);
            if (existing != null)
            {
                int idx = RegisteredIndicators.IndexOf(existing);
                RegisteredIndicators[idx] = toSave;
            }
            else
            {
                RegisteredIndicators.Add(toSave);
            }

            SelectedRegisteredIndicator = toSave;
            WeakReferenceMessenger.Default.Send(new SingleIndicatorSettingsChangedMessage(toSave));

            var msg = LocalizationManager.Instance["Msg_Added"] ?? "Added";
            _toastService?.ShowNotification($"{toSave.DisplayName} {msg}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register source indicator");
        }
    }

    [RelayCommand]
    private async Task DeleteRegisteredIndicatorAsync(CoreIndicatorSettings? item)
    {
        if (item == null || string.IsNullOrEmpty(item.Id) || _isDisposed) return;

        try
        {
            await _sourceIndicatorService.DeleteSourceIndicatorAsync(item.Id);
            var existing = RegisteredIndicators.FirstOrDefault(i => i.Id == item.Id);
            if (existing != null)
            {
                RegisteredIndicators.Remove(existing);
            }

            if (SelectedRegisteredIndicator?.Id == item.Id)
            {
                SelectedRegisteredIndicator = RegisteredIndicators.FirstOrDefault();
            }

            WeakReferenceMessenger.Default.Send(new SingleIndicatorSettingsChangedMessage(item));

            var msg = LocalizationManager.Instance["Msg_Deleted"] ?? "Deleted";
            _toastService?.ShowNotification($"{item.DisplayName} {msg}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete registered source indicator {Id}", item.Id);
        }
    }

    [RelayCommand]
    private void Close()
    {
        RequestClose?.Invoke();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (EditingSettings?.ParameterObject != null)
        {
            EditingSettings.ParameterObject.PropertyChanged -= OnParameterChanged;
        }
        if (EditingSettings != null)
        {
            EditingSettings.PropertyChanged -= OnEditingSettingsPropertyChanged;
        }

        GC.SuppressFinalize(this);
    }
}
