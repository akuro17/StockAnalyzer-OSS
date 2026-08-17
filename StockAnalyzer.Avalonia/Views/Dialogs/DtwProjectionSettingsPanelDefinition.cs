using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public sealed class DtwProjectionSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is DtwProjectionObject;

    public void Activate(Window dialogWindow)
    {
        var dtwPanel = dialogWindow.FindControl<StackPanel>("DtwProjectionPanel");
        var genericColorPanel = dialogWindow.FindControl<StackPanel>("GenericColorPanel");
        var thicknessPanel = dialogWindow.FindControl<StackPanel>("ThicknessPanel");
        // Unlike most patterns, DtwProjection explicitly keeps the generic Color/Thickness
        // controls visible (they are shared/hidden by default only for tools that opt out).
        if (genericColorPanel != null) genericColorPanel.IsVisible = true;
        if (thicknessPanel != null) thicknessPanel.IsVisible = true;
        if (dtwPanel != null) dtwPanel.IsVisible = true;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not DtwProjectionObject dtwObj) return;
        var handleSizeSpin = dialogWindow.FindControl<NumericUpDown>("DtwHandleSizeSpin");
        if (handleSizeSpin != null) handleSizeSpin.Value = (decimal)dtwObj.HandleSize;
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not DtwProjectionObject dtwObj) return;
        var handleSizeSpin = dialogWindow.FindControl<NumericUpDown>("DtwHandleSizeSpin");
        if (handleSizeSpin?.Value != null) dtwObj.HandleSize = (double)handleSizeSpin.Value;
    }
}
