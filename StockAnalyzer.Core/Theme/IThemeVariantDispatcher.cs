namespace StockAnalyzer.Core.Theme;

/// <summary>
/// Interface for a dispatcher that applies theme changes to the UI framework.
/// This decouples the Core logic from specific UI implementations (e.g., Avalonia).
/// </summary>
public interface IThemeVariantDispatcher
{
    /// <summary>
    /// Applies the specified theme mode to the current application.
    /// </summary>
    /// <param name="mode">The theme mode to apply.</param>
    void ApplyTheme(AppThemeMode mode);

    /// <summary>
    /// Gets the actual theme mode currently in use by the UI framework (e.g., when Mode is System).
    /// </summary>
    AppThemeMode GetActualThemeMode();
}
