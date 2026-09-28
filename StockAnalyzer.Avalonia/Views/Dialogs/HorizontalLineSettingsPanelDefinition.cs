using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public sealed class HorizontalLineSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is HorizontalLineObject;

    public void Activate(Window dialogWindow)
    {
        var hPanel = dialogWindow.FindControl<StackPanel>("HorizontalLinePanel");
        if (hPanel != null) hPanel.IsVisible = true;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not HorizontalLineObject hLine) return;
        var priceSpin = dialogWindow.FindControl<NumericUpDown>("PriceSpin");
        if (priceSpin != null && hLine.Points.Count > 0)
        {
            priceSpin.Value = hLine.Points[0].Price;
        }
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not HorizontalLineObject hLine) return;
        var priceSpin = dialogWindow.FindControl<NumericUpDown>("PriceSpin");
        if (priceSpin?.Value != null && hLine.Points.Count > 0)
        {
            hLine.Points[0] = new ChartPoint(hLine.Points[0].Time, (decimal)priceSpin.Value);
        }
    }
}
