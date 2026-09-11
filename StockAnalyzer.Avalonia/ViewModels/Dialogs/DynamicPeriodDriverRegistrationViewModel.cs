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
/// ViewModel for the independent Adaptive Period (Dynamic Period Driver) registration window.
/// Allows registering, updating, and removing adaptive period drivers (inputs for adaptive modulation)
/// with a 3-column layout mirroring the Base Indicator window, without Sub-Window Panel selection.
/// </summary>
public partial class DynamicPeriodDriverRegistrationViewModel : ViewModelBase, IDisposable
{
    private readonly IDynamicPeriodDriverService _driverService;
    private readonly IIndicatorFactory _indicatorFactory;
    private readonly IToastNotificationService? _toastService;
    private readonly ITemplateService? _templateService;
    private readonly TemplateCrudHelper<DynamicPeriodDriverTemplate>? _templateCrud;
    private readonly IDispatcherService? _dispatcherService;
    private readonly IScreenerCatalogProvider _catalogProvider;
    private readonly ILogger<DynamicPeriodDriverRegistrationViewModel> _logger;
    private bool _isDisposed;
    private bool _isLoadingSettings;
    private bool _isSyncingPriceSource;
    private bool _isSyncingParameterChange;

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
    private DynamicPeriodDriverTemplate? _selectedTemplate;

    [ObservableProperty]
    private string _newTemplateName = string.Empty;

    public ObservableCollection<DynamicPeriodDriverTemplate> Templates { get; } = new();
    public ObservableCollection<string> SelectedTemplateIndicatorNames { get; } = new();

    [ObservableProperty]
    private bool _isPriceSelected;

    [ObservableProperty]
    private CoreIndicatorCategory? _selectedCategory;

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>
    /// List of available price types for price source selection, mirroring
    /// IndicatorPropertiesViewModel/IndicatorSettingsDialogViewModel so the currently-edited
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

    public Action? RequestClose { get; set; }
    public Task InitializationTask { get; }

    public DynamicPeriodDriverRegistrationViewModel(
        IDynamicPeriodDriverService driverService,
        IIndicatorFactory indicatorFactory,
        IToastNotificationService? toastService = null,
        ITemplateService? templateService = null,
        IDispatcherService? dispatcherService = null,
        ILogger<DynamicPeriodDriverRegistrationViewModel>? logger = null,
        IScreenerCatalogProvider? catalogProvider = null)
    {
        _driverService = driverService ?? throw new ArgumentNullException(nameof(driverService));
        _indicatorFactory = indicatorFactory ?? throw new ArgumentNullException(nameof(indicatorFactory));
        _toastService = toastService;
        _templateService = templateService;
        _dispatcherService = dispatcherService;
        _logger = logger ?? NullLogger<DynamicPeriodDriverRegistrationViewModel>.Instance;
        _catalogProvider = catalogProvider ?? new ScreenerCatalogProvider();

        if (_templateService != null)
        {
            _templateCrud = new TemplateCrudHelper<DynamicPeriodDriverTemplate>(_templateService, _toastService, TemplateType.DynamicPeriodDriver);
            _ = _templateCrud.LoadAllAsync(Templates, ex => _logger.LogError(ex, "Failed to load dynamic period driver templates"));
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
            var items = await _driverService.GetDynamicPeriodDriversAsync().ConfigureAwait(false);
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
            _logger.LogError(ex, "Failed to load dynamic period drivers");
        }
    }

    private void ApplyFilters()
    {
        if (_isDisposed) return;
        FilteredCatalogItems.Clear();

        if (IsPriceSelected)
        {
            foreach (var item in PriceCatalogItems)
            {
                if (string.IsNullOrWhiteSpace(SearchText) ||
                    item.DisplayName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                    item.ShortName.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
                {
                    FilteredCatalogItems.Add(item);
                }
            }
            return;
        }

        IEnumerable<IndicatorCatalogItem> query = _allCatalogItems;

        if (SelectedCategory.HasValue)
        {
            query = query.Where(i => i.Category == SelectedCategory.Value);
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var search = SearchText.Trim();
            query = query.Where(i =>
                i.ShortName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                i.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var item in query)
        {
            FilteredCatalogItems.Add(item);
        }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilters();

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

    partial void OnIsPriceSelectedChanged(bool value)
    {
        if (value)
        {
            IsSelectedMode = false;
            IsTemplatesSelected = false;
            SelectedCategory = null;
            ApplyFilters();
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

    partial void OnSelectedTemplateChanged(DynamicPeriodDriverTemplate? value) => RefreshSelectedTemplatePreview();

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
                var template = existing ?? new DynamicPeriodDriverTemplate
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
                "Cannot save invalid dynamic period driver template '{Name}': {Errors}", trimmedName, string.Join(", ", validation.Errors)),
            onError: ex => _logger.LogError(ex, "Failed to save dynamic period driver template '{Name}'", NewTemplateName));
    }

    [RelayCommand]
    private async Task LoadTemplateAsync(DynamicPeriodDriverTemplate? template)
    {
        if (_isDisposed || _templateCrud == null || _templateCrud.IsBusy || template == null) return;

        await _templateCrud.ApplyAsync(
            template,
            append: false,
            apply: async t =>
            {
                var existing = await _driverService.GetDynamicPeriodDriversAsync().ConfigureAwait(false);
                foreach (var ind in existing)
                {
                    await _driverService.DeleteDynamicPeriodDriverAsync(ind.Id).ConfigureAwait(false);
                }

                var newClones = new List<CoreIndicatorSettings>();
                foreach (var ind in t.Indicators)
                {
                    var clone = ind.Clone();
                    await _driverService.SaveDynamicPeriodDriverAsync(clone).ConfigureAwait(false);
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
    private async Task AppendTemplateAsync(DynamicPeriodDriverTemplate? template)
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
                    await _driverService.SaveDynamicPeriodDriverAsync(clone).ConfigureAwait(false);
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
    private async Task DeleteTemplateAsync(DynamicPeriodDriverTemplate? template)
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

    partial void OnSelectedCatalogItemChanged(IndicatorCatalogItem? value)
    {
        if (value == null) return;

        if (value.Type == IndicatorType.Price && value.PriceType.HasValue)
        {
            var priceSetting = new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.Price,
                PriceSource = value.PriceType.Value,
                DisplayName = value.DisplayName,
                IsOverlay = true
            };
            LoadEditingSettings(priceSetting);
        }
        else
        {
            var defaults = DefaultCoreIndicatorSettings.GetDefault();
            var matched = defaults.FirstOrDefault(s => s.TypeEnum == value.Type);
            if (matched != null)
            {
                LoadEditingSettings(matched.Snapshot());
            }
            else
            {
                // No static DefaultCoreIndicatorSettings entry for this type (e.g. CMO, FRAMA, CCI,
                // Momentum, NATR, TRIX, VolumeMA): fall back to the indicator's own reflection-based
                // GetDefaultSettings() for ParameterObject, mirroring SourceIndicatorRegistrationViewModel.
                // Without this, ParameterObject stayed null and the Parameters/Period UI never appeared
                // here even though it renders correctly in Source Indicator / Screener registration.
                var instance = _indicatorFactory.Create(value.Type);
                var newSetting = new CoreIndicatorSettings
                {
                    TypeEnum = value.Type,
                    DisplayName = value.DisplayName,
                    IsOverlay = true,
                    ParameterObject = instance?.GetDefaultSettings().ParameterObject
                };
                LoadEditingSettings(newSetting);
            }
        }
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
        }
    }

    private void LoadEditingSettings(CoreIndicatorSettings settings)
    {
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
            // Snapshot()/Clone() are MemberwiseClone()-based, which shallow-copies the PropertyChanged
            // event's backing delegate field - a freshly cloned settings object can therefore silently
            // already carry this handler if it (or an object it was cloned from) was ever subscribed.
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

        // Guard against re-entrancy: CoreIndicatorSettings.Snapshot()/Clone() are MemberwiseClone()-based,
        // which shallow-copies the PropertyChanged event's backing delegate field. That means
        // SelectedRegisteredIndicator (itself derived via a chain of Snapshot() calls from a
        // once-subscribed EditingSettings) can silently already carry this very handler, so the
        // PriceSource assignment below can synchronously re-enter this method. _isSyncingPriceSource
        // makes that a no-op instead of firing a redundant extra save.
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

    private void FireAndForgetSave(CoreIndicatorSettings snapshot)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _driverService.SaveDynamicPeriodDriverAsync(snapshot);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist dynamic period driver {Id}", snapshot.Id);
            }
        });
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

            await _driverService.SaveDynamicPeriodDriverAsync(toSave);

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
            _logger.LogError(ex, "Failed to register dynamic period driver");
            var errMsg = LocalizationManager.Instance["Msg_DriverRegisterFailed"] ?? "Failed to register dynamic period driver.";
            _toastService?.ShowNotification(errMsg);
        }
    }

    [RelayCommand]
    private async Task DeleteRegisteredIndicatorAsync(CoreIndicatorSettings? indicator)
    {
        if (indicator == null || string.IsNullOrEmpty(indicator.Id) || _isDisposed) return;

        try
        {
            await _driverService.DeleteDynamicPeriodDriverAsync(indicator.Id);
            var existing = RegisteredIndicators.FirstOrDefault(i => i.Id == indicator.Id);
            if (existing != null)
            {
                RegisteredIndicators.Remove(existing);
            }

            if (SelectedRegisteredIndicator?.Id == indicator.Id)
            {
                SelectedRegisteredIndicator = RegisteredIndicators.FirstOrDefault();
            }

            WeakReferenceMessenger.Default.Send(new SingleIndicatorSettingsChangedMessage(indicator));

            var msg = LocalizationManager.Instance["Msg_DriverDeleted"] ?? "Dynamic period driver removed.";
            _toastService?.ShowNotification(msg);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete dynamic period driver {Id}", indicator.Id);
            var errMsg = LocalizationManager.Instance["Msg_DriverDeleteFailed"] ?? "Failed to delete dynamic period driver.";
            _toastService?.ShowNotification(errMsg);
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
