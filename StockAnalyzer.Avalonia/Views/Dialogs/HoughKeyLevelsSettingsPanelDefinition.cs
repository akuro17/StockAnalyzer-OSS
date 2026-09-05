using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public sealed class HoughKeyLevelsSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing.GetType() == typeof(HoughKeyLevelsObject);

    public void Activate(Window dialogWindow)
    {
        var genericColorPanel = dialogWindow.FindControl<StackPanel>("GenericColorPanel");
        var thicknessPanel = dialogWindow.FindControl<StackPanel>("ThicknessPanel");
        var panel = dialogWindow.FindControl<StackPanel>("HoughKeyLevelsPanel");

        if (genericColorPanel != null) genericColorPanel.IsVisible = true;
        if (thicknessPanel != null) thicknessPanel.IsVisible = true;
        if (panel != null) panel.IsVisible = true;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not HoughKeyLevelsObject obj) return;

        var pivotSpin = dialogWindow.FindControl<NumericUpDown>("HoughKeyLevelsPivotSpin");
        var thresholdSpin = dialogWindow.FindControl<NumericUpDown>("HoughKeyLevelsThresholdSpin");
        var maxLevelsSpin = dialogWindow.FindControl<NumericUpDown>("HoughKeyLevelsMaxLevelsSpin");
        var extendRightCheck = dialogWindow.FindControl<CheckBox>("HoughKeyLevelsExtendRightCheck");
        var showLabelsCheck = dialogWindow.FindControl<CheckBox>("HoughKeyLevelsShowLabelsCheck");
        var supportColorPicker = dialogWindow.FindControl<ColorPicker>("HoughKeyLevelsSupportColorPicker");
        var resistanceColorPicker = dialogWindow.FindControl<ColorPicker>("HoughKeyLevelsResistanceColorPicker");

        if (pivotSpin != null) pivotSpin.Value = obj.PivotWindow;
        if (thresholdSpin != null) thresholdSpin.Value = obj.VoteThreshold;
        if (maxLevelsSpin != null) maxLevelsSpin.Value = obj.MaxLevels;
        if (extendRightCheck != null) extendRightCheck.IsChecked = obj.ExtendRight;
        if (showLabelsCheck != null) showLabelsCheck.IsChecked = obj.ShowLabels;
        if (supportColorPicker != null) supportColorPicker.Color = obj.SupportColor;
        if (resistanceColorPicker != null) resistanceColorPicker.Color = obj.ResistanceColor;
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not HoughKeyLevelsObject obj) return;

        var pivotSpin = dialogWindow.FindControl<NumericUpDown>("HoughKeyLevelsPivotSpin");
        var thresholdSpin = dialogWindow.FindControl<NumericUpDown>("HoughKeyLevelsThresholdSpin");
        var maxLevelsSpin = dialogWindow.FindControl<NumericUpDown>("HoughKeyLevelsMaxLevelsSpin");
        var extendRightCheck = dialogWindow.FindControl<CheckBox>("HoughKeyLevelsExtendRightCheck");
        var showLabelsCheck = dialogWindow.FindControl<CheckBox>("HoughKeyLevelsShowLabelsCheck");
        var supportColorPicker = dialogWindow.FindControl<ColorPicker>("HoughKeyLevelsSupportColorPicker");
        var resistanceColorPicker = dialogWindow.FindControl<ColorPicker>("HoughKeyLevelsResistanceColorPicker");

        if (pivotSpin?.Value != null) obj.PivotWindow = (int)pivotSpin.Value.Value;
        if (thresholdSpin?.Value != null) obj.VoteThreshold = (int)thresholdSpin.Value.Value;
        if (maxLevelsSpin?.Value != null) obj.MaxLevels = (int)maxLevelsSpin.Value.Value;
        if (extendRightCheck != null) obj.ExtendRight = extendRightCheck.IsChecked ?? true;
        if (showLabelsCheck != null) obj.ShowLabels = showLabelsCheck.IsChecked ?? true;
        if (supportColorPicker != null) obj.SupportColor = supportColorPicker.Color;
        if (resistanceColorPicker != null) obj.ResistanceColor = resistanceColorPicker.Color;

        obj.InvalidateCache();
    }
}
