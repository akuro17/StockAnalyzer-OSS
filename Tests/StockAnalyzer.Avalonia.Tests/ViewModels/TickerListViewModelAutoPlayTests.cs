using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.ViewModels.TickerList;
using StockAnalyzer.Avalonia.ViewModels.Watchlist;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>sa_implement (Ticker Auto Play, Y:\Temp\sa_implementation_plan_TickerAutoPlay.md Phase 2):
/// TickerListViewModel's Auto Play integration - checkbox state, visible-order source, refused start, list
/// transition. The timing itself is covered by TickerAutoPlayControllerTests; the production timer is
/// never awaited here (every test disposes the ViewModel).</summary>
[Collection("MessengerSharedState")]
public class TickerListViewModelAutoPlayTests
{
    private static TickerListViewModel Create(FakeTickersSettingsManager? manager, IToastNotificationService? toast = null)
    {
        var watchlists = new Mock<IWatchlistManager>();
        watchlists.Setup(w => w.GetAllProfiles()).Returns(new List<StockAnalyzer.Core.Models.Watchlist.WatchlistProfile>());
        return new TickerListViewModel(
            marketDataProvider: new Mock<IMarketDataProvider>().Object,
            pythonService: null!,
            messenger: WeakReferenceMessenger.Default,
            dispatcherService: new SynchronousDispatcherService(),
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

    private static void AddRows(TickerListViewModel vm, params string[] symbols)
    {
        foreach (var s in symbols) vm.DisplayItems.Add(Row(s));
    }

    [Fact]
    public void ManagerMissing_AutoPlayIsUnavailable_AndCheckingItIsRefused()
    {
        var toast = new ToastNotificationService();
        var vm = Create(null, toast);
        AddRows(vm, "AAA");

        Assert.False(vm.IsAutoPlayAvailable);
        vm.IsAutoPlayEnabled = true;

        Assert.False(vm.IsAutoPlayEnabled);
        // The cause is the unavailable settings, not an empty list: the toast must say so.
        var expected = LocalizationManager.Instance["TickerList_AutoPlay_Unavailable"];
        Assert.Equal(expected, toast.NotificationMessage);
        Assert.False(expected.StartsWith("[", StringComparison.Ordinal), "The key resolved to the missing-key sentinel.");
        Assert.NotEqual(LocalizationManager.Instance["TickerList_AutoPlay_EmptyList"], toast.NotificationMessage);
        vm.Dispose();
    }

    [Fact]
    public void CheckingWithAnEmptyList_UnchecksAndShowsAToastToTheUser()
    {
        // Regression (sa_minimal_fix): the reason was written to StatusMessage, which no view of the Tickers tab
        // displays, so the box unchecked itself without any visible explanation. It now uses the toast overlay.
        var toast = new ToastNotificationService();
        var vm = Create(new FakeTickersSettingsManager(), toast);

        vm.IsAutoPlayEnabled = true;

        Assert.False(vm.IsAutoPlayEnabled);
        Assert.Same(toast, vm.ToastService);
        Assert.True(toast.IsNotificationVisible);
        var expected = LocalizationManager.Instance["TickerList_AutoPlay_EmptyList"];
        Assert.Equal(expected, toast.NotificationMessage);
        Assert.False(expected.StartsWith("[", StringComparison.Ordinal), "The key resolved to the missing-key sentinel.");
        vm.Dispose();
    }

    [Fact]
    public void RefusedStart_WithoutAToastService_IsStillNeverSilent()
    {
        // No toast host (design time / tests): the ViewModel must not create one itself and must not swallow the reason.
        var vm = Create(new FakeTickersSettingsManager());

        vm.IsAutoPlayEnabled = true;

        Assert.Null(vm.ToastService);
        Assert.False(vm.IsAutoPlayEnabled);
        Assert.Equal(LocalizationManager.Instance["TickerList_AutoPlay_EmptyList"], vm.StatusMessage);
        vm.Dispose();
    }

    [Fact]
    public void CheckingWithNoSelection_SelectsTheFirstTickerAtOnce()
    {
        var vm = Create(new FakeTickersSettingsManager());
        AddRows(vm, "AAA", "BBB");

        vm.IsAutoPlayEnabled = true;

        Assert.True(vm.IsAutoPlayEnabled);
        Assert.Equal("AAA", vm.SelectedTicker);
        Assert.Equal("AAA", vm.SelectedItem?.Symbol);
        vm.Dispose();
    }

    [Fact]
    public void CheckingWithASelectionInTheList_KeepsIt()
    {
        var vm = Create(new FakeTickersSettingsManager());
        AddRows(vm, "AAA", "BBB");
        vm.SelectedItem = vm.DisplayItems[1];

        vm.IsAutoPlayEnabled = true;

        Assert.Equal("BBB", vm.SelectedTicker);
        vm.Dispose();
    }

    [Fact]
    public void VisibleOrderProvider_IsUsedWhenItListsExactlyTheDisplayedTickers()
    {
        var vm = Create(new FakeTickersSettingsManager());
        AddRows(vm, "AAA", "BBB", "CCC");
        vm.VisibleOrderProvider = () => new List<WatchlistItemViewModel> { vm.DisplayItems[2], vm.DisplayItems[0], vm.DisplayItems[1] };

        vm.IsAutoPlayEnabled = true;

        Assert.Equal("CCC", vm.SelectedTicker); // first row of the visible (sorted) order, not of DisplayItems
        vm.Dispose();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("shorter")]
    [InlineData("different")]
    [InlineData("duplicate")]
    public void VisibleOrderProvider_FallsBackToDisplayItemsOrder_WhenInconsistent(string kind)
    {
        var vm = Create(new FakeTickersSettingsManager());
        AddRows(vm, "AAA", "BBB", "CCC");
        vm.VisibleOrderProvider = () => kind switch
        {
            "null" => null,
            "shorter" => new List<WatchlistItemViewModel> { vm.DisplayItems[2], vm.DisplayItems[1] },
            "different" => new List<WatchlistItemViewModel> { Row("ZZZ"), vm.DisplayItems[1], vm.DisplayItems[2] },
            _ => new List<WatchlistItemViewModel> { vm.DisplayItems[2], vm.DisplayItems[2], vm.DisplayItems[1] }
        };

        vm.IsAutoPlayEnabled = true;

        Assert.Equal("AAA", vm.SelectedTicker);
        vm.Dispose();
    }

    [Fact]
    public void UncheckingStopsAutoPlay()
    {
        var vm = Create(new FakeTickersSettingsManager());
        AddRows(vm, "AAA");
        vm.IsAutoPlayEnabled = true;

        vm.IsAutoPlayEnabled = false;

        Assert.False(vm.IsAutoPlayEnabled);
        vm.Dispose();
    }

    private static StockAnalyzer.Avalonia.ViewModels.TickerList.TickerGroupNode AnotherList(TickerListViewModel vm) =>
        vm.Groups.First(n => n is not ActionNode && n.Id != vm.SelectedNode!.Id);

    [Fact]
    public void ListTransition_StopsAutoPlay_WhenStopOnListChangeIsOn()
    {
        var manager = new FakeTickersSettingsManager();
        manager.SetAutoPlayStopOnListChange(true);
        var vm = Create(manager);
        AddRows(vm, "AAA");
        vm.IsAutoPlayEnabled = true;

        vm.SelectedNode = AnotherList(vm);

        Assert.False(vm.IsAutoPlayEnabled);
        vm.Dispose();
    }

    [Fact]
    public void ListTransition_KeepsAutoPlay_WhenStopOnListChangeIsOff()
    {
        var manager = new FakeTickersSettingsManager();
        manager.SetAutoPlayStopOnListChange(false);
        var vm = Create(manager);
        AddRows(vm, "AAA");
        vm.IsAutoPlayEnabled = true;

        vm.SelectedNode = AnotherList(vm);

        Assert.True(vm.IsAutoPlayEnabled);
        vm.Dispose();
    }

    [Fact]
    public void Dispose_StopsAutoPlay()
    {
        var vm = Create(new FakeTickersSettingsManager());
        AddRows(vm, "AAA");
        vm.IsAutoPlayEnabled = true;

        vm.Dispose();

        Assert.False(vm.IsAutoPlayEnabled);
    }
}
