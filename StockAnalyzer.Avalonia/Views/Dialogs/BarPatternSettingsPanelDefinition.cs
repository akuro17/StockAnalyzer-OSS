using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public sealed class BarPatternSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is BarPatternObject;

    public void Activate(Window dialogWindow)
    {
        var bpPanel = dialogWindow.FindControl<StackPanel>("BarPatternPanel");
        var thicknessPanel = dialogWindow.FindControl<StackPanel>("ThicknessPanel");
        var genericColorPanel = dialogWindow.FindControl<StackPanel>("GenericColorPanel");
        if (bpPanel != null) bpPanel.IsVisible = true;
        if (thicknessPanel != null) thicknessPanel.IsVisible = false;
        if (genericColorPanel != null) genericColorPanel.IsVisible = false;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not BarPatternObject barPattern) return;
        var bpOpacitySpin = dialogWindow.FindControl<NumericUpDown>("BarOpacitySpin");
        var barUpColorPicker = dialogWindow.FindControl<ColorPicker>("BarUpColorPicker");
        var barDownColorPicker = dialogWindow.FindControl<ColorPicker>("BarDownColorPicker");

        if (bpOpacitySpin != null) bpOpacitySpin.Value = barPattern.Transparency;
        if (barUpColorPicker != null) barUpColorPicker.Color = barPattern.UpColor;
        if (barDownColorPicker != null) barDownColorPicker.Color = barPattern.DownColor;
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not BarPatternObject barPattern) return;
        var bpOpacitySpin = dialogWindow.FindControl<NumericUpDown>("BarOpacitySpin");
        var barUpColorPicker = dialogWindow.FindControl<ColorPicker>("BarUpColorPicker");
        var barDownColorPicker = dialogWindow.FindControl<ColorPicker>("BarDownColorPicker");

        if (barUpColorPicker != null) barPattern.UpColor = barUpColorPicker.Color;
        if (barDownColorPicker != null) barPattern.DownColor = barDownColorPicker.Color;
        if (bpOpacitySpin?.Value != null) barPattern.Transparency = (int)bpOpacitySpin.Value;
    }
}
