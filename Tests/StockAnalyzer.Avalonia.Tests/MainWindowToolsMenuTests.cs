using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
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

    [AvaloniaFact]
    public void ToolsMenu_GroupsTheModelWindowsIntoModelTrainingAndModelExecution_InTheOwnerOrder()
    {
        LocalizationManager.Instance.Initialize("en");
        var window = new MainWindow();
        var tools = window.GetLogicalDescendants().OfType<MenuItem>().Single(m => AutomationProperties.GetAutomationId(m) == "Menu_Tools");
        string Id(object item) => AutomationProperties.GetAutomationId((MenuItem)item);
        var groups = tools.Items.Cast<object>().OfType<MenuItem>().Where(m => Id(m) is not null).ToDictionary(Id);

        Assert.Equal(new[] { "Menu_TrainingWizard", RlUiConstants.Menu, PrlUiConstants.Menu },
            groups[ToolsMenuUiConstants.ModelTraining].Items.Cast<object>().Select(Id));
        Assert.Equal(new[] { PredictionModelsUiConstants.Menu, RlUiConstants.PlaybackMenu },
            groups[ToolsMenuUiConstants.ModelExecution].Items.Cast<object>().Select(Id));
        Assert.All(new[] { "Menu_TrainingWizard", RlUiConstants.Menu, PrlUiConstants.Menu, PredictionModelsUiConstants.Menu, RlUiConstants.PlaybackMenu },
            id => Assert.DoesNotContain(tools.Items.Cast<object>().OfType<MenuItem>(), m => Id(m) == id));   // no longer direct children of Tools
        Assert.Equal("Model Training", LocalizationManager.Instance.Get("Menu_ModelTraining"));
        Assert.Equal("Reinforcement Learning Playback", LocalizationManager.Instance.Get("Settings_RlPlayback"));
    }
}
