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
        if (patternPanel != null) patternPanel.IsVisible = true;
        if (thicknessPanel != null) thicknessPanel.IsVisible = false;
        if (genericColorPanel != null) genericColorPanel.IsVisible = false;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not HarmonicPatternObject harmonicPattern) return;
        var autoThresholdCheck = dialogWindow.FindControl<CheckBox>("HarmonicAutoZigZagCheck");
        var zigzagThresholdSpin = dialogWindow.FindControl<NumericUpDown>("HarmonicZigZagThresholdSpin");
        var showPrzCheck = dialogWindow.FindControl<CheckBox>("HarmonicShowPrzCheck");

        if (autoThresholdCheck != null) autoThresholdCheck.IsChecked = !harmonicPattern.ZigZagThreshold.HasValue;
        if (zigzagThresholdSpin != null) zigzagThresholdSpin.Value = harmonicPattern.ZigZagThreshold ?? StockAnalyzer.Core.ChartConstants.DefaultHarmonicZigZagThreshold;
        if (showPrzCheck != null) showPrzCheck.IsChecked = harmonicPattern.ShowPrz;
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not HarmonicPatternObject harmonicPattern) return;
        var autoThresholdCheck = dialogWindow.FindControl<CheckBox>("HarmonicAutoZigZagCheck");
        var zigzagThresholdSpin = dialogWindow.FindControl<NumericUpDown>("HarmonicZigZagThresholdSpin");
        var showPrzCheck = dialogWindow.FindControl<CheckBox>("HarmonicShowPrzCheck");

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
    }
}
