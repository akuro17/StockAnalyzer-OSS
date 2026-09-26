using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.Services.Export;

/// <summary>
/// Lightweight, read-only IThemeManager implementation used exclusively during chart image export.
/// </summary>
public sealed class ExportThemeManager : IThemeManager
{
    public event PropertyChangedEventHandler? PropertyChanged
    {
        add { }
        remove { }
    }

    public ThemeColors CurrentTheme { get; }
    public AppThemeMode CurrentMode { get; }

    public ExportThemeManager(ThemeColors theme, AppThemeMode mode)
    {
        CurrentTheme = theme;
        CurrentMode = mode;
    }

    public void ChangeTheme(ThemeColors newTheme) { }
    public void SetThemeMode(AppThemeMode mode) { }
    public void UpdateSingleColor(ThemeColorKey key, IndicatorColor color) { }
    public IReadOnlyDictionary<ThemeColorKey, IndicatorColor> GetCurrentColors() => new Dictionary<ThemeColorKey, IndicatorColor>();
    public Task SaveAsync() => Task.CompletedTask;
    public Task LoadAsync() => Task.CompletedTask;
}
