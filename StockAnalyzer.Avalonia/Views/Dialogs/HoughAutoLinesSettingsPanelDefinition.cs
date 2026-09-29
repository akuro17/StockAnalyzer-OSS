using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public sealed class HoughAutoLinesSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing.GetType() == typeof(HoughAutoLinesObject);

    public void Activate(Window dialogWindow)
    {
        var genericColorPanel = dialogWindow.FindControl<StackPanel>("GenericColorPanel");
        var thicknessPanel = dialogWindow.FindControl<StackPanel>("ThicknessPanel");
        var panel = dialogWindow.FindControl<StackPanel>("HoughAutoLinesPanel");

        if (genericColorPanel != null) genericColorPanel.IsVisible = true;
        if (thicknessPanel != null) thicknessPanel.IsVisible = true;
        if (panel != null) panel.IsVisible = true;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not HoughAutoLinesObject obj) return;

        var pivotSpin = dialogWindow.FindControl<NumericUpDown>("HoughAutoLinesPivotSpin");
        var thresholdSpin = dialogWindow.FindControl<NumericUpDown>("HoughAutoLinesThresholdSpin");
        var maxLinesSpin = dialogWindow.FindControl<NumericUpDown>("HoughAutoLinesMaxLinesSpin");
        var showChannelsCheck = dialogWindow.FindControl<CheckBox>("HoughAutoLinesShowChannelsCheck");
        var showLabelsCheck = dialogWindow.FindControl<CheckBox>("HoughAutoLinesShowLabelsCheck");
        var extendRightCheck = dialogWindow.FindControl<CheckBox>("HoughAutoLinesExtendRightCheck");

        var showTrendCheck = dialogWindow.FindControl<CheckBox>("HoughAutoLinesShowTrendCheck");
        var trendColorPicker = dialogWindow.FindControl<ColorPicker>("HoughAutoLinesTrendColorPicker");
        var showSupportCheck = dialogWindow.FindControl<CheckBox>("HoughAutoLinesShowSupportCheck");
        var supportColorPicker = dialogWindow.FindControl<ColorPicker>("HoughAutoLinesSupportColorPicker");
        var showResistanceCheck = dialogWindow.FindControl<CheckBox>("HoughAutoLinesShowResistanceCheck");
        var resistanceColorPicker = dialogWindow.FindControl<ColorPicker>("HoughAutoLinesResistanceColorPicker");

        if (pivotSpin != null) pivotSpin.Value = obj.PivotWindow;
        if (thresholdSpin != null) thresholdSpin.Value = obj.VoteThreshold;
        if (maxLinesSpin != null) maxLinesSpin.Value = obj.MaxLines;
        if (showChannelsCheck != null) showChannelsCheck.IsChecked = obj.ShowChannels;
        if (showLabelsCheck != null) showLabelsCheck.IsChecked = obj.ShowLabels;
        if (extendRightCheck != null) extendRightCheck.IsChecked = obj.ExtendLinesToRight;

        if (showTrendCheck != null) showTrendCheck.IsChecked = obj.ShowTrendLines;
        if (trendColorPicker != null) trendColorPicker.Color = obj.TrendLineColor;
        if (showSupportCheck != null) showSupportCheck.IsChecked = obj.ShowSupportLines;
        if (supportColorPicker != null) supportColorPicker.Color = obj.SupportColor;
        if (showResistanceCheck != null) showResistanceCheck.IsChecked = obj.ShowResistanceLines;
        if (resistanceColorPicker != null) resistanceColorPicker.Color = obj.ResistanceColor;
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not HoughAutoLinesObject obj) return;

        var pivotSpin = dialogWindow.FindControl<NumericUpDown>("HoughAutoLinesPivotSpin");
        var thresholdSpin = dialogWindow.FindControl<NumericUpDown>("HoughAutoLinesThresholdSpin");
        var maxLinesSpin = dialogWindow.FindControl<NumericUpDown>("HoughAutoLinesMaxLinesSpin");
        var showChannelsCheck = dialogWindow.FindControl<CheckBox>("HoughAutoLinesShowChannelsCheck");
        var showLabelsCheck = dialogWindow.FindControl<CheckBox>("HoughAutoLinesShowLabelsCheck");
        var extendRightCheck = dialogWindow.FindControl<CheckBox>("HoughAutoLinesExtendRightCheck");

        var showTrendCheck = dialogWindow.FindControl<CheckBox>("HoughAutoLinesShowTrendCheck");
        var trendColorPicker = dialogWindow.FindControl<ColorPicker>("HoughAutoLinesTrendColorPicker");
        var showSupportCheck = dialogWindow.FindControl<CheckBox>("HoughAutoLinesShowSupportCheck");
        var supportColorPicker = dialogWindow.FindControl<ColorPicker>("HoughAutoLinesSupportColorPicker");
        var showResistanceCheck = dialogWindow.FindControl<CheckBox>("HoughAutoLinesShowResistanceCheck");
        var resistanceColorPicker = dialogWindow.FindControl<ColorPicker>("HoughAutoLinesResistanceColorPicker");

        if (pivotSpin?.Value != null) obj.PivotWindow = (int)pivotSpin.Value.Value;
        if (thresholdSpin?.Value != null) obj.VoteThreshold = (int)thresholdSpin.Value.Value;
        if (maxLinesSpin?.Value != null) obj.MaxLines = (int)maxLinesSpin.Value.Value;
        if (showChannelsCheck != null) obj.ShowChannels = showChannelsCheck.IsChecked ?? true;
        if (showLabelsCheck != null) obj.ShowLabels = showLabelsCheck.IsChecked ?? true;
        if (extendRightCheck != null) obj.ExtendLinesToRight = extendRightCheck.IsChecked ?? false;

        if (showTrendCheck != null) obj.ShowTrendLines = showTrendCheck.IsChecked ?? true;
        if (trendColorPicker != null) obj.TrendLineColor = trendColorPicker.Color;
        if (showSupportCheck != null) obj.ShowSupportLines = showSupportCheck.IsChecked ?? true;
        if (supportColorPicker != null) obj.SupportColor = supportColorPicker.Color;
        if (showResistanceCheck != null) obj.ShowResistanceLines = showResistanceCheck.IsChecked ?? true;
        if (resistanceColorPicker != null) obj.ResistanceColor = resistanceColorPicker.Color;

        obj.InvalidateCache();
    }
}
