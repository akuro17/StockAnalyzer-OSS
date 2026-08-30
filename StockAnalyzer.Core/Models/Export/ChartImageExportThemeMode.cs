namespace StockAnalyzer.Core.Models.Export;

/// <summary>
/// Defines the theme mode to apply when exporting a chart image.
/// </summary>
public enum ChartImageExportThemeMode
{
    /// <summary>
    /// Uses the currently active theme in the application.
    /// </summary>
    Current = 0,

    /// <summary>
    /// Forces a Dark theme background and colors for export.
    /// </summary>
    Dark = 1,

    /// <summary>
    /// Forces a Light theme background and colors for export.
    /// </summary>
    Light = 2
}
