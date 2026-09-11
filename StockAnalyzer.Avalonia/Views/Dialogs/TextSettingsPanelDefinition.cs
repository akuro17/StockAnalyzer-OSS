using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

/// <summary>
/// Settings-dialog behavior shared by TextObject and CalloutObject
/// (<see cref="ITextAnnotatedObject"/>). Both tools used byte-for-byte identical wiring logic
/// inside DrawingSettingsDialog's constructor/OnOkClick; consolidating them here removes that
/// duplication (SSoT) in addition to removing the type-branch itself.
/// </summary>
public sealed class TextSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is ITextAnnotatedObject;

    public void Activate(Window dialogWindow)
    {
        var tPanel = dialogWindow.FindControl<StackPanel>("TextPanel");
        var thicknessPanel = dialogWindow.FindControl<StackPanel>("ThicknessPanel");
        if (tPanel != null) tPanel.IsVisible = true;
        if (thicknessPanel != null) thicknessPanel.IsVisible = false;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not ITextAnnotatedObject textObj) return;

        var contentBox = dialogWindow.FindControl<TextBox>("TextContentBox");
        var fontSizeSpin = dialogWindow.FindControl<NumericUpDown>("TextFontSizeSpin");
        var alignmentCombo = dialogWindow.FindControl<ComboBox>("TextAlignmentCombo");
        var showBgCheck = dialogWindow.FindControl<CheckBox>("TextShowBackgroundCheck");
        var bgPicker = dialogWindow.FindControl<ColorPicker>("TextBackgroundColorPicker");
        var paddingSpin = dialogWindow.FindControl<NumericUpDown>("TextPaddingSpin");
        var cornerRadiusSpin = dialogWindow.FindControl<NumericUpDown>("TextCornerRadiusSpin");

        if (contentBox != null) contentBox.Text = textObj.Text;
        if (fontSizeSpin != null) fontSizeSpin.Value = (decimal)textObj.FontSize;
        if (alignmentCombo != null) alignmentCombo.SelectedIndex = (int)textObj.Alignment;
        if (showBgCheck != null) showBgCheck.IsChecked = textObj.ShowBackgroundBox;
        if (bgPicker != null) bgPicker.Color = textObj.BackgroundColor;
        if (paddingSpin != null) paddingSpin.Value = (decimal)textObj.BackgroundPadding;
        if (cornerRadiusSpin != null) cornerRadiusSpin.Value = (decimal)textObj.CornerRadius;
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not ITextAnnotatedObject textObj) return;

        var contentBox = dialogWindow.FindControl<TextBox>("TextContentBox");
        var fontSizeSpin = dialogWindow.FindControl<NumericUpDown>("TextFontSizeSpin");
        var alignmentCombo = dialogWindow.FindControl<ComboBox>("TextAlignmentCombo");
        var showBgCheck = dialogWindow.FindControl<CheckBox>("TextShowBackgroundCheck");
        var bgPicker = dialogWindow.FindControl<ColorPicker>("TextBackgroundColorPicker");
        var paddingSpin = dialogWindow.FindControl<NumericUpDown>("TextPaddingSpin");
        var cornerRadiusSpin = dialogWindow.FindControl<NumericUpDown>("TextCornerRadiusSpin");

        if (contentBox != null) textObj.Text = contentBox.Text ?? "";
        if (fontSizeSpin?.Value != null) textObj.FontSize = (double)fontSizeSpin.Value;
        if (alignmentCombo != null && alignmentCombo.SelectedIndex >= 0) textObj.Alignment = (TextHorizontalAlignment)alignmentCombo.SelectedIndex;
        if (showBgCheck?.IsChecked != null) textObj.ShowBackgroundBox = showBgCheck.IsChecked.Value;
        if (bgPicker != null) textObj.BackgroundColor = bgPicker.Color;
        if (paddingSpin?.Value != null) textObj.BackgroundPadding = (float)paddingSpin.Value;
        if (cornerRadiusSpin?.Value != null) textObj.CornerRadius = (float)cornerRadiusSpin.Value;
    }
}
