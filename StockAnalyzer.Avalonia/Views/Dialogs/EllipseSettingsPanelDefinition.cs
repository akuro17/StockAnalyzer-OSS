using System;
using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

/// <summary>
/// Settings-dialog behavior for the unified <see cref="EllipseObject"/>: the Circle/Ellipse
/// aspect-ratio toggle, the Arc-enable toggle, and two independent line-display toggles (Radius
/// Lines, Chord). The two circumference-constrained angle control points (Points[2]/[3]) always exist
/// (added by <see cref="EllipseObject"/>'s own constructor) and are shown/draggable regardless of the
/// Arc toggle — <see cref="IsArcEnabled"/> only switches whether the shape renders/hit-tests as a
/// plain closed ellipse or a partial arc. Commit still carries a one-time migration for objects
/// persisted before this control-point model existed (Points.Count &lt; 4), so opening this dialog on
/// old saved data brings it up to the current 4-point shape.
/// </summary>
public sealed class EllipseSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is EllipseObject;

    public void Activate(Window dialogWindow)
    {
        var panel = dialogWindow.FindControl<StackPanel>("EllipsePanel");
        if (panel != null) panel.IsVisible = true;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not EllipseObject ellipse) return;

        // The checkbox represents "Ellipse mode" (independent aspect ratio), the inverse of IsCircular:
        // unchecked (the default) = Circle, checked = Ellipse.
        var circularCheck = dialogWindow.FindControl<CheckBox>("EllipseCircularCheck");
        if (circularCheck != null) circularCheck.IsChecked = !ellipse.IsCircular;

        var arcEnabledCheck = dialogWindow.FindControl<CheckBox>("EllipseArcEnabledCheck");
        if (arcEnabledCheck != null) arcEnabledCheck.IsChecked = ellipse.IsArcEnabled;

        var radiusLinesCheck = dialogWindow.FindControl<CheckBox>("EllipseShowRadiusLinesCheck");
        if (radiusLinesCheck != null) radiusLinesCheck.IsChecked = ellipse.ShowRadiusLines;

        var chordLineCheck = dialogWindow.FindControl<CheckBox>("EllipseShowChordLineCheck");
        if (chordLineCheck != null) chordLineCheck.IsChecked = ellipse.ShowChordLine;

        var tangentLinesCheck = dialogWindow.FindControl<CheckBox>("EllipseShowTangentLinesCheck");
        if (tangentLinesCheck != null) tangentLinesCheck.IsChecked = ellipse.ShowTangentLines;

        var extendTangentLinesCheck = dialogWindow.FindControl<CheckBox>("EllipseExtendTangentLinesToChartCheck");
        if (extendTangentLinesCheck != null) extendTangentLinesCheck.IsChecked = ellipse.ExtendTangentLinesToChart;

        var aspectRatioSpin = dialogWindow.FindControl<NumericUpDown>("EllipseAspectRatioSpin");
        if (aspectRatioSpin != null) aspectRatioSpin.Value = (decimal)ellipse.AspectRatio;

        var dynamicAspectRatioCheck = dialogWindow.FindControl<CheckBox>("EllipseDynamicAspectRatioCheck");
        if (dynamicAspectRatioCheck != null) dynamicAspectRatioCheck.IsChecked = ellipse.DynamicAspectRatioByDistance;

        var innerRadiusSpin = dialogWindow.FindControl<NumericUpDown>("EllipseInnerRadiusSpin");
        if (innerRadiusSpin != null) innerRadiusSpin.Value = (decimal)ellipse.InnerRadiusRatio;

        var controlPointColorPicker = dialogWindow.FindControl<ColorPicker>("EllipseControlPointColorPicker");
        if (controlPointColorPicker != null) controlPointColorPicker.Color = ellipse.ControlPointColor;
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not EllipseObject ellipse) return;

        // One-time migration for objects persisted before circumference control points always existed
        // (see EllipseObject's constructor): back-fill Points[2]/[3] on the boundary opposite the
        // corner's direction (same default EllipseObject's constructor uses) so old saved data gains
        // the same always-on, non-overlapping handles as newly-created objects.
        if (ellipse.Points.Count < 4 && ellipse.Points.Count >= 2)
        {
            var boundaryPoint = EllipseArcGeometry.ComputeChartSpaceOppositeBoundaryPoint(ellipse.Points[0], ellipse.Points[1]);
            ellipse.Points.Add(boundaryPoint);
            ellipse.Points.Add(boundaryPoint);
        }

        // Captured before IsCircular/AspectRatio are overwritten below, so the rising-edge capture of
        // DynamicAspectRatioReferenceCorner (further down) can reconstruct the EFFECTIVE aspect ratio
        // the shape had a moment ago — not just whatever AspectRatio happens to be stored (which is
        // ignored/stale while IsCircular is true).
        bool oldIsCircular = ellipse.IsCircular;
        double oldAspectRatio = ellipse.AspectRatio;

        var circularCheck = dialogWindow.FindControl<CheckBox>("EllipseCircularCheck");
        if (circularCheck?.IsChecked != null)
        {
            bool newIsCircular = !circularCheck.IsChecked.Value;
            // Rising edge (Ellipse Mode just turned ON): capture the corner's current position so the
            // shape keeps looking exactly like the circle it was until the corner is actually dragged —
            // see EllipticityActivationCorner's remarks for why enabling the mode must not itself change
            // the shape's appearance.
            if (!newIsCircular && oldIsCircular && ellipse.Points.Count >= 2)
            {
                ellipse.EllipticityActivationCorner = ellipse.Points[1];
            }
            ellipse.IsCircular = newIsCircular;
        }

        var arcEnabledCheck = dialogWindow.FindControl<CheckBox>("EllipseArcEnabledCheck");
        if (arcEnabledCheck?.IsChecked != null) ellipse.IsArcEnabled = arcEnabledCheck.IsChecked.Value;

        var radiusLinesCheck = dialogWindow.FindControl<CheckBox>("EllipseShowRadiusLinesCheck");
        if (radiusLinesCheck?.IsChecked != null) ellipse.ShowRadiusLines = radiusLinesCheck.IsChecked.Value;

        var chordLineCheck = dialogWindow.FindControl<CheckBox>("EllipseShowChordLineCheck");
        if (chordLineCheck?.IsChecked != null) ellipse.ShowChordLine = chordLineCheck.IsChecked.Value;

        var tangentLinesCheck = dialogWindow.FindControl<CheckBox>("EllipseShowTangentLinesCheck");
        if (tangentLinesCheck?.IsChecked != null) ellipse.ShowTangentLines = tangentLinesCheck.IsChecked.Value;

        var extendTangentLinesCheck = dialogWindow.FindControl<CheckBox>("EllipseExtendTangentLinesToChartCheck");
        if (extendTangentLinesCheck?.IsChecked != null) ellipse.ExtendTangentLinesToChart = extendTangentLinesCheck.IsChecked.Value;

        var aspectRatioSpin = dialogWindow.FindControl<NumericUpDown>("EllipseAspectRatioSpin");
        if (aspectRatioSpin?.Value != null) ellipse.AspectRatio = (double)aspectRatioSpin.Value;

        var dynamicAspectRatioCheck = dialogWindow.FindControl<CheckBox>("EllipseDynamicAspectRatioCheck");
        bool newDynamicAspectRatio = dynamicAspectRatioCheck?.IsChecked ?? ellipse.DynamicAspectRatioByDistance;
        if (newDynamicAspectRatio && !ellipse.DynamicAspectRatioByDistance && ellipse.Points.Count >= 2)
        {
            // Rising edge: lock in a reference circle whose AREA matches the shape's CURRENT
            // appearance (using the effective aspect ratio just before this toggle, oldIsCircular ?
            // 1.0 : oldAspectRatio), not the raw corner distance. A raw copy would make Ry snap to Rx
            // the instant this is turned on (an equivalent-radius reference always has distance ==
            // Rx == Ry at that moment), discarding whatever elongation the ellipse already had —
            // turning this on must continue smoothly from the shape as it currently looks, only
            // changing as the corner is subsequently dragged. Scaling the chart-space corner vector by
            // sqrt(effective aspect ratio) reproduces exactly this equivalent-area radius; see
            // ScaleCornerInChartSpace's remarks for why this needs no ICoordinateTransform.
            double effectiveAspectRatioBeforeToggle = oldIsCircular ? 1.0 : oldAspectRatio;
            double scale = Math.Sqrt(Math.Max(effectiveAspectRatioBeforeToggle, 1e-6));
            ellipse.DynamicAspectRatioReferenceCorner = EllipseArcGeometry.ScaleCornerInChartSpace(ellipse.Points[0], ellipse.Points[1], scale);
        }
        ellipse.DynamicAspectRatioByDistance = newDynamicAspectRatio;

        var innerRadiusSpin = dialogWindow.FindControl<NumericUpDown>("EllipseInnerRadiusSpin");
        if (innerRadiusSpin?.Value != null) ellipse.InnerRadiusRatio = (double)innerRadiusSpin.Value;

        var controlPointColorPicker = dialogWindow.FindControl<ColorPicker>("EllipseControlPointColorPicker");
        if (controlPointColorPicker != null) ellipse.ControlPointColor = controlPointColorPicker.Color;
    }
}
