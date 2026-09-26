using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public sealed class AutoTimeCycleSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is AutoTimeCycleObject;

    public void Activate(Window dialogWindow)
    {
        var genericColorPanel = dialogWindow.FindControl<StackPanel>("GenericColorPanel");
        var thicknessPanel = dialogWindow.FindControl<StackPanel>("ThicknessPanel");
        var cyclePanel = dialogWindow.FindControl<StackPanel>("AutoTimeCyclePanel");
        var fillPanel = dialogWindow.FindControl<StackPanel>("AutoTimeCycleFillPanel");

        if (genericColorPanel != null) genericColorPanel.IsVisible = true;
        if (thicknessPanel != null) thicknessPanel.IsVisible = true;
        if (cyclePanel != null) cyclePanel.IsVisible = true;
        if (fillPanel != null) fillPanel.IsVisible = true;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not AutoTimeCycleObject cycleObj) return;

        var priceFieldCombo = dialogWindow.FindControl<ComboBox>("AutoTimeCyclePriceFieldCombo");
        var detrendCheck = dialogWindow.FindControl<CheckBox>("AutoTimeCycleApplyDetrendCheck");
        var minPeriodSpin = dialogWindow.FindControl<NumericUpDown>("AutoTimeCycleMinPeriodSpin");
        var maxPeriodSpin = dialogWindow.FindControl<NumericUpDown>("AutoTimeCycleMaxPeriodSpin");
        var alignmentCombo = dialogWindow.FindControl<ComboBox>("AutoTimeCycleAlignmentCombo");
        var interpolationCheck = dialogWindow.FindControl<CheckBox>("AutoTimeCycleFrequencyInterpolationCheck");

        var fillColorPicker = dialogWindow.FindControl<ColorPicker>("AutoTimeCycleFillColorPicker");
        var fillOpacitySpin = dialogWindow.FindControl<NumericUpDown>("AutoTimeCycleFillOpacitySpin");
        var cycleCountSpin = dialogWindow.FindControl<NumericUpDown>("AutoTimeCycleCountSpin");
        var showPeriodLabelCheck = dialogWindow.FindControl<CheckBox>("AutoTimeCycleShowPeriodLabelCheck");

        if (priceFieldCombo != null) priceFieldCombo.SelectedIndex = PriceDataHelper.GetPriceTypeIndex(cycleObj.PriceSource);
        if (detrendCheck != null) detrendCheck.IsChecked = cycleObj.ApplyDetrend;
        if (minPeriodSpin != null) minPeriodSpin.Value = (decimal)cycleObj.MinPeriod;
        if (maxPeriodSpin != null) maxPeriodSpin.Value = (decimal)cycleObj.MaxPeriod;
        if (alignmentCombo != null) alignmentCombo.SelectedIndex = (int)cycleObj.Alignment;
        if (interpolationCheck != null) interpolationCheck.IsChecked = cycleObj.EnableFrequencyInterpolation;

        if (fillColorPicker != null) fillColorPicker.Color = cycleObj.FillColor;
        if (fillOpacitySpin != null) fillOpacitySpin.Value = cycleObj.FillOpacity;
        if (cycleCountSpin != null) cycleCountSpin.Value = cycleObj.CycleCount;
        if (showPeriodLabelCheck != null) showPeriodLabelCheck.IsChecked = cycleObj.ShowPeriodLabel;
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not AutoTimeCycleObject cycleObj) return;

        var priceFieldCombo = dialogWindow.FindControl<ComboBox>("AutoTimeCyclePriceFieldCombo");
        var detrendCheck = dialogWindow.FindControl<CheckBox>("AutoTimeCycleApplyDetrendCheck");
        var minPeriodSpin = dialogWindow.FindControl<NumericUpDown>("AutoTimeCycleMinPeriodSpin");
        var maxPeriodSpin = dialogWindow.FindControl<NumericUpDown>("AutoTimeCycleMaxPeriodSpin");
        var alignmentCombo = dialogWindow.FindControl<ComboBox>("AutoTimeCycleAlignmentCombo");
        var interpolationCheck = dialogWindow.FindControl<CheckBox>("AutoTimeCycleFrequencyInterpolationCheck");

        var fillColorPicker = dialogWindow.FindControl<ColorPicker>("AutoTimeCycleFillColorPicker");
        var fillOpacitySpin = dialogWindow.FindControl<NumericUpDown>("AutoTimeCycleFillOpacitySpin");
        var cycleCountSpin = dialogWindow.FindControl<NumericUpDown>("AutoTimeCycleCountSpin");
        var showPeriodLabelCheck = dialogWindow.FindControl<CheckBox>("AutoTimeCycleShowPeriodLabelCheck");

        if (priceFieldCombo != null) cycleObj.PriceSource = PriceDataHelper.GetPriceTypeByIndex(priceFieldCombo.SelectedIndex);
        if (detrendCheck?.IsChecked != null) cycleObj.ApplyDetrend = detrendCheck.IsChecked.Value;
        if (minPeriodSpin?.Value != null) cycleObj.MinPeriod = (double)minPeriodSpin.Value.Value;
        if (maxPeriodSpin?.Value != null) cycleObj.MaxPeriod = (double)maxPeriodSpin.Value.Value;
        if (alignmentCombo != null && alignmentCombo.SelectedIndex >= 0)
        {
            cycleObj.Alignment = (AutoCycleAlignment)alignmentCombo.SelectedIndex;
        }
        if (interpolationCheck?.IsChecked != null) cycleObj.EnableFrequencyInterpolation = interpolationCheck.IsChecked.Value;

        if (fillColorPicker != null) cycleObj.FillColor = fillColorPicker.Color;
        if (fillOpacitySpin?.Value != null) cycleObj.FillOpacity = (int)fillOpacitySpin.Value.Value;
        if (cycleCountSpin?.Value != null) cycleObj.CycleCount = (int)cycleCountSpin.Value.Value;
        if (showPeriodLabelCheck?.IsChecked != null) cycleObj.ShowPeriodLabel = showPeriodLabelCheck.IsChecked.Value;
    }
}
