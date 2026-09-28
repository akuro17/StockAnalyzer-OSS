using System;
using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public sealed class CatenaryCurveSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is CatenaryCurveObject;

    public void Activate(Window dialogWindow)
    {
        var catPanel = dialogWindow.FindControl<StackPanel>("CatenaryCurvePanel");
        if (catPanel != null) catPanel.IsVisible = true;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not CatenaryCurveObject cat) return;
        var p0PriceSpin = dialogWindow.FindControl<NumericUpDown>("CatenaryP0PriceSpin");
        var p1PriceSpin = dialogWindow.FindControl<NumericUpDown>("CatenaryP1PriceSpin");
        var p2PriceSpin = dialogWindow.FindControl<NumericUpDown>("CatenaryP2PriceSpin");
        var showProjCheck = dialogWindow.FindControl<CheckBox>("CatenaryShowProjectionCheck");
        var projBarsSpin = dialogWindow.FindControl<NumericUpDown>("CatenaryProjectionBarsSpin");

        if (cat.Points.Count >= 3)
        {
            if (p0PriceSpin != null) p0PriceSpin.Value = cat.Points[0].Price;
            if (p1PriceSpin != null) p1PriceSpin.Value = cat.Points[1].Price;
            if (p2PriceSpin != null) p2PriceSpin.Value = cat.Points[2].Price;
        }
        if (showProjCheck != null) showProjCheck.IsChecked = cat.ShowProjection;
        if (projBarsSpin != null) projBarsSpin.Value = cat.ProjectionBars;
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not CatenaryCurveObject cat) return;
        var p0PriceSpin = dialogWindow.FindControl<NumericUpDown>("CatenaryP0PriceSpin");
        var p1PriceSpin = dialogWindow.FindControl<NumericUpDown>("CatenaryP1PriceSpin");
        var p2PriceSpin = dialogWindow.FindControl<NumericUpDown>("CatenaryP2PriceSpin");
        var showProjCheck = dialogWindow.FindControl<CheckBox>("CatenaryShowProjectionCheck");
        var projBarsSpin = dialogWindow.FindControl<NumericUpDown>("CatenaryProjectionBarsSpin");

        if (cat.Points.Count >= 3)
        {
            decimal p0Price = p0PriceSpin?.Value ?? cat.Points[0].Price;
            decimal p1Price = p1PriceSpin?.Value ?? cat.Points[1].Price;
            decimal p2Price = p2PriceSpin?.Value ?? cat.Points[2].Price;

            cat.Points[0] = new ChartPoint(cat.Points[0].Time, p0Price);
            cat.Points[1] = new ChartPoint(cat.Points[1].Time, p1Price);

            // Automatically center P2 in time between P0 and P1
            long midTicks = (cat.Points[0].Time.Ticks + cat.Points[1].Time.Ticks) / 2;
            cat.Points[2] = new ChartPoint(new DateTime(midTicks), p2Price);
        }

        if (showProjCheck?.IsChecked != null) cat.ShowProjection = showProjCheck.IsChecked.Value;
        if (projBarsSpin?.Value != null) cat.ProjectionBars = (int)projBarsSpin.Value;
    }
}
