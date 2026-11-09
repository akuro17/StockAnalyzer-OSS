using System;
using StockAnalyzer.Avalonia.Drawing;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>
/// Service interface for managing pre-configured drawing tool prototypes/presets.
/// Allows user to configure drawing object parameters in the sidebar before drawing on the chart canvas.
/// </summary>
public interface IDrawingToolPresetService
{
    /// <summary>
    /// Gets or creates a cached prototype <see cref="IChartObject"/> for the specified tool.
    /// Returns null for non-configurable tools (Pointer, Eraser, Ruler, Eyedropper).
    /// </summary>
    IChartObject? GetOrCreatePresetObject(DrawingTool tool);

    /// <summary>
    /// Applies the pre-configured preset styles and parameters from the tool prototype to the newly instantiated chart object.
    /// Preserves infrastructure and identity properties (Id, Points, ZIndex, IsSelected, IsVisible, IsLocked, etc.).
    /// </summary>
    void ApplyPresetToNewObject(DrawingTool tool, IChartObject target);

    /// <summary>
    /// Notifies listeners that a preset for the specified tool was modified.
    /// </summary>
    void NotifyPresetModified(DrawingTool tool);

    /// <summary>
    /// Event raised when a preset's properties have been modified.
    /// </summary>
    event Action<DrawingTool, IChartObject>? PresetChanged;
}
