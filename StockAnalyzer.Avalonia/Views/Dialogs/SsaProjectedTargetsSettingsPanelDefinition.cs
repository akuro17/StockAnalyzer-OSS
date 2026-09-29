using System;
using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public sealed class SsaProjectedTargetsSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is SsaProjectedTargetsObject;

    public void Activate(Window dialogWindow)
    {
        var genericColorPanel = dialogWindow.FindControl<StackPanel>("GenericColorPanel");
        var thicknessPanel = dialogWindow.FindControl<StackPanel>("ThicknessPanel");
        var panel = dialogWindow.FindControl<StackPanel>("SsaProjectedTargetsPanel");

        if (genericColorPanel != null) genericColorPanel.IsVisible = true;
        if (thicknessPanel != null) thicknessPanel.IsVisible = true;
        if (panel != null) panel.IsVisible = true;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not SsaProjectedTargetsObject ssaObj) return;

        var embeddingSpin = dialogWindow.FindControl<NumericUpDown>("SsaTargetsEmbeddingDimensionSpin");
        var numComponentsSpin = dialogWindow.FindControl<NumericUpDown>("SsaTargetsNumComponentsSpin");
        var autoRankCheck = dialogWindow.FindControl<CheckBox>("SsaTargetsAutoRankCheck");
        var priceFieldCombo = dialogWindow.FindControl<ComboBox>("SsaTargetsPriceFieldCombo");
        var detrendMethodCombo = dialogWindow.FindControl<ComboBox>("SsaTargetsDetrendMethodCombo");

        var futureStepsSpin = dialogWindow.FindControl<NumericUpDown>("SsaTargetsFutureStepsSpin");
        var forecastModeCombo = dialogWindow.FindControl<ComboBox>("SsaTargetsForecastModeCombo");
        var extendLinesCheck = dialogWindow.FindControl<CheckBox>("SsaTargetsExtendLinesCheck");

        var resColorPicker = dialogWindow.FindControl<ColorPicker>("SsaTargetsResistanceColorPicker");
        var supColorPicker = dialogWindow.FindControl<ColorPicker>("SsaTargetsSupportColorPicker");
        var centerColorPicker = dialogWindow.FindControl<ColorPicker>("SsaTargetsCenterColorPicker");

        if (embeddingSpin != null) embeddingSpin.Value = ssaObj.EmbeddingDimension;
        if (numComponentsSpin != null) numComponentsSpin.Value = ssaObj.NumComponents;
        if (autoRankCheck != null) autoRankCheck.IsChecked = ssaObj.AutoRank;
        if (priceFieldCombo != null) priceFieldCombo.SelectedIndex = PriceTypeToIndex(ssaObj.PriceSource);
        if (detrendMethodCombo != null) detrendMethodCombo.SelectedIndex = (int)ssaObj.DetrendMethod;

        if (futureStepsSpin != null) futureStepsSpin.Value = ssaObj.FutureSteps;
        if (forecastModeCombo != null) forecastModeCombo.SelectedIndex = (int)ssaObj.ForecastMode;
        if (extendLinesCheck != null) extendLinesCheck.IsChecked = ssaObj.ExtendLinesToRight;

        if (resColorPicker != null) resColorPicker.Color = ssaObj.ResistanceColor;
        if (supColorPicker != null) supColorPicker.Color = ssaObj.SupportColor;
        if (centerColorPicker != null) centerColorPicker.Color = ssaObj.CenterLineColor;
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not SsaProjectedTargetsObject ssaObj) return;

        var embeddingSpin = dialogWindow.FindControl<NumericUpDown>("SsaTargetsEmbeddingDimensionSpin");
        var numComponentsSpin = dialogWindow.FindControl<NumericUpDown>("SsaTargetsNumComponentsSpin");
        var autoRankCheck = dialogWindow.FindControl<CheckBox>("SsaTargetsAutoRankCheck");
        var priceFieldCombo = dialogWindow.FindControl<ComboBox>("SsaTargetsPriceFieldCombo");
        var detrendMethodCombo = dialogWindow.FindControl<ComboBox>("SsaTargetsDetrendMethodCombo");

        var futureStepsSpin = dialogWindow.FindControl<NumericUpDown>("SsaTargetsFutureStepsSpin");
        var forecastModeCombo = dialogWindow.FindControl<ComboBox>("SsaTargetsForecastModeCombo");
        var extendLinesCheck = dialogWindow.FindControl<CheckBox>("SsaTargetsExtendLinesCheck");

        var resColorPicker = dialogWindow.FindControl<ColorPicker>("SsaTargetsResistanceColorPicker");
        var supColorPicker = dialogWindow.FindControl<ColorPicker>("SsaTargetsSupportColorPicker");
        var centerColorPicker = dialogWindow.FindControl<ColorPicker>("SsaTargetsCenterColorPicker");

        if (embeddingSpin?.Value != null) ssaObj.EmbeddingDimension = (int)embeddingSpin.Value.Value;
        if (numComponentsSpin?.Value != null) ssaObj.NumComponents = (int)numComponentsSpin.Value.Value;
        if (autoRankCheck?.IsChecked != null) ssaObj.AutoRank = autoRankCheck.IsChecked.Value;
        if (priceFieldCombo != null) ssaObj.PriceSource = IndexToPriceType(priceFieldCombo.SelectedIndex);
        if (detrendMethodCombo != null && detrendMethodCombo.SelectedIndex >= 0)
        {
            ssaObj.DetrendMethod = (SsaDetrendMode)detrendMethodCombo.SelectedIndex;
        }

        if (futureStepsSpin?.Value != null) ssaObj.FutureSteps = (int)futureStepsSpin.Value.Value;
        if (forecastModeCombo != null && forecastModeCombo.SelectedIndex >= 0)
        {
            ssaObj.ForecastMode = (SsaForecastMode)forecastModeCombo.SelectedIndex;
        }
        if (extendLinesCheck?.IsChecked != null) ssaObj.ExtendLinesToRight = extendLinesCheck.IsChecked.Value;

        if (resColorPicker != null) ssaObj.ResistanceColor = resColorPicker.Color;
        if (supColorPicker != null) ssaObj.SupportColor = supColorPicker.Color;
        if (centerColorPicker != null) ssaObj.CenterLineColor = centerColorPicker.Color;

        ssaObj.InvalidateCache();
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
