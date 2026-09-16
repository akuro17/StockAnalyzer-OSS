using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public sealed class AutoElliottWaveSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is AutoElliottWaveObject;

    public void Activate(Window dialogWindow)
    {
        var patternPanel = dialogWindow.FindControl<StackPanel>("AutoElliottPanel");
        if (patternPanel != null) patternPanel.IsVisible = true;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not AutoElliottWaveObject autoElliottPattern) return;
        var autoThresholdCheck = dialogWindow.FindControl<CheckBox>("AutoElliottAutoZigZagCheck");
        var zigzagThresholdSpin = dialogWindow.FindControl<NumericUpDown>("AutoElliottZigZagThresholdSpin");
        var fillOpacitySpin = dialogWindow.FindControl<NumericUpDown>("AutoElliottFillOpacitySpin");
        var fillColorPicker = dialogWindow.FindControl<ColorPicker>("AutoElliottFillColorPicker");

        if (autoThresholdCheck != null) autoThresholdCheck.IsChecked = !autoElliottPattern.ZigZagThreshold.HasValue;
        if (zigzagThresholdSpin != null) zigzagThresholdSpin.Value = autoElliottPattern.ZigZagThreshold ?? 2.0m;
        if (fillOpacitySpin != null) fillOpacitySpin.Value = autoElliottPattern.FillOpacity;
        if (fillColorPicker != null) fillColorPicker.Color = autoElliottPattern.FillColor;
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not AutoElliottWaveObject autoElliottPattern) return;
        var autoThresholdCheck = dialogWindow.FindControl<CheckBox>("AutoElliottAutoZigZagCheck");
        var zigzagThresholdSpin = dialogWindow.FindControl<NumericUpDown>("AutoElliottZigZagThresholdSpin");
        var fillOpacitySpin = dialogWindow.FindControl<NumericUpDown>("AutoElliottFillOpacitySpin");
        var fillColorPicker = dialogWindow.FindControl<ColorPicker>("AutoElliottFillColorPicker");

        if (autoThresholdCheck?.IsChecked == true)
        {
            autoElliottPattern.ZigZagThreshold = null;
        }
        else if (zigzagThresholdSpin?.Value != null)
        {
            autoElliottPattern.ZigZagThreshold = (decimal)zigzagThresholdSpin.Value;
        }

        if (fillOpacitySpin?.Value != null)
        {
            autoElliottPattern.FillOpacity = (int)fillOpacitySpin.Value;
        }

        if (fillColorPicker != null)
        {
            autoElliottPattern.FillColor = fillColorPicker.Color;
        }
    }
}
