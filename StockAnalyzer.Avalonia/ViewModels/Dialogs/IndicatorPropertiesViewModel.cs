using System;
using System.Collections.Generic;
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

    public IndicatorPropertiesViewModel(CoreIndicatorSettings indicator, IMessenger messenger, IDispatcherService dispatcherService)
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

        // Subscribe to changes for real-time preview (via debounced Command)
        _editingSettings.PropertyChanged += PropertyChangedHandler;
        if (_editingSettings.ParameterObject is System.ComponentModel.INotifyPropertyChanged npc)
        {
            npc.PropertyChanged += PropertyChangedHandler;
        }
        
        HookSeriesColors(_editingSettings.SeriesColors);
        _editingSettings.SeriesColors.CollectionChanged += CollectionChangedHandler;
    }

    private void HookSeriesColors(System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig> collection)
    {
        foreach (var item in collection)
        {
            item.PropertyChanged -= SeriesColorItem_PropertyChanged;
            item.PropertyChanged += SeriesColorItem_PropertyChanged;
        }
    }

    private void SeriesColorItem_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        ApplyCommand.Execute(null);
    }

    private System.Threading.CancellationTokenSource? _applyDebounceCts;

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (_isDisposed) return;
        _applyDebounceCts?.Cancel();
        _applyDebounceCts?.Dispose();
        _applyDebounceCts = new System.Threading.CancellationTokenSource();
        var cts = _applyDebounceCts;

        try
        {
            // Small delay to debounce rapid property changes (e.g. typing)
            await Task.Delay(SettingsConstants.UI_FastInputThrottleMs, cts.Token);
            
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
        catch (OperationCanceledException)
        {
            // Cancelled by a newer change
        }
    }

    [RelayCommand]
    private void Ok()
    {
        if (_isDisposed) return;
        // Cancel any pending debounced apply and do an immediate one
        _applyDebounceCts?.Cancel();
        
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
        
        // 2. Unhook old settings
        EditingSettings.PropertyChanged -= PropertyChangedHandler;
        if (EditingSettings.ParameterObject is System.ComponentModel.INotifyPropertyChanged oldNpc)
        {
            oldNpc.PropertyChanged -= PropertyChangedHandler;
        }
        EditingSettings.SeriesColors.CollectionChanged -= CollectionChangedHandler;
        UnhookSeriesColors(EditingSettings.SeriesColors);

        // 3. Apply restored values
        EditingSettings = restored;

        // 4. Hook new settings
        EditingSettings.PropertyChanged += PropertyChangedHandler;
        if (EditingSettings.ParameterObject is System.ComponentModel.INotifyPropertyChanged newNpc)
        {
            newNpc.PropertyChanged += PropertyChangedHandler;
        }
        EditingSettings.SeriesColors.CollectionChanged += CollectionChangedHandler;
        HookSeriesColors(EditingSettings.SeriesColors);
        
        // Immediate preview
        ApplyCommand.Execute(null);

        OnPropertyChanged(nameof(ShowPanelSelector));
        OnPropertyChanged(nameof(ShowThicknessAndOffset));
        OnPropertyChanged(nameof(ShowUpDownColors));
        OnPropertyChanged(nameof(ShowSignals));
        OnPropertyChanged(nameof(SelectedOverlayPanelOption));
    }

    private void PropertyChangedHandler(object? s, System.ComponentModel.PropertyChangedEventArgs e) => ApplyCommand.Execute(null);
    private void CollectionChangedHandler(object? s, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) 
    {
        HookSeriesColors(EditingSettings.SeriesColors);
        ApplyCommand.Execute(null);
    }
    
    private void UnhookSeriesColors(System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig> collection)
    {
        foreach (var item in collection)
        {
            item.PropertyChanged -= SeriesColorItem_PropertyChanged;
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
    
    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _editingSettings.PropertyChanged -= PropertyChangedHandler;
        if (_editingSettings.ParameterObject is System.ComponentModel.INotifyPropertyChanged npc)
        {
            npc.PropertyChanged -= PropertyChangedHandler;
        }

        _editingSettings.SeriesColors.CollectionChanged -= CollectionChangedHandler;
        UnhookSeriesColors(_editingSettings.SeriesColors);

        _applyDebounceCts?.Cancel();
        _applyDebounceCts?.Dispose();

        _messenger.UnregisterAll(this);
        GC.SuppressFinalize(this);
    }
}
