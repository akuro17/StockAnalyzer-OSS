using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>
/// Abstract base class for all chart-specific settings ViewModels.
/// Handles IChartSettingsManager integration, Rx-based throttled preview, and change management.
/// </summary>
public abstract partial class BaseChartSettingsViewModel : ViewModelBase, ISettingsPageViewModel, IDisposable
{
    protected readonly IChartSettingsManager SettingsManager;
    protected readonly ILogger Logger;
    protected GlobalChartSettings Snapshot;
    
    private bool _isDisposed;
    private readonly IDisposable? _autoSaveSubscription;

    public abstract string TitleKey { get; }
    public abstract string IconKey { get; }

    public abstract bool IsModified { get; }

    protected BaseChartSettingsViewModel(IChartSettingsManager settingsManager, ILogger? logger = null)
    {
        SettingsManager = settingsManager;
        Logger = logger ?? NullLogger.Instance;
        Snapshot = settingsManager.Current;

        LoadFromSettings(Snapshot);
        UpdateInitialState();

        // Setup Rx Preview (Debounce 500ms)
        // Provides real-time visual feedback to the chart without persisting to disk on every keystroke.
        _autoSaveSubscription = Observable.FromEventPattern<PropertyChangedEventHandler, PropertyChangedEventArgs>(
                h => this.PropertyChanged += h,
                h => this.PropertyChanged -= h)
            .Where(x => x.EventArgs.PropertyName != nameof(IsModified))
            .Throttle(TimeSpan.FromMilliseconds(Models.SettingsConstants.SettingsPreviewThrottleIntervalMs))
            .Subscribe(_ => 
            {
                if (!_isDisposed)
                {
                    Logger.LogDebug("Updating chart preview (Throttled) for {Type}", GetType().Name);
                    SettingsManager.UpdatePreview(CreateSettings());
                }
            });
    }

    /// <summary>
    /// Loads properties from the provided settings snapshot.
    /// </summary>
    protected abstract void LoadFromSettings(GlobalChartSettings settings);

    /// <summary>
    /// Captures the current property values as the 'initial' state for IsModified comparison.
    /// </summary>
    protected virtual void UpdateInitialState() { }

    /// <summary>
    /// Creates a new GlobalChartSettings snapshot based on the current UI state.
    /// </summary>
    protected abstract GlobalChartSettings CreateSettings();

    public virtual async Task SaveChangesAsync()
    {
        if (_isDisposed) return;

        var newSettings = CreateSettings();
        await SettingsManager.UpdateAsync(newSettings);
        Snapshot = newSettings;
        
        // Final reload to ensure UI reflects any clamped/validated values from the core model
        LoadFromSettings(Snapshot);
        UpdateInitialState();
        OnPropertyChanged(nameof(IsModified));
    }

    public virtual void RevertChanges()
    {
        if (_isDisposed) return;
        
        // Restore live preview to original snapshot
        SettingsManager.UpdatePreview(Snapshot);
        
        LoadFromSettings(Snapshot);
        UpdateInitialState();
        OnPropertyChanged(nameof(IsModified));
    }

    public virtual void ResetToDefault()
    {
        if (_isDisposed) return;
        
        // Load defaults into properties, but do NOT update initial state.
        // This ensures IsModified returns true if defaults differ from current Snapshot.
        LoadFromSettings(new GlobalChartSettings());
        
        // Trigger preview update immediately
        SettingsManager.UpdatePreview(CreateSettings());
        
        OnPropertyChanged(nameof(IsModified));
    }

    public virtual void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _autoSaveSubscription?.Dispose();
        GC.SuppressFinalize(this);
    }
}
