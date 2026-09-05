using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public sealed class FrechetProjectionSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is FrechetProjectionObject;

    public void Activate(Window dialogWindow)
    {
        var genericColorPanel = dialogWindow.FindControl<StackPanel>("GenericColorPanel");
        var thicknessPanel = dialogWindow.FindControl<StackPanel>("ThicknessPanel");
        var frechetPanel = dialogWindow.FindControl<StackPanel>("FrechetProjectionPanel");
        var fillOpacityPanel = dialogWindow.FindControl<StackPanel>("FrechetFillOpacityPanel");

        if (genericColorPanel != null) genericColorPanel.IsVisible = true;
        if (thicknessPanel != null) thicknessPanel.IsVisible = true;
        if (frechetPanel != null) frechetPanel.IsVisible = true;
        if (fillOpacityPanel != null) fillOpacityPanel.IsVisible = true;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not FrechetProjectionObject frechetObj) return;

        var unmatchedPicker = dialogWindow.FindControl<ColorPicker>("FrechetUnmatchedColorPicker");
        var priceFieldCombo = dialogWindow.FindControl<ComboBox>("FrechetProjectionPriceFieldCombo");
        var maxDistanceSpin = dialogWindow.FindControl<NumericUpDown>("FrechetMaxDistanceSpin");
        var showMatchHighlightCheck = dialogWindow.FindControl<CheckBox>("FrechetShowMatchHighlightCheck");

        var fillColorPicker = dialogWindow.FindControl<ColorPicker>("FrechetFillColorPicker");
        var fillOpacitySpin = dialogWindow.FindControl<NumericUpDown>("FrechetFillOpacitySpin");
        var futureStepsSpin = dialogWindow.FindControl<NumericUpDown>("FrechetFutureStepsSpin");
        var showConfidenceBandCheck = dialogWindow.FindControl<CheckBox>("FrechetShowConfidenceBandCheck");
        var confidenceMultiplierSpin = dialogWindow.FindControl<NumericUpDown>("FrechetConfidenceMultiplierSpin");

        if (unmatchedPicker != null) unmatchedPicker.Color = frechetObj.UnmatchedColor;
        if (priceFieldCombo != null) priceFieldCombo.SelectedIndex = PriceTypeToIndex(frechetObj.PriceSource);
        if (maxDistanceSpin != null) maxDistanceSpin.Value = (decimal)frechetObj.MaxDistance;
        if (showMatchHighlightCheck != null) showMatchHighlightCheck.IsChecked = frechetObj.ShowMatchHighlight;

        if (fillColorPicker != null) fillColorPicker.Color = frechetObj.FillColor;
        if (fillOpacitySpin != null) fillOpacitySpin.Value = frechetObj.FillOpacity;
        if (futureStepsSpin != null) futureStepsSpin.Value = frechetObj.FutureSteps;
        if (showConfidenceBandCheck != null) showConfidenceBandCheck.IsChecked = frechetObj.ShowConfidenceBand;
        if (confidenceMultiplierSpin != null) confidenceMultiplierSpin.Value = frechetObj.ConfidenceMultiplier;
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not FrechetProjectionObject frechetObj) return;

        var unmatchedPicker = dialogWindow.FindControl<ColorPicker>("FrechetUnmatchedColorPicker");
        var priceFieldCombo = dialogWindow.FindControl<ComboBox>("FrechetProjectionPriceFieldCombo");
        var maxDistanceSpin = dialogWindow.FindControl<NumericUpDown>("FrechetMaxDistanceSpin");
        var showMatchHighlightCheck = dialogWindow.FindControl<CheckBox>("FrechetShowMatchHighlightCheck");

        var fillColorPicker = dialogWindow.FindControl<ColorPicker>("FrechetFillColorPicker");
        var fillOpacitySpin = dialogWindow.FindControl<NumericUpDown>("FrechetFillOpacitySpin");
        var futureStepsSpin = dialogWindow.FindControl<NumericUpDown>("FrechetFutureStepsSpin");
        var showConfidenceBandCheck = dialogWindow.FindControl<CheckBox>("FrechetShowConfidenceBandCheck");
        var confidenceMultiplierSpin = dialogWindow.FindControl<NumericUpDown>("FrechetConfidenceMultiplierSpin");

        if (unmatchedPicker != null) frechetObj.UnmatchedColor = unmatchedPicker.Color;
        if (priceFieldCombo != null) frechetObj.PriceSource = IndexToPriceType(priceFieldCombo.SelectedIndex);
        if (maxDistanceSpin?.Value != null) frechetObj.MaxDistance = (double)maxDistanceSpin.Value.Value;
        if (showMatchHighlightCheck?.IsChecked != null) frechetObj.ShowMatchHighlight = showMatchHighlightCheck.IsChecked.Value;

        if (fillColorPicker != null) frechetObj.FillColor = fillColorPicker.Color;
        if (fillOpacitySpin?.Value != null) frechetObj.FillOpacity = (int)fillOpacitySpin.Value.Value;
        if (futureStepsSpin?.Value != null) frechetObj.FutureSteps = (int)futureStepsSpin.Value.Value;
        if (showConfidenceBandCheck?.IsChecked != null) frechetObj.ShowConfidenceBand = showConfidenceBandCheck.IsChecked.Value;
        if (confidenceMultiplierSpin?.Value != null) frechetObj.ConfidenceMultiplier = confidenceMultiplierSpin.Value.Value;
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
