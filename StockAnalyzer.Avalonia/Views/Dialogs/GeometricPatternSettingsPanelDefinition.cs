using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public sealed class GeometricPatternSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is GeometricPatternObject;

    public void Activate(Window dialogWindow)
    {
        var patternPanel = dialogWindow.FindControl<StackPanel>("GeometricPatternPanel");
        var thicknessPanel = dialogWindow.FindControl<StackPanel>("ThicknessPanel");
        var genericColorPanel = dialogWindow.FindControl<StackPanel>("GenericColorPanel");
        if (patternPanel != null) patternPanel.IsVisible = true;
        if (thicknessPanel != null) thicknessPanel.IsVisible = false;
        if (genericColorPanel != null) genericColorPanel.IsVisible = false;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not GeometricPatternObject geomPattern) return;
        var showChannelsCheck = dialogWindow.FindControl<CheckBox>("ShowChannelsCheck");
        var showWedgesCheck = dialogWindow.FindControl<CheckBox>("ShowWedgesCheck");
        var showTrianglesCheck = dialogWindow.FindControl<CheckBox>("ShowTrianglesCheck");
        var showPennantsCheck = dialogWindow.FindControl<CheckBox>("ShowPennantsCheck");
        var showMegaphoneCheck = dialogWindow.FindControl<CheckBox>("ShowMegaphoneCheck");
        var autoThresholdCheck = dialogWindow.FindControl<CheckBox>("AutoZigZagCheck");
        var zigzagThresholdSpin = dialogWindow.FindControl<NumericUpDown>("ZigZagThresholdSpin");

        if (showChannelsCheck != null) showChannelsCheck.IsChecked = geomPattern.ShowChannels;
        if (showWedgesCheck != null) showWedgesCheck.IsChecked = geomPattern.ShowWedges;
        if (showTrianglesCheck != null) showTrianglesCheck.IsChecked = geomPattern.ShowTriangles;
        if (showPennantsCheck != null) showPennantsCheck.IsChecked = geomPattern.ShowPennantsAndFlags;
        if (showMegaphoneCheck != null) showMegaphoneCheck.IsChecked = geomPattern.ShowMegaphone;

        if (autoThresholdCheck != null) autoThresholdCheck.IsChecked = !geomPattern.ZigZagThreshold.HasValue;
        if (zigzagThresholdSpin != null) zigzagThresholdSpin.Value = geomPattern.ZigZagThreshold ?? 2.0m;
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not GeometricPatternObject geomPattern) return;
        var showChannelsCheck = dialogWindow.FindControl<CheckBox>("ShowChannelsCheck");
        var showWedgesCheck = dialogWindow.FindControl<CheckBox>("ShowWedgesCheck");
        var showTrianglesCheck = dialogWindow.FindControl<CheckBox>("ShowTrianglesCheck");
        var showPennantsCheck = dialogWindow.FindControl<CheckBox>("ShowPennantsCheck");
        var showMegaphoneCheck = dialogWindow.FindControl<CheckBox>("ShowMegaphoneCheck");
        var autoThresholdCheck = dialogWindow.FindControl<CheckBox>("AutoZigZagCheck");
        var zigzagThresholdSpin = dialogWindow.FindControl<NumericUpDown>("ZigZagThresholdSpin");

        if (showChannelsCheck?.IsChecked != null) geomPattern.ShowChannels = showChannelsCheck.IsChecked.Value;
        if (showWedgesCheck?.IsChecked != null) geomPattern.ShowWedges = showWedgesCheck.IsChecked.Value;
        if (showTrianglesCheck?.IsChecked != null) geomPattern.ShowTriangles = showTrianglesCheck.IsChecked.Value;
        if (showPennantsCheck?.IsChecked != null) geomPattern.ShowPennantsAndFlags = showPennantsCheck.IsChecked.Value;
        if (showMegaphoneCheck?.IsChecked != null) geomPattern.ShowMegaphone = showMegaphoneCheck.IsChecked.Value;

        if (autoThresholdCheck?.IsChecked == true)
        {
            geomPattern.ZigZagThreshold = null;
        }
        else if (zigzagThresholdSpin?.Value != null)
        {
            geomPattern.ZigZagThreshold = (decimal)zigzagThresholdSpin.Value;
        }
    }
}
