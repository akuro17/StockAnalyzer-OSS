using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public sealed class NurbsTrendCurveSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is NurbsTrendCurveObject;

    public void Activate(Window dialogWindow)
    {
        var nurbsPanel = dialogWindow.FindControl<StackPanel>("NurbsTrendCurvePanel");
        if (nurbsPanel != null) nurbsPanel.IsVisible = true;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not NurbsTrendCurveObject nurbsCurve) return;
        var degreeSpin = dialogWindow.FindControl<NumericUpDown>("NurbsDegreeSpin");
        if (degreeSpin != null) degreeSpin.Value = nurbsCurve.Degree;
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not NurbsTrendCurveObject nurbsCurve) return;
        var degreeSpin = dialogWindow.FindControl<NumericUpDown>("NurbsDegreeSpin");
        if (degreeSpin?.Value != null) nurbsCurve.Degree = (int)degreeSpin.Value;
    }
}
