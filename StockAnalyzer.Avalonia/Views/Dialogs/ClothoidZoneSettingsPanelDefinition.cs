using Avalonia.Controls;
using System.Collections.Generic;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Views.Controls;
using StockAnalyzer.Core.Constants;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

/// <summary>
/// Settings-dialog panel definition for ClothoidZoneObject.
/// Enables dynamic settings UI for zone strength, boundary mode, and fill/gradient styling while hiding Common stroke properties.
/// </summary>
public sealed class ClothoidZoneSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    private static readonly string[] HiddenTags = [DrawingParameterTags.Common, DrawingParameterTags.Geometry];

    public DrawingSettingsWindowHint? WindowHint => null;

    public bool UsesDynamicSettings => true;

    public IReadOnlyCollection<string> DynamicHiddenParameterTags => HiddenTags;

    public bool CanHandle(IChartObject drawing) => drawing is ClothoidZoneObject;

    public void Activate(Window dialogWindow)
    {
        var dynamicSettings = dialogWindow.FindControl<DynamicDrawingSettingsView>("DynamicSettingsView");
        if (dynamicSettings != null) dynamicSettings.IsVisible = true;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        var dynamicSettings = dialogWindow.FindControl<DynamicDrawingSettingsView>("DynamicSettingsView");
        if (dynamicSettings == null) return;
        dynamicSettings.HiddenParameterTags = HiddenTags;
        dynamicSettings.ParameterObject = drawing;
    }

    public void Commit(Window dialogWindow, IChartObject drawing) { }
}
