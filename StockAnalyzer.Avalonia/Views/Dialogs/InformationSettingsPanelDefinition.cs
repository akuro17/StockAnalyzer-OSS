using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

/// <summary>
/// Settings panel definition for the Information drawing tool.
/// Activates and synchronizes Color (GenericColorPanel), LineThickness (ThicknessPanel),
/// Fill Color, Fill Opacity, Font Color, and Font Size (InformationPanel).
/// </summary>
public sealed class InformationSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is InformationObject;

    public void Activate(Window dialogWindow)
    {
        var genericColorPanel = dialogWindow.FindControl<StackPanel>("GenericColorPanel");
        var thicknessPanel = dialogWindow.FindControl<StackPanel>("ThicknessPanel");
        var infoPanel = dialogWindow.FindControl<StackPanel>("InformationPanel");

        if (genericColorPanel != null) genericColorPanel.IsVisible = true;
        if (thicknessPanel != null) thicknessPanel.IsVisible = true;
        if (infoPanel != null) infoPanel.IsVisible = true;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not InformationObject infoObj) return;

        var fillColorPicker = dialogWindow.FindControl<ColorPicker>("InformationFillColorPicker");
        var fillOpacitySpin = dialogWindow.FindControl<NumericUpDown>("InformationFillOpacitySpin");
        var fontColorPicker = dialogWindow.FindControl<ColorPicker>("InformationFontColorPicker");
        var fontSizeSpin = dialogWindow.FindControl<NumericUpDown>("InformationFontSizeSpin");

        if (fillColorPicker != null) fillColorPicker.Color = infoObj.FillColor;
        if (fillOpacitySpin != null) fillOpacitySpin.Value = infoObj.FillOpacity;
        if (fontColorPicker != null) fontColorPicker.Color = infoObj.FontColor;
        if (fontSizeSpin != null) fontSizeSpin.Value = (decimal)infoObj.FontSize;
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not InformationObject infoObj) return;

        var fillColorPicker = dialogWindow.FindControl<ColorPicker>("InformationFillColorPicker");
        var fillOpacitySpin = dialogWindow.FindControl<NumericUpDown>("InformationFillOpacitySpin");
        var fontColorPicker = dialogWindow.FindControl<ColorPicker>("InformationFontColorPicker");
        var fontSizeSpin = dialogWindow.FindControl<NumericUpDown>("InformationFontSizeSpin");

        if (fillColorPicker != null) infoObj.FillColor = fillColorPicker.Color;
        if (fillOpacitySpin?.Value != null) infoObj.FillOpacity = (int)fillOpacitySpin.Value.Value;
        if (fontColorPicker != null) infoObj.FontColor = fontColorPicker.Color;
        if (fontSizeSpin?.Value != null) infoObj.FontSize = (double)fontSizeSpin.Value.Value;
    }
}
