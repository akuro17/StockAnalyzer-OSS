using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

/// <summary>
/// Settings-dialog behavior shared by any <see cref="INurbsWeightedCurveObject"/> implementer
/// (currently <see cref="NurbsHyperbolaObject"/> and <see cref="NurbsConicArcObject"/>), which
/// otherwise used byte-for-byte identical wiring logic inside two separate Definition classes for a
/// single-Weight NumericUpDown panel (mirrors <see cref="NurbsConicShapeSettingsPanelDefinition"/>'s
/// consolidation of <c>NurbsConicObject</c>/<c>NurbsEllipseObject</c>).
/// </summary>
public sealed class NurbsWeightedCurveSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is INurbsWeightedCurveObject;

    public void Activate(Window dialogWindow)
    {
        var panel = dialogWindow.FindControl<StackPanel>("NurbsWeightedCurvePanel");
        if (panel != null) panel.IsVisible = true;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not INurbsWeightedCurveObject weighted) return;

        var label = dialogWindow.FindControl<TextBlock>("NurbsWeightedCurveWeightLabel");
        if (label != null) label.Text = LocalizationManager.Instance[weighted.WeightLabelKey];

        var weightSpin = dialogWindow.FindControl<NumericUpDown>("NurbsWeightedCurveWeightSpin");
        if (weightSpin != null)
        {
            // Maximum MUST be assigned before Minimum: NumericUpDown coerces an incoming Minimum
            // against the control's *current* Maximum, so setting Minimum first while Maximum still
            // holds the XAML placeholder value (1) silently clamps Minimum down to it (e.g. the
            // Hyperbola case's Minimum=1.01 would collapse to 1).
            weightSpin.Maximum = (decimal)weighted.WeightRangeMax;
            weightSpin.Minimum = (decimal)weighted.WeightRangeMin;
            weightSpin.Increment = (decimal)weighted.WeightIncrement;
            weightSpin.FormatString = weighted.WeightFormatString;
            weightSpin.Value = (decimal)weighted.Weight;
        }
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not INurbsWeightedCurveObject weighted) return;
        var weightSpin = dialogWindow.FindControl<NumericUpDown>("NurbsWeightedCurveWeightSpin");
        if (weightSpin?.Value != null) weighted.Weight = (double)weightSpin.Value;
    }
}
