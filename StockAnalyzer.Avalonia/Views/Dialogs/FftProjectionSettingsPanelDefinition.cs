using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public sealed class FftProjectionSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is FftProjectionObject;

    public void Activate(Window dialogWindow)
    {
        var genericColorPanel = dialogWindow.FindControl<StackPanel>("GenericColorPanel");
        var thicknessPanel = dialogWindow.FindControl<StackPanel>("ThicknessPanel");
        var fftPanel = dialogWindow.FindControl<StackPanel>("FftProjectionPanel");
        var fillOpacityPanel = dialogWindow.FindControl<StackPanel>("FftFillOpacityPanel");

        if (genericColorPanel != null) genericColorPanel.IsVisible = true;
        if (thicknessPanel != null) thicknessPanel.IsVisible = true;
        if (fftPanel != null) fftPanel.IsVisible = true;
        if (fillOpacityPanel != null) fillOpacityPanel.IsVisible = true;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not FftProjectionObject fftObj) return;

        var harmonicCountSpin = dialogWindow.FindControl<NumericUpDown>("FftHarmonicCountSpin");
        var detrendCheck = dialogWindow.FindControl<CheckBox>("FftProjectionApplyDetrendCheck");
        var minPeriodSpin = dialogWindow.FindControl<NumericUpDown>("FftProjectionMinPeriodSpin");
        var maxPeriodSpin = dialogWindow.FindControl<NumericUpDown>("FftProjectionMaxPeriodSpin");
        var priceFieldCombo = dialogWindow.FindControl<ComboBox>("FftProjectionPriceFieldCombo");

        var fillColorPicker = dialogWindow.FindControl<ColorPicker>("FftFillColorPicker");
        var fillOpacitySpin = dialogWindow.FindControl<NumericUpDown>("FftFillOpacitySpin");
        var futureStepsSpin = dialogWindow.FindControl<NumericUpDown>("FftFutureStepsSpin");
        var showConfidenceBandCheck = dialogWindow.FindControl<CheckBox>("FftShowConfidenceBandCheck");
        var confidenceMultiplierSpin = dialogWindow.FindControl<NumericUpDown>("FftConfidenceMultiplierSpin");

        if (harmonicCountSpin != null) harmonicCountSpin.Value = fftObj.HarmonicCount;
        if (detrendCheck != null) detrendCheck.IsChecked = fftObj.ApplyDetrend;
        if (minPeriodSpin != null) minPeriodSpin.Value = (decimal)fftObj.MinPeriod;
        if (maxPeriodSpin != null) maxPeriodSpin.Value = (decimal)fftObj.MaxPeriod;
        if (priceFieldCombo != null) priceFieldCombo.SelectedIndex = PriceTypeToIndex(fftObj.PriceSource);

        if (fillColorPicker != null) fillColorPicker.Color = fftObj.FillColor;
        if (fillOpacitySpin != null) fillOpacitySpin.Value = fftObj.FillOpacity;
        if (futureStepsSpin != null) futureStepsSpin.Value = fftObj.FutureSteps;
        if (showConfidenceBandCheck != null) showConfidenceBandCheck.IsChecked = fftObj.ShowConfidenceBand;
        if (confidenceMultiplierSpin != null) confidenceMultiplierSpin.Value = fftObj.ConfidenceMultiplier;
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not FftProjectionObject fftObj) return;

        var harmonicCountSpin = dialogWindow.FindControl<NumericUpDown>("FftHarmonicCountSpin");
        var detrendCheck = dialogWindow.FindControl<CheckBox>("FftProjectionApplyDetrendCheck");
        var minPeriodSpin = dialogWindow.FindControl<NumericUpDown>("FftProjectionMinPeriodSpin");
        var maxPeriodSpin = dialogWindow.FindControl<NumericUpDown>("FftProjectionMaxPeriodSpin");
        var priceFieldCombo = dialogWindow.FindControl<ComboBox>("FftProjectionPriceFieldCombo");

        var fillColorPicker = dialogWindow.FindControl<ColorPicker>("FftFillColorPicker");
        var fillOpacitySpin = dialogWindow.FindControl<NumericUpDown>("FftFillOpacitySpin");
        var futureStepsSpin = dialogWindow.FindControl<NumericUpDown>("FftFutureStepsSpin");
        var showConfidenceBandCheck = dialogWindow.FindControl<CheckBox>("FftShowConfidenceBandCheck");
        var confidenceMultiplierSpin = dialogWindow.FindControl<NumericUpDown>("FftConfidenceMultiplierSpin");

        if (harmonicCountSpin?.Value != null) fftObj.HarmonicCount = (int)harmonicCountSpin.Value.Value;
        if (detrendCheck?.IsChecked != null) fftObj.ApplyDetrend = detrendCheck.IsChecked.Value;
        if (minPeriodSpin?.Value != null) fftObj.MinPeriod = (double)minPeriodSpin.Value.Value;
        if (maxPeriodSpin?.Value != null) fftObj.MaxPeriod = (double)maxPeriodSpin.Value.Value;
        if (priceFieldCombo != null) fftObj.PriceSource = IndexToPriceType(priceFieldCombo.SelectedIndex);

        if (fillColorPicker != null) fftObj.FillColor = fillColorPicker.Color;
        if (fillOpacitySpin?.Value != null) fftObj.FillOpacity = (int)fillOpacitySpin.Value.Value;
        if (futureStepsSpin?.Value != null) fftObj.FutureSteps = (int)futureStepsSpin.Value.Value;
        if (showConfidenceBandCheck?.IsChecked != null) fftObj.ShowConfidenceBand = showConfidenceBandCheck.IsChecked.Value;
        if (confidenceMultiplierSpin?.Value != null) fftObj.ConfidenceMultiplier = confidenceMultiplierSpin.Value.Value;
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