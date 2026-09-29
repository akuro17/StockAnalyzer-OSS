using System;
using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public sealed class SsaSupportResistanceSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing.GetType() == typeof(SsaSupportResistanceObject);

    public void Activate(Window dialogWindow)
    {
        var genericColorPanel = dialogWindow.FindControl<StackPanel>("GenericColorPanel");
        var thicknessPanel = dialogWindow.FindControl<StackPanel>("ThicknessPanel");
        var ssaPanel = dialogWindow.FindControl<StackPanel>("SsaSupportResistancePanel");

        if (genericColorPanel != null) genericColorPanel.IsVisible = true;
        if (thicknessPanel != null) thicknessPanel.IsVisible = true;
        if (ssaPanel != null) ssaPanel.IsVisible = true;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not SsaSupportResistanceObject ssaObj) return;

        var modeCombo = dialogWindow.FindControl<ComboBox>("SsaSnrModeCombo");
        var embeddingSpin = dialogWindow.FindControl<NumericUpDown>("SsaSnrEmbeddingDimensionSpin");
        var numComponentsSpin = dialogWindow.FindControl<NumericUpDown>("SsaSnrNumComponentsSpin");
        var autoRankCheck = dialogWindow.FindControl<CheckBox>("SsaSnrAutoRankCheck");
        var priceFieldCombo = dialogWindow.FindControl<ComboBox>("SsaSnrPriceFieldCombo");
        var detrendMethodCombo = dialogWindow.FindControl<ComboBox>("SsaSnrDetrendMethodCombo");

        var maxLevelsSpin = dialogWindow.FindControl<NumericUpDown>("SsaSnrMaxLevelsSpin");
        var clusterToleranceSpin = dialogWindow.FindControl<NumericUpDown>("SsaSnrClusterToleranceSpin");
        var extendLinesCheck = dialogWindow.FindControl<CheckBox>("SsaSnrExtendLinesCheck");

        var multiplierSpin = dialogWindow.FindControl<NumericUpDown>("SsaSnrMultiplierSpin");
        var channelFillOpacitySpin = dialogWindow.FindControl<NumericUpDown>("SsaSnrChannelFillOpacitySpin");

        var futureStepsSpin = dialogWindow.FindControl<NumericUpDown>("SsaSnrFutureStepsSpin");
        var forecastModeCombo = dialogWindow.FindControl<ComboBox>("SsaSnrForecastModeCombo");

        var resColorPicker = dialogWindow.FindControl<ColorPicker>("SsaSnrResistanceColorPicker");
        var supColorPicker = dialogWindow.FindControl<ColorPicker>("SsaSnrSupportColorPicker");
        var centerColorPicker = dialogWindow.FindControl<ColorPicker>("SsaSnrCenterColorPicker");

        var mode1Section = dialogWindow.FindControl<StackPanel>("SsaSnrMode1Section");
        var mode2Section = dialogWindow.FindControl<StackPanel>("SsaSnrMode2Section");
        var mode3Section = dialogWindow.FindControl<StackPanel>("SsaSnrMode3Section");

        if (modeCombo != null)
        {
            modeCombo.SelectedIndex = (int)ssaObj.Mode;
            UpdateModeSectionsVisibility(ssaObj.Mode, mode1Section, mode2Section, mode3Section);

            modeCombo.SelectionChanged += (_, _) =>
            {
                if (modeCombo.SelectedIndex >= 0)
                {
                    UpdateModeSectionsVisibility((SsaSupportResistanceMode)modeCombo.SelectedIndex, mode1Section, mode2Section, mode3Section);
                }
            };
        }

        if (embeddingSpin != null) embeddingSpin.Value = ssaObj.EmbeddingDimension;
        if (numComponentsSpin != null) numComponentsSpin.Value = ssaObj.NumComponents;
        if (autoRankCheck != null) autoRankCheck.IsChecked = ssaObj.AutoRank;
        if (priceFieldCombo != null) priceFieldCombo.SelectedIndex = PriceTypeToIndex(ssaObj.PriceSource);
        if (detrendMethodCombo != null) detrendMethodCombo.SelectedIndex = (int)ssaObj.DetrendMethod;

        if (maxLevelsSpin != null) maxLevelsSpin.Value = ssaObj.MaxLevelsPerSide;
        if (clusterToleranceSpin != null) clusterToleranceSpin.Value = ssaObj.ClusterTolerance;
        if (extendLinesCheck != null) extendLinesCheck.IsChecked = ssaObj.ExtendLinesToRight;

        if (multiplierSpin != null) multiplierSpin.Value = ssaObj.Multiplier;
        if (channelFillOpacitySpin != null) channelFillOpacitySpin.Value = ssaObj.ChannelFillOpacity;

        if (futureStepsSpin != null) futureStepsSpin.Value = ssaObj.FutureSteps;
        if (forecastModeCombo != null) forecastModeCombo.SelectedIndex = (int)ssaObj.ForecastMode;

        if (resColorPicker != null) resColorPicker.Color = ssaObj.ResistanceColor;
        if (supColorPicker != null) supColorPicker.Color = ssaObj.SupportColor;
        if (centerColorPicker != null) centerColorPicker.Color = ssaObj.CenterLineColor;
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not SsaSupportResistanceObject ssaObj) return;

        var modeCombo = dialogWindow.FindControl<ComboBox>("SsaSnrModeCombo");
        var embeddingSpin = dialogWindow.FindControl<NumericUpDown>("SsaSnrEmbeddingDimensionSpin");
        var numComponentsSpin = dialogWindow.FindControl<NumericUpDown>("SsaSnrNumComponentsSpin");
        var autoRankCheck = dialogWindow.FindControl<CheckBox>("SsaSnrAutoRankCheck");
        var priceFieldCombo = dialogWindow.FindControl<ComboBox>("SsaSnrPriceFieldCombo");
        var detrendMethodCombo = dialogWindow.FindControl<ComboBox>("SsaSnrDetrendMethodCombo");

        var maxLevelsSpin = dialogWindow.FindControl<NumericUpDown>("SsaSnrMaxLevelsSpin");
        var clusterToleranceSpin = dialogWindow.FindControl<NumericUpDown>("SsaSnrClusterToleranceSpin");
        var extendLinesCheck = dialogWindow.FindControl<CheckBox>("SsaSnrExtendLinesCheck");

        var multiplierSpin = dialogWindow.FindControl<NumericUpDown>("SsaSnrMultiplierSpin");
        var channelFillOpacitySpin = dialogWindow.FindControl<NumericUpDown>("SsaSnrChannelFillOpacitySpin");

        var futureStepsSpin = dialogWindow.FindControl<NumericUpDown>("SsaSnrFutureStepsSpin");
        var forecastModeCombo = dialogWindow.FindControl<ComboBox>("SsaSnrForecastModeCombo");

        var resColorPicker = dialogWindow.FindControl<ColorPicker>("SsaSnrResistanceColorPicker");
        var supColorPicker = dialogWindow.FindControl<ColorPicker>("SsaSnrSupportColorPicker");
        var centerColorPicker = dialogWindow.FindControl<ColorPicker>("SsaSnrCenterColorPicker");

        if (modeCombo != null && modeCombo.SelectedIndex >= 0)
        {
            ssaObj.Mode = (SsaSupportResistanceMode)modeCombo.SelectedIndex;
        }

        if (embeddingSpin?.Value != null) ssaObj.EmbeddingDimension = (int)embeddingSpin.Value.Value;
        if (numComponentsSpin?.Value != null) ssaObj.NumComponents = (int)numComponentsSpin.Value.Value;
        if (autoRankCheck?.IsChecked != null) ssaObj.AutoRank = autoRankCheck.IsChecked.Value;
        if (priceFieldCombo != null) ssaObj.PriceSource = IndexToPriceType(priceFieldCombo.SelectedIndex);
        if (detrendMethodCombo != null && detrendMethodCombo.SelectedIndex >= 0)
        {
            ssaObj.DetrendMethod = (SsaDetrendMode)detrendMethodCombo.SelectedIndex;
        }

        if (maxLevelsSpin?.Value != null) ssaObj.MaxLevelsPerSide = (int)maxLevelsSpin.Value.Value;
        if (clusterToleranceSpin?.Value != null) ssaObj.ClusterTolerance = clusterToleranceSpin.Value.Value;
        if (extendLinesCheck?.IsChecked != null) ssaObj.ExtendLinesToRight = extendLinesCheck.IsChecked.Value;

        if (multiplierSpin?.Value != null) ssaObj.Multiplier = multiplierSpin.Value.Value;
        if (channelFillOpacitySpin?.Value != null) ssaObj.ChannelFillOpacity = (int)channelFillOpacitySpin.Value.Value;

        if (futureStepsSpin?.Value != null) ssaObj.FutureSteps = (int)futureStepsSpin.Value.Value;
        if (forecastModeCombo != null && forecastModeCombo.SelectedIndex >= 0)
        {
            ssaObj.ForecastMode = (SsaForecastMode)forecastModeCombo.SelectedIndex;
        }

        if (resColorPicker != null) ssaObj.ResistanceColor = resColorPicker.Color;
        if (supColorPicker != null) ssaObj.SupportColor = supColorPicker.Color;
        if (centerColorPicker != null) ssaObj.CenterLineColor = centerColorPicker.Color;

        ssaObj.InvalidateCache();
    }

    private static void UpdateModeSectionsVisibility(
        SsaSupportResistanceMode mode,
        StackPanel? mode1Section,
        StackPanel? mode2Section,
        StackPanel? mode3Section)
    {
        if (mode1Section != null) mode1Section.IsVisible = (mode == SsaSupportResistanceMode.StructuralPivots);
        if (mode2Section != null) mode2Section.IsVisible = (mode == SsaSupportResistanceMode.DynamicEnvelopes);
        if (mode3Section != null) mode3Section.IsVisible = (mode == SsaSupportResistanceMode.ProjectedTargets);
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
