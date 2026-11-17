using System;
using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public sealed class FibonacciRetracementSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    private static readonly FibonacciRetracementExtendMode[] DisplayOrder =
    [
        FibonacciRetracementExtendMode.Default,
        FibonacciRetracementExtendMode.CyclicLines,
        FibonacciRetracementExtendMode.FibTimeZone,
        FibonacciRetracementExtendMode.ExtendRight,
        FibonacciRetracementExtendMode.ExtendLeft,
        FibonacciRetracementExtendMode.ExtendBoth
    ];

    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is FibonacciRetracementObject;

    public void Activate(Window dialogWindow)
    {
        var fibPanel = dialogWindow.FindControl<StackPanel>("FibonacciRetracementPanel");
        if (fibPanel != null) fibPanel.IsVisible = true;

        var extendCombo = dialogWindow.FindControl<ComboBox>("FibRetracementExtendModeCombo");
        if (extendCombo != null)
        {
            extendCombo.ItemsSource = DisplayOrder;
            extendCombo.ItemTemplate = DrawingParameterViewBuilder.LocalizedEnumItemTemplate;
        }
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
            if (extendCombo.ItemsSource == null)
            {
                extendCombo.ItemsSource = DisplayOrder;
                extendCombo.ItemTemplate = DrawingParameterViewBuilder.LocalizedEnumItemTemplate;
            }
            extendCombo.SelectedItem = Enum.IsDefined(typeof(FibonacciRetracementExtendMode), fib.ExtendMode)
                ? fib.ExtendMode
                : FibonacciRetracementExtendMode.Default;
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
        if (extendCombo?.SelectedItem is FibonacciRetracementExtendMode selectedMode &&
            Enum.IsDefined(typeof(FibonacciRetracementExtendMode), selectedMode))
        {
            fib.ExtendMode = selectedMode;
        }
    }
}
