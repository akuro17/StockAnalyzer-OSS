using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>
/// Optional per-tool override for the shared DrawingSettingsDialog's window sizing.
/// Null fields keep the dialog's own default (360px, fixed).
/// </summary>
public sealed class DrawingSettingsWindowHint
{
    public double? Width { get; init; }
    public double? MinWidth { get; init; }
    public bool CanResize { get; init; }
}

/// <summary>
/// A drawing tool's settings-dialog behavior, resolved by <see cref="IDrawingSettingsPanelRegistry"/>
/// from the concrete <see cref="IChartObject"/> type instead of a hardcoded branch inside
/// DrawingSettingsDialog. New tools plug in by implementing this interface and registering an
/// instance in ServiceCollectionExtensions — no edits to DrawingSettingsDialog.axaml.cs are ever
/// required. DrawingSettingsDialog.axaml itself only stays untouched when a tool reuses an
/// existing panel's markup (as LineText/CurveLineText do); a tool with genuinely new UI fields
/// still needs its panel markup added there, since <see cref="Activate"/>/<see cref="Populate"/>/
/// <see cref="Commit"/> resolve controls by name from that shared window.
/// </summary>
public interface IDrawingSettingsPanelDefinition
{
    bool CanHandle(IChartObject drawing);

    DrawingSettingsWindowHint? WindowHint { get; }

    /// <summary>True if this definition syncs the dialog's shared ColorPickerControl's width
    /// itself (e.g. to a narrower column inside its own panel) in <see cref="Activate"/>. When
    /// true, the dialog's generic full-content-width stretch handler must skip that control so
    /// the two width sources don't fight.</summary>
    bool ManagesGenericColorPickerWidth => false;

    /// <summary>Makes this tool's panel(s) in the dialog visible and performs any one-time
    /// layout adjustments (e.g. moving shared controls into a tool-specific column).</summary>
    void Activate(Window dialogWindow);

    /// <summary>Pushes the drawing object's current values into the dialog's controls.</summary>
    void Populate(Window dialogWindow, IChartObject drawing);

    /// <summary>Reads the dialog's controls back into the drawing object on OK.</summary>
    void Commit(Window dialogWindow, IChartObject drawing);
}
