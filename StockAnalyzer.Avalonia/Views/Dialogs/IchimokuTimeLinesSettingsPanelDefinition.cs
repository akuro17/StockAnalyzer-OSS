using System;
using System.Linq;
using Avalonia.Controls;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public sealed class IchimokuTimeLinesSettingsPanelDefinition : IDrawingSettingsPanelDefinition
{
    public DrawingSettingsWindowHint? WindowHint => null;

    public bool CanHandle(IChartObject drawing) => drawing is IchimokuTimeLinesObject;

    public void Activate(Window dialogWindow)
    {
        var panel = dialogWindow.FindControl<StackPanel>("IchimokuTimeLinesPanel");
        if (panel != null) panel.IsVisible = true;
    }

    public void Populate(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not IchimokuTimeLinesObject ichimoku) return;

        var check = dialogWindow.FindControl<CheckBox>("IchimokuShowLabelsCheck");
        if (check != null)
        {
            check.IsChecked = ichimoku.ShowLabels;
        }
    }

    public void Commit(Window dialogWindow, IChartObject drawing)
    {
        if (drawing is not IchimokuTimeLinesObject ichimoku) return;

        var check = dialogWindow.FindControl<CheckBox>("IchimokuShowLabelsCheck");
        if (check?.IsChecked != null)
        {
            ichimoku.ShowLabels = check.IsChecked.Value;
        }
    }
}
