using System.Collections.Generic;
using SkiaSharp;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Interface for objects that project values onto the Y-axis (right axis).
/// Implementors provide a set of axis label requests that the AxisLabelRenderer
/// will collect and render with automatic anti-overlap handling.
/// </summary>
public interface IAxisProjectable
{
    /// <summary>
    /// Returns the set of axis label projection requests.
    /// Each request describes a value, color, label text, and style
    /// to be rendered on the Y-axis.
    /// </summary>
    IEnumerable<AxisLabelRequest> GetAxisProjections(ChartDataSnapshot snapshot, IChartRenderConfig config);
}

/// <summary>
/// Describes a single label request for the Y-axis projection.
/// Uses record struct for zero-allocation semantics.
/// </summary>
/// <param name="Value">The price/value to project onto the Y-axis.</param>
/// <param name="Color">The color for the label background and pointer.</param>
/// <param name="Label">The text to display inside the label.</param>
/// <param name="Style">The visual style of the label.</param>
public readonly record struct AxisLabelRequest(
    decimal Value,
    SKColor Color,
    string Label,
    AxisLabelStyle Style = AxisLabelStyle.Default,
    int PanelIndex = -1
);

/// <summary>
/// Visual styles for Y-axis projection labels.
/// </summary>
public enum AxisLabelStyle
{
    /// <summary>Default label style with rounded rectangle background.</summary>
    Default,

    /// <summary>Style for current/latest price indicator (filled background).</summary>
    CurrentPrice,

    /// <summary>Style for target price projections (outline style).</summary>
    TargetPrice,

    /// <summary>Style for labels in the right panel area without an arrow pointer.</summary>
    PanelOnly
}
