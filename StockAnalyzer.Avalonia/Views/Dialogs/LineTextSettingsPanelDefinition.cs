using Avalonia;
using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

/// <summary>
/// Settings-dialog behavior for LineText/CurveLineText (<see cref="ILineTextAnnotatedObject"/>),
/// resolved via <see cref="IDrawingSettingsPanelRegistry"/> instead of a hardcoded branch inside
/// DrawingSettingsDialog. The dialog's shared "Common"/"Top"/"Bottom" panel markup
/// (LineTextPanel and its children) still lives in DrawingSettingsDialog.axaml — only the
/// tool-specific wiring logic that was previously duplicated across the dialog's constructor and
/// OnOkClick moved here.
/// </summary>
public sealed class LineTextSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => new()
    {
        Width = 720,
        // The dialog's default MinWidth (360, shared by every other tool) is far below what this
        // 3-column layout needs; without raising it, the user could shrink this dialog down to a
        // width where labels/controls overlap.
        MinWidth = 650,
        CanResize = true
    };

    public bool CanHandle(IChartObject drawing) => drawing is ILineTextAnnotatedObject;

    public bool ManagesGenericColorPickerWidth => true;

    public void Activate(Window dialogWindow)
    {
        var ltPanel = dialogWindow.FindControl<StackPanel>("LineTextPanel");
        if (ltPanel != null) ltPanel.IsVisible = true;

        // The generic Color/Line Thickness controls are shared (same ColorPickerControl/
        // ThicknessSpin instances and bindings used by every drawing tool). For LineText/
        // CurveLineText only, move those same instances into the panel's "Common" column instead
        // of duplicating them there, so no other tool's dialog is affected.
        var settingsContentPanel = dialogWindow.FindControl<StackPanel>("SettingsContentPanel");
        var genericColorPanel = dialogWindow.FindControl<StackPanel>("GenericColorPanel");
        var thicknessPanel = dialogWindow.FindControl<StackPanel>("ThicknessPanel");
        var commonColumnPanel = dialogWindow.FindControl<StackPanel>("LineTextCommonColumnPanel");
        if (settingsContentPanel != null && genericColorPanel != null && thicknessPanel != null && commonColumnPanel != null)
        {
            settingsContentPanel.Children.Remove(genericColorPanel);
            settingsContentPanel.Children.Remove(thicknessPanel);
            commonColumnPanel.Children.Insert(0, thicknessPanel);
            commonColumnPanel.Children.Insert(0, genericColorPanel);
        }

        // Sync the Common column's ColorPicker (Color) to that column's own width, not the full
        // dialog width, so it cannot overflow past its divider. The Top/Bottom "Font Color"
        // pickers are single-line label+control rows with a fixed Width set in XAML instead,
        // since only the remaining space after the label (not the whole column) is available to
        // them.
        var commonColorPicker = dialogWindow.FindControl<ColorPicker>("ColorPickerControl");
        if (commonColumnPanel != null && commonColorPicker != null)
        {
            commonColumnPanel.PropertyChanged += (s, e) =>
            {
                if (e.Property == Visual.BoundsProperty && commonColumnPanel.Bounds.Width > 0)
                {
                    commonColorPicker.Width = commonColumnPanel.Bounds.Width;
                }
            };
        }
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not ILineTextAnnotatedObject lineTextObj) return;

        var matchBgCheck = dialogWindow.FindControl<CheckBox>("LineTextMatchBackgroundCheck");
        var topTextBox = dialogWindow.FindControl<TextBox>("LineTextTopTextBox");
        var topFontSizeSpin = dialogWindow.FindControl<NumericUpDown>("LineTextTopFontSizeSpin");
        var topAlignmentCombo = dialogWindow.FindControl<ComboBox>("LineTextTopAlignmentCombo");
        var topOffsetSpin = dialogWindow.FindControl<NumericUpDown>("LineTextTopOffsetSpin");
        var topColorPicker = dialogWindow.FindControl<ColorPicker>("LineTextTopColorPicker");
        var topRotationCombo = dialogWindow.FindControl<ComboBox>("LineTextTopRotationCombo");
        var topReverseOrderCheck = dialogWindow.FindControl<CheckBox>("LineTextTopReverseOrderCheck");
        var topOrientationCombo = dialogWindow.FindControl<ComboBox>("LineTextTopOrientationCombo");
        var topPositionFixedCheck = dialogWindow.FindControl<CheckBox>("LineTextTopPositionFixedCheck");
        var topPositionLockedCheck = dialogWindow.FindControl<CheckBox>("LineTextTopPositionLockedCheck");
        var topExtendBeyondLineCheck = dialogWindow.FindControl<CheckBox>("LineTextTopExtendBeyondLineCheck");
        var bottomTextBox = dialogWindow.FindControl<TextBox>("LineTextBottomTextBox");
        var bottomFontSizeSpin = dialogWindow.FindControl<NumericUpDown>("LineTextBottomFontSizeSpin");
        var bottomAlignmentCombo = dialogWindow.FindControl<ComboBox>("LineTextBottomAlignmentCombo");
        var bottomOffsetSpin = dialogWindow.FindControl<NumericUpDown>("LineTextBottomOffsetSpin");
        var bottomColorPicker = dialogWindow.FindControl<ColorPicker>("LineTextBottomColorPicker");
        var bottomRotationCombo = dialogWindow.FindControl<ComboBox>("LineTextBottomRotationCombo");
        var bottomReverseOrderCheck = dialogWindow.FindControl<CheckBox>("LineTextBottomReverseOrderCheck");
        var bottomOrientationCombo = dialogWindow.FindControl<ComboBox>("LineTextBottomOrientationCombo");
        var bottomPositionFixedCheck = dialogWindow.FindControl<CheckBox>("LineTextBottomPositionFixedCheck");
        var bottomPositionLockedCheck = dialogWindow.FindControl<CheckBox>("LineTextBottomPositionLockedCheck");
        var bottomExtendBeyondLineCheck = dialogWindow.FindControl<CheckBox>("LineTextBottomExtendBeyondLineCheck");

        if (matchBgCheck != null) matchBgCheck.IsChecked = lineTextObj.MatchBackgroundColor;

        if (topTextBox != null) topTextBox.Text = lineTextObj.TopText;
        if (topFontSizeSpin != null) topFontSizeSpin.Value = (decimal)lineTextObj.TopFontSize;
        if (topAlignmentCombo != null) topAlignmentCombo.SelectedIndex = (int)lineTextObj.TopAlignment;
        if (topOffsetSpin != null) topOffsetSpin.Value = (decimal)lineTextObj.TopOffsetPx;
        if (topColorPicker != null) topColorPicker.Color = lineTextObj.TopTextColor;
        if (topRotationCombo != null) topRotationCombo.SelectedIndex = (int)lineTextObj.TopRotationMode;
        if (topReverseOrderCheck != null) topReverseOrderCheck.IsChecked = lineTextObj.TopTextReverseOrder;
        if (topOrientationCombo != null) topOrientationCombo.SelectedIndex = (int)lineTextObj.TopTextOrientationOverride;
        if (topPositionFixedCheck != null) topPositionFixedCheck.IsChecked = lineTextObj.TopPositionFixed;
        if (topPositionLockedCheck != null) topPositionLockedCheck.IsChecked = lineTextObj.TopPositionLocked;
        if (topExtendBeyondLineCheck != null) topExtendBeyondLineCheck.IsChecked = lineTextObj.TopExtendBeyondLine;

        if (bottomTextBox != null) bottomTextBox.Text = lineTextObj.BottomText;
        if (bottomFontSizeSpin != null) bottomFontSizeSpin.Value = (decimal)lineTextObj.BottomFontSize;
        if (bottomAlignmentCombo != null) bottomAlignmentCombo.SelectedIndex = (int)lineTextObj.BottomAlignment;
        if (bottomOffsetSpin != null) bottomOffsetSpin.Value = (decimal)lineTextObj.BottomOffsetPx;
        if (bottomColorPicker != null) bottomColorPicker.Color = lineTextObj.BottomTextColor;
        if (bottomRotationCombo != null) bottomRotationCombo.SelectedIndex = (int)lineTextObj.BottomRotationMode;
        if (bottomReverseOrderCheck != null) bottomReverseOrderCheck.IsChecked = lineTextObj.BottomTextReverseOrder;
        if (bottomOrientationCombo != null) bottomOrientationCombo.SelectedIndex = (int)lineTextObj.BottomTextOrientationOverride;
        if (bottomPositionFixedCheck != null) bottomPositionFixedCheck.IsChecked = lineTextObj.BottomPositionFixed;
        if (bottomPositionLockedCheck != null) bottomPositionLockedCheck.IsChecked = lineTextObj.BottomPositionLocked;
        if (bottomExtendBeyondLineCheck != null) bottomExtendBeyondLineCheck.IsChecked = lineTextObj.BottomExtendBeyondLine;
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not ILineTextAnnotatedObject lineTextObj) return;

        var matchBgCheck = dialogWindow.FindControl<CheckBox>("LineTextMatchBackgroundCheck");
        var topTextBox = dialogWindow.FindControl<TextBox>("LineTextTopTextBox");
        var topFontSizeSpin = dialogWindow.FindControl<NumericUpDown>("LineTextTopFontSizeSpin");
        var topAlignmentCombo = dialogWindow.FindControl<ComboBox>("LineTextTopAlignmentCombo");
        var topOffsetSpin = dialogWindow.FindControl<NumericUpDown>("LineTextTopOffsetSpin");
        var topColorPicker = dialogWindow.FindControl<ColorPicker>("LineTextTopColorPicker");
        var topRotationCombo = dialogWindow.FindControl<ComboBox>("LineTextTopRotationCombo");
        var topReverseOrderCheck = dialogWindow.FindControl<CheckBox>("LineTextTopReverseOrderCheck");
        var topOrientationCombo = dialogWindow.FindControl<ComboBox>("LineTextTopOrientationCombo");
        var topPositionFixedCheck = dialogWindow.FindControl<CheckBox>("LineTextTopPositionFixedCheck");
        var topPositionLockedCheck = dialogWindow.FindControl<CheckBox>("LineTextTopPositionLockedCheck");
        var topExtendBeyondLineCheck = dialogWindow.FindControl<CheckBox>("LineTextTopExtendBeyondLineCheck");
        var bottomTextBox = dialogWindow.FindControl<TextBox>("LineTextBottomTextBox");
        var bottomFontSizeSpin = dialogWindow.FindControl<NumericUpDown>("LineTextBottomFontSizeSpin");
        var bottomAlignmentCombo = dialogWindow.FindControl<ComboBox>("LineTextBottomAlignmentCombo");
        var bottomOffsetSpin = dialogWindow.FindControl<NumericUpDown>("LineTextBottomOffsetSpin");
        var bottomColorPicker = dialogWindow.FindControl<ColorPicker>("LineTextBottomColorPicker");
        var bottomRotationCombo = dialogWindow.FindControl<ComboBox>("LineTextBottomRotationCombo");
        var bottomReverseOrderCheck = dialogWindow.FindControl<CheckBox>("LineTextBottomReverseOrderCheck");
        var bottomOrientationCombo = dialogWindow.FindControl<ComboBox>("LineTextBottomOrientationCombo");
        var bottomPositionFixedCheck = dialogWindow.FindControl<CheckBox>("LineTextBottomPositionFixedCheck");
        var bottomPositionLockedCheck = dialogWindow.FindControl<CheckBox>("LineTextBottomPositionLockedCheck");
        var bottomExtendBeyondLineCheck = dialogWindow.FindControl<CheckBox>("LineTextBottomExtendBeyondLineCheck");

        if (matchBgCheck?.IsChecked != null) lineTextObj.MatchBackgroundColor = matchBgCheck.IsChecked.Value;

        if (topTextBox != null) lineTextObj.TopText = topTextBox.Text ?? "";
        if (topFontSizeSpin?.Value != null) lineTextObj.TopFontSize = (double)topFontSizeSpin.Value;
        if (topAlignmentCombo != null && topAlignmentCombo.SelectedIndex >= 0) lineTextObj.TopAlignment = (TextHorizontalAlignment)topAlignmentCombo.SelectedIndex;
        if (topOffsetSpin?.Value != null) lineTextObj.TopOffsetPx = (float)topOffsetSpin.Value;
        if (topColorPicker != null) lineTextObj.TopTextColor = topColorPicker.Color;
        if (topRotationCombo != null && topRotationCombo.SelectedIndex >= 0) lineTextObj.TopRotationMode = (TextRotationMode)topRotationCombo.SelectedIndex;
        if (topReverseOrderCheck?.IsChecked != null) lineTextObj.TopTextReverseOrder = topReverseOrderCheck.IsChecked.Value;
        if (topOrientationCombo != null && topOrientationCombo.SelectedIndex >= 0) lineTextObj.TopTextOrientationOverride = (TextManualOrientation)topOrientationCombo.SelectedIndex;
        if (topPositionFixedCheck?.IsChecked != null) lineTextObj.TopPositionFixed = topPositionFixedCheck.IsChecked.Value;
        if (topPositionLockedCheck?.IsChecked != null) lineTextObj.TopPositionLocked = topPositionLockedCheck.IsChecked.Value;
        if (topExtendBeyondLineCheck?.IsChecked != null) lineTextObj.TopExtendBeyondLine = topExtendBeyondLineCheck.IsChecked.Value;

        if (bottomTextBox != null) lineTextObj.BottomText = bottomTextBox.Text ?? "";
        if (bottomFontSizeSpin?.Value != null) lineTextObj.BottomFontSize = (double)bottomFontSizeSpin.Value;
        if (bottomAlignmentCombo != null && bottomAlignmentCombo.SelectedIndex >= 0) lineTextObj.BottomAlignment = (TextHorizontalAlignment)bottomAlignmentCombo.SelectedIndex;
        if (bottomOffsetSpin?.Value != null) lineTextObj.BottomOffsetPx = (float)bottomOffsetSpin.Value;
        if (bottomColorPicker != null) lineTextObj.BottomTextColor = bottomColorPicker.Color;
        if (bottomRotationCombo != null && bottomRotationCombo.SelectedIndex >= 0) lineTextObj.BottomRotationMode = (TextRotationMode)bottomRotationCombo.SelectedIndex;
        if (bottomReverseOrderCheck?.IsChecked != null) lineTextObj.BottomTextReverseOrder = bottomReverseOrderCheck.IsChecked.Value;
        if (bottomOrientationCombo != null && bottomOrientationCombo.SelectedIndex >= 0) lineTextObj.BottomTextOrientationOverride = (TextManualOrientation)bottomOrientationCombo.SelectedIndex;
        if (bottomPositionFixedCheck?.IsChecked != null) lineTextObj.BottomPositionFixed = bottomPositionFixedCheck.IsChecked.Value;
        if (bottomPositionLockedCheck?.IsChecked != null) lineTextObj.BottomPositionLocked = bottomPositionLockedCheck.IsChecked.Value;
        if (bottomExtendBeyondLineCheck?.IsChecked != null) lineTextObj.BottomExtendBeyondLine = bottomExtendBeyondLineCheck.IsChecked.Value;
    }
}
