using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

/// <summary>
/// Settings dialog panel definition for IconObject.
/// Displays exclusively the 3 required settings: Font Color, Font Size, and Font Opacity.
/// </summary>
public sealed class IconSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is IconObject;

    public void Activate(Window dialogWindow)
    {
        var iconPanel = dialogWindow.FindControl<StackPanel>("IconSettingsPanel");
        var thicknessPanel = dialogWindow.FindControl<StackPanel>("ThicknessPanel");
        var genericColorPanel = dialogWindow.FindControl<StackPanel>("GenericColorPanel");

        if (iconPanel != null) iconPanel.IsVisible = true;
        if (thicknessPanel != null) thicknessPanel.IsVisible = false;
        if (genericColorPanel != null) genericColorPanel.IsVisible = false;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not IconObject iconObj) return;

        var fontColorPicker = dialogWindow.FindControl<ColorPicker>("IconFontColorPicker");
        var fontSizeSpin = dialogWindow.FindControl<NumericUpDown>("IconFontSizeSpin");
        var fontOpacitySpin = dialogWindow.FindControl<NumericUpDown>("IconFontOpacitySpin");

        if (fontColorPicker != null) fontColorPicker.Color = iconObj.FontColor;
        if (fontSizeSpin != null) fontSizeSpin.Value = (decimal)iconObj.FontSize;
        if (fontOpacitySpin != null) fontOpacitySpin.Value = (decimal)iconObj.FontOpacity;
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not IconObject iconObj) return;

        var fontColorPicker = dialogWindow.FindControl<ColorPicker>("IconFontColorPicker");
        var fontSizeSpin = dialogWindow.FindControl<NumericUpDown>("IconFontSizeSpin");
        var fontOpacitySpin = dialogWindow.FindControl<NumericUpDown>("IconFontOpacitySpin");

        if (fontColorPicker != null) iconObj.FontColor = fontColorPicker.Color;
        if (fontSizeSpin?.Value != null) iconObj.FontSize = (double)fontSizeSpin.Value;
        if (fontOpacitySpin?.Value != null) iconObj.FontOpacity = (double)fontOpacitySpin.Value;
    }
}
