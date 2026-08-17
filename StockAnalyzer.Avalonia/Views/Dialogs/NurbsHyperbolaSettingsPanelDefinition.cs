using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public sealed class NurbsHyperbolaSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is NurbsHyperbolaObject;

    public void Activate(Window dialogWindow)
    {
        var hypPanel = dialogWindow.FindControl<StackPanel>("NurbsHyperbolaPanel");
        if (hypPanel != null) hypPanel.IsVisible = true;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not NurbsHyperbolaObject nurbsHyperbola) return;
        var weightSpin = dialogWindow.FindControl<NumericUpDown>("NurbsHyperbolaWeightSpin");
        if (weightSpin != null) weightSpin.Value = (decimal)nurbsHyperbola.Weight;
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not NurbsHyperbolaObject nurbsHyperbola) return;
        var weightSpin = dialogWindow.FindControl<NumericUpDown>("NurbsHyperbolaWeightSpin");
        if (weightSpin?.Value != null) nurbsHyperbola.Weight = (double)weightSpin.Value;
    }
}
