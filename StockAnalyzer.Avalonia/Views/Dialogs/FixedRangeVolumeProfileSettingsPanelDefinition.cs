using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public sealed class FixedRangeVolumeProfileSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is FixedRangeVolumeProfileObject;

    public void Activate(Window dialogWindow)
    {
        var vpPanel = dialogWindow.FindControl<StackPanel>("VolumeProfilePanel");
        var thicknessPanel = dialogWindow.FindControl<StackPanel>("ThicknessPanel");
        if (vpPanel != null) vpPanel.IsVisible = true;
        if (thicknessPanel != null) thicknessPanel.IsVisible = false;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not FixedRangeVolumeProfileObject frvp) return;
        var valueAreaColorPicker = dialogWindow.FindControl<ColorPicker>("ValueAreaColorPicker");
        var opacitySpin = dialogWindow.FindControl<NumericUpDown>("OpacitySpin");

        if (valueAreaColorPicker != null) valueAreaColorPicker.Color = frvp.ValueAreaColor;
        if (opacitySpin != null) opacitySpin.Value = (decimal)(frvp.Opacity * 100);
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not FixedRangeVolumeProfileObject frvp) return;
        var valueAreaColorPicker = dialogWindow.FindControl<ColorPicker>("ValueAreaColorPicker");
        var opacitySpin = dialogWindow.FindControl<NumericUpDown>("OpacitySpin");

        if (valueAreaColorPicker != null) frvp.ValueAreaColor = valueAreaColorPicker.Color;
        if (opacitySpin?.Value != null) frvp.Opacity = (double)opacitySpin.Value / 100.0;
    }
}
