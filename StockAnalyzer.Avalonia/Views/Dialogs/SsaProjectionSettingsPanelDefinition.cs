using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public sealed class SsaProjectionSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is SsaProjectionObject;

    public void Activate(Window dialogWindow)
    {
        var genericColorPanel = dialogWindow.FindControl<StackPanel>("GenericColorPanel");
        var thicknessPanel = dialogWindow.FindControl<StackPanel>("ThicknessPanel");
        var ssaPanel = dialogWindow.FindControl<StackPanel>("SsaProjectionPanel");
        var fillOpacityPanel = dialogWindow.FindControl<StackPanel>("SsaFillOpacityPanel");

        if (genericColorPanel != null) genericColorPanel.IsVisible = true;
        if (thicknessPanel != null) thicknessPanel.IsVisible = true;
        if (ssaPanel != null) ssaPanel.IsVisible = true;
        if (fillOpacityPanel != null) fillOpacityPanel.IsVisible = true;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not SsaProjectionObject ssaObj) return;

        var embeddingDimensionSpin = dialogWindow.FindControl<NumericUpDown>("SsaEmbeddingDimensionSpin");
        var numComponentsSpin = dialogWindow.FindControl<NumericUpDown>("SsaNumComponentsSpin");
        var priceFieldCombo = dialogWindow.FindControl<ComboBox>("SsaProjectionPriceFieldCombo");
        var detrendMethodCombo = dialogWindow.FindControl<ComboBox>("SsaProjectionDetrendMethodCombo");
        var forecastModeCombo = dialogWindow.FindControl<ComboBox>("SsaProjectionForecastModeCombo");
        var showReconstructedCheck = dialogWindow.FindControl<CheckBox>("SsaShowReconstructedPathCheck");

        var fillColorPicker = dialogWindow.FindControl<ColorPicker>("SsaFillColorPicker");
        var fillOpacitySpin = dialogWindow.FindControl<NumericUpDown>("SsaFillOpacitySpin");
        var futureStepsSpin = dialogWindow.FindControl<NumericUpDown>("SsaFutureStepsSpin");
        var showConfidenceBandCheck = dialogWindow.FindControl<CheckBox>("SsaShowConfidenceBandCheck");
        var confidenceMultiplierSpin = dialogWindow.FindControl<NumericUpDown>("SsaConfidenceMultiplierSpin");

        if (embeddingDimensionSpin != null) embeddingDimensionSpin.Value = ssaObj.EmbeddingDimension;
        if (numComponentsSpin != null) numComponentsSpin.Value = ssaObj.NumComponents;
        if (priceFieldCombo != null) priceFieldCombo.SelectedIndex = PriceTypeToIndex(ssaObj.PriceSource);
        if (detrendMethodCombo != null) detrendMethodCombo.SelectedIndex = (int)ssaObj.DetrendMethod;
        if (forecastModeCombo != null) forecastModeCombo.SelectedIndex = (int)ssaObj.ForecastMode;
        if (showReconstructedCheck != null) showReconstructedCheck.IsChecked = ssaObj.ShowReconstructedPath;

        if (fillColorPicker != null) fillColorPicker.Color = ssaObj.FillColor;
        if (fillOpacitySpin != null) fillOpacitySpin.Value = ssaObj.FillOpacity;
        if (futureStepsSpin != null) futureStepsSpin.Value = ssaObj.FutureSteps;
        if (showConfidenceBandCheck != null) showConfidenceBandCheck.IsChecked = ssaObj.ShowConfidenceBand;
        if (confidenceMultiplierSpin != null) confidenceMultiplierSpin.Value = ssaObj.ConfidenceMultiplier;
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not SsaProjectionObject ssaObj) return;

        var embeddingDimensionSpin = dialogWindow.FindControl<NumericUpDown>("SsaEmbeddingDimensionSpin");
        var numComponentsSpin = dialogWindow.FindControl<NumericUpDown>("SsaNumComponentsSpin");
        var priceFieldCombo = dialogWindow.FindControl<ComboBox>("SsaProjectionPriceFieldCombo");
        var detrendMethodCombo = dialogWindow.FindControl<ComboBox>("SsaProjectionDetrendMethodCombo");
        var forecastModeCombo = dialogWindow.FindControl<ComboBox>("SsaProjectionForecastModeCombo");
        var showReconstructedCheck = dialogWindow.FindControl<CheckBox>("SsaShowReconstructedPathCheck");

        var fillColorPicker = dialogWindow.FindControl<ColorPicker>("SsaFillColorPicker");
        var fillOpacitySpin = dialogWindow.FindControl<NumericUpDown>("SsaFillOpacitySpin");
        var futureStepsSpin = dialogWindow.FindControl<NumericUpDown>("SsaFutureStepsSpin");
        var showConfidenceBandCheck = dialogWindow.FindControl<CheckBox>("SsaShowConfidenceBandCheck");
        var confidenceMultiplierSpin = dialogWindow.FindControl<NumericUpDown>("SsaConfidenceMultiplierSpin");

        if (embeddingDimensionSpin?.Value != null) ssaObj.EmbeddingDimension = (int)embeddingDimensionSpin.Value.Value;
        if (numComponentsSpin?.Value != null) ssaObj.NumComponents = (int)numComponentsSpin.Value.Value;
        if (priceFieldCombo != null) ssaObj.PriceSource = IndexToPriceType(priceFieldCombo.SelectedIndex);
        if (detrendMethodCombo != null && detrendMethodCombo.SelectedIndex >= 0)
        {
            ssaObj.DetrendMethod = (SsaDetrendMode)detrendMethodCombo.SelectedIndex;
        }
        if (forecastModeCombo != null && forecastModeCombo.SelectedIndex >= 0)
        {
            ssaObj.ForecastMode = (SsaForecastMode)forecastModeCombo.SelectedIndex;
        }
        if (showReconstructedCheck?.IsChecked != null) ssaObj.ShowReconstructedPath = showReconstructedCheck.IsChecked.Value;

        if (fillColorPicker != null) ssaObj.FillColor = fillColorPicker.Color;
        if (fillOpacitySpin?.Value != null) ssaObj.FillOpacity = (int)fillOpacitySpin.Value.Value;
        if (futureStepsSpin?.Value != null) ssaObj.FutureSteps = (int)futureStepsSpin.Value.Value;
        if (showConfidenceBandCheck?.IsChecked != null) ssaObj.ShowConfidenceBand = showConfidenceBandCheck.IsChecked.Value;
        if (confidenceMultiplierSpin?.Value != null) ssaObj.ConfidenceMultiplier = confidenceMultiplierSpin.Value.Value;
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
