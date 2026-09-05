using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public sealed class HoughMagneticLineSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing.GetType() == typeof(HoughMagneticLineObject);

    public void Activate(Window dialogWindow)
    {
        var genericColorPanel = dialogWindow.FindControl<StackPanel>("GenericColorPanel");
        var thicknessPanel = dialogWindow.FindControl<StackPanel>("ThicknessPanel");
        var panel = dialogWindow.FindControl<StackPanel>("HoughMagneticLinePanel");

        if (genericColorPanel != null) genericColorPanel.IsVisible = true;
        if (thicknessPanel != null) thicknessPanel.IsVisible = true;
        if (panel != null) panel.IsVisible = true;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not HoughMagneticLineObject obj) return;

        var pivotSpin = dialogWindow.FindControl<NumericUpDown>("HoughMagneticLinePivotSpin");
        var thresholdSpin = dialogWindow.FindControl<NumericUpDown>("HoughMagneticLineThresholdSpin");
        var extendRightCheck = dialogWindow.FindControl<CheckBox>("HoughMagneticLineExtendRightCheck");
        var showLabelsCheck = dialogWindow.FindControl<CheckBox>("HoughMagneticLineShowLabelsCheck");

        if (pivotSpin != null) pivotSpin.Value = obj.PivotWindow;
        if (thresholdSpin != null) thresholdSpin.Value = obj.VoteThreshold;
        if (extendRightCheck != null) extendRightCheck.IsChecked = obj.ExtendRight;
        if (showLabelsCheck != null) showLabelsCheck.IsChecked = obj.ShowLabels;
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not HoughMagneticLineObject obj) return;

        var pivotSpin = dialogWindow.FindControl<NumericUpDown>("HoughMagneticLinePivotSpin");
        var thresholdSpin = dialogWindow.FindControl<NumericUpDown>("HoughMagneticLineThresholdSpin");
        var extendRightCheck = dialogWindow.FindControl<CheckBox>("HoughMagneticLineExtendRightCheck");
        var showLabelsCheck = dialogWindow.FindControl<CheckBox>("HoughMagneticLineShowLabelsCheck");

        if (pivotSpin?.Value != null) obj.PivotWindow = (int)pivotSpin.Value.Value;
        if (thresholdSpin?.Value != null) obj.VoteThreshold = (int)thresholdSpin.Value.Value;
        if (extendRightCheck != null) obj.ExtendRight = extendRightCheck.IsChecked ?? true;
        if (showLabelsCheck != null) obj.ShowLabels = showLabelsCheck.IsChecked ?? true;

        obj.InvalidateCache();
    }
}
