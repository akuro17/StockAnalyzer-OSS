using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Resolves the display name shown for a drawing object across the Layers Panel and the Drawing
/// Settings dialog: the user's <see cref="IChartObject.CustomName"/> if set, otherwise the
/// localized type name. Kept as a single SSoT so both UI surfaces stay in sync automatically.
/// </summary>
public static class DrawingObjectDisplayNameHelper
{
    public static string GetDisplayName(IChartObject obj)
    {
        return string.IsNullOrEmpty(obj.CustomName)
            ? (LocalizationManager.Instance[$"DrawTool_{obj.Type}"] ?? obj.Type.ToString())
            : obj.CustomName;
    }
}
