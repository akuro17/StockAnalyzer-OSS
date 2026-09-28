namespace StockAnalyzer.Core.Theme;

/// <summary>
/// Defines the available theme modes for the application.
/// </summary>
public enum AppThemeMode : byte
{
    /// <summary>
    /// Follow the system (OS) theme settings.
    /// </summary>
    System = 0,

    /// <summary>
    /// Force Light theme.
    /// </summary>
    Light = 1,

    /// <summary>
    /// Force Dark theme.
    /// </summary>
    Dark = 2
}
