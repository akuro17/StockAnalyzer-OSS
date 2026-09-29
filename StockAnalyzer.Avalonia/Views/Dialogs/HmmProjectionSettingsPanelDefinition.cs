using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public sealed class HmmProjectionSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is HmmProjectionObject;

    public void Activate(Window dialogWindow)
    {
        var genericColorPanel = dialogWindow.FindControl<StackPanel>("GenericColorPanel");
        var thicknessPanel = dialogWindow.FindControl<StackPanel>("ThicknessPanel");
        var hmmPanel = dialogWindow.FindControl<StackPanel>("HmmProjectionPanel");
        var fillOpacityPanel = dialogWindow.FindControl<StackPanel>("HmmFillOpacityPanel");

        if (genericColorPanel != null) genericColorPanel.IsVisible = true;
        if (thicknessPanel != null) thicknessPanel.IsVisible = true;
        if (hmmPanel != null) hmmPanel.IsVisible = true;
        if (fillOpacityPanel != null) fillOpacityPanel.IsVisible = true;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not HmmProjectionObject hmmObj) return;

        var statesSpin = dialogWindow.FindControl<NumericUpDown>("HmmStatesSpin");
        var maxIterationsSpin = dialogWindow.FindControl<NumericUpDown>("HmmMaxIterationsSpin");
        var toleranceSpin = dialogWindow.FindControl<NumericUpDown>("HmmToleranceSpin");
        var priceFieldCombo = dialogWindow.FindControl<ComboBox>("HmmProjectionPriceFieldCombo");

        var fillColorPicker = dialogWindow.FindControl<ColorPicker>("HmmFillColorPicker");
        var fillOpacitySpin = dialogWindow.FindControl<NumericUpDown>("HmmFillOpacitySpin");
        var futureStepsSpin = dialogWindow.FindControl<NumericUpDown>("HmmFutureStepsSpin");
        var showConfidenceBandCheck = dialogWindow.FindControl<CheckBox>("HmmShowConfidenceBandCheck");
        var confidenceMultiplierSpin = dialogWindow.FindControl<NumericUpDown>("HmmConfidenceMultiplierSpin");

        if (statesSpin != null) statesSpin.Value = hmmObj.States;
        if (maxIterationsSpin != null) maxIterationsSpin.Value = hmmObj.MaxIterations;
        if (toleranceSpin != null) toleranceSpin.Value = (decimal)hmmObj.Tolerance;
        if (priceFieldCombo != null) priceFieldCombo.SelectedIndex = PriceTypeToIndex(hmmObj.PriceSource);

        if (fillColorPicker != null) fillColorPicker.Color = hmmObj.FillColor;
        if (fillOpacitySpin != null) fillOpacitySpin.Value = hmmObj.FillOpacity;
        if (futureStepsSpin != null) futureStepsSpin.Value = hmmObj.FutureSteps;
        if (showConfidenceBandCheck != null) showConfidenceBandCheck.IsChecked = hmmObj.ShowConfidenceBand;
        if (confidenceMultiplierSpin != null) confidenceMultiplierSpin.Value = hmmObj.ConfidenceMultiplier;
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not HmmProjectionObject hmmObj) return;

        var statesSpin = dialogWindow.FindControl<NumericUpDown>("HmmStatesSpin");
        var maxIterationsSpin = dialogWindow.FindControl<NumericUpDown>("HmmMaxIterationsSpin");
        var toleranceSpin = dialogWindow.FindControl<NumericUpDown>("HmmToleranceSpin");
        var priceFieldCombo = dialogWindow.FindControl<ComboBox>("HmmProjectionPriceFieldCombo");

        var fillColorPicker = dialogWindow.FindControl<ColorPicker>("HmmFillColorPicker");
        var fillOpacitySpin = dialogWindow.FindControl<NumericUpDown>("HmmFillOpacitySpin");
        var futureStepsSpin = dialogWindow.FindControl<NumericUpDown>("HmmFutureStepsSpin");
        var showConfidenceBandCheck = dialogWindow.FindControl<CheckBox>("HmmShowConfidenceBandCheck");
        var confidenceMultiplierSpin = dialogWindow.FindControl<NumericUpDown>("HmmConfidenceMultiplierSpin");

        if (statesSpin?.Value != null) hmmObj.States = (int)statesSpin.Value.Value;
        if (maxIterationsSpin?.Value != null) hmmObj.MaxIterations = (int)maxIterationsSpin.Value.Value;
        if (toleranceSpin?.Value != null) hmmObj.Tolerance = (double)toleranceSpin.Value.Value;
        if (priceFieldCombo != null) hmmObj.PriceSource = IndexToPriceType(priceFieldCombo.SelectedIndex);

        if (fillColorPicker != null) hmmObj.FillColor = fillColorPicker.Color;
        if (fillOpacitySpin?.Value != null) hmmObj.FillOpacity = (int)fillOpacitySpin.Value.Value;
        if (futureStepsSpin?.Value != null) hmmObj.FutureSteps = (int)futureStepsSpin.Value.Value;
        if (showConfidenceBandCheck?.IsChecked != null) hmmObj.ShowConfidenceBand = showConfidenceBandCheck.IsChecked.Value;
        if (confidenceMultiplierSpin?.Value != null) hmmObj.ConfidenceMultiplier = confidenceMultiplierSpin.Value.Value;
    }

    private static int PriceTypeToIndex(PriceType type)
    {
        for (int i = 0; i < PriceDataHelper.PriceTypeOptions.Count; i++)
        {
            if (PriceDataHelper.PriceTypeOptions[i] == type) return i;
        }
        return 4; // Default to Median
    }

    private static PriceType IndexToPriceType(int index)
    {
        return index >= 0 && index < PriceDataHelper.PriceTypeOptions.Count
            ? PriceDataHelper.PriceTypeOptions[index]
            : PriceType.Median;
    }
}
