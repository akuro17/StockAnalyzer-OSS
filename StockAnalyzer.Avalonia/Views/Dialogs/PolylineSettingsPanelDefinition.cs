using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public sealed class PolylineSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is PolylineObject;

    public void Activate(Window dialogWindow)
    {
        var elliottPanel = dialogWindow.FindControl<StackPanel>("ElliottPanel");
        if (elliottPanel != null) elliottPanel.IsVisible = true;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not PolylineObject poly) return;
        var labelTypeCombo = dialogWindow.FindControl<ComboBox>("LabelTypeCombo");
        var showLabelsCheck = dialogWindow.FindControl<CheckBox>("ShowLabelsCheck");
        var fontSizeSpin = dialogWindow.FindControl<NumericUpDown>("FontSizeSpin");
        var smoothCheck = dialogWindow.FindControl<CheckBox>("PolylineSmoothCheck");
        var tensionSpin = dialogWindow.FindControl<NumericUpDown>("PolylineTensionSpin");

        if (labelTypeCombo != null) labelTypeCombo.SelectedIndex = (int)poly.LabelType;
        if (showLabelsCheck != null) showLabelsCheck.IsChecked = poly.ShowLabels;
        if (fontSizeSpin != null) fontSizeSpin.Value = (decimal)poly.FontSize;
        if (smoothCheck != null) smoothCheck.IsChecked = poly.IsSmooth;
        if (tensionSpin != null) tensionSpin.Value = (decimal)poly.Tension;
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not PolylineObject poly) return;
        var labelTypeCombo = dialogWindow.FindControl<ComboBox>("LabelTypeCombo");
        var showLabelsCheck = dialogWindow.FindControl<CheckBox>("ShowLabelsCheck");
        var fontSizeSpin = dialogWindow.FindControl<NumericUpDown>("FontSizeSpin");
        var smoothCheck = dialogWindow.FindControl<CheckBox>("PolylineSmoothCheck");
        var tensionSpin = dialogWindow.FindControl<NumericUpDown>("PolylineTensionSpin");

        if (labelTypeCombo != null) poly.LabelType = (PolylineLabelType)labelTypeCombo.SelectedIndex;
        if (showLabelsCheck?.IsChecked != null) poly.ShowLabels = showLabelsCheck.IsChecked.Value;
        if (fontSizeSpin?.Value != null) poly.FontSize = (double)fontSizeSpin.Value;
        if (smoothCheck?.IsChecked != null) poly.IsSmooth = smoothCheck.IsChecked.Value;
        if (tensionSpin?.Value != null) poly.Tension = (double)tensionSpin.Value;
    }
}
