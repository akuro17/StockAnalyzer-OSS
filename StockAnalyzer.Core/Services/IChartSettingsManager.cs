using StockAnalyzer.Core.Models.Settings;
using System;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Interface for managing and persisting global chart settings.
/// </summary>
public interface IChartSettingsManager
{
    /// <summary>
    /// GS-8-2: Gets the current settings as an immutable snapshot.
    /// </summary>
    GlobalChartSettings Current { get; }

    /// <summary>
    /// GS-12-1: Occurs when settings have been changed and persisted.
    /// </summary>
    event Action? SettingsChanged;

    /// <summary>
    /// GS-11: Updates and persists the settings with a debounce/throttle strategy.
    /// </summary>
    Task UpdateAsync(GlobalChartSettings settings);

    /// <summary>
    /// Updates the current settings snapshot in memory and notifies listeners, without persisting to disk.
    /// Used for real-time preview during editing.
    /// </summary>
    void UpdatePreview(GlobalChartSettings settings);

    /// <summary>
    /// GS-6-3: Reloads settings from disk, with fallback on corruption.
    /// </summary>
    Task LoadAsync();
}
