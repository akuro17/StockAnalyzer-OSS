using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

/// <summary>
/// Settings-dialog behavior for <see cref="EllipseAnnulusObject"/>: the shared Circle/Ellipse toggle
/// (see <see cref="EllipseCircularSettingsPanelDefinition"/> for the plain Ellipse tool's own copy)
/// plus an inner-radius fine-tuning NumericUpDown (mirrors <see cref="NurbsWeightedCurveSettingsPanelDefinition"/>'s
/// Weight spinner pattern).
/// </summary>
public sealed class EllipseAnnulusSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is EllipseAnnulusObject;

    public void Activate(Window dialogWindow)
    {
        var panel = dialogWindow.FindControl<StackPanel>("EllipseAnnulusPanel");
        if (panel != null) panel.IsVisible = true;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not EllipseAnnulusObject ring) return;
        var circularCheck = dialogWindow.FindControl<CheckBox>("EllipseAnnulusCircularCheck");
        if (circularCheck != null) circularCheck.IsChecked = ring.IsCircular;

        var innerRadiusSpin = dialogWindow.FindControl<NumericUpDown>("EllipseAnnulusInnerRadiusSpin");
        if (innerRadiusSpin != null) innerRadiusSpin.Value = (decimal)ring.InnerRadiusRatio;
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not EllipseAnnulusObject ring) return;
        var circularCheck = dialogWindow.FindControl<CheckBox>("EllipseAnnulusCircularCheck");
        if (circularCheck?.IsChecked != null) ring.IsCircular = circularCheck.IsChecked.Value;

        var innerRadiusSpin = dialogWindow.FindControl<NumericUpDown>("EllipseAnnulusInnerRadiusSpin");
        if (innerRadiusSpin?.Value != null) ring.InnerRadiusRatio = (double)innerRadiusSpin.Value;
    }
}
