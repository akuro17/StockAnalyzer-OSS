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
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

using System.IO;
using System.Text.Json;
using StockAnalyzer.Core.Serialization;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>
/// ViewModel for the main Indicator Settings dialog.
/// </summary>
public partial class IndicatorSettingsDialogViewModel : ViewModelBase, IRecipient<SingleIndicatorSettingsChangedMessage>, IDisposable
{
    private static readonly string TemplatesFilePath = StockAnalyzer.Core.Common.PathDiscovery.ResolveConfigPath("indicator_templates.json");
    private readonly ILogger<IndicatorSettingsDialogViewModel> _logger;
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = WorkspacePolymorphicResolver.CreateResolver(),
        IncludeFields = true,
        PropertyNameCaseInsensitive = true
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowThicknessAndOffset))]
    [NotifyPropertyChangedFor(nameof(ShowUpDownColors))]
    [NotifyPropertyChangedFor(nameof(ShowPanelSelector))]
    [NotifyPropertyChangedFor(nameof(SelectedOverlayPanelOption))]
    private CoreIndicatorSettings? _selectedIndicator;

    public bool ShowThicknessAndOffset => SelectedIndicator?.TypeEnum != IndicatorType.GranvilleLaw;
    public bool ShowUpDownColors => SelectedIndicator?.TypeEnum != IndicatorType.GranvilleLaw;

    /// <summary>
    /// Panel selector is visible only for non-overlay, non-GranvilleLaw indicators.
    /// </summary>
    public bool ShowPanelSelector => SelectedIndicator != null
        && !SelectedIndicator.IsOverlay
        && SelectedIndicator.TypeEnum != IndicatorType.GranvilleLaw;

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

    private readonly IIndicatorFactory _indicatorFactory;
    private readonly Dictionary<IndicatorType, CoreIndicatorSettings> _defaultSettingsCache = new();

    public IndicatorSettingsDialogViewModel(
        IDialogService dialogService, 
        IIndicatorFactory indicatorFactory, 
        IToastNotificationService toastService,
        ILogger<IndicatorSettingsDialogViewModel>? logger = null)
    {
        _indicatorFactory = indicatorFactory;
        ToastService = toastService;
        _logger = logger ?? NullLogger<IndicatorSettingsDialogViewModel>.Instance;
        
        WeakReferenceMessenger.Default.Register<SingleIndicatorSettingsChangedMessage>(this);

        // Build the dynamic default settings cache
        var staticDefaults = DefaultCoreIndicatorSettings.GetDefault();
        foreach (var type in _indicatorFactory.GetRegisteredTypes())
        {
            var oldDefault = staticDefaults.FirstOrDefault(s => s.TypeEnum == type);
            if (oldDefault != null)
            {
                _defaultSettingsCache[type] = oldDefault;
            }
            else
            {
                var inst = _indicatorFactory.Create(type);
                if (inst != null)
                {
                    var settings = inst.GetDefaultSettings();
                    settings.TypeEnum = type; // ensure TypeEnum is set explicitly
                    _defaultSettingsCache[type] = settings;
                }
            }
        }

        BuildCatalog();

        // Load templates
        var saved = LoadTemplates();
        foreach (var t in saved)
        {
            Templates.Add(t);
        }
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
                Indicators.Add(clone);
            }
        }
        
        if (Indicators.Any())
        {
            SelectedIndicator = Indicators.First();
        }

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

        foreach (var kvp in _defaultSettingsCache)
        {
            var type = kvp.Key;
            var settings = kvp.Value;

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

        var filtered = AllCatalogItems.AsEnumerable();

        // Filter by category
        if (SelectedCategory.HasValue)
        {
            filtered = filtered.Where(item => item.Category == SelectedCategory.Value);
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
    }

    partial void OnSearchTextChanged(string value) => ApplyFilters();
    
    partial void OnIsLibraryModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsTemplatesCategory));
        OnPropertyChanged(nameof(IsNotTemplatesCategory));
    }

    partial void OnSelectedCategoryChanged(CoreIndicatorCategory? value)
    {
        if (value != null)
        {
            IsTemplatesSelected = false;
        }
        ApplyFilters();
        OnPropertyChanged(nameof(IsTemplatesCategory));
        OnPropertyChanged(nameof(IsNotTemplatesCategory));
    }

    partial void OnIsTemplatesSelectedChanged(bool value)
    {
        if (value)
        {
            SelectedCategory = null;
        }
        OnPropertyChanged(nameof(IsTemplatesCategory));
        OnPropertyChanged(nameof(IsNotTemplatesCategory));
    }

    partial void OnSelectedTemplateChanged(IndicatorTemplate? value)
    {
        SelectedTemplateIndicatorNames.Clear();
        if (value != null)
        {
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
    private void SelectCategory(CoreIndicatorCategory? category)
    {
        // Toggle: if same category is clicked again, clear the filter
        SelectedCategory = SelectedCategory == category ? null : category;
    }

    [RelayCommand]
    private void ClearCategoryFilter()
    {
        SelectedCategory = null;
    }

    /// <summary>
    /// Adds an indicator from the Library catalog.
    /// </summary>
    [RelayCommand]
    private void AddFromLibrary(IndicatorCatalogItem? catalogItem)
    {
        if (catalogItem == null) return;

        AddIndicator(catalogItem.Type.ToString());
    }

    [RelayCommand]
    private void AddIndicator(string typeName)
    {
        if (Enum.TryParse<IndicatorType>(typeName, out var type))
        {
             if (_defaultSettingsCache.TryGetValue(type, out var defaultSettings))
             {
                 var newSettings = defaultSettings.Clone();
                 newSettings.IsEnabled = true; // Enable it so it renders
                 newSettings.UpdateDisplayName();

                 Indicators.Add(newSettings);
                 SelectedIndicator = newSettings;
                 
                 ToastService.ShowNotification($"{newSettings.DisplayName} {LocalizationManager.Instance["Msg_Added"] ?? "Added"}");
                 
                 // Auto-apply immediately to Main Window when added from library
                 Apply();
             }
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
    private void Reset()
    {
        if (SelectedIndicator == null || SelectedIndicator.TypeEnum == null) return;
        
        if (_defaultSettingsCache.TryGetValue(SelectedIndicator.TypeEnum.Value, out var defaultSettings))
        {
            // Replace current indicator with default clone in the list
            var index = Indicators.IndexOf(SelectedIndicator);
            if (index >= 0)
            {
                var newSettings = defaultSettings.Clone();
                newSettings.IsEnabled = true;
                newSettings.UpdateDisplayName();
                
                Indicators[index] = newSettings;
                SelectedIndicator = newSettings;
            }
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
        }
    }

    [RelayCommand]
    private void SelectTemplates()
    {
        IsTemplatesSelected = true;
    }

    [RelayCommand]
    private void SaveTemplate()
    {
        if (string.IsNullOrWhiteSpace(NewTemplateName)) return;
        if (!Indicators.Any()) return;

        var existing = Templates.FirstOrDefault(t => string.Equals(t.Name, NewTemplateName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.Indicators = Indicators.Select(i => i.Clone()).ToList();
        }
        else
        {
            var template = new IndicatorTemplate
            {
                Name = NewTemplateName.Trim(),
                Indicators = Indicators.Select(i => i.Clone()).ToList()
            };
            Templates.Add(template);
        }

        SaveTemplates(Templates.ToList());
        NewTemplateName = string.Empty;
        ToastService.ShowNotification("Template saved successfully");
    }

    [RelayCommand]
    private void LoadTemplate(IndicatorTemplate template)
    {
        if (template == null) return;
        Indicators.Clear();
        foreach (var ind in template.Indicators)
        {
            Indicators.Add(ind.Clone());
        }
        SelectedIndicator = Indicators.FirstOrDefault();
        ToastService.ShowNotification($"Template {template.Name} loaded");
        Apply();
    }

    [RelayCommand]
    private void DeleteTemplate(IndicatorTemplate template)
    {
        if (template == null) return;
        Templates.Remove(template);
        SaveTemplates(Templates.ToList());
        if (SelectedTemplate == template)
        {
            SelectedTemplate = null;
        }
        ToastService.ShowNotification("Template deleted");
    }

    private List<IndicatorTemplate> LoadTemplates()
    {
        try
        {
            if (File.Exists(TemplatesFilePath))
            {
                var json = File.ReadAllText(TemplatesFilePath);
                return JsonSerializer.Deserialize<List<IndicatorTemplate>>(json, JsonOptions) ?? new List<IndicatorTemplate>();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load indicator templates.");
        }
        return new List<IndicatorTemplate>();
    }

    private void SaveTemplates(List<IndicatorTemplate> templates)
    {
        try
        {
            var directory = Path.GetDirectoryName(TemplatesFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            var json = JsonSerializer.Serialize(templates, JsonOptions);
            File.WriteAllText(TemplatesFilePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save indicator templates.");
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        WeakReferenceMessenger.Default.UnregisterAll(this);
        GC.SuppressFinalize(this);
    }
}

public class IndicatorTemplate
{
    public string Name { get; set; } = string.Empty;
    public List<CoreIndicatorSettings> Indicators { get; set; } = new();
}
