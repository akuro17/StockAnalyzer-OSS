using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Views.Dialogs;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Services.Backtest.Configuration;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views;

public class BacktestSettingsViewTests
{
    [AvaloniaFact]
    public void ViewLocator_ResolvesTheBacktestSettingsView()
    {
        var vm = new BacktestSettingsViewModel();

        Control? built = new ViewLocator().Build(vm);

        Assert.IsType<BacktestSettingsView>(built);
    }

    [AvaloniaFact]
    public void View_RendersEveryDefaultItemAndHidesValidationMessageWhenValid()
    {
        string path = Path.Combine(Path.GetTempPath(), $"backtest-settings-view-{Guid.NewGuid():N}.json");
        var vm = new BacktestSettingsViewModel(new BacktestDefaultSettingsManager(path));
        var view = new BacktestSettingsView { DataContext = vm };
        var window = new Window { Content = view, Width = 900, Height = 900 };

        try
        {
            window.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            // 14 numeric items in groups B-D (incl. 3 annualization periods) minus none: 11 single + 3 periods.
            Assert.Equal(15, view.GetVisualDescendants().OfType<NumericUpDown>().Count());
            // The sizing-model selector plus the Group E equity color mode selector.
            Assert.Equal(2, view.GetVisualDescendants().OfType<ComboBox>().Count());

            TextBlock Error(string key) => view.GetVisualDescendants().OfType<TextBlock>()
                .Single(t => t.GetValue(global::Avalonia.Automation.AutomationProperties.AutomationIdProperty) == $"BacktestSettings_Error_{key}");

            TextBlock margin = Error("MaintenanceMarginRatio");
            TextBlock bootstrap = Error("BootstrapIterations");
            Assert.False(margin.IsVisible);
            Assert.False(bootstrap.IsVisible);

            vm.BootstrapIterations = 10;
            Dispatcher.UIThread.RunJobs();
            Assert.True(bootstrap.IsVisible);
            Assert.False(margin.IsVisible);
            Assert.NotEmpty(bootstrap.Text!);

            vm.MaintenanceMarginRatio = vm.InitialMarginRatio;
            Dispatcher.UIThread.RunJobs();
            Assert.True(margin.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    private static string AutomationId(Control control) =>
        control.GetValue(global::Avalonia.Automation.AutomationProperties.AutomationIdProperty) ?? string.Empty;

    [AvaloniaFact]
    public void GroupE_ShowsTheModeSelector_AndOnlyTheColorsTheModeUses()
    {
        string path = Path.Combine(Path.GetTempPath(), $"backtest-settings-view-{Guid.NewGuid():N}.json");
        var vm = new BacktestSettingsViewModel(new BacktestDefaultSettingsManager(path), new FakeChartSettingsManager());
        var view = new BacktestSettingsView { DataContext = vm };
        var window = new Window { Content = view, Width = 900, Height = 1400 };

        try
        {
            window.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            Assert.Single(view.GetVisualDescendants().OfType<ComboBox>(), c => AutomationId(c) == "BacktestSettings_EquityColorMode");

            string[] VisibleColorPickers()
            {
                Dispatcher.UIThread.RunJobs();
                return view.GetVisualDescendants().OfType<ColorPicker>()
                    .Where(p => p.IsEffectivelyVisible)
                    .Select(AutomationId)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToArray();
            }

            // Default mode is Drawdown: the line color plus the high / drawdown pair.
            Assert.Equal(
                new[] { "BacktestSettings_EquityDownColor", "BacktestSettings_EquityLineColor", "BacktestSettings_EquityUpColor" },
                VisibleColorPickers());

            vm.EquityColorMode = BacktestEquityColorMode.Single;
            Assert.Equal(new[] { "BacktestSettings_EquityLineColor" }, VisibleColorPickers());

            vm.EquityColorMode = BacktestEquityColorMode.PreviousBar;
            Assert.Equal(
                new[] { "BacktestSettings_EquityDownColor", "BacktestSettings_EquityLineColor", "BacktestSettings_EquityUpColor" },
                VisibleColorPickers());
        }
        finally
        {
            window.Close();
        }
    }
}
