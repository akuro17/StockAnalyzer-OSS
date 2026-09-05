using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public sealed class HoughResonantFanSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing.GetType() == typeof(HoughResonantFanObject);

    public void Activate(Window dialogWindow)
    {
        var genericColorPanel = dialogWindow.FindControl<StackPanel>("GenericColorPanel");
        var thicknessPanel = dialogWindow.FindControl<StackPanel>("ThicknessPanel");
        var panel = dialogWindow.FindControl<StackPanel>("HoughResonantFanPanel");

        if (genericColorPanel != null) genericColorPanel.IsVisible = true;
        if (thicknessPanel != null) thicknessPanel.IsVisible = true;
        if (panel != null) panel.IsVisible = true;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not HoughResonantFanObject obj) return;

        var pivotSpin = dialogWindow.FindControl<NumericUpDown>("HoughResonantFanPivotSpin");
        var angleBinSpin = dialogWindow.FindControl<NumericUpDown>("HoughResonantFanAngleBinSpin");
        var minVotesSpin = dialogWindow.FindControl<NumericUpDown>("HoughResonantFanMinVotesSpin");
        var maxLinesSpin = dialogWindow.FindControl<NumericUpDown>("HoughResonantFanMaxLinesSpin");
        var extendRightCheck = dialogWindow.FindControl<CheckBox>("HoughResonantFanExtendRightCheck");
        var showLabelsCheck = dialogWindow.FindControl<CheckBox>("HoughResonantFanShowLabelsCheck");

        if (pivotSpin != null) pivotSpin.Value = obj.PivotWindow;
        if (angleBinSpin != null) angleBinSpin.Value = (decimal)obj.AngleBinDegrees;
        if (minVotesSpin != null) minVotesSpin.Value = obj.MinVotes;
        if (maxLinesSpin != null) maxLinesSpin.Value = obj.MaxFanLines;
        if (extendRightCheck != null) extendRightCheck.IsChecked = obj.ExtendRight;
        if (showLabelsCheck != null) showLabelsCheck.IsChecked = obj.ShowLabels;
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not HoughResonantFanObject obj) return;

        var pivotSpin = dialogWindow.FindControl<NumericUpDown>("HoughResonantFanPivotSpin");
        var angleBinSpin = dialogWindow.FindControl<NumericUpDown>("HoughResonantFanAngleBinSpin");
        var minVotesSpin = dialogWindow.FindControl<NumericUpDown>("HoughResonantFanMinVotesSpin");
        var maxLinesSpin = dialogWindow.FindControl<NumericUpDown>("HoughResonantFanMaxLinesSpin");
        var extendRightCheck = dialogWindow.FindControl<CheckBox>("HoughResonantFanExtendRightCheck");
        var showLabelsCheck = dialogWindow.FindControl<CheckBox>("HoughResonantFanShowLabelsCheck");

        if (pivotSpin?.Value != null) obj.PivotWindow = (int)pivotSpin.Value.Value;
        if (angleBinSpin?.Value != null) obj.AngleBinDegrees = (double)angleBinSpin.Value.Value;
        if (minVotesSpin?.Value != null) obj.MinVotes = (int)minVotesSpin.Value.Value;
        if (maxLinesSpin?.Value != null) obj.MaxFanLines = (int)maxLinesSpin.Value.Value;
        if (extendRightCheck != null) obj.ExtendRight = extendRightCheck.IsChecked ?? true;
        if (showLabelsCheck != null) obj.ShowLabels = showLabelsCheck.IsChecked ?? true;

        obj.InvalidateCache();
    }
}
