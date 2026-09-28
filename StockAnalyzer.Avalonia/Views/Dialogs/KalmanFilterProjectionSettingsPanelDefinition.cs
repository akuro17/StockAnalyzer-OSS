using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public sealed class KalmanFilterProjectionSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is KalmanFilterProjectionObject;

    public void Activate(Window dialogWindow)
    {
        var genericColorPanel = dialogWindow.FindControl<StackPanel>("GenericColorPanel");
        var thicknessPanel = dialogWindow.FindControl<StackPanel>("ThicknessPanel");
        var kalmanPanel = dialogWindow.FindControl<StackPanel>("KalmanFilterProjectionPanel");
        var fillOpacityPanel = dialogWindow.FindControl<StackPanel>("KalmanFillOpacityPanel");

        if (genericColorPanel != null) genericColorPanel.IsVisible = true;
        if (thicknessPanel != null) thicknessPanel.IsVisible = true;
        if (kalmanPanel != null) kalmanPanel.IsVisible = true;
        if (fillOpacityPanel != null) fillOpacityPanel.IsVisible = true;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not KalmanFilterProjectionObject kalmanObj) return;

        var qSpin = dialogWindow.FindControl<NumericUpDown>("KalmanQSpin");
        var rSpin = dialogWindow.FindControl<NumericUpDown>("KalmanRSpin");
        var fillColorPicker = dialogWindow.FindControl<ColorPicker>("KalmanFillColorPicker");
        var fillOpacitySpin = dialogWindow.FindControl<NumericUpDown>("KalmanFillOpacitySpin");
        var futureStepsSpin = dialogWindow.FindControl<NumericUpDown>("KalmanFutureStepsSpin");
        var showConfidenceBandCheck = dialogWindow.FindControl<CheckBox>("KalmanShowConfidenceBandCheck");
        var confidenceMultiplierSpin = dialogWindow.FindControl<NumericUpDown>("KalmanConfidenceMultiplierSpin");

        if (qSpin != null) qSpin.Value = kalmanObj.Q;
        if (rSpin != null) rSpin.Value = kalmanObj.R;
        if (fillColorPicker != null) fillColorPicker.Color = kalmanObj.FillColor;
        if (fillOpacitySpin != null) fillOpacitySpin.Value = kalmanObj.FillOpacity;
        if (futureStepsSpin != null) futureStepsSpin.Value = kalmanObj.FutureSteps;
        if (showConfidenceBandCheck != null) showConfidenceBandCheck.IsChecked = kalmanObj.ShowConfidenceBand;
        if (confidenceMultiplierSpin != null) confidenceMultiplierSpin.Value = kalmanObj.ConfidenceMultiplier;
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not KalmanFilterProjectionObject kalmanObj) return;

        var qSpin = dialogWindow.FindControl<NumericUpDown>("KalmanQSpin");
        var rSpin = dialogWindow.FindControl<NumericUpDown>("KalmanRSpin");
        var fillColorPicker = dialogWindow.FindControl<ColorPicker>("KalmanFillColorPicker");
        var fillOpacitySpin = dialogWindow.FindControl<NumericUpDown>("KalmanFillOpacitySpin");
        var futureStepsSpin = dialogWindow.FindControl<NumericUpDown>("KalmanFutureStepsSpin");
        var showConfidenceBandCheck = dialogWindow.FindControl<CheckBox>("KalmanShowConfidenceBandCheck");
        var confidenceMultiplierSpin = dialogWindow.FindControl<NumericUpDown>("KalmanConfidenceMultiplierSpin");

        if (qSpin?.Value != null) kalmanObj.Q = qSpin.Value.Value;
        if (rSpin?.Value != null) kalmanObj.R = rSpin.Value.Value;
        if (fillColorPicker != null) kalmanObj.FillColor = fillColorPicker.Color;
        if (fillOpacitySpin?.Value != null) kalmanObj.FillOpacity = (int)fillOpacitySpin.Value.Value;
        if (futureStepsSpin?.Value != null) kalmanObj.FutureSteps = (int)futureStepsSpin.Value.Value;
        if (showConfidenceBandCheck?.IsChecked != null) kalmanObj.ShowConfidenceBand = showConfidenceBandCheck.IsChecked.Value;
        if (confidenceMultiplierSpin?.Value != null) kalmanObj.ConfidenceMultiplier = confidenceMultiplierSpin.Value.Value;
    }
}
