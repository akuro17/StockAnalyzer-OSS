using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public sealed class HarmonicPatternSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is HarmonicPatternObject;

    public void Activate(Window dialogWindow)
    {
        var patternPanel = dialogWindow.FindControl<StackPanel>("HarmonicPatternPanel");
        var thicknessPanel = dialogWindow.FindControl<StackPanel>("ThicknessPanel");
        var genericColorPanel = dialogWindow.FindControl<StackPanel>("GenericColorPanel");
        // Like DtwProjectionSettingsPanelDefinition, keep the generic Color/Thickness controls
        // visible -- they are already fully handled by DrawingSettingsDialog's generic
        // _drawing.Color/_drawing.Thickness read-write, matching every other line-based tool
        // (e.g. Angle) that never opts out of them.
        if (patternPanel != null) patternPanel.IsVisible = true;
        if (thicknessPanel != null) thicknessPanel.IsVisible = true;
        if (genericColorPanel != null) genericColorPanel.IsVisible = true;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not HarmonicPatternObject harmonicPattern) return;
        var autoThresholdCheck = dialogWindow.FindControl<CheckBox>("HarmonicAutoZigZagCheck");
        var zigzagThresholdSpin = dialogWindow.FindControl<NumericUpDown>("HarmonicZigZagThresholdSpin");
        var showPrzCheck = dialogWindow.FindControl<CheckBox>("HarmonicShowPrzCheck");
        var fillOpacitySpin = dialogWindow.FindControl<NumericUpDown>("HarmonicFillOpacitySpin");
        var fillColorPicker = dialogWindow.FindControl<ColorPicker>("HarmonicFillColorPicker");

        if (autoThresholdCheck != null) autoThresholdCheck.IsChecked = !harmonicPattern.ZigZagThreshold.HasValue;
        if (zigzagThresholdSpin != null) zigzagThresholdSpin.Value = harmonicPattern.ZigZagThreshold ?? StockAnalyzer.Core.ChartConstants.DefaultHarmonicZigZagThreshold;
        if (showPrzCheck != null) showPrzCheck.IsChecked = harmonicPattern.ShowPrz;
        if (fillOpacitySpin != null) fillOpacitySpin.Value = harmonicPattern.FillOpacity;
        if (fillColorPicker != null) fillColorPicker.Color = harmonicPattern.FillColor;
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not HarmonicPatternObject harmonicPattern) return;
        var autoThresholdCheck = dialogWindow.FindControl<CheckBox>("HarmonicAutoZigZagCheck");
        var zigzagThresholdSpin = dialogWindow.FindControl<NumericUpDown>("HarmonicZigZagThresholdSpin");
        var showPrzCheck = dialogWindow.FindControl<CheckBox>("HarmonicShowPrzCheck");
        var fillOpacitySpin = dialogWindow.FindControl<NumericUpDown>("HarmonicFillOpacitySpin");
        var fillColorPicker = dialogWindow.FindControl<ColorPicker>("HarmonicFillColorPicker");

        if (autoThresholdCheck?.IsChecked == true)
        {
            harmonicPattern.ZigZagThreshold = null;
        }
        else if (zigzagThresholdSpin?.Value != null)
        {
            harmonicPattern.ZigZagThreshold = (decimal)zigzagThresholdSpin.Value;
        }

        if (showPrzCheck?.IsChecked != null)
        {
            harmonicPattern.ShowPrz = showPrzCheck.IsChecked.Value;
        }

        if (fillOpacitySpin?.Value != null)
        {
            harmonicPattern.FillOpacity = (int)fillOpacitySpin.Value;
        }

        if (fillColorPicker != null)
        {
            harmonicPattern.FillColor = fillColorPicker.Color;
        }
    }
}
