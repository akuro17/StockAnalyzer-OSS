using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Avalonia.Models;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>
/// ViewModel for the single-indicator properties dialog.
/// Operates on a deep clone of the original settings, publishing changes
/// via <see cref="SingleIndicatorSettingsChangedMessage"/>.
/// </summary>
public partial class IndicatorPropertiesViewModel : ObservableObject, IDisposable
{
    private readonly CoreIndicatorSettings _originalSnapshot;
    private readonly IMessenger _messenger;
    private readonly IDispatcherService _dispatcherService;
    private bool _isDisposed;

    [ObservableProperty]
    private CoreIndicatorSettings _editingSettings;

    [ObservableProperty]
    private bool? _dialogResult;

    /// <summary>
    /// List of available price types for price source selection.
    /// Ordered as Open, High, Low, Close, Median, Typical, Weighted via PriceDataHelper.
    /// </summary>
    public IReadOnlyList<PriceType> PriceTypeOptions { get; } = PriceDataHelper.PriceTypeOptions;

    /// <summary>
    /// Available source indicators that can be chained as input series (excluding self).
    /// </summary>
    public ObservableCollection<IndicatorReferenceOption> AvailableSourceIndicators { get; } = new();

    /// <summary>
    /// Available dynamic period drivers (excluding self).
    /// </summary>
    public ObservableCollection<IndicatorReferenceOption> AvailableDynamicPeriodDrivers { get; } = new();

    public IndicatorReferenceOption? SelectedSourceIndicatorOption
    {
        get
        {
            if (EditingSettings == null || string.IsNullOrEmpty(EditingSettings.SourceIndicatorId))
                return AvailableSourceIndicators.FirstOrDefault();
            return AvailableSourceIndicators.FirstOrDefault(o => o.Id == EditingSettings.SourceIndicatorId) 
                   ?? AvailableSourceIndicators.FirstOrDefault();
        }
        set
        {
            if (EditingSettings != null)
            {
                EditingSettings.SourceIndicatorId = value?.Id;
                OnPropertyChanged();
            }
        }
    }

    public IndicatorReferenceOption? SelectedDynamicPeriodDriverOption
    {
        get
        {
            if (EditingSettings == null || string.IsNullOrEmpty(EditingSettings.DynamicPeriodIndicatorId))
                return AvailableDynamicPeriodDrivers.FirstOrDefault();
            return AvailableDynamicPeriodDrivers.FirstOrDefault(o => o.Id == EditingSettings.DynamicPeriodIndicatorId)
                   ?? AvailableDynamicPeriodDrivers.FirstOrDefault();
        }
        set
        {
            if (EditingSettings != null)
            {
                EditingSettings.DynamicPeriodIndicatorId = value?.Id;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasDynamicPeriodDriver));
                OnPropertyChanged(nameof(HiddenParameterTags));
            }
        }
    }

    /// <summary>
    /// Computed pass-through over EditingSettings.DynamicPeriodIndicatorId, exposed as a checkbox-friendly
    /// boolean. Turning it on auto-selects the first available driver (if any); turning it off clears the
    /// driver id (reverting SelectedDynamicPeriodDriverOption to the "(None / Static Parameter)" sentinel).
    /// No independent state is stored, so this can never desync from the combobox selection.
    /// </summary>
    public bool HasDynamicPeriodDriver
    {
        get => DynamicPeriodDriverHelper.GetHasDynamicPeriodDriver(EditingSettings);
        set
        {
            if (EditingSettings == null) return;

            bool changed = DynamicPeriodDriverHelper.TrySetHasDynamicPeriodDriver(
                EditingSettings, AvailableDynamicPeriodDrivers, value);

            OnPropertyChanged();
            if (changed)
            {
                OnPropertyChanged(nameof(SelectedDynamicPeriodDriverOption));
                OnPropertyChanged(nameof(HiddenParameterTags));
            }
        }
    }

    /// <summary>
    /// Parameter tags to hide in the reflection-based settings panel for the edited indicator,
    /// derived from HasDynamicPeriodDriver. See ParameterTagAttribute / ParameterViewBuilder.
    /// </summary>
    public IReadOnlyCollection<string> HiddenParameterTags =>
        DynamicPeriodDriverHelper.GetHiddenParameterTags(HasDynamicPeriodDriver);

    /// <summary>
    /// Whether the edited indicator's parameter object supports dynamic period driver.
    /// Used as capability gate to show/hide the dynamic period driver checkbox and dropdown.
    /// </summary>
    public bool SupportsDynamicPeriod =>
        DynamicPeriodDriverHelper.GetSupportsDynamicPeriod(EditingSettings?.ParameterObject);

    /// <summary>
    /// Whether the panel selector should be visible (non-overlay, non-GranvilleLaw).
    /// </summary>
    public bool ShowPanelSelector => EditingSettings != null
        && !EditingSettings.IsOverlay
        && EditingSettings.TypeEnum != IndicatorType.GranvilleLaw;

    public bool ShowThicknessAndOffset => EditingSettings?.TypeEnum != IndicatorType.GranvilleLaw;
    public bool ShowUpDownColors => EditingSettings?.TypeEnum != IndicatorType.GranvilleLaw;

    public bool ShowSignals => EditingSettings != null &&
        (EditingSettings.SeriesColors.Any(c => c.TargetSeries.Contains("Signals")) ||
         EditingSettings.TypeEnum == IndicatorType.MACD ||
         EditingSettings.TypeEnum == IndicatorType.NATR);

    /// <summary>
    /// Panel selection options for the overlay panel ComboBox.
    /// </summary>
    public List<string> PanelOptions { get; } = BuildPanelOptions();

    /// <summary>
    /// Gets/sets the selected overlay panel option.
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
        }
    }

    public Action<bool?>? RequestClose { get; set; }
    public Action<CoreIndicatorSettings>? OnApplyCallback { get; set; }

    public IndicatorPropertiesViewModel(
        CoreIndicatorSettings indicator, 
        IMessenger messenger, 
        IDispatcherService dispatcherService,
        IEnumerable<CoreIndicatorSettings>? allIndicators = null)
    {
        // Keep original snapshot for Reset; work on a clone for editing
        _originalSnapshot = indicator;
        _messenger = messenger;
        _dispatcherService = dispatcherService;
        _editingSettings = indicator.Clone();
        // Preserve original Id so we can match it back
        _editingSettings.Id = indicator.Id;
        // Reset MathVersion to 0 so it acts as a "delta" during the editing session.
        // Only mathematical parameter changes will increment it from 0.
        _editingSettings.MathematicalVersion = 0;

        BuildReferenceOptions(allIndicators);
    }

    private void BuildReferenceOptions(IEnumerable<CoreIndicatorSettings>? allIndicators)
    {
        IndicatorReferenceHelper.PopulateReferenceOptions(
            AvailableSourceIndicators,
            AvailableDynamicPeriodDrivers,
            allIndicators,
            _editingSettings?.Id,
            _editingSettings?.SourceIndicatorId,
            _editingSettings?.DynamicPeriodIndicatorId);
    }

    [RelayCommand]
    private void Apply()
    {
        if (_isDisposed) return;
        
        if (OnApplyCallback != null)
        {
            OnApplyCallback.Invoke(EditingSettings);
        }
        else
        {
            _messenger.Send(
                new SingleIndicatorSettingsChangedMessage(EditingSettings));
        }
    }

    [RelayCommand]
    private void Ok()
    {
        if (_isDisposed) return;
        
        if (OnApplyCallback != null)
        {
            OnApplyCallback.Invoke(EditingSettings);
        }
        else
        {
            _messenger.Send(
                new SingleIndicatorSettingsChangedMessage(EditingSettings));
        }
        
        DialogResult = true;
        RequestClose?.Invoke(true);
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
        if (_isDisposed) return;
        // 1. Restore to the state when dialog opened (user expectation)
        var restored = _originalSnapshot.Clone();
        restored.Id = _originalSnapshot.Id;
        // Force a full recalc by incrementing MathVersion (Clone resets to 0)
        restored.MathematicalVersion = 1;

        // 2. Apply restored values
        EditingSettings = restored;

        OnPropertyChanged(nameof(ShowPanelSelector));
        OnPropertyChanged(nameof(ShowThicknessAndOffset));
        OnPropertyChanged(nameof(ShowUpDownColors));
        OnPropertyChanged(nameof(ShowSignals));
        OnPropertyChanged(nameof(SelectedOverlayPanelOption));
        OnPropertyChanged(nameof(SelectedSourceIndicatorOption));
        OnPropertyChanged(nameof(SelectedDynamicPeriodDriverOption));
        OnPropertyChanged(nameof(HasDynamicPeriodDriver));
        OnPropertyChanged(nameof(HiddenParameterTags));
        OnPropertyChanged(nameof(SupportsDynamicPeriod));
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
    
    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _messenger.UnregisterAll(this);
        GC.SuppressFinalize(this);
    }
}
