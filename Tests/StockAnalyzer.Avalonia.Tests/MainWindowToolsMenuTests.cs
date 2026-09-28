using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using StockAnalyzer.Avalonia.Views;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests;

/// <summary>
/// Implementation task (ToolsMenuAddTicker): the header Tools menu exposes "Add Ticker" right after "Tab Window"
/// (the action group), above the separator that precedes "Settings".
/// </summary>
public class MainWindowToolsMenuTests
{
    [AvaloniaFact]
    public void ToolsMenu_HasAddTicker_AboveTheSeparatorThatPrecedesSettings()
    {
        var window = new MainWindow();
        var tools = window.GetLogicalDescendants()
            .OfType<MenuItem>()
            .Single(m => AutomationProperties.GetAutomationId(m) == "Menu_Tools");

        var items = tools.Items.Cast<object>().ToList();
        var addTickerIndex = items.FindIndex(i => i is MenuItem m && AutomationProperties.GetAutomationId(m) == "Menu_AddTicker");
        var settingsIndex = items.FindIndex(i => i is MenuItem m && AutomationProperties.GetAutomationId(m) == "Menu_ThemeSettings");

        Assert.True(addTickerIndex >= 0, "Tools menu must contain Add Ticker.");
        Assert.True(settingsIndex > addTickerIndex, "Add Ticker must be above Settings.");
        Assert.IsType<Separator>(items[addTickerIndex + 1]);
        Assert.Equal(addTickerIndex + 2, settingsIndex);
    }
}
