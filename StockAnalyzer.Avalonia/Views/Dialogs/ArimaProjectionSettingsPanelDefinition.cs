using System;
using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public sealed class ArimaProjectionSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is ArimaProjectionObject;

    public void Activate(Window dialogWindow)
    {
        var genericColorPanel = dialogWindow.FindControl<StackPanel>("GenericColorPanel");
        var thicknessPanel = dialogWindow.FindControl<StackPanel>("ThicknessPanel");
        var arimaPanel = dialogWindow.FindControl<StackPanel>("ArimaProjectionPanel");
        var fillOpacityPanel = dialogWindow.FindControl<StackPanel>("ArimaFillOpacityPanel");

        if (genericColorPanel != null) genericColorPanel.IsVisible = true;
        if (thicknessPanel != null) thicknessPanel.IsVisible = true;
        if (arimaPanel != null) arimaPanel.IsVisible = true;
        if (fillOpacityPanel != null) fillOpacityPanel.IsVisible = true;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not ArimaProjectionObject arimaObj) return;

        var pSpin = dialogWindow.FindControl<NumericUpDown>("ArimaPSpin");
        var dSpin = dialogWindow.FindControl<NumericUpDown>("ArimaDSpin");
        var qSpin = dialogWindow.FindControl<NumericUpDown>("ArimaQSpin");
        var priceSourceCombo = dialogWindow.FindControl<ComboBox>("ArimaPriceSourceCombo");
        var fillColorPicker = dialogWindow.FindControl<ColorPicker>("ArimaFillColorPicker");
        var fillOpacitySpin = dialogWindow.FindControl<NumericUpDown>("ArimaFillOpacitySpin");
        var futureStepsSpin = dialogWindow.FindControl<NumericUpDown>("ArimaFutureStepsSpin");
        var showConfidenceBandCheck = dialogWindow.FindControl<CheckBox>("ArimaShowConfidenceBandCheck");
        var confidenceMultiplierSpin = dialogWindow.FindControl<NumericUpDown>("ArimaConfidenceMultiplierSpin");

        if (pSpin != null) pSpin.Value = arimaObj.P;
        if (dSpin != null) dSpin.Value = arimaObj.D;
        if (qSpin != null) qSpin.Value = arimaObj.Q;
        if (priceSourceCombo != null)
        {
            priceSourceCombo.ItemsSource = PriceDataHelper.PriceTypeOptions;
            priceSourceCombo.SelectedItem = arimaObj.PriceSource;
        }
        if (fillColorPicker != null) fillColorPicker.Color = arimaObj.FillColor;
        if (fillOpacitySpin != null) fillOpacitySpin.Value = arimaObj.FillOpacity;
        if (futureStepsSpin != null) futureStepsSpin.Value = arimaObj.FutureSteps;
        if (showConfidenceBandCheck != null) showConfidenceBandCheck.IsChecked = arimaObj.ShowConfidenceBand;
        if (confidenceMultiplierSpin != null) confidenceMultiplierSpin.Value = arimaObj.ConfidenceMultiplier;
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not ArimaProjectionObject arimaObj) return;

        var pSpin = dialogWindow.FindControl<NumericUpDown>("ArimaPSpin");
        var dSpin = dialogWindow.FindControl<NumericUpDown>("ArimaDSpin");
        var qSpin = dialogWindow.FindControl<NumericUpDown>("ArimaQSpin");
        var priceSourceCombo = dialogWindow.FindControl<ComboBox>("ArimaPriceSourceCombo");
        var fillColorPicker = dialogWindow.FindControl<ColorPicker>("ArimaFillColorPicker");
        var fillOpacitySpin = dialogWindow.FindControl<NumericUpDown>("ArimaFillOpacitySpin");
        var futureStepsSpin = dialogWindow.FindControl<NumericUpDown>("ArimaFutureStepsSpin");
        var showConfidenceBandCheck = dialogWindow.FindControl<CheckBox>("ArimaShowConfidenceBandCheck");
        var confidenceMultiplierSpin = dialogWindow.FindControl<NumericUpDown>("ArimaConfidenceMultiplierSpin");

        if (pSpin?.Value != null) arimaObj.P = (int)pSpin.Value.Value;
        if (dSpin?.Value != null) arimaObj.D = (int)dSpin.Value.Value;
        if (qSpin?.Value != null) arimaObj.Q = (int)qSpin.Value.Value;
        if (priceSourceCombo?.SelectedItem is PriceType priceType) arimaObj.PriceSource = priceType;
        if (fillColorPicker != null) arimaObj.FillColor = fillColorPicker.Color;
        if (fillOpacitySpin?.Value != null) arimaObj.FillOpacity = (int)fillOpacitySpin.Value.Value;
        if (futureStepsSpin?.Value != null) arimaObj.FutureSteps = (int)futureStepsSpin.Value.Value;
        if (showConfidenceBandCheck?.IsChecked != null) arimaObj.ShowConfidenceBand = showConfidenceBandCheck.IsChecked.Value;
        if (confidenceMultiplierSpin?.Value != null) arimaObj.ConfidenceMultiplier = confidenceMultiplierSpin.Value.Value;
    }
}
