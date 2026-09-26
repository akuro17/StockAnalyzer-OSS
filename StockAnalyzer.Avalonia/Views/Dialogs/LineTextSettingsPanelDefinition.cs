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
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is ILineTextAnnotatedObject;

    public bool ManagesGenericColorPickerWidth => false;

    public void Activate(Window dialogWindow)
    {
        var ltPanel = dialogWindow.FindControl<StackPanel>("LineTextPanel");
        if (ltPanel != null) ltPanel.IsVisible = true;

        var ltCommonPanel = dialogWindow.FindControl<StackPanel>("LineTextCommonPanel");
        if (ltCommonPanel != null) ltCommonPanel.IsVisible = true;

        var genericColorPanel = dialogWindow.FindControl<StackPanel>("GenericColorPanel");
        if (genericColorPanel != null) genericColorPanel.IsVisible = true;

        var thicknessPanel = dialogWindow.FindControl<StackPanel>("ThicknessPanel");
        if (thicknessPanel != null) thicknessPanel.IsVisible = true;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not ILineTextAnnotatedObject lineTextObj) return;

        var transparentCheck = dialogWindow.FindControl<CheckBox>("LineTextTransparentCheck")
            ?? dialogWindow.FindControl<CheckBox>("LineTextMatchBackgroundCheck");
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

        if (transparentCheck != null) transparentCheck.IsChecked = lineTextObj.IsTransparent;

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

        var transparentCheck = dialogWindow.FindControl<CheckBox>("LineTextTransparentCheck")
            ?? dialogWindow.FindControl<CheckBox>("LineTextMatchBackgroundCheck");
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

        if (transparentCheck?.IsChecked != null) lineTextObj.IsTransparent = transparentCheck.IsChecked.Value;

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
