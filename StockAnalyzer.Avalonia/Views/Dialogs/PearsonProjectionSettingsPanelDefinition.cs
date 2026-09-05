using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public sealed class PearsonProjectionSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is PearsonProjectionObject;

    public void Activate(Window dialogWindow)
    {
        var genericColorPanel = dialogWindow.FindControl<StackPanel>("GenericColorPanel");
        var thicknessPanel = dialogWindow.FindControl<StackPanel>("ThicknessPanel");
        var pearsonPanel = dialogWindow.FindControl<StackPanel>("PearsonProjectionPanel");
        var fillOpacityPanel = dialogWindow.FindControl<StackPanel>("PearsonFillOpacityPanel");

        if (genericColorPanel != null) genericColorPanel.IsVisible = true;
        if (thicknessPanel != null) thicknessPanel.IsVisible = true;
        if (pearsonPanel != null) pearsonPanel.IsVisible = true;
        if (fillOpacityPanel != null) fillOpacityPanel.IsVisible = true;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not PearsonProjectionObject pearsonObj) return;

        var unmatchedPicker = dialogWindow.FindControl<ColorPicker>("PearsonUnmatchedColorPicker");
        var minCorrelationSpin = dialogWindow.FindControl<NumericUpDown>("PearsonMinCorrelationSpin");
        var topKSpin = dialogWindow.FindControl<NumericUpDown>("PearsonTopKSpin");
        var priceFieldCombo = dialogWindow.FindControl<ComboBox>("PearsonProjectionPriceFieldCombo");
        var applyVolatilityScalingCheck = dialogWindow.FindControl<CheckBox>("PearsonApplyVolatilityScalingCheck");
        var applyDetrendCheck = dialogWindow.FindControl<CheckBox>("PearsonApplyDetrendCheck");
        var showMatchHighlightCheck = dialogWindow.FindControl<CheckBox>("PearsonShowMatchHighlightCheck");

        var fillColorPicker = dialogWindow.FindControl<ColorPicker>("PearsonFillColorPicker");
        var fillOpacitySpin = dialogWindow.FindControl<NumericUpDown>("PearsonFillOpacitySpin");
        var futureStepsSpin = dialogWindow.FindControl<NumericUpDown>("PearsonFutureStepsSpin");
        var showConfidenceBandCheck = dialogWindow.FindControl<CheckBox>("PearsonShowConfidenceBandCheck");
        var confidenceMultiplierSpin = dialogWindow.FindControl<NumericUpDown>("PearsonConfidenceMultiplierSpin");

        if (unmatchedPicker != null) unmatchedPicker.Color = pearsonObj.UnmatchedColor;
        if (minCorrelationSpin != null) minCorrelationSpin.Value = (decimal)pearsonObj.MinCorrelation;
        if (topKSpin != null) topKSpin.Value = pearsonObj.TopK;
        if (priceFieldCombo != null) priceFieldCombo.SelectedIndex = PriceTypeToIndex(pearsonObj.PriceSource);
        if (applyVolatilityScalingCheck != null) applyVolatilityScalingCheck.IsChecked = pearsonObj.ApplyVolatilityScaling;
        if (applyDetrendCheck != null) applyDetrendCheck.IsChecked = pearsonObj.ApplyDetrend;
        if (showMatchHighlightCheck != null) showMatchHighlightCheck.IsChecked = pearsonObj.ShowMatchHighlight;

        if (fillColorPicker != null) fillColorPicker.Color = pearsonObj.FillColor;
        if (fillOpacitySpin != null) fillOpacitySpin.Value = pearsonObj.FillOpacity;
        if (futureStepsSpin != null) futureStepsSpin.Value = pearsonObj.FutureSteps;
        if (showConfidenceBandCheck != null) showConfidenceBandCheck.IsChecked = pearsonObj.ShowConfidenceBand;
        if (confidenceMultiplierSpin != null) confidenceMultiplierSpin.Value = pearsonObj.ConfidenceMultiplier;
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not PearsonProjectionObject pearsonObj) return;

        var unmatchedPicker = dialogWindow.FindControl<ColorPicker>("PearsonUnmatchedColorPicker");
        var minCorrelationSpin = dialogWindow.FindControl<NumericUpDown>("PearsonMinCorrelationSpin");
        var topKSpin = dialogWindow.FindControl<NumericUpDown>("PearsonTopKSpin");
        var priceFieldCombo = dialogWindow.FindControl<ComboBox>("PearsonProjectionPriceFieldCombo");
        var applyVolatilityScalingCheck = dialogWindow.FindControl<CheckBox>("PearsonApplyVolatilityScalingCheck");
        var applyDetrendCheck = dialogWindow.FindControl<CheckBox>("PearsonApplyDetrendCheck");
        var showMatchHighlightCheck = dialogWindow.FindControl<CheckBox>("PearsonShowMatchHighlightCheck");

        var fillColorPicker = dialogWindow.FindControl<ColorPicker>("PearsonFillColorPicker");
        var fillOpacitySpin = dialogWindow.FindControl<NumericUpDown>("PearsonFillOpacitySpin");
        var futureStepsSpin = dialogWindow.FindControl<NumericUpDown>("PearsonFutureStepsSpin");
        var showConfidenceBandCheck = dialogWindow.FindControl<CheckBox>("PearsonShowConfidenceBandCheck");
        var confidenceMultiplierSpin = dialogWindow.FindControl<NumericUpDown>("PearsonConfidenceMultiplierSpin");

        if (unmatchedPicker != null) pearsonObj.UnmatchedColor = unmatchedPicker.Color;
        if (minCorrelationSpin?.Value != null) pearsonObj.MinCorrelation = (double)minCorrelationSpin.Value.Value;
        if (topKSpin?.Value != null) pearsonObj.TopK = (int)topKSpin.Value.Value;
        if (priceFieldCombo != null) pearsonObj.PriceSource = IndexToPriceType(priceFieldCombo.SelectedIndex);
        if (applyVolatilityScalingCheck?.IsChecked != null) pearsonObj.ApplyVolatilityScaling = applyVolatilityScalingCheck.IsChecked.Value;
        if (applyDetrendCheck?.IsChecked != null) pearsonObj.ApplyDetrend = applyDetrendCheck.IsChecked.Value;
        if (showMatchHighlightCheck?.IsChecked != null) pearsonObj.ShowMatchHighlight = showMatchHighlightCheck.IsChecked.Value;

        if (fillColorPicker != null) pearsonObj.FillColor = fillColorPicker.Color;
        if (fillOpacitySpin?.Value != null) pearsonObj.FillOpacity = (int)fillOpacitySpin.Value.Value;
        if (futureStepsSpin?.Value != null) pearsonObj.FutureSteps = (int)futureStepsSpin.Value.Value;
        if (showConfidenceBandCheck?.IsChecked != null) pearsonObj.ShowConfidenceBand = showConfidenceBandCheck.IsChecked.Value;
        if (confidenceMultiplierSpin?.Value != null) pearsonObj.ConfidenceMultiplier = confidenceMultiplierSpin.Value.Value;
    }

    private static int PriceTypeToIndex(PriceType type)
    {
        for (int i = 0; i < PriceDataHelper.PriceTypeOptions.Count; i++)
        {
            if (PriceDataHelper.PriceTypeOptions[i] == type) return i;
        }
        return 3; // Default to Close (index 3)
    }

    private static PriceType IndexToPriceType(int index)
    {
        return index >= 0 && index < PriceDataHelper.PriceTypeOptions.Count
            ? PriceDataHelper.PriceTypeOptions[index]
            : PriceType.Close;
    }
}
