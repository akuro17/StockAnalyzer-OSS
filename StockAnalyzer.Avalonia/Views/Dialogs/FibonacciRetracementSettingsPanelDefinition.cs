using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public sealed class FibonacciRetracementSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is FibonacciRetracementObject;

    public void Activate(Window dialogWindow)
    {
        var fibPanel = dialogWindow.FindControl<StackPanel>("FibonacciRetracementPanel");
        if (fibPanel != null) fibPanel.IsVisible = true;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not FibonacciRetracementObject fib) return;

        var showExtCheck = dialogWindow.FindControl<CheckBox>("FibRetracementShowExtensionsCheck");
        if (showExtCheck != null)
        {
            showExtCheck.IsChecked = fib.ShowExtensions;
        }

        var extendCombo = dialogWindow.FindControl<ComboBox>("FibRetracementExtendModeCombo");
        if (extendCombo != null)
        {
            extendCombo.SelectedIndex = (int)fib.ExtendMode;
        }
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not FibonacciRetracementObject fib) return;

        var showExtCheck = dialogWindow.FindControl<CheckBox>("FibRetracementShowExtensionsCheck");
        if (showExtCheck?.IsChecked != null)
        {
            fib.ShowExtensions = showExtCheck.IsChecked.Value;
        }

        var extendCombo = dialogWindow.FindControl<ComboBox>("FibRetracementExtendModeCombo");
        if (extendCombo != null && extendCombo.SelectedIndex >= 0)
        {
            fib.ExtendMode = (FibonacciRetracementExtendMode)extendCombo.SelectedIndex;
        }
    }
}
