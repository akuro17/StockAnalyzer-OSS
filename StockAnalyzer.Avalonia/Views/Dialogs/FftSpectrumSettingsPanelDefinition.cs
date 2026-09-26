using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public sealed class FftSpectrumSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is FftSpectrumObject;

    public void Activate(Window dialogWindow)
    {
        var fftPanel = dialogWindow.FindControl<StackPanel>("FftSpectrumPanel");
        var thicknessPanel = dialogWindow.FindControl<StackPanel>("ThicknessPanel");
        if (fftPanel != null) fftPanel.IsVisible = true;
        if (thicknessPanel != null) thicknessPanel.IsVisible = true;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not FftSpectrumObject fft) return;

        var peakColorPicker = dialogWindow.FindControl<ColorPicker>("FftPeakColorPicker");
        var priceFieldCombo = dialogWindow.FindControl<ComboBox>("FftPriceFieldCombo");
        var minPeriodSpin = dialogWindow.FindControl<NumericUpDown>("FftMinPeriodSpin");
        var maxPeriodSpin = dialogWindow.FindControl<NumericUpDown>("FftMaxPeriodSpin");
        var detrendCheck = dialogWindow.FindControl<CheckBox>("FftDetrendCheck");
        var windowCheck = dialogWindow.FindControl<CheckBox>("FftWindowCheck");
        var opacitySpin = dialogWindow.FindControl<NumericUpDown>("FftOpacitySpin");

        if (peakColorPicker != null) peakColorPicker.Color = fft.PeakColor;
        if (priceFieldCombo != null) priceFieldCombo.SelectedIndex = PriceTypeToIndex(fft.PriceSource);
        if (minPeriodSpin != null) minPeriodSpin.Value = (decimal)fft.MinPeriod;
        if (maxPeriodSpin != null) maxPeriodSpin.Value = (decimal)fft.MaxPeriod;
        if (detrendCheck != null) detrendCheck.IsChecked = fft.ApplyDetrend;
        if (windowCheck != null) windowCheck.IsChecked = fft.ApplyWindow;
        if (opacitySpin != null) opacitySpin.Value = (decimal)(fft.Opacity * 100);
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not FftSpectrumObject fft) return;

        var peakColorPicker = dialogWindow.FindControl<ColorPicker>("FftPeakColorPicker");
        var priceFieldCombo = dialogWindow.FindControl<ComboBox>("FftPriceFieldCombo");
        var minPeriodSpin = dialogWindow.FindControl<NumericUpDown>("FftMinPeriodSpin");
        var maxPeriodSpin = dialogWindow.FindControl<NumericUpDown>("FftMaxPeriodSpin");
        var detrendCheck = dialogWindow.FindControl<CheckBox>("FftDetrendCheck");
        var windowCheck = dialogWindow.FindControl<CheckBox>("FftWindowCheck");
        var opacitySpin = dialogWindow.FindControl<NumericUpDown>("FftOpacitySpin");

        if (peakColorPicker != null) fft.PeakColor = peakColorPicker.Color;
        if (priceFieldCombo != null) fft.PriceSource = IndexToPriceType(priceFieldCombo.SelectedIndex);
        if (minPeriodSpin?.Value != null) fft.MinPeriod = (double)minPeriodSpin.Value;
        if (maxPeriodSpin?.Value != null) fft.MaxPeriod = (double)maxPeriodSpin.Value;
        if (detrendCheck != null) fft.ApplyDetrend = detrendCheck.IsChecked ?? true;
        if (windowCheck != null) fft.ApplyWindow = windowCheck.IsChecked ?? true;
        if (opacitySpin?.Value != null) fft.Opacity = (double)opacitySpin.Value / 100.0;
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
