using System;
using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public sealed class TimeAtPriceSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is TimeAtPriceObject;

    public void Activate(Window dialogWindow)
    {
        var genericColorPanel = dialogWindow.FindControl<StackPanel>("GenericColorPanel");
        var thicknessPanel = dialogWindow.FindControl<StackPanel>("ThicknessPanel");
        var tapPanel = dialogWindow.FindControl<StackPanel>("TimeAtPricePanel");

        if (genericColorPanel != null) genericColorPanel.IsVisible = true;
        if (thicknessPanel != null) thicknessPanel.IsVisible = true;
        if (tapPanel != null) tapPanel.IsVisible = true;

        var priceSourceCombo = dialogWindow.FindControl<ComboBox>("TapPriceSourceCombo");
        if (priceSourceCombo != null && priceSourceCombo.Items.Count == 0)
        {
            foreach (var opt in PriceDataHelper.PriceTypeOptions)
            {
                priceSourceCombo.Items.Add(new ComboBoxItem
                {
                    Content = PriceDataHelper.FormatPriceTypeLabel(opt),
                    Tag = opt
                });
            }
        }

        var repeatModeCombo = dialogWindow.FindControl<ComboBox>("TapRepeatModeCombo");
        var lockRangeCheck = dialogWindow.FindControl<CheckBox>("TapLockRangeCheck");
        var rangeBarsSpin = dialogWindow.FindControl<NumericUpDown>("TapRangeBarsSpin");

        if (repeatModeCombo != null)
        {
            repeatModeCombo.SelectionChanged += (_, _) =>
            {
                UpdateRangeBarsState(repeatModeCombo, rangeBarsSpin, lockRangeCheck);
            };
        }
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not TimeAtPriceObject tap) return;

        var priceSourceCombo = dialogWindow.FindControl<ComboBox>("TapPriceSourceCombo");
        var valueAreaColorPicker = dialogWindow.FindControl<ColorPicker>("TapValueAreaColorPicker");
        var profileColorPicker = dialogWindow.FindControl<ColorPicker>("TapProfileColorPicker");
        var opacitySpin = dialogWindow.FindControl<NumericUpDown>("TapOpacitySpin");
        var fillColorPicker = dialogWindow.FindControl<ColorPicker>("TapFillColorPicker");
        var fillOpacitySpin = dialogWindow.FindControl<NumericUpDown>("TapFillOpacitySpin");
        var repeatModeCombo = dialogWindow.FindControl<ComboBox>("TapRepeatModeCombo");
        var lockRangeCheck = dialogWindow.FindControl<CheckBox>("TapLockRangeCheck");
        var rangeBarsSpin = dialogWindow.FindControl<NumericUpDown>("TapRangeBarsSpin");

        if (priceSourceCombo != null)
        {
            int idx = -1;
            for (int i = 0; i < PriceDataHelper.PriceTypeOptions.Count; i++)
            {
                if (PriceDataHelper.PriceTypeOptions[i] == tap.PriceType)
                {
                    idx = i;
                    break;
                }
            }
            priceSourceCombo.SelectedIndex = idx >= 0 ? idx : 3; // Default to Close (index 3 in PriceTypeOptions)
        }

        if (valueAreaColorPicker != null) valueAreaColorPicker.Color = tap.ValueAreaColor;
        if (profileColorPicker != null) profileColorPicker.Color = tap.ProfileColor;
        if (opacitySpin != null) opacitySpin.Value = (decimal)(tap.Opacity * 100);
        if (fillColorPicker != null) fillColorPicker.Color = tap.FillColor;
        if (fillOpacitySpin != null) fillOpacitySpin.Value = tap.FillOpacity;

        if (repeatModeCombo != null)
        {
            repeatModeCombo.SelectedIndex = tap.RepeatMode switch
            {
                VolumeProfileRepeatMode.Default => 0,
                VolumeProfileRepeatMode.RangeBar => 1,
                VolumeProfileRepeatMode.Weekly => 2,
                VolumeProfileRepeatMode.Monthly => 3,
                _ => 0
            };
        }
        if (lockRangeCheck != null) lockRangeCheck.IsChecked = tap.LockRange;
        if (rangeBarsSpin != null && tap.RangeBars > 0) rangeBarsSpin.Value = tap.RangeBars;

        UpdateRangeBarsState(repeatModeCombo, rangeBarsSpin, lockRangeCheck);
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not TimeAtPriceObject tap) return;

        var priceSourceCombo = dialogWindow.FindControl<ComboBox>("TapPriceSourceCombo");
        var valueAreaColorPicker = dialogWindow.FindControl<ColorPicker>("TapValueAreaColorPicker");
        var profileColorPicker = dialogWindow.FindControl<ColorPicker>("TapProfileColorPicker");
        var opacitySpin = dialogWindow.FindControl<NumericUpDown>("TapOpacitySpin");
        var fillColorPicker = dialogWindow.FindControl<ColorPicker>("TapFillColorPicker");
        var fillOpacitySpin = dialogWindow.FindControl<NumericUpDown>("TapFillOpacitySpin");
        var repeatModeCombo = dialogWindow.FindControl<ComboBox>("TapRepeatModeCombo");
        var lockRangeCheck = dialogWindow.FindControl<CheckBox>("TapLockRangeCheck");
        var rangeBarsSpin = dialogWindow.FindControl<NumericUpDown>("TapRangeBarsSpin");

        if (priceSourceCombo != null && priceSourceCombo.SelectedIndex >= 0 && priceSourceCombo.SelectedIndex < PriceDataHelper.PriceTypeOptions.Count)
        {
            tap.PriceType = PriceDataHelper.PriceTypeOptions[priceSourceCombo.SelectedIndex];
        }

        if (valueAreaColorPicker != null) tap.ValueAreaColor = valueAreaColorPicker.Color;
        if (profileColorPicker != null) tap.ProfileColor = profileColorPicker.Color;
        if (opacitySpin?.Value != null) tap.Opacity = (double)opacitySpin.Value / 100.0;
        if (fillColorPicker != null) tap.FillColor = fillColorPicker.Color;
        if (fillOpacitySpin?.Value != null) tap.FillOpacity = (int)fillOpacitySpin.Value;

        if (repeatModeCombo != null && repeatModeCombo.SelectedIndex >= 0)
        {
            tap.RepeatMode = repeatModeCombo.SelectedIndex switch
            {
                1 => VolumeProfileRepeatMode.RangeBar,
                2 => VolumeProfileRepeatMode.Weekly,
                3 => VolumeProfileRepeatMode.Monthly,
                _ => VolumeProfileRepeatMode.Default
            };
        }
        if (lockRangeCheck?.IsChecked != null) tap.LockRange = lockRangeCheck.IsChecked.Value;
        if (rangeBarsSpin?.Value != null && (int)rangeBarsSpin.Value != tap.RangeBars)
        {
            tap.RangeBars = (int)rangeBarsSpin.Value;
        }
    }

    private static void UpdateRangeBarsState(ComboBox? repeatModeCombo, NumericUpDown? rangeBarsSpin, CheckBox? lockRangeCheck)
    {
        if (repeatModeCombo == null) return;
        bool isRangeBarsApplicable = repeatModeCombo.SelectedIndex == 0 || repeatModeCombo.SelectedIndex == 1;
        if (rangeBarsSpin != null) rangeBarsSpin.IsEnabled = isRangeBarsApplicable;
        if (lockRangeCheck != null) lockRangeCheck.IsEnabled = isRangeBarsApplicable;
    }
}
