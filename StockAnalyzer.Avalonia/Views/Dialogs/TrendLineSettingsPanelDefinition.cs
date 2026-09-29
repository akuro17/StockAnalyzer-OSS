using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public sealed class TrendLineSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is TrendLineObject;

    public void Activate(Window dialogWindow)
    {
        var trendPanel = dialogWindow.FindControl<StackPanel>("TrendLinePanel");
        if (trendPanel != null) trendPanel.IsVisible = true;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not TrendLineObject trend) return;
        var showProjCheck = dialogWindow.FindControl<CheckBox>("ShowProjectionCheck");
        var projColsSpin = dialogWindow.FindControl<NumericUpDown>("ProjectionColumnsSpin");

        if (showProjCheck != null) showProjCheck.IsChecked = trend.ShowProjection;
        if (projColsSpin != null) projColsSpin.Value = trend.ProjectionColumns;
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not TrendLineObject trend) return;
        var showProjCheck = dialogWindow.FindControl<CheckBox>("ShowProjectionCheck");
        var projColsSpin = dialogWindow.FindControl<NumericUpDown>("ProjectionColumnsSpin");

        if (showProjCheck?.IsChecked != null) trend.ShowProjection = showProjCheck.IsChecked.Value;
        if (projColsSpin?.Value != null) trend.ProjectionColumns = (int)projColsSpin.Value;
    }
}
