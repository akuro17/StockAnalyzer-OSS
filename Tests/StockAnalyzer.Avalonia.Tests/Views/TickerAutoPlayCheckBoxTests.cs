using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.ViewModels.Watchlist;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views;

/// <summary>sa_implement (Ticker Auto Play, Y:\Temp\sa_implementation_plan_TickerAutoPlay.md Phase 3): the
/// checkbox in the Tickers tab filter row and the View's visible-order provider (decision D1).</summary>
[Collection("MessengerSharedState")]
public class TickerAutoPlayCheckBoxTests
{
    private static void Pump()
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static TickerListViewModel CreateViewModel(FakeTickersSettingsManager? manager, IDispatcherService? dispatcher = null, IToastNotificationService? toast = null)
    {
        var watchlists = new Mock<IWatchlistManager>();
        watchlists.Setup(w => w.GetAllProfiles()).Returns(new List<StockAnalyzer.Core.Models.Watchlist.WatchlistProfile>());
        return new TickerListViewModel(
            marketDataProvider: new Mock<IMarketDataProvider>().Object,
            pythonService: null!,
            messenger: WeakReferenceMessenger.Default,
            dispatcherService: dispatcher ?? new SynchronousDispatcherService(),
            watchlistManager: watchlists.Object,
            portfolioManager: null!,
            dialogService: null!,
            tickerImportService: null!,
            chartSettingsManager: new MockChartSettingsManager(),
            logger: NullLogger<TickerListViewModel>.Instance,
            tickersSettingsManager: manager,
            toastService: toast);
    }

    private static WatchlistItemViewModel Row(string symbol) =>
        new(symbol, symbol + " Inc.", sector: "", industry: "", open: 1m, high: 1m, low: 1m, close: 1m, volume: 1L, changePercent: 0d);

    [AvaloniaFact]
    public void CheckBox_SitsRightAfterTheColumnCustomizationComboBox_AndBindsTwoWay()
    {
        var vm = CreateViewModel(new FakeTickersSettingsManager());
        var view = new StockAnalyzer.Avalonia.Views.TickerListView { DataContext = vm };
        var window = new Window { Content = view, Width = 1100, Height = 600 };

        try
        {
            window.Show();
            Pump();

            var combo = view.FindControl<ComboBox>("ColumnTemplateComboBox")!;
            var check = view.FindControl<CheckBox>("AutoPlayCheckBox")!;
            Assert.NotNull(check);
            var panel = Assert.IsType<StackPanel>(combo.Parent);
            Assert.Same(panel, check.Parent);
            Assert.Equal(panel.Children.IndexOf(combo) + 1, panel.Children.IndexOf(check));
            Assert.True(check.IsEnabled);

            vm.DisplayItems.Add(Row("AAA"));
            check.IsChecked = true;
            Pump();
            Assert.True(vm.IsAutoPlayEnabled);

            vm.IsAutoPlayEnabled = false;
            Pump();
            Assert.False(check.IsChecked);
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public void RefusedStart_ShowsTheToastOverlayInTheTickersTab()
    {
        // Regression (sa_minimal_fix): the refusal reason was not visible anywhere on the tab.
        var vm = CreateViewModel(new FakeTickersSettingsManager(), toast: new ToastNotificationService());
        var view = new StockAnalyzer.Avalonia.Views.TickerListView { DataContext = vm };
        var window = new Window { Content = view, Width = 1100, Height = 600 };

        try
        {
            window.Show();
            Pump();

            view.FindControl<CheckBox>("AutoPlayCheckBox")!.IsChecked = true; // empty list: refused
            Pump();

            var toast = view.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(t => t.Text == StockAnalyzer.Avalonia.Services.LocalizationManager.Instance["TickerList_AutoPlay_EmptyList"]);
            Assert.NotNull(toast);
            Assert.True(toast!.IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public void RefusedStart_UnchecksTheControlItself_NotOnlyTheViewModel()
    {
        // Regression (sa_minimal_fix item 2): the ViewModel reverted IsAutoPlayEnabled from inside the change
        // notification of the control's own TwoWay binding write; the control must visibly end up unchecked.
        // The production dispatcher (not the synchronous test one): the fix depends on the revert being deferred.
        var vm = CreateViewModel(new FakeTickersSettingsManager(), new DispatcherService());
        var view = new StockAnalyzer.Avalonia.Views.TickerListView { DataContext = vm };
        var window = new Window { Content = view, Width = 1100, Height = 600 };

        try
        {
            window.Show();
            Pump();

            var check = view.FindControl<CheckBox>("AutoPlayCheckBox")!;
            check.IsChecked = true; // empty list: refused
            Pump();

            Assert.False(vm.IsAutoPlayEnabled);
            Assert.False(check.IsChecked);
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public void ListTransition_UnchecksTheControl_WhenStopOnListChangeIsOn()
    {
        var manager = new FakeTickersSettingsManager();
        manager.SetAutoPlayStopOnListChange(true);
        var vm = CreateViewModel(manager, new DispatcherService());
        var view = new StockAnalyzer.Avalonia.Views.TickerListView { DataContext = vm };
        var window = new Window { Content = view, Width = 1100, Height = 600 };

        try
        {
            window.Show();
            Pump();
            vm.DisplayItems.Add(Row("AAA"));

            var check = view.FindControl<CheckBox>("AutoPlayCheckBox")!;
            check.IsChecked = true;
            Pump();
            Assert.True(check.IsChecked);

            vm.SelectedNode = vm.Groups.First(n => n is not StockAnalyzer.Avalonia.ViewModels.TickerList.ActionNode && n.Id != vm.SelectedNode!.Id);
            Pump();

            Assert.False(vm.IsAutoPlayEnabled);
            Assert.False(check.IsChecked);
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public void CheckBox_IsDisabled_WhenSettingsAreUnavailable()
    {
        var vm = CreateViewModel(null);
        var view = new StockAnalyzer.Avalonia.Views.TickerListView { DataContext = vm };
        var window = new Window { Content = view, Width = 1100, Height = 600 };

        try
        {
            window.Show();
            Pump();

            Assert.False(view.FindControl<CheckBox>("AutoPlayCheckBox")!.IsEnabled);
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public void OrderProvider_IsInstalledOnAttach_AndClearedOnDetach()
    {
        var vm = CreateViewModel(new FakeTickersSettingsManager());
        var view = new StockAnalyzer.Avalonia.Views.TickerListView { DataContext = vm };
        var window = new Window { Content = view, Width = 1100, Height = 600 };

        try
        {
            window.Show();
            Pump();
            Assert.NotNull(vm.VisibleOrderProvider);

            window.Content = null;
            Pump();
            Assert.Null(vm.VisibleOrderProvider);
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public void OrderProvider_ReturnsTheGridsSortedOrder_NotTheDisplayItemsOrder()
    {
        var vm = CreateViewModel(new FakeTickersSettingsManager());
        var view = new StockAnalyzer.Avalonia.Views.TickerListView { DataContext = vm };
        var window = new Window { Content = view, Width = 1100, Height = 600 };

        try
        {
            window.Show();
            Pump();
            foreach (var s in new[] { "AAA", "BBB", "CCC" }) vm.DisplayItems.Add(Row(s));
            Pump();
            Assert.Equal(new[] { "AAA", "BBB", "CCC" }, vm.VisibleOrderProvider!()!.Select(i => i.Symbol));

            // The user sorts the Symbol column descending: only the grid knows this order.
            vm.ApplySortState("Symbol", 2);
            Pump();

            Assert.Equal(new[] { "CCC", "BBB", "AAA" }, vm.VisibleOrderProvider!()!.Select(i => i.Symbol));
            Assert.Equal(new[] { "AAA", "BBB", "CCC" }, vm.DisplayItems.Select(i => i.Symbol));

            // Auto play therefore starts from the top row of the SORTED grid.
            vm.IsAutoPlayEnabled = true;
            Assert.Equal("CCC", vm.SelectedTicker);
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }
}
