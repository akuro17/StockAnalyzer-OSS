using System.Collections.Generic;
using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Views.Controls;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public sealed class AnchoredVwapSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    private static readonly string[] HiddenTags = [DrawingParameterTags.Common, DrawingParameterTags.Geometry];

    public DrawingSettingsWindowHint? WindowHint => null;

    public bool UsesDynamicSettings => true;

    public IReadOnlyCollection<string> DynamicHiddenParameterTags => HiddenTags;

    public bool CanHandle(IChartObject drawing) => drawing is AnchoredVwapObject;

    public void Activate(Window dialogWindow)
    {
        var genericColorPanel = dialogWindow.FindControl<StackPanel>("GenericColorPanel");
        var thicknessPanel = dialogWindow.FindControl<StackPanel>("ThicknessPanel");
        var vwapPanel = dialogWindow.FindControl<StackPanel>("AnchoredVwapPanel");
        var dynamicSettings = dialogWindow.FindControl<DynamicDrawingSettingsView>("DynamicSettingsView");

        if (genericColorPanel != null) genericColorPanel.IsVisible = true;
        if (thicknessPanel != null) thicknessPanel.IsVisible = true;
        if (vwapPanel != null) vwapPanel.IsVisible = true;
        if (dynamicSettings != null) dynamicSettings.IsVisible = true;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not AnchoredVwapObject vwap) return;

        var priceFieldCombo = dialogWindow.FindControl<ComboBox>("AnchoredVwapPriceFieldCombo");
        if (priceFieldCombo != null)
        {
            priceFieldCombo.SelectedIndex = PriceDataHelper.GetPriceTypeIndex(vwap.PriceSource);
        }
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not AnchoredVwapObject vwap) return;

        var priceFieldCombo = dialogWindow.FindControl<ComboBox>("AnchoredVwapPriceFieldCombo");
        if (priceFieldCombo != null && priceFieldCombo.SelectedIndex >= 0)
        {
            vwap.PriceSource = PriceDataHelper.GetPriceTypeByIndex(priceFieldCombo.SelectedIndex);
        }
    }
}
