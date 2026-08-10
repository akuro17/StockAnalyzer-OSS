using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>
/// Lightweight model representing an indicator in the Library catalog grid.
/// Used for browsing and adding indicators from the Library tab.
/// </summary>
public class IndicatorCatalogItem
{
    /// <summary>
    /// The indicator type enum value used for adding the indicator.
    /// </summary>
    public IndicatorType Type { get; init; }

    /// <summary>
    /// Full display name (e.g., "Simple Moving Average").
    /// </summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>
    /// Short programmatic name (e.g., "SMA").
    /// </summary>
    public string ShortName { get; init; } = string.Empty;

    /// <summary>
    /// Category for grouping in the accordion/filter.
    /// </summary>
    public CoreIndicatorCategory Category { get; init; }
}
