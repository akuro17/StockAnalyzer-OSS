using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.ViewModels.Watchlist;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

// Receives TickerDataRefreshedMessage on the shared static WeakReferenceMessenger.Default using
// symbol names that overlap with EditTickerNotesDialogViewModelTests (see MessengerSharedStateCollection.cs).
[Collection("MessengerSharedState")]
public class TickerListViewModelTests
{
    private static TickerListViewModel CreateViewModel(IMarketDataProvider marketDataProvider)
    {
        var mockWatchlistManager = new Mock<IWatchlistManager>();
        mockWatchlistManager.Setup(w => w.GetAllProfiles()).Returns(new List<StockAnalyzer.Core.Models.Watchlist.WatchlistProfile>());

        return new TickerListViewModel(
            marketDataProvider: marketDataProvider,
            pythonService: null!,
            messenger: WeakReferenceMessenger.Default,
            dispatcherService: new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService(),
            watchlistManager: mockWatchlistManager.Object,
            portfolioManager: null!,
            dialogService: null!,
            tickerImportService: null!,
            chartSettingsManager: new MockChartSettingsManager(),
            logger: NullLogger<TickerListViewModel>.Instance);
    }

    [Fact]
    public void Receive_TickerDataRefreshedMessage_ForSymbolInWatchlist_RefreshesRowFromProvider()
    {
        // Arrange: the provider now returns fresher/truncated data than the row currently shows
        // (simulating a History-tab delete that removed the row's previously-cached latest candle).
        var refreshedCandle = new CandleData(new DateTime(2024, 1, 2), 150m, 155m, 148m, 152m, 5_000_000);
        var mockProvider = new Mock<IMarketDataProvider>();
        mockProvider.Setup(p => p.GetTickersDataAsync("AAPL", TimeFrame.D1))
            .ReturnsAsync(new List<CandleData> { refreshedCandle });

        var vm = CreateViewModel(mockProvider.Object);
        var item = new WatchlistItemViewModel("AAPL", "Apple Inc.", sector: "", industry: "", open: 10m, high: 11m, low: 9m, close: 10.5m, volume: 1000L, changePercent: 0d);
        vm.DisplayItems.Add(item);

        try
        {
            // Act: send exactly as EditTickerNotesDialogViewModel does after a confirmed delete.
            WeakReferenceMessenger.Default.Send(new TickerDataRefreshedMessage("AAPL"));

            // Assert: the watchlist row no longer shows the stale (pre-delete) values.
            Assert.Equal(152m, item.Close);
            Assert.Equal(5_000_000L, item.Volume);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void Receive_TickerDataRefreshedMessage_ProviderReturnsEmpty_ClearsStaleRowValues()
    {
        // Arrange: simulates deleting a ticker's *entire* history from the History tab —
        // the provider now has no rows left for this symbol at all.
        var mockProvider = new Mock<IMarketDataProvider>();
        mockProvider.Setup(p => p.GetTickersDataAsync("AAPL", TimeFrame.D1))
            .ReturnsAsync(new List<CandleData>());

        var vm = CreateViewModel(mockProvider.Object);
        var item = new WatchlistItemViewModel("AAPL", "Apple Inc.", sector: "", industry: "", open: 10m, high: 11m, low: 9m, close: 10.5m, volume: 1000L, changePercent: 0d);
        item.LastUpdatedUtc = DateTimeOffset.UtcNow;
        vm.DisplayItems.Add(item);

        try
        {
            // Act
            WeakReferenceMessenger.Default.Send(new TickerDataRefreshedMessage("AAPL"));

            // Assert: the row must not keep showing the now-deleted data.
            Assert.Equal(0m, item.Open);
            Assert.Equal(0m, item.High);
            Assert.Equal(0m, item.Low);
            Assert.Equal(0m, item.Close);
            Assert.Equal(0L, item.Volume);
            Assert.Equal(0m, item.Change);
            Assert.Equal(0d, item.ChangePercent);
            Assert.Null(item.LastUpdatedUtc);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void Receive_TickerDataRefreshedMessage_ForSymbolNotInWatchlist_DoesNotCallProvider()
    {
        var mockProvider = new Mock<IMarketDataProvider>();
        var vm = CreateViewModel(mockProvider.Object);
        var item = new WatchlistItemViewModel("AAPL", "Apple Inc.", sector: "", industry: "", open: 10m, high: 11m, low: 9m, close: 10.5m, volume: 1000L, changePercent: 0d);
        vm.DisplayItems.Add(item);

        try
        {
            WeakReferenceMessenger.Default.Send(new TickerDataRefreshedMessage("MSFT"));

            mockProvider.Verify(p => p.GetTickersDataAsync(It.IsAny<string>(), It.IsAny<TimeFrame>()), Times.Never);
            Assert.Equal(10.5m, item.Close); // untouched
        }
        finally
        {
            vm.Dispose();
        }
    }

    // Regression test (sa_minimal_fix / sa_constraint_check): ReminderCellControl used to resolve
    // IDialogService itself via App.Current.Services.GetService<T>() - a service-locator pattern
    // prohibited by SA_ARCHITECTURE_RULES.md §2. It now sends OpenReminderDialogRequestedMessage
    // instead, handled here where IDialogService is properly constructor-injected.
    [Fact]
    public async Task Receive_OpenReminderDialogRequestedMessage_OpensDialogForTheMessagesTicker_NotSelectedItem()
    {
        var mockWatchlistManager = new Mock<IWatchlistManager>();
        mockWatchlistManager.Setup(w => w.GetAllProfiles()).Returns(new List<StockAnalyzer.Core.Models.Watchlist.WatchlistProfile>());
        var mockDialogService = new Mock<IDialogService>();
        var dialogOpenedTcs = new TaskCompletionSource<string>();
        mockDialogService
            .Setup(d => d.ShowEditTickerNotesDialogAsync(
                It.IsAny<string>(), It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<decimal?>(),
                It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<string?>(),
                It.IsAny<Action<decimal?, decimal?, decimal?, decimal?, decimal?, decimal?, string?>?>()))
            .Callback<string, decimal?, decimal?, decimal?, decimal?, decimal?, decimal?, string?, Action<decimal?, decimal?, decimal?, decimal?, decimal?, decimal?, string?>?>(
                (ticker, _, _, _, _, _, _, _, _) => dialogOpenedTcs.TrySetResult(ticker))
            .ReturnsAsync(true);

        var vm = new TickerListViewModel(
            marketDataProvider: new Mock<IMarketDataProvider>().Object,
            pythonService: null!,
            messenger: WeakReferenceMessenger.Default,
            dispatcherService: new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService(),
            watchlistManager: mockWatchlistManager.Object,
            portfolioManager: null!,
            dialogService: mockDialogService.Object,
            tickerImportService: null!,
            chartSettingsManager: new MockChartSettingsManager(),
            logger: NullLogger<TickerListViewModel>.Instance);

        // Two rows are present, but SelectedItem is deliberately left on AAPL while the message asks
        // to open MSFT's Reminder - this is exactly the scenario the old SelectedItem-based command
        // handled wrong (or App.Current.Services bypassed the ViewModel for entirely).
        var aapl = new WatchlistItemViewModel("AAPL", "Apple Inc.", sector: "", industry: "", open: 10m, high: 11m, low: 9m, close: 10.5m, volume: 1000L, changePercent: 0d);
        var msft = new WatchlistItemViewModel("MSFT", "Microsoft Corp.", sector: "", industry: "", open: 20m, high: 21m, low: 19m, close: 20.5m, volume: 2000L, changePercent: 0d);
        vm.DisplayItems.Add(aapl);
        vm.DisplayItems.Add(msft);
        vm.SelectedItem = aapl;

        try
        {
            WeakReferenceMessenger.Default.Send(new OpenReminderDialogRequestedMessage("MSFT"));

            var openedTicker = await Task.WhenAny(dialogOpenedTcs.Task, Task.Delay(2000)) == dialogOpenedTcs.Task
                ? await dialogOpenedTcs.Task
                : null;
            Assert.Equal("MSFT", openedTicker);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void SelectingWatchlistsCategoryNode_AggregatesChildTickers_DeduplicatedBySymbolAndSorted()
    {
        // Arrange: two watchlists sharing one overlapping ticker (AAPL), each with a unique ticker too.
        var watchlistA = new StockAnalyzer.Core.Models.Watchlist.WatchlistProfile(
            Guid.NewGuid(), "Tech", StockAnalyzer.Core.Models.IndicatorColor.FromRgb(0, 0, 255), isPortfolio: false,
            items: new List<StockAnalyzer.Core.Models.Watchlist.WatchlistItem>
            {
                new("MSFT", DateTimeOffset.UtcNow),
                new("AAPL", DateTimeOffset.UtcNow),
            });
        var watchlistB = new StockAnalyzer.Core.Models.Watchlist.WatchlistProfile(
            Guid.NewGuid(), "Growth", StockAnalyzer.Core.Models.IndicatorColor.FromRgb(0, 255, 0), isPortfolio: false,
            items: new List<StockAnalyzer.Core.Models.Watchlist.WatchlistItem>
            {
                new("aapl", DateTimeOffset.UtcNow), // Same symbol as watchlistA, different case.
                new("GOOGL", DateTimeOffset.UtcNow),
            });

        var mockWatchlistManager = new Mock<IWatchlistManager>();
        mockWatchlistManager.Setup(w => w.GetAllProfiles())
            .Returns(new List<StockAnalyzer.Core.Models.Watchlist.WatchlistProfile> { watchlistA, watchlistB });

        var vm = new TickerListViewModel(
            marketDataProvider: null!,
            pythonService: null!,
            messenger: WeakReferenceMessenger.Default,
            dispatcherService: new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService(),
            watchlistManager: mockWatchlistManager.Object,
            portfolioManager: null!,
            dialogService: null!,
            tickerImportService: null!,
            chartSettingsManager: new MockChartSettingsManager(),
            logger: NullLogger<TickerListViewModel>.Instance);

        try
        {
            var watchlistsCategory = vm.Groups.First(n => n.Id == TickerListViewModel.WatchlistsCategoryId);

            // Act
            vm.SelectedNode = watchlistsCategory;

            // Assert: union of both watchlists' tickers, de-duplicated by Symbol (case-insensitive), sorted like other nodes.
            Assert.Equal(new List<string> { "AAPL", "GOOGL", "MSFT" }, vm.DisplayItems.Select(i => i.Symbol).ToList());
        }
        finally
        {
            vm.Dispose();
        }
    }

    /// <summary>Regression test for the Note-tab "unified scope menu" feature (sa_implement, Task 1):
    /// GetTickersForNode was promoted from a TickerListViewModel-private helper to a public
    /// ITickerStateStore member so NoteTimelineViewModel can resolve a picked node (Watchlist/
    /// Portfolio/AllTickers/Filter) into a concrete ticker set without depending on the concrete
    /// TickerListViewModel type. This proves the interface-level contract resolves each node kind
    /// correctly and - critically - never mutates the store's own SelectedNode, so a Notes-tab
    /// caller can never leak a selection change into the Tickers tab's own grid.</summary>
    [Fact]
    public void ITickerStateStore_GetTickersForNode_ResolvesEachNodeKind_WithoutMutatingSelectedNode()
    {
        var watchlist = new StockAnalyzer.Core.Models.Watchlist.WatchlistProfile(
            Guid.NewGuid(), "Tech", StockAnalyzer.Core.Models.IndicatorColor.FromRgb(0, 0, 255), isPortfolio: false,
            items: new List<StockAnalyzer.Core.Models.Watchlist.WatchlistItem>
            {
                new("MSFT", DateTimeOffset.UtcNow),
                new("AAPL", DateTimeOffset.UtcNow),
            });
        var portfolio = new StockAnalyzer.Core.Models.Watchlist.WatchlistProfile(
            Guid.NewGuid(), "Core", StockAnalyzer.Core.Models.IndicatorColor.FromRgb(0, 255, 0), isPortfolio: true,
            items: new List<StockAnalyzer.Core.Models.Watchlist.WatchlistItem>
            {
                new("GOOGL", DateTimeOffset.UtcNow),
            });

        var mockWatchlistManager = new Mock<IWatchlistManager>();
        mockWatchlistManager.Setup(w => w.GetAllProfiles())
            .Returns(new List<StockAnalyzer.Core.Models.Watchlist.WatchlistProfile> { watchlist, portfolio });

        var vm = new TickerListViewModel(
            marketDataProvider: null!,
            pythonService: null!,
            messenger: WeakReferenceMessenger.Default,
            dispatcherService: new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService(),
            watchlistManager: mockWatchlistManager.Object,
            portfolioManager: null!,
            dialogService: null!,
            tickerImportService: null!,
            chartSettingsManager: new MockChartSettingsManager(),
            logger: NullLogger<TickerListViewModel>.Instance);

        try
        {
            ITickerStateStore store = vm;
            var watchlistsCategory = vm.Groups.First(n => n.Id == TickerListViewModel.WatchlistsCategoryId);
            var watchlistNode = watchlistsCategory.Children!.First();
            var portfoliosCategory = vm.Groups.First(n => n.Id == TickerListViewModel.PortfoliosCategoryId);
            var portfolioNode = portfoliosCategory.Children!.First();
            var allTickersNode = vm.Groups.First(n => n.Id == TickerListViewModel.AllTickersId);

            var selectedNodeBefore = vm.SelectedNode;

            Assert.Equal(new[] { "AAPL", "MSFT" }, store.GetTickersForNode(watchlistNode).OrderBy(t => t));
            Assert.Equal(new[] { "GOOGL" }, store.GetTickersForNode(portfolioNode));
            Assert.Empty(store.GetTickersForNode(allTickersNode)); // no tickers imported in this test
            Assert.Empty(store.GetTickersForNode(null));

            Assert.Same(selectedNodeBefore, vm.SelectedNode);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task SetAsRelativePerformanceTargetsCommand_UpdatesComparisonTargetsWithCheckedTickers()
    {
        // Arrange
        var settingsManager = new MockChartSettingsManager();
        var mockWatchlistManager = new Mock<IWatchlistManager>();
        mockWatchlistManager.Setup(w => w.GetAllProfiles()).Returns(new List<StockAnalyzer.Core.Models.Watchlist.WatchlistProfile>());
        var vm = new TickerListViewModel(
            marketDataProvider: null!,
            pythonService: null!,
            messenger: CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default,
            dispatcherService: new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService(),
            watchlistManager: mockWatchlistManager.Object,
            portfolioManager: null!,
            dialogService: null!,
            tickerImportService: null!,
            chartSettingsManager: settingsManager,
            logger: NullLogger<TickerListViewModel>.Instance);

        // Add dummy items
        var item1 = new WatchlistItemViewModel("AAPL", "Apple Inc.", sector: "", industry: "", open: 0m, high: 0m, low: 0m, close: 0m, volume: 0L, changePercent: 0d) { IsChecked = true };
        var item2 = new WatchlistItemViewModel("MSFT", "Microsoft Corp.", sector: "", industry: "", open: 0m, high: 0m, low: 0m, close: 0m, volume: 0L, changePercent: 0d) { IsChecked = false };
        var item3 = new WatchlistItemViewModel("GOOGL", "Alphabet Inc.", sector: "", industry: "", open: 0m, high: 0m, low: 0m, close: 0m, volume: 0L, changePercent: 0d) { IsChecked = true };

        vm.DisplayItems.Add(item1);
        vm.DisplayItems.Add(item2);
        vm.DisplayItems.Add(item3);

        try
        {
            // Act
            await vm.SetAsRelativePerformanceTargetsCommand.ExecuteAsync(null);

            // Assert
            Assert.Equal(new List<string> { "AAPL", "GOOGL" }, settingsManager.Current.ComparisonTargets);
        }
        finally
        {
            // Must unregister from the shared WeakReferenceMessenger.Default: this VM now also
            // listens for TickerDataRefreshedMessage, and an undisposed instance would keep
            // reacting to messages sent by unrelated tests for as long as it stays reachable.
            vm.Dispose();
        }
    }

    [Fact]
    public async Task SetAsRelativePerformanceTargetsCommand_FallsBackToSelectedItem_WhenNoCheckboxesChecked()
    {
        // Arrange
        var settingsManager = new MockChartSettingsManager();
        var mockWatchlistManager = new Mock<IWatchlistManager>();
        mockWatchlistManager.Setup(w => w.GetAllProfiles()).Returns(new List<StockAnalyzer.Core.Models.Watchlist.WatchlistProfile>());
        var vm = new TickerListViewModel(
            marketDataProvider: null!,
            pythonService: null!,
            messenger: CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default,
            dispatcherService: new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService(),
            watchlistManager: mockWatchlistManager.Object,
            portfolioManager: null!,
            dialogService: null!,
            tickerImportService: null!,
            chartSettingsManager: settingsManager,
            logger: NullLogger<TickerListViewModel>.Instance);

        var item1 = new WatchlistItemViewModel("NVDA", "NVIDIA Corp.", sector: "", industry: "", open: 0m, high: 0m, low: 0m, close: 0m, volume: 0L, changePercent: 0d) { IsChecked = false };
        vm.DisplayItems.Add(item1);
        vm.SelectedItem = item1;

        try
        {
            // Act
            await vm.SetAsRelativePerformanceTargetsCommand.ExecuteAsync(null);

            // Assert
            Assert.Equal(new List<string> { "NVDA" }, settingsManager.Current.ComparisonTargets);
        }
        finally
        {
            vm.Dispose();
        }
    }
}
