using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public sealed class SsaMultiComponentSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is SsaMultiComponentObject;

    public void Activate(Window dialogWindow)
    {
        var genericColorPanel = dialogWindow.FindControl<StackPanel>("GenericColorPanel");
        var thicknessPanel = dialogWindow.FindControl<StackPanel>("ThicknessPanel");
        var ssaMultiPanel = dialogWindow.FindControl<StackPanel>("SsaMultiComponentPanel");

        if (genericColorPanel != null) genericColorPanel.IsVisible = true;
        if (thicknessPanel != null) thicknessPanel.IsVisible = true;
        if (ssaMultiPanel != null) ssaMultiPanel.IsVisible = true;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not SsaMultiComponentObject ssaObj) return;

        var embeddingDimensionSpin = dialogWindow.FindControl<NumericUpDown>("SsaMultiEmbeddingDimensionSpin");
        var numComponentsSpin = dialogWindow.FindControl<NumericUpDown>("SsaMultiNumComponentsSpin");
        var priceFieldCombo = dialogWindow.FindControl<ComboBox>("SsaMultiPriceFieldCombo");
        var detrendMethodCombo = dialogWindow.FindControl<ComboBox>("SsaMultiDetrendMethodCombo");
        var showTrendCheck = dialogWindow.FindControl<CheckBox>("SsaMultiShowTrendCheck");
        var showPrimaryCycleCheck = dialogWindow.FindControl<CheckBox>("SsaMultiShowPrimaryCycleCheck");
        var showCompositeCheck = dialogWindow.FindControl<CheckBox>("SsaMultiShowCompositeCheck");
        var showNoiseBandCheck = dialogWindow.FindControl<CheckBox>("SsaMultiShowNoiseBandCheck");
        var noiseMultiplierSpin = dialogWindow.FindControl<NumericUpDown>("SsaMultiNoiseMultiplierSpin");

        var trendColorPicker = dialogWindow.FindControl<ColorPicker>("SsaMultiTrendColorPicker");
        var cycleColorPicker = dialogWindow.FindControl<ColorPicker>("SsaMultiCycleColorPicker");
        var compositeColorPicker = dialogWindow.FindControl<ColorPicker>("SsaMultiCompositeColorPicker");
        var noiseColorPicker = dialogWindow.FindControl<ColorPicker>("SsaMultiNoiseColorPicker");

        if (embeddingDimensionSpin != null) embeddingDimensionSpin.Value = ssaObj.EmbeddingDimension;
        if (numComponentsSpin != null) numComponentsSpin.Value = ssaObj.NumComponents;
        if (priceFieldCombo != null) priceFieldCombo.SelectedIndex = PriceTypeToIndex(ssaObj.PriceSource);
        if (detrendMethodCombo != null) detrendMethodCombo.SelectedIndex = (int)ssaObj.DetrendMethod;
        if (showTrendCheck != null) showTrendCheck.IsChecked = ssaObj.ShowTrendLayer;
        if (showPrimaryCycleCheck != null) showPrimaryCycleCheck.IsChecked = ssaObj.ShowPrimaryCycleLayer;
        if (showCompositeCheck != null) showCompositeCheck.IsChecked = ssaObj.ShowCompositeLayer;
        if (showNoiseBandCheck != null) showNoiseBandCheck.IsChecked = ssaObj.ShowNoiseBand;
        if (noiseMultiplierSpin != null) noiseMultiplierSpin.Value = ssaObj.NoiseMultiplier;

        if (trendColorPicker != null) trendColorPicker.Color = ssaObj.TrendColor;
        if (cycleColorPicker != null) cycleColorPicker.Color = ssaObj.PrimaryCycleColor;
        if (compositeColorPicker != null) compositeColorPicker.Color = ssaObj.CompositeColor;
        if (noiseColorPicker != null) noiseColorPicker.Color = ssaObj.NoiseBandColor;
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not SsaMultiComponentObject ssaObj) return;

        var embeddingDimensionSpin = dialogWindow.FindControl<NumericUpDown>("SsaMultiEmbeddingDimensionSpin");
        var numComponentsSpin = dialogWindow.FindControl<NumericUpDown>("SsaMultiNumComponentsSpin");
        var priceFieldCombo = dialogWindow.FindControl<ComboBox>("SsaMultiPriceFieldCombo");
        var detrendMethodCombo = dialogWindow.FindControl<ComboBox>("SsaMultiDetrendMethodCombo");
        var showTrendCheck = dialogWindow.FindControl<CheckBox>("SsaMultiShowTrendCheck");
        var showPrimaryCycleCheck = dialogWindow.FindControl<CheckBox>("SsaMultiShowPrimaryCycleCheck");
        var showCompositeCheck = dialogWindow.FindControl<CheckBox>("SsaMultiShowCompositeCheck");
        var showNoiseBandCheck = dialogWindow.FindControl<CheckBox>("SsaMultiShowNoiseBandCheck");
        var noiseMultiplierSpin = dialogWindow.FindControl<NumericUpDown>("SsaMultiNoiseMultiplierSpin");

        var trendColorPicker = dialogWindow.FindControl<ColorPicker>("SsaMultiTrendColorPicker");
        var cycleColorPicker = dialogWindow.FindControl<ColorPicker>("SsaMultiCycleColorPicker");
        var compositeColorPicker = dialogWindow.FindControl<ColorPicker>("SsaMultiCompositeColorPicker");
        var noiseColorPicker = dialogWindow.FindControl<ColorPicker>("SsaMultiNoiseColorPicker");

        if (embeddingDimensionSpin?.Value != null) ssaObj.EmbeddingDimension = (int)embeddingDimensionSpin.Value.Value;
        if (numComponentsSpin?.Value != null) ssaObj.NumComponents = (int)numComponentsSpin.Value.Value;
        if (priceFieldCombo != null) ssaObj.PriceSource = IndexToPriceType(priceFieldCombo.SelectedIndex);
        if (detrendMethodCombo != null && detrendMethodCombo.SelectedIndex >= 0)
        {
            ssaObj.DetrendMethod = (SsaDetrendMode)detrendMethodCombo.SelectedIndex;
        }
        if (showTrendCheck?.IsChecked != null) ssaObj.ShowTrendLayer = showTrendCheck.IsChecked.Value;
        if (showPrimaryCycleCheck?.IsChecked != null) ssaObj.ShowPrimaryCycleLayer = showPrimaryCycleCheck.IsChecked.Value;
        if (showCompositeCheck?.IsChecked != null) ssaObj.ShowCompositeLayer = showCompositeCheck.IsChecked.Value;
        if (showNoiseBandCheck?.IsChecked != null) ssaObj.ShowNoiseBand = showNoiseBandCheck.IsChecked.Value;
        if (noiseMultiplierSpin?.Value != null) ssaObj.NoiseMultiplier = noiseMultiplierSpin.Value.Value;

        if (trendColorPicker != null) ssaObj.TrendColor = trendColorPicker.Color;
        if (cycleColorPicker != null) ssaObj.PrimaryCycleColor = cycleColorPicker.Color;
        if (compositeColorPicker != null) ssaObj.CompositeColor = compositeColorPicker.Color;
        if (noiseColorPicker != null) ssaObj.NoiseBandColor = noiseColorPicker.Color;
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
