using System;
using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public sealed class SsaDynamicEnvelopesSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is SsaDynamicEnvelopesObject;

    public void Activate(Window dialogWindow)
    {
        var genericColorPanel = dialogWindow.FindControl<StackPanel>("GenericColorPanel");
        var thicknessPanel = dialogWindow.FindControl<StackPanel>("ThicknessPanel");
        var panel = dialogWindow.FindControl<StackPanel>("SsaDynamicEnvelopesPanel");

        if (genericColorPanel != null) genericColorPanel.IsVisible = true;
        if (thicknessPanel != null) thicknessPanel.IsVisible = true;
        if (panel != null) panel.IsVisible = true;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not SsaDynamicEnvelopesObject ssaObj) return;

        var embeddingSpin = dialogWindow.FindControl<NumericUpDown>("SsaEnvelopesEmbeddingDimensionSpin");
        var numComponentsSpin = dialogWindow.FindControl<NumericUpDown>("SsaEnvelopesNumComponentsSpin");
        var autoRankCheck = dialogWindow.FindControl<CheckBox>("SsaEnvelopesAutoRankCheck");
        var priceFieldCombo = dialogWindow.FindControl<ComboBox>("SsaEnvelopesPriceFieldCombo");
        var detrendMethodCombo = dialogWindow.FindControl<ComboBox>("SsaEnvelopesDetrendMethodCombo");

        var multiplierSpin = dialogWindow.FindControl<NumericUpDown>("SsaEnvelopesMultiplierSpin");
        var channelFillOpacitySpin = dialogWindow.FindControl<NumericUpDown>("SsaEnvelopesChannelFillOpacitySpin");

        var resColorPicker = dialogWindow.FindControl<ColorPicker>("SsaEnvelopesResistanceColorPicker");
        var supColorPicker = dialogWindow.FindControl<ColorPicker>("SsaEnvelopesSupportColorPicker");
        var centerColorPicker = dialogWindow.FindControl<ColorPicker>("SsaEnvelopesCenterColorPicker");

        if (embeddingSpin != null) embeddingSpin.Value = ssaObj.EmbeddingDimension;
        if (numComponentsSpin != null) numComponentsSpin.Value = ssaObj.NumComponents;
        if (autoRankCheck != null) autoRankCheck.IsChecked = ssaObj.AutoRank;
        if (priceFieldCombo != null) priceFieldCombo.SelectedIndex = PriceTypeToIndex(ssaObj.PriceSource);
        if (detrendMethodCombo != null) detrendMethodCombo.SelectedIndex = (int)ssaObj.DetrendMethod;

        if (multiplierSpin != null) multiplierSpin.Value = ssaObj.Multiplier;
        if (channelFillOpacitySpin != null) channelFillOpacitySpin.Value = ssaObj.ChannelFillOpacity;

        if (resColorPicker != null) resColorPicker.Color = ssaObj.ResistanceColor;
        if (supColorPicker != null) supColorPicker.Color = ssaObj.SupportColor;
        if (centerColorPicker != null) centerColorPicker.Color = ssaObj.CenterLineColor;
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not SsaDynamicEnvelopesObject ssaObj) return;

        var embeddingSpin = dialogWindow.FindControl<NumericUpDown>("SsaEnvelopesEmbeddingDimensionSpin");
        var numComponentsSpin = dialogWindow.FindControl<NumericUpDown>("SsaEnvelopesNumComponentsSpin");
        var autoRankCheck = dialogWindow.FindControl<CheckBox>("SsaEnvelopesAutoRankCheck");
        var priceFieldCombo = dialogWindow.FindControl<ComboBox>("SsaEnvelopesPriceFieldCombo");
        var detrendMethodCombo = dialogWindow.FindControl<ComboBox>("SsaEnvelopesDetrendMethodCombo");

        var multiplierSpin = dialogWindow.FindControl<NumericUpDown>("SsaEnvelopesMultiplierSpin");
        var channelFillOpacitySpin = dialogWindow.FindControl<NumericUpDown>("SsaEnvelopesChannelFillOpacitySpin");

        var resColorPicker = dialogWindow.FindControl<ColorPicker>("SsaEnvelopesResistanceColorPicker");
        var supColorPicker = dialogWindow.FindControl<ColorPicker>("SsaEnvelopesSupportColorPicker");
        var centerColorPicker = dialogWindow.FindControl<ColorPicker>("SsaEnvelopesCenterColorPicker");

        if (embeddingSpin?.Value != null) ssaObj.EmbeddingDimension = (int)embeddingSpin.Value.Value;
        if (numComponentsSpin?.Value != null) ssaObj.NumComponents = (int)numComponentsSpin.Value.Value;
        if (autoRankCheck?.IsChecked != null) ssaObj.AutoRank = autoRankCheck.IsChecked.Value;
        if (priceFieldCombo != null) ssaObj.PriceSource = IndexToPriceType(priceFieldCombo.SelectedIndex);
        if (detrendMethodCombo != null && detrendMethodCombo.SelectedIndex >= 0)
        {
            ssaObj.DetrendMethod = (SsaDetrendMode)detrendMethodCombo.SelectedIndex;
        }

        if (multiplierSpin?.Value != null) ssaObj.Multiplier = multiplierSpin.Value.Value;
        if (channelFillOpacitySpin?.Value != null) ssaObj.ChannelFillOpacity = (int)channelFillOpacitySpin.Value.Value;

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
