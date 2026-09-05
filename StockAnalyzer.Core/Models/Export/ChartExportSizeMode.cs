namespace StockAnalyzer.Core.Models.Export;

/// <summary>
/// Defines image sizing presets for chart export.
/// </summary>
public enum ChartExportSizeMode
{
    /// <summary>
    /// Uses current window/chart display dimensions.
    /// </summary>
    CurrentWindow = 0,

    /// <summary>
    /// 1280 x 720 px (HD).
    /// </summary>
    Preset1280x720 = 1,

    /// <summary>
    /// 1920 x 1080 px (Full HD).
    /// </summary>
    Preset1920x1080 = 2,

    /// <summary>
    /// 3840 x 2160 px (4K UHD).
    /// </summary>
    Preset3840x2160 = 3,

    /// <summary>
    /// Custom width and height in pixels.
    /// </summary>
    Custom = 4
}
