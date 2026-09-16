namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Opt-in interface for ellipse-family chart objects (Ellipse and, in future sprints, the
/// arc/sector/annulus family sharing the same geometric parameterization) that support toggling
/// between a freely-proportioned ellipse and an aspect-ratio-locked circle without changing their
/// underlying Points structure.
/// </summary>
public interface IEllipseFamilyObject : IFillableChartObject
{
    /// <summary>
    /// When true, rendering and hit-testing constrain the shape to a circle (equal radii) derived
    /// from the existing bounding points, rather than an independently-scaled ellipse.
    /// </summary>
    bool IsCircular { get; set; }
}
