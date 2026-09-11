using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public sealed class LongShortPositionSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is LongShortPositionObject;

    public void Activate(Window dialogWindow)
    {
        var lsPanel = dialogWindow.FindControl<StackPanel>("LongShortPanel");
        var thicknessPanel = dialogWindow.FindControl<StackPanel>("ThicknessPanel");
        var genericColorPanel = dialogWindow.FindControl<StackPanel>("GenericColorPanel");
        if (lsPanel != null) lsPanel.IsVisible = true;
        if (thicknessPanel != null) thicknessPanel.IsVisible = false;
        if (genericColorPanel != null) genericColorPanel.IsVisible = false;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not LongShortPositionObject lsObj) return;

        var opacitySpin = dialogWindow.FindControl<NumericUpDown>("LsOpacitySpin");
        var entrySpin = dialogWindow.FindControl<NumericUpDown>("LsEntryPriceSpin");
        var targetSpin = dialogWindow.FindControl<NumericUpDown>("LsTargetPriceSpin");
        var stopSpin = dialogWindow.FindControl<NumericUpDown>("LsStopPriceSpin");

        if (lsObj.Points.Count >= 3)
        {
            if (entrySpin != null) entrySpin.Value = lsObj.Points[0].Price;
            if (stopSpin != null) stopSpin.Value = lsObj.Points[1].Price;
            if (targetSpin != null) targetSpin.Value = lsObj.Points[2].Price;
        }

        if (opacitySpin != null) opacitySpin.Value = (decimal)(lsObj.AreaOpacity * 100);

        var lsTargetColorPicker = dialogWindow.FindControl<ColorPicker>("LsTargetColorPicker");
        if (lsTargetColorPicker != null) lsTargetColorPicker.Color = lsObj.TargetColor;

        var lsStopColorPicker = dialogWindow.FindControl<ColorPicker>("LsStopColorPicker");
        if (lsStopColorPicker != null) lsStopColorPicker.Color = lsObj.StopColor;
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not LongShortPositionObject lsObj) return;

        var entrySpin = dialogWindow.FindControl<NumericUpDown>("LsEntryPriceSpin");
        var targetSpin = dialogWindow.FindControl<NumericUpDown>("LsTargetPriceSpin");
        var stopSpin = dialogWindow.FindControl<NumericUpDown>("LsStopPriceSpin");
        var opacitySpin = dialogWindow.FindControl<NumericUpDown>("LsOpacitySpin");

        if (lsObj.Points.Count >= 3)
        {
            decimal entry = entrySpin?.Value ?? lsObj.Points[0].Price;
            decimal stop = LongShortPositionObject.ClampStopPrice(stopSpin?.Value ?? lsObj.Points[1].Price, entry, lsObj.IsLong);
            decimal target = LongShortPositionObject.ClampTargetPrice(targetSpin?.Value ?? lsObj.Points[2].Price, entry, lsObj.IsLong);

            lsObj.Points[0] = new ChartPoint(lsObj.Points[0].Time, entry);
            lsObj.Points[1] = new ChartPoint(lsObj.Points[1].Time, stop);
            lsObj.Points[2] = new ChartPoint(lsObj.Points[2].Time, target);
        }

        var lsTargetColorPicker = dialogWindow.FindControl<ColorPicker>("LsTargetColorPicker");
        if (lsTargetColorPicker != null) lsObj.TargetColor = lsTargetColorPicker.Color;

        var lsStopColorPicker = dialogWindow.FindControl<ColorPicker>("LsStopColorPicker");
        if (lsStopColorPicker != null) lsObj.StopColor = lsStopColorPicker.Color;

        if (opacitySpin?.Value != null) lsObj.AreaOpacity = (double)opacitySpin.Value / 100.0;
    }
}
