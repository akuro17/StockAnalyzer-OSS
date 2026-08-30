using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

/// <summary>Settings-dialog behavior for PriceLabelObject (font size only).</summary>
public sealed class PriceLabelSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is PriceLabelObject;

    public void Activate(Window dialogWindow)
    {
        var pPanel = dialogWindow.FindControl<StackPanel>("PriceLabelPanel");
        var thicknessPanel = dialogWindow.FindControl<StackPanel>("ThicknessPanel");
        if (pPanel != null) pPanel.IsVisible = true;
        if (thicknessPanel != null) thicknessPanel.IsVisible = false;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not PriceLabelObject priceObj) return;
        var pfSpin = dialogWindow.FindControl<NumericUpDown>("PriceFontSizeSpin");
        if (pfSpin != null) pfSpin.Value = (decimal)priceObj.FontSize;
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not PriceLabelObject priceObj) return;
        var pfSpin = dialogWindow.FindControl<NumericUpDown>("PriceFontSizeSpin");
        if (pfSpin?.Value != null) priceObj.FontSize = (double)pfSpin.Value;
    }
}
