using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public sealed class DtwProjectionSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is DtwProjectionObject;

    public void Activate(Window dialogWindow)
    {
        var dtwPanel = dialogWindow.FindControl<StackPanel>("DtwProjectionPanel");
        var genericColorPanel = dialogWindow.FindControl<StackPanel>("GenericColorPanel");
        var thicknessPanel = dialogWindow.FindControl<StackPanel>("ThicknessPanel");
        var extraSettingsPanel = dialogWindow.FindControl<StackPanel>("DtwExtraSettingsPanel");
        // Unlike most patterns, DtwProjection explicitly keeps the generic Color/Thickness
        // controls visible (they are shared/hidden by default only for tools that opt out).
        if (genericColorPanel != null) genericColorPanel.IsVisible = true;
        if (thicknessPanel != null) thicknessPanel.IsVisible = true;
        if (dtwPanel != null) dtwPanel.IsVisible = true;
        if (extraSettingsPanel != null) extraSettingsPanel.IsVisible = true;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not DtwProjectionObject dtwObj) return;
        var unmatchedPicker = dialogWindow.FindControl<ColorPicker>("DtwUnmatchedColorPicker");
        var fillColorPicker = dialogWindow.FindControl<ColorPicker>("DtwFillColorPicker");
        var fillOpacitySpin = dialogWindow.FindControl<NumericUpDown>("DtwFillOpacitySpin");
        var futureStepsSpin = dialogWindow.FindControl<NumericUpDown>("DtwFutureStepsSpin");
        if (unmatchedPicker != null) unmatchedPicker.Color = dtwObj.UnmatchedColor;
        if (fillColorPicker != null) fillColorPicker.Color = dtwObj.FillColor;
        if (fillOpacitySpin != null) fillOpacitySpin.Value = dtwObj.FillOpacity;
        if (futureStepsSpin != null) futureStepsSpin.Value = dtwObj.FutureSteps;
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not DtwProjectionObject dtwObj) return;
        var unmatchedPicker = dialogWindow.FindControl<ColorPicker>("DtwUnmatchedColorPicker");
        var fillColorPicker = dialogWindow.FindControl<ColorPicker>("DtwFillColorPicker");
        var fillOpacitySpin = dialogWindow.FindControl<NumericUpDown>("DtwFillOpacitySpin");
        var futureStepsSpin = dialogWindow.FindControl<NumericUpDown>("DtwFutureStepsSpin");
        if (unmatchedPicker != null) dtwObj.UnmatchedColor = unmatchedPicker.Color;
        if (fillColorPicker != null) dtwObj.FillColor = fillColorPicker.Color;
        if (fillOpacitySpin?.Value != null) dtwObj.FillOpacity = (int)fillOpacitySpin.Value;
        if (futureStepsSpin?.Value != null) dtwObj.FutureSteps = (int)futureStepsSpin.Value;
    }
}
