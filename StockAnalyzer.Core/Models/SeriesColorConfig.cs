using CommunityToolkit.Mvvm.ComponentModel;
using StockAnalyzer.Core.Models.Rendering;

namespace StockAnalyzer.Core.Models;

/// <summary>
/// Configuration for a specific series or group of series within an indicator.
/// </summary>
public partial class SeriesColorConfig : ObservableObject
{
    /// <summary>
    /// Unique identifier for this color setting within the indicator.
    /// Usually matches one of the TargetSeries if it's a single series, or a descriptive group name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Display name shown in the UI.
    /// </summary>
    [ObservableProperty]
    private string _displayName = string.Empty;

    /// <summary>
    /// The color for this series/group.
    /// </summary>
    [ObservableProperty]
    private IndicatorColor _color;

    /// <summary>
    /// List of series names that this color applies to.
    /// </summary>
    public List<string> TargetSeries { get; set; } = new();

    /// <summary>
    /// Whether the series matching this configuration is visible on the chart (visual-only).
    /// </summary>
    [ObservableProperty]
    private bool _isVisible = true;

    /// <summary>
    /// Optional per-series rendering primitive override (visual-only).
    /// Null resolves to the indicator or series legacy default.
    /// </summary>
    [ObservableProperty]
    private SeriesPrimitiveType? _renderPrimitive;

    public SeriesColorConfig Duplicate()
    {
        return new SeriesColorConfig
        {
            Name = Name,
            DisplayName = DisplayName,
            Color = Color, // Struct copy
            TargetSeries = TargetSeries != null ? new List<string>(TargetSeries) : new List<string>(),
            IsVisible = IsVisible,
            RenderPrimitive = RenderPrimitive
        };
    }
}
