using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Views.Dialogs;
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
            Assert.Single(view.GetVisualDescendants().OfType<ComboBox>());

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
}
