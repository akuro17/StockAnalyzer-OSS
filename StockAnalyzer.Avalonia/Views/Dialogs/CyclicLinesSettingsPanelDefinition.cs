using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public sealed class CyclicLinesSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is CyclicLinesObject;

    public void Activate(Window dialogWindow)
    {
        var panel = dialogWindow.FindControl<StackPanel>("CyclicLinesPanel");
        if (panel != null) panel.IsVisible = true;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not CyclicLinesObject cyclic) return;

        var check = dialogWindow.FindControl<CheckBox>("CyclicLinesCustomRangeCheck");
        if (check != null)
        {
            check.IsChecked = cyclic.IsCustomRangeEnabled;
        }

        var textBox = dialogWindow.FindControl<TextBox>("CyclicLinesCustomRangeTextBox");
        if (textBox != null)
        {
            textBox.Text = cyclic.CustomRangeText;
        }

        var combo = dialogWindow.FindControl<ComboBox>("CyclicLinesCustomModeCombo");
        if (combo != null)
        {
            combo.SelectedIndex = (int)cyclic.CustomMode;
        }
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not CyclicLinesObject cyclic) return;

        var check = dialogWindow.FindControl<CheckBox>("CyclicLinesCustomRangeCheck");
        if (check?.IsChecked != null)
        {
            cyclic.IsCustomRangeEnabled = check.IsChecked.Value;
        }

        var textBox = dialogWindow.FindControl<TextBox>("CyclicLinesCustomRangeTextBox");
        if (textBox?.Text != null)
        {
            cyclic.CustomRangeText = textBox.Text;
        }

        var combo = dialogWindow.FindControl<ComboBox>("CyclicLinesCustomModeCombo");
        if (combo != null && combo.SelectedIndex >= 0)
        {
            cyclic.CustomMode = (CyclicLinesCustomMode)combo.SelectedIndex;
        }
    }
}
