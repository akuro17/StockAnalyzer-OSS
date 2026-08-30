using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public sealed class RangeSplineSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is RangeSplineObject;

    public void Activate(Window dialogWindow)
    {
        var rPanel = dialogWindow.FindControl<StackPanel>("RangeSplinePanel");
        if (rPanel != null) rPanel.IsVisible = true;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not RangeSplineObject rangeSpline) return;
        var showResistanceCheck = dialogWindow.FindControl<CheckBox>("RangeSplineShowResistanceCheck");
        var showSupportCheck = dialogWindow.FindControl<CheckBox>("RangeSplineShowSupportCheck");
        var priceFieldCombo = dialogWindow.FindControl<ComboBox>("RangeSplinePriceFieldCombo");
        var tensionSpin = dialogWindow.FindControl<NumericUpDown>("RangeSplineTensionSpin");
        var minSwingSpin = dialogWindow.FindControl<NumericUpDown>("RangeSplineMinSwingSpin");
        var maxLevelsSpin = dialogWindow.FindControl<NumericUpDown>("RangeSplineMaxLevelsSpin");

        if (showResistanceCheck != null) showResistanceCheck.IsChecked = rangeSpline.ShowResistanceLevels;
        if (showSupportCheck != null) showSupportCheck.IsChecked = rangeSpline.ShowSupportLevels;
        if (priceFieldCombo != null) priceFieldCombo.SelectedIndex = (int)rangeSpline.PriceField;
        if (tensionSpin != null) tensionSpin.Value = (decimal)rangeSpline.Tension;
        if (minSwingSpin != null) minSwingSpin.Value = (decimal)rangeSpline.MinSwingPercent;
        if (maxLevelsSpin != null) maxLevelsSpin.Value = rangeSpline.MaxLevels;
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not RangeSplineObject rangeSpline) return;
        var showResistanceCheck = dialogWindow.FindControl<CheckBox>("RangeSplineShowResistanceCheck");
        var showSupportCheck = dialogWindow.FindControl<CheckBox>("RangeSplineShowSupportCheck");
        var priceFieldCombo = dialogWindow.FindControl<ComboBox>("RangeSplinePriceFieldCombo");
        var tensionSpin = dialogWindow.FindControl<NumericUpDown>("RangeSplineTensionSpin");
        var minSwingSpin = dialogWindow.FindControl<NumericUpDown>("RangeSplineMinSwingSpin");
        var maxLevelsSpin = dialogWindow.FindControl<NumericUpDown>("RangeSplineMaxLevelsSpin");

        if (showResistanceCheck?.IsChecked != null) rangeSpline.ShowResistanceLevels = showResistanceCheck.IsChecked.Value;
        if (showSupportCheck?.IsChecked != null) rangeSpline.ShowSupportLevels = showSupportCheck.IsChecked.Value;
        if (priceFieldCombo != null && priceFieldCombo.SelectedIndex >= 0) rangeSpline.PriceField = (PriceField)priceFieldCombo.SelectedIndex;
        if (tensionSpin?.Value != null) rangeSpline.Tension = (double)tensionSpin.Value;
        if (minSwingSpin?.Value != null) rangeSpline.MinSwingPercent = (double)minSwingSpin.Value;
        if (maxLevelsSpin?.Value != null) rangeSpline.MaxLevels = (int)maxLevelsSpin.Value;
        rangeSpline.InvalidateExtrema();
    }
}
