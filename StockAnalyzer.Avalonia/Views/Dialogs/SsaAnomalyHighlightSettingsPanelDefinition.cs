using System;
using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public sealed class SsaAnomalyHighlightSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is SsaAnomalyHighlightObject;

    public void Activate(Window dialogWindow)
    {
        var genericColorPanel = dialogWindow.FindControl<StackPanel>("GenericColorPanel");
        var thicknessPanel = dialogWindow.FindControl<StackPanel>("ThicknessPanel");
        var panel = dialogWindow.FindControl<StackPanel>("SsaAnomalyHighlightPanel");

        if (genericColorPanel != null) genericColorPanel.IsVisible = true;
        if (thicknessPanel != null) thicknessPanel.IsVisible = true;
        if (panel != null) panel.IsVisible = true;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not SsaAnomalyHighlightObject ssaObj) return;

        var embeddingSpin = dialogWindow.FindControl<NumericUpDown>("SsaAnomalyEmbeddingDimensionSpin");
        var numComponentsSpin = dialogWindow.FindControl<NumericUpDown>("SsaAnomalyNumComponentsSpin");
        var autoRankCheck = dialogWindow.FindControl<CheckBox>("SsaAnomalyAutoRankCheck");
        var priceFieldCombo = dialogWindow.FindControl<ComboBox>("SsaAnomalyPriceFieldCombo");
        var detrendMethodCombo = dialogWindow.FindControl<ComboBox>("SsaAnomalyDetrendMethodCombo");

        var enterThresholdSpin = dialogWindow.FindControl<NumericUpDown>("SsaAnomalyEnterThresholdSpin");
        var exitThresholdSpin = dialogWindow.FindControl<NumericUpDown>("SsaAnomalyExitThresholdSpin");
        var coolDownSpin = dialogWindow.FindControl<NumericUpDown>("SsaAnomalyCoolDownPeriodSpin");
        var minDurationSpin = dialogWindow.FindControl<NumericUpDown>("SsaAnomalyMinDurationSpin");
        var highlightOpacitySpin = dialogWindow.FindControl<NumericUpDown>("SsaAnomalyHighlightOpacitySpin");

        var showStructuralLineCheck = dialogWindow.FindControl<CheckBox>("SsaAnomalyShowStructuralLineCheck");
        var showBoundaryBandsCheck = dialogWindow.FindControl<CheckBox>("SsaAnomalyShowBoundaryBandsCheck");
        var showAnomalyBadgesCheck = dialogWindow.FindControl<CheckBox>("SsaAnomalyShowAnomalyBadgesCheck");

        var bullishColorPicker = dialogWindow.FindControl<ColorPicker>("SsaAnomalyBullishColorPicker");
        var bearishColorPicker = dialogWindow.FindControl<ColorPicker>("SsaAnomalyBearishColorPicker");
        var structuralColorPicker = dialogWindow.FindControl<ColorPicker>("SsaAnomalyStructuralColorPicker");

        if (embeddingSpin != null) embeddingSpin.Value = ssaObj.EmbeddingDimension;
        if (numComponentsSpin != null) numComponentsSpin.Value = ssaObj.NumComponents;
        if (autoRankCheck != null) autoRankCheck.IsChecked = ssaObj.AutoRank;
        if (priceFieldCombo != null) priceFieldCombo.SelectedIndex = PriceTypeToIndex(ssaObj.PriceSource);
        if (detrendMethodCombo != null) detrendMethodCombo.SelectedIndex = (int)ssaObj.DetrendMethod;

        if (enterThresholdSpin != null) enterThresholdSpin.Value = (decimal)ssaObj.EnterThreshold;
        if (exitThresholdSpin != null) exitThresholdSpin.Value = (decimal)ssaObj.ExitThreshold;
        if (coolDownSpin != null) coolDownSpin.Value = ssaObj.CoolDownPeriod;
        if (minDurationSpin != null) minDurationSpin.Value = ssaObj.MinDuration;
        if (highlightOpacitySpin != null) highlightOpacitySpin.Value = ssaObj.HighlightOpacity;

        if (showStructuralLineCheck != null) showStructuralLineCheck.IsChecked = ssaObj.ShowStructuralLine;
        if (showBoundaryBandsCheck != null) showBoundaryBandsCheck.IsChecked = ssaObj.ShowBoundaryBands;
        if (showAnomalyBadgesCheck != null) showAnomalyBadgesCheck.IsChecked = ssaObj.ShowAnomalyBadges;

        if (bullishColorPicker != null) bullishColorPicker.Color = ssaObj.BullishColor;
        if (bearishColorPicker != null) bearishColorPicker.Color = ssaObj.BearishColor;
        if (structuralColorPicker != null) structuralColorPicker.Color = ssaObj.StructuralLineColor;
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not SsaAnomalyHighlightObject ssaObj) return;

        var embeddingSpin = dialogWindow.FindControl<NumericUpDown>("SsaAnomalyEmbeddingDimensionSpin");
        var numComponentsSpin = dialogWindow.FindControl<NumericUpDown>("SsaAnomalyNumComponentsSpin");
        var autoRankCheck = dialogWindow.FindControl<CheckBox>("SsaAnomalyAutoRankCheck");
        var priceFieldCombo = dialogWindow.FindControl<ComboBox>("SsaAnomalyPriceFieldCombo");
        var detrendMethodCombo = dialogWindow.FindControl<ComboBox>("SsaAnomalyDetrendMethodCombo");

        var enterThresholdSpin = dialogWindow.FindControl<NumericUpDown>("SsaAnomalyEnterThresholdSpin");
        var exitThresholdSpin = dialogWindow.FindControl<NumericUpDown>("SsaAnomalyExitThresholdSpin");
        var coolDownSpin = dialogWindow.FindControl<NumericUpDown>("SsaAnomalyCoolDownPeriodSpin");
        var minDurationSpin = dialogWindow.FindControl<NumericUpDown>("SsaAnomalyMinDurationSpin");
        var highlightOpacitySpin = dialogWindow.FindControl<NumericUpDown>("SsaAnomalyHighlightOpacitySpin");

        var showStructuralLineCheck = dialogWindow.FindControl<CheckBox>("SsaAnomalyShowStructuralLineCheck");
        var showBoundaryBandsCheck = dialogWindow.FindControl<CheckBox>("SsaAnomalyShowBoundaryBandsCheck");
        var showAnomalyBadgesCheck = dialogWindow.FindControl<CheckBox>("SsaAnomalyShowAnomalyBadgesCheck");

        var bullishColorPicker = dialogWindow.FindControl<ColorPicker>("SsaAnomalyBullishColorPicker");
        var bearishColorPicker = dialogWindow.FindControl<ColorPicker>("SsaAnomalyBearishColorPicker");
        var structuralColorPicker = dialogWindow.FindControl<ColorPicker>("SsaAnomalyStructuralColorPicker");

        if (embeddingSpin?.Value != null) ssaObj.EmbeddingDimension = (int)embeddingSpin.Value.Value;
        if (numComponentsSpin?.Value != null) ssaObj.NumComponents = (int)numComponentsSpin.Value.Value;
        if (autoRankCheck?.IsChecked != null) ssaObj.AutoRank = autoRankCheck.IsChecked.Value;
        if (priceFieldCombo != null) ssaObj.PriceSource = IndexToPriceType(priceFieldCombo.SelectedIndex);
        if (detrendMethodCombo != null && detrendMethodCombo.SelectedIndex >= 0)
        {
            ssaObj.DetrendMethod = (SsaDetrendMode)detrendMethodCombo.SelectedIndex;
        }

        if (enterThresholdSpin?.Value != null) ssaObj.EnterThreshold = (double)enterThresholdSpin.Value.Value;
        if (exitThresholdSpin?.Value != null) ssaObj.ExitThreshold = (double)exitThresholdSpin.Value.Value;
        if (coolDownSpin?.Value != null) ssaObj.CoolDownPeriod = (int)coolDownSpin.Value.Value;
        if (minDurationSpin?.Value != null) ssaObj.MinDuration = (int)minDurationSpin.Value.Value;
        if (highlightOpacitySpin?.Value != null) ssaObj.HighlightOpacity = (int)highlightOpacitySpin.Value.Value;

        if (showStructuralLineCheck?.IsChecked != null) ssaObj.ShowStructuralLine = showStructuralLineCheck.IsChecked.Value;
        if (showBoundaryBandsCheck?.IsChecked != null) ssaObj.ShowBoundaryBands = showBoundaryBandsCheck.IsChecked.Value;
        if (showAnomalyBadgesCheck?.IsChecked != null) ssaObj.ShowAnomalyBadges = showAnomalyBadgesCheck.IsChecked.Value;

        if (bullishColorPicker != null) ssaObj.BullishColor = bullishColorPicker.Color;
        if (bearishColorPicker != null) ssaObj.BearishColor = bearishColorPicker.Color;
        if (structuralColorPicker != null) ssaObj.StructuralLineColor = structuralColorPicker.Color;

        ssaObj.InvalidateCache();
    }

    private static int PriceTypeToIndex(PriceType type)
    {
        for (int i = 0; i < PriceDataHelper.PriceTypeOptions.Count; i++)
        {
            if (PriceDataHelper.PriceTypeOptions[i] == type) return i;
        }
        return 3; // Default to Close
    }

    private static PriceType IndexToPriceType(int index)
    {
        return index >= 0 && index < PriceDataHelper.PriceTypeOptions.Count
            ? PriceDataHelper.PriceTypeOptions[index]
            : PriceType.Close;
    }
}
