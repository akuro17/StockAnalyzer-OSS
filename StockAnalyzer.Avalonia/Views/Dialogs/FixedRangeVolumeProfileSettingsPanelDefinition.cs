using System;
using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public sealed class FixedRangeVolumeProfileSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is FixedRangeVolumeProfileObject;

    public void Activate(Window dialogWindow)
    {
        var genericColorPanel = dialogWindow.FindControl<StackPanel>("GenericColorPanel");
        var thicknessPanel = dialogWindow.FindControl<StackPanel>("ThicknessPanel");
        var vpPanel = dialogWindow.FindControl<StackPanel>("VolumeProfilePanel");

        if (genericColorPanel != null) genericColorPanel.IsVisible = true;
        if (thicknessPanel != null) thicknessPanel.IsVisible = true;
        if (vpPanel != null) vpPanel.IsVisible = true;

        var repeatModeCombo = dialogWindow.FindControl<ComboBox>("VpRepeatModeCombo");
        var lockRangeCheck = dialogWindow.FindControl<CheckBox>("VpLockRangeCheck");
        var rangeBarsSpin = dialogWindow.FindControl<NumericUpDown>("VpRangeBarsSpin");

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
        if (drawing is not FixedRangeVolumeProfileObject frvp) return;
        var valueAreaColorPicker = dialogWindow.FindControl<ColorPicker>("ValueAreaColorPicker");
        var profileColorPicker = dialogWindow.FindControl<ColorPicker>("ProfileColorPicker");
        var opacitySpin = dialogWindow.FindControl<NumericUpDown>("OpacitySpin");
        var fillColorPicker = dialogWindow.FindControl<ColorPicker>("VpFillColorPicker");
        var fillOpacitySpin = dialogWindow.FindControl<NumericUpDown>("VpFillOpacitySpin");
        var repeatModeCombo = dialogWindow.FindControl<ComboBox>("VpRepeatModeCombo");
        var lockRangeCheck = dialogWindow.FindControl<CheckBox>("VpLockRangeCheck");
        var rangeBarsSpin = dialogWindow.FindControl<NumericUpDown>("VpRangeBarsSpin");

        if (valueAreaColorPicker != null) valueAreaColorPicker.Color = frvp.ValueAreaColor;
        if (profileColorPicker != null) profileColorPicker.Color = frvp.ProfileColor;
        if (opacitySpin != null) opacitySpin.Value = (decimal)(frvp.Opacity * 100);
        if (fillColorPicker != null) fillColorPicker.Color = frvp.FillColor;
        if (fillOpacitySpin != null) fillOpacitySpin.Value = frvp.FillOpacity;
        if (repeatModeCombo != null)
        {
            repeatModeCombo.SelectedIndex = frvp.RepeatMode switch
            {
                VolumeProfileRepeatMode.Default => 0,
                VolumeProfileRepeatMode.RangeBar => 1,
                VolumeProfileRepeatMode.Weekly => 2,
                VolumeProfileRepeatMode.Monthly => 3,
                _ => 0
            };
        }
        if (lockRangeCheck != null) lockRangeCheck.IsChecked = frvp.LockRange;
        if (rangeBarsSpin != null && frvp.RangeBars > 0) rangeBarsSpin.Value = frvp.RangeBars;

        UpdateRangeBarsState(repeatModeCombo, rangeBarsSpin, lockRangeCheck);
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not FixedRangeVolumeProfileObject frvp) return;
        var valueAreaColorPicker = dialogWindow.FindControl<ColorPicker>("ValueAreaColorPicker");
        var profileColorPicker = dialogWindow.FindControl<ColorPicker>("ProfileColorPicker");
        var opacitySpin = dialogWindow.FindControl<NumericUpDown>("OpacitySpin");
        var fillColorPicker = dialogWindow.FindControl<ColorPicker>("VpFillColorPicker");
        var fillOpacitySpin = dialogWindow.FindControl<NumericUpDown>("VpFillOpacitySpin");
        var repeatModeCombo = dialogWindow.FindControl<ComboBox>("VpRepeatModeCombo");
        var lockRangeCheck = dialogWindow.FindControl<CheckBox>("VpLockRangeCheck");
        var rangeBarsSpin = dialogWindow.FindControl<NumericUpDown>("VpRangeBarsSpin");

        if (valueAreaColorPicker != null) frvp.ValueAreaColor = valueAreaColorPicker.Color;
        if (profileColorPicker != null) frvp.ProfileColor = profileColorPicker.Color;
        if (opacitySpin?.Value != null) frvp.Opacity = (double)opacitySpin.Value / 100.0;
        if (fillColorPicker != null) frvp.FillColor = fillColorPicker.Color;
        if (fillOpacitySpin?.Value != null) frvp.FillOpacity = (int)fillOpacitySpin.Value;
        if (repeatModeCombo != null && repeatModeCombo.SelectedIndex >= 0)
        {
            frvp.RepeatMode = repeatModeCombo.SelectedIndex switch
            {
                1 => VolumeProfileRepeatMode.RangeBar,
                2 => VolumeProfileRepeatMode.Weekly,
                3 => VolumeProfileRepeatMode.Monthly,
                _ => VolumeProfileRepeatMode.Default
            };
        }
        if (lockRangeCheck?.IsChecked != null) frvp.LockRange = lockRangeCheck.IsChecked.Value;
        if (rangeBarsSpin?.Value != null && (int)rangeBarsSpin.Value != frvp.RangeBars)
        {
            frvp.RangeBars = (int)rangeBarsSpin.Value;
        }
    }

    private static void UpdateRangeBarsState(ComboBox? repeatModeCombo, NumericUpDown? rangeBarsSpin, CheckBox? lockRangeCheck)
    {
        if (repeatModeCombo == null) return;
        // RangeBars and LockRange apply to Default (0) and RangeBar (1) modes, but not to calendar-based Weekly (2) / Monthly (3)
        bool isRangeBarsApplicable = repeatModeCombo.SelectedIndex == 0 || repeatModeCombo.SelectedIndex == 1;
        if (rangeBarsSpin != null) rangeBarsSpin.IsEnabled = isRangeBarsApplicable;
        if (lockRangeCheck != null) lockRangeCheck.IsEnabled = isRangeBarsApplicable;
    }
}
