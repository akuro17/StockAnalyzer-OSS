using StockAnalyzer.Core.Models;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Theme;

/// <summary>
/// Interface for Managing the global application theme colors and styling.
/// </summary>
public interface IThemeManager : INotifyPropertyChanged
{
    ThemeColors CurrentTheme { get; }
    AppThemeMode CurrentMode { get; }
    void ChangeTheme(ThemeColors newTheme);
    void SetThemeMode(AppThemeMode mode);

    /// <summary>
    /// Updates a single color item without necessarily saving to persistence.
    /// Used for live preview and granular control.
    /// </summary>
    void UpdateSingleColor(ThemeColorKey key, IndicatorColor color);

    /// <summary>
    /// Returns the current mapping of all configurable theme colors.
    /// </summary>
    IReadOnlyDictionary<ThemeColorKey, IndicatorColor> GetCurrentColors();

    /// <summary>
    /// Saves the current theme settings to a persistent storage.
    /// </summary>
    Task SaveAsync();

    /// <summary>
    /// Loads the theme settings from persistent storage.
    /// </summary>
    Task LoadAsync();
}
