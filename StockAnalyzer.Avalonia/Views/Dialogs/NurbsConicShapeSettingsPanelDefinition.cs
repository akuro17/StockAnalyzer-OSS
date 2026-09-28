using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

/// <summary>
/// Settings-dialog behavior shared by NurbsConicObject and NurbsEllipseObject
/// (<see cref="INurbsConicShapeObject"/>), which used byte-for-byte identical wiring logic inside
/// DrawingSettingsDialog's constructor/OnOkClick (same NurbsConicPanel and controls).
/// </summary>
public sealed class NurbsConicShapeSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is INurbsConicShapeObject;

    public void Activate(Window dialogWindow)
    {
        var conicPanel = dialogWindow.FindControl<StackPanel>("NurbsConicPanel");
        if (conicPanel != null) conicPanel.IsVisible = true;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not INurbsConicShapeObject shape) return;
        var filledCheck = dialogWindow.FindControl<CheckBox>("NurbsConicFilledCheck");
        var fillPicker = dialogWindow.FindControl<ColorPicker>("NurbsConicFillColorPicker");

        if (filledCheck != null) filledCheck.IsChecked = shape.IsFilled;
        if (fillPicker != null) fillPicker.Color = shape.FillColor;
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not INurbsConicShapeObject shape) return;
        var filledCheck = dialogWindow.FindControl<CheckBox>("NurbsConicFilledCheck");
        var fillPicker = dialogWindow.FindControl<ColorPicker>("NurbsConicFillColorPicker");

        if (filledCheck?.IsChecked != null) shape.IsFilled = filledCheck.IsChecked.Value;
        if (fillPicker != null) shape.FillColor = fillPicker.Color;
    }
}
