using Avalonia.Controls;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public sealed class ClothoidCurveProjectionSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is ClothoidCurveProjectionObject;

    public void Activate(Window dialogWindow)
    {
        var genericColorPanel = dialogWindow.FindControl<StackPanel>("GenericColorPanel");
        var thicknessPanel = dialogWindow.FindControl<StackPanel>("ThicknessPanel");
        var clothoidPanel = dialogWindow.FindControl<StackPanel>("ClothoidCurveProjectionPanel");
        var fillOpacityPanel = dialogWindow.FindControl<StackPanel>("ClothoidFillOpacityPanel");

        if (genericColorPanel != null) genericColorPanel.IsVisible = true;
        if (thicknessPanel != null) thicknessPanel.IsVisible = true;
        if (clothoidPanel != null) clothoidPanel.IsVisible = true;
        if (fillOpacityPanel != null) fillOpacityPanel.IsVisible = true;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not ClothoidCurveProjectionObject clothoidObj) return;

        var autoPolarityCheck = dialogWindow.FindControl<CheckBox>("ClothoidAutoPolarityCheck");
        var curvatureRateSpin = dialogWindow.FindControl<NumericUpDown>("ClothoidCurvatureRateSpin");
        var priceSourceCombo = dialogWindow.FindControl<ComboBox>("ClothoidPriceSourceCombo");
        var fillColorPicker = dialogWindow.FindControl<ColorPicker>("ClothoidFillColorPicker");
        var fillOpacitySpin = dialogWindow.FindControl<NumericUpDown>("ClothoidFillOpacitySpin");
        var futureStepsSpin = dialogWindow.FindControl<NumericUpDown>("ClothoidFutureStepsSpin");
        var showConfidenceBandCheck = dialogWindow.FindControl<CheckBox>("ClothoidShowConfidenceBandCheck");
        var confidenceMultiplierSpin = dialogWindow.FindControl<NumericUpDown>("ClothoidConfidenceMultiplierSpin");

        if (autoPolarityCheck != null) autoPolarityCheck.IsChecked = clothoidObj.AutoPolarity;
        if (curvatureRateSpin != null) curvatureRateSpin.Value = clothoidObj.CurvatureRate;
        if (priceSourceCombo != null)
        {
            priceSourceCombo.ItemsSource = PriceDataHelper.PriceTypeOptions;
            priceSourceCombo.SelectedItem = clothoidObj.PriceSource;
        }
        if (fillColorPicker != null) fillColorPicker.Color = clothoidObj.FillColor;
        if (fillOpacitySpin != null) fillOpacitySpin.Value = clothoidObj.FillOpacity;
        if (futureStepsSpin != null) futureStepsSpin.Value = clothoidObj.FutureSteps;
        if (showConfidenceBandCheck != null) showConfidenceBandCheck.IsChecked = clothoidObj.ShowConfidenceBand;
        if (confidenceMultiplierSpin != null) confidenceMultiplierSpin.Value = clothoidObj.ConfidenceMultiplier;
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not ClothoidCurveProjectionObject clothoidObj) return;

        var autoPolarityCheck = dialogWindow.FindControl<CheckBox>("ClothoidAutoPolarityCheck");
        var curvatureRateSpin = dialogWindow.FindControl<NumericUpDown>("ClothoidCurvatureRateSpin");
        var priceSourceCombo = dialogWindow.FindControl<ComboBox>("ClothoidPriceSourceCombo");
        var fillColorPicker = dialogWindow.FindControl<ColorPicker>("ClothoidFillColorPicker");
        var fillOpacitySpin = dialogWindow.FindControl<NumericUpDown>("ClothoidFillOpacitySpin");
        var futureStepsSpin = dialogWindow.FindControl<NumericUpDown>("ClothoidFutureStepsSpin");
        var showConfidenceBandCheck = dialogWindow.FindControl<CheckBox>("ClothoidShowConfidenceBandCheck");
        var confidenceMultiplierSpin = dialogWindow.FindControl<NumericUpDown>("ClothoidConfidenceMultiplierSpin");

        if (autoPolarityCheck?.IsChecked != null) clothoidObj.AutoPolarity = autoPolarityCheck.IsChecked.Value;
        if (curvatureRateSpin?.Value != null) clothoidObj.CurvatureRate = curvatureRateSpin.Value.Value;
        if (priceSourceCombo?.SelectedItem is PriceType priceType) clothoidObj.PriceSource = priceType;
        if (fillColorPicker != null) clothoidObj.FillColor = fillColorPicker.Color;
        if (fillOpacitySpin?.Value != null) clothoidObj.FillOpacity = (int)fillOpacitySpin.Value.Value;
        if (futureStepsSpin?.Value != null) clothoidObj.FutureSteps = (int)futureStepsSpin.Value.Value;
        if (showConfidenceBandCheck?.IsChecked != null) clothoidObj.ShowConfidenceBand = showConfidenceBandCheck.IsChecked.Value;
        if (confidenceMultiplierSpin?.Value != null) clothoidObj.ConfidenceMultiplier = confidenceMultiplierSpin.Value.Value;
    }
}
