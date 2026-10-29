using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Screener;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views;

/// <summary>
/// The time frame of the Screener window: the tab has no frame choice of its own (each condition side picks its frame in the Indicators tab), and a "complete bars only"
/// check box that is shown only while an enabled condition reads a weekly or monthly bar list.
/// </summary>
[Collection("LocalizationSharedState")]
public sealed class ScreenerWindowTimeFrameTests
{
    private static (ScreenerWindow Window, ScreenerViewModel Vm, CheckBox Check) Open()
    {
        var vm = new ScreenerViewModel();
        var window = new ScreenerWindow { DataContext = vm };
        window.Show();
        var check = window.FindControl<CheckBox>("CompleteBarsOnlyCheckBox") ?? throw new InvalidOperationException("The complete-bars check box is missing.");
        return (window, vm, check);
    }

    private static ScreenerIndicatorEntry Entry(TimeFrame frame) => new()
    {
        LeftHand = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.Price, OutputName = "Close", TimeFrame = frame },
    };

    [AvaloniaFact]
    public void TheSourcePanelHasNoFrameChoiceAndTheCompleteBarsChoiceStartsHidden()
    {
        (ScreenerWindow window, _, CheckBox check) = Open();
        try
        {
            Assert.Null(window.FindControl<ComboBox>("ScanTimeFrameComboBox"));
            Assert.False(check.IsVisible);
            Assert.True(check.IsChecked);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void TheCompleteBarsChoiceAppearsWithAnEnabledWeeklyConditionAndGoesWithIt()
    {
        (ScreenerWindow window, ScreenerViewModel vm, CheckBox check) = Open();
        try
        {
            var entries = vm.IndicatorRegistrationViewModel.RegisteredEntries;
            ScreenerIndicatorEntry daily = Entry(TimeFrame.D1);
            ScreenerIndicatorEntry weekly = Entry(TimeFrame.W1);

            entries.Add(daily);
            Assert.False(check.IsVisible);

            entries.Add(weekly);
            Assert.True(check.IsVisible);

            weekly.IsEnabled = false;
            Assert.False(check.IsVisible);

            weekly.IsEnabled = true;
            entries.Remove(weekly);
            Assert.False(check.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void TheCheckBoxWritesTheChoiceToTheViewModel()
    {
        (ScreenerWindow window, ScreenerViewModel vm, CheckBox check) = Open();
        try
        {
            vm.IndicatorRegistrationViewModel.RegisteredEntries.Add(Entry(TimeFrame.MN1));
            Assert.True(check.IsVisible);

            check.IsChecked = false;

            Assert.False(vm.CompleteBarsOnly);
        }
        finally { window.Close(); }
    }
}
