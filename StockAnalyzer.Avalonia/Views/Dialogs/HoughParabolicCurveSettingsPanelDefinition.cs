using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Analysis;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public sealed class HoughParabolicCurveSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing.GetType() == typeof(HoughParabolicCurveObject);

    public void Activate(Window dialogWindow)
    {
        var genericColorPanel = dialogWindow.FindControl<StackPanel>("GenericColorPanel");
        var thicknessPanel = dialogWindow.FindControl<StackPanel>("ThicknessPanel");
        var panel = dialogWindow.FindControl<StackPanel>("HoughParabolicCurvePanel");

        if (genericColorPanel != null) genericColorPanel.IsVisible = true;
        if (thicknessPanel != null) thicknessPanel.IsVisible = true;
        if (panel != null) panel.IsVisible = true;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not HoughParabolicCurveObject obj) return;

        var pivotSpin = dialogWindow.FindControl<NumericUpDown>("HoughParabolicCurvePivotSpin");
        var thresholdSpin = dialogWindow.FindControl<NumericUpDown>("HoughParabolicCurveThresholdSpin");
        var maxCurvesSpin = dialogWindow.FindControl<NumericUpDown>("HoughParabolicCurveMaxCurvesSpin");
        var curvatureCombo = dialogWindow.FindControl<ComboBox>("HoughParabolicCurveCurvatureCombo");
        var showLabelsCheck = dialogWindow.FindControl<CheckBox>("HoughParabolicCurveShowLabelsCheck");

        if (pivotSpin != null) pivotSpin.Value = obj.PivotWindow;
        if (thresholdSpin != null) thresholdSpin.Value = obj.VoteThreshold;
        if (maxCurvesSpin != null) maxCurvesSpin.Value = obj.MaxCurves;
        if (curvatureCombo != null) curvatureCombo.SelectedIndex = (int)obj.CurvatureSign;
        if (showLabelsCheck != null) showLabelsCheck.IsChecked = obj.ShowLabels;
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not HoughParabolicCurveObject obj) return;

        var pivotSpin = dialogWindow.FindControl<NumericUpDown>("HoughParabolicCurvePivotSpin");
        var thresholdSpin = dialogWindow.FindControl<NumericUpDown>("HoughParabolicCurveThresholdSpin");
        var maxCurvesSpin = dialogWindow.FindControl<NumericUpDown>("HoughParabolicCurveMaxCurvesSpin");
        var curvatureCombo = dialogWindow.FindControl<ComboBox>("HoughParabolicCurveCurvatureCombo");
        var showLabelsCheck = dialogWindow.FindControl<CheckBox>("HoughParabolicCurveShowLabelsCheck");

        if (pivotSpin?.Value != null) obj.PivotWindow = (int)pivotSpin.Value.Value;
        if (thresholdSpin?.Value != null) obj.VoteThreshold = (int)thresholdSpin.Value.Value;
        if (maxCurvesSpin?.Value != null) obj.MaxCurves = (int)maxCurvesSpin.Value.Value;
        if (curvatureCombo != null && curvatureCombo.SelectedIndex >= 0)
        {
            obj.CurvatureSign = (ParabolicHoughCurvatureSign)curvatureCombo.SelectedIndex;
        }
        if (showLabelsCheck != null) obj.ShowLabels = showLabelsCheck.IsChecked ?? true;

        obj.InvalidateCache();
    }
}
