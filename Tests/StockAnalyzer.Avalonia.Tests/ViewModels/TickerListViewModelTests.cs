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
using StockAnalyzer.Avalonia.ViewModels.TickerList;
using StockAnalyzer.Avalonia.ViewModels.Watchlist;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

// Receives TickerDataRefreshedMessage on the shared static WeakReferenceMessenger.Default using
// symbol names that overlap with EditTickerNotesDialogViewModelTests (see MessengerSharedStateCollection.cs).
[Collection("MessengerSharedState")]
public class TickerListViewModelTests
{
    internal static TickerListViewModel CreateViewModel(IMarketDataProvider marketDataProvider)
        => CreateViewModel(marketDataProvider, new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService());

    internal static TickerListViewModel CreateViewModel(
        IMarketDataProvider marketDataProvider,
        StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService dispatcher)
    {
        var mockWatchlistManager = new Mock<IWatchlistManager>();
        mockWatchlistManager.Setup(w => w.GetAllProfiles()).Returns(new List<StockAnalyzer.Core.Models.Watchlist.WatchlistProfile>());

        return new TickerListViewModel(
            marketDataProvider: marketDataProvider,
            pythonService: null!,
            messenger: WeakReferenceMessenger.Default,
            dispatcherService: dispatcher,
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

    [Fact]
    public void Groups_SeededWithThreeSystemCategories_DefaultingToAllTicker()
    {
        var vm = CreateViewModel(new Mock<IMarketDataProvider>().Object);

        try
        {
            Assert.IsType<AllTickersNode>(vm.Groups.First(n => n.Id == TickerListViewModel.AllTickersId));
            Assert.IsType<CategoryNode>(vm.Groups.First(n => n.Id == TickerListViewModel.WatchlistsCategoryId));
            Assert.IsType<CategoryNode>(vm.Groups.First(n => n.Id == TickerListViewModel.PortfoliosCategoryId));
            Assert.Equal(TickerListViewModel.AllTickersId, vm.SelectedNode?.Id);
        }
        finally
        {
            vm.Dispose();
        }
    }

    /// <summary>Selecting the parent category aggregates across every list folder nested under it
    /// (mirrors how selecting the fixed Watchlists/Portfolios category aggregates its children) -
    /// the parent category itself never holds tickers directly.</summary>
    [Fact]
    public void SelectingCustomParentCategory_AggregatesTickersAcrossAllItsListFolders()
    {
        var vm = CreateViewModel(new Mock<IMarketDataProvider>().Object);

        try
        {
            var folderA = new StockAnalyzer.Core.Models.Settings.TickerListFolderSettings
            {
                Name = "Folder A",
                ChildTickers = new List<string> { "GOOGL" },
            };
            var folderB = new StockAnalyzer.Core.Models.Settings.TickerListFolderSettings
            {
                Name = "Folder B",
                ChildTickers = new List<string> { "MSFT" },
            };
            var customCategory = new StockAnalyzer.Core.Models.Settings.TickerParentCategorySettings
            {
                Name = "My Sector",
                Folders = new List<StockAnalyzer.Core.Models.Settings.TickerListFolderSettings> { folderA, folderB },
            };
            vm.ImportCustomTickerCategories(new[] { customCategory });

            // Act: select the parent category, not either folder itself.
            vm.SelectedNode = vm.Groups.First(n => n.Id == customCategory.Id);

            // Assert: union across both list folders.
            Assert.Equal(new List<string> { "GOOGL", "MSFT" }, vm.DisplayItems.Select(i => i.Symbol).ToList());
        }
        finally
        {
            vm.Dispose();
        }
    }

    /// <summary>Regression test (SAで修正): a FilterNode added directly under a CustomCategoryNode
    /// (via that node's own "Add Filter" menu item) used to send GetTickersForNode(CustomCategoryNode)
    /// into infinite mutual recursion with GetTickersForNode(FilterNode) -&gt; FindParentNode
    /// (StackOverflowException), because the aggregation unioned every Children entry with no type
    /// filter. It must now aggregate only the TickerListFolderNode children, ignoring any sibling
    /// FilterNode entirely when the parent category itself is selected.</summary>
    [Fact]
    public void SelectingCustomParentCategory_WithADirectFilterNodeSibling_DoesNotRecurseInfinitely()
    {
        var vm = CreateViewModel(new Mock<IMarketDataProvider>().Object);

        try
        {
            var folder = new StockAnalyzer.Core.Models.Settings.TickerListFolderSettings
            {
                Name = "My List",
                ChildTickers = new List<string> { "GOOGL" },
            };
            var customCategory = new StockAnalyzer.Core.Models.Settings.TickerParentCategorySettings
            {
                Name = "My Sector",
                Folders = new List<StockAnalyzer.Core.Models.Settings.TickerListFolderSettings> { folder },
            };
            vm.ImportCustomTickerCategories(new[] { customCategory });

            var categoryNode = (CustomCategoryNode)vm.Groups.First(n => n.Id == customCategory.Id);

            // Mirrors what AddFilterCommand does: a FilterNode becomes a direct sibling of the
            // TickerListFolderNode entries under the same CustomCategoryNode.
            var filterSettings = new StockAnalyzer.Core.Models.Settings.FilterSettings
            {
                Name = "My Filter",
                ParentId = categoryNode.Id,
            };
            categoryNode.Children!.Add(new FilterNode(filterSettings));

            // Act: selecting the parent category must not stack-overflow, and must still resolve
            // the list folder's ticker, ignoring the sibling FilterNode.
            vm.SelectedNode = categoryNode;

            Assert.Equal(new List<string> { "GOOGL" }, vm.DisplayItems.Select(i => i.Symbol).ToList());
        }
        finally
        {
            vm.Dispose();
        }
    }

    /// <summary>Regression test (SAで修正): BulkRemove() used to resolve its removal target
    /// exclusively via SelectedWatchlist (WatchlistNode/PortfolioNode only), so it silently did
    /// nothing when a TickerListFolderNode was selected - "Remove from list" was a dead button for
    /// custom category list folders.</summary>
    [Fact]
    public void BulkRemoveCommand_OnListFolder_RemovesCheckedTickerFromFolderAndDisplayItems()
    {
        var vm = CreateViewModel(new Mock<IMarketDataProvider>().Object);

        try
        {
            var folder = new StockAnalyzer.Core.Models.Settings.TickerListFolderSettings
            {
                Name = "My List",
                ChildTickers = new List<string> { "AAPL", "MSFT" },
            };
            var customCategory = new StockAnalyzer.Core.Models.Settings.TickerParentCategorySettings
            {
                Name = "My Sector",
                Folders = new List<StockAnalyzer.Core.Models.Settings.TickerListFolderSettings> { folder },
            };
            vm.ImportCustomTickerCategories(new[] { customCategory });
            var folderNode = (TickerListFolderNode)vm.Groups
                .First(n => n.Id == customCategory.Id).Children!.First();
            vm.SelectedNode = folderNode;

            var aapl = vm.DisplayItems.Single(i => i.Symbol == "AAPL");
            aapl.IsChecked = true;

            vm.BulkRemoveCommand.Execute(null);

            Assert.Equal(new[] { "MSFT" }, folder.ChildTickers);
            Assert.DoesNotContain(vm.DisplayItems, i => i.Symbol == "AAPL");
        }
        finally
        {
            vm.Dispose();
        }
    }

    /// <summary>Regression test (SAで修正): DeleteFromMasterList() cascaded to
    /// IWatchlistManager.RemoveTickersFromAllProfiles() but never purged a deleted ticker from any
    /// custom category list folder's ChildTickers, leaving a ghost entry that kept showing in that
    /// folder's view after the ticker no longer existed in the master registry.</summary>
    [Fact]
    public async Task DeleteFromMasterListCommand_CascadesToCustomCategoryListFolders()
    {
        var mockDialogService = new Mock<IDialogService>();
        mockDialogService
            .Setup(d => d.ShowConfirmationAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        var mockMarketDataProvider = new Mock<IMarketDataProvider>();
        mockMarketDataProvider.Setup(p => p.GetAvailableTickersAsync()).ReturnsAsync(new List<string>());
        var mockWatchlistManager = new Mock<IWatchlistManager>();
        mockWatchlistManager.Setup(w => w.GetAllProfiles()).Returns(new List<StockAnalyzer.Core.Models.Watchlist.WatchlistProfile>());

        var vm = new TickerListViewModel(
            marketDataProvider: mockMarketDataProvider.Object,
            pythonService: null!,
            messenger: WeakReferenceMessenger.Default,
            dispatcherService: new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService(),
            watchlistManager: mockWatchlistManager.Object,
            portfolioManager: null!,
            dialogService: mockDialogService.Object,
            tickerImportService: null!,
            chartSettingsManager: new MockChartSettingsManager(),
            logger: NullLogger<TickerListViewModel>.Instance);

        try
        {
            var folder = new StockAnalyzer.Core.Models.Settings.TickerListFolderSettings
            {
                Name = "My List",
                ChildTickers = new List<string> { "AAPL", "MSFT" },
            };
            var customCategory = new StockAnalyzer.Core.Models.Settings.TickerParentCategorySettings
            {
                Name = "My Sector",
                Folders = new List<StockAnalyzer.Core.Models.Settings.TickerListFolderSettings> { folder },
            };
            vm.ImportCustomTickerCategories(new[] { customCategory });

            var aapl = new WatchlistItemViewModel("AAPL", "Apple Inc.", "", "", 0, 0, 0, 0, 0, 0) { IsChecked = true };
            vm.DisplayItems.Add(aapl);

            await vm.DeleteFromMasterListCommand.ExecuteAsync(null);

            Assert.Equal(new[] { "MSFT" }, folder.ChildTickers);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task AddCustomCategoryCommand_PromptedName_AddsCategoryAndSelectsIt()
    {
        var mockDialogService = new Mock<IDialogService>();
        mockDialogService
            .Setup(d => d.ShowInputAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync("My Sector");
        var mockWatchlistManager = new Mock<IWatchlistManager>();
        mockWatchlistManager.Setup(w => w.GetAllProfiles()).Returns(new List<StockAnalyzer.Core.Models.Watchlist.WatchlistProfile>());

        var vm = new TickerListViewModel(
            marketDataProvider: null!,
            pythonService: null!,
            messenger: WeakReferenceMessenger.Default,
            dispatcherService: new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService(),
            watchlistManager: mockWatchlistManager.Object,
            portfolioManager: null!,
            dialogService: mockDialogService.Object,
            tickerImportService: null!,
            chartSettingsManager: new MockChartSettingsManager(),
            logger: NullLogger<TickerListViewModel>.Instance);

        try
        {
            await vm.AddCustomCategoryCommand.ExecuteAsync(null);

            var added = vm.Groups.OfType<CustomCategoryNode>().Single();
            Assert.Equal("My Sector", added.DisplayName);
            Assert.IsType<CustomCategoryNode>(vm.SelectedNode);
            Assert.Equal(added.Id, vm.SelectedNode?.Id);
            Assert.Single(vm.ExportCustomTickerCategories());
        }
        finally
        {
            vm.Dispose();
        }
    }

    /// <summary>Only a <see cref="CustomCategoryNode"/> can ever be passed to this command - the 3
    /// fixed system categories (All Ticker/Watchlist/Portfolio) are never that type, so the
    /// "system categories cannot be renamed" rule is now enforced at compile time by the command's
    /// parameter type rather than a runtime IsSystem guard. This covers the remaining runtime guard:
    /// a null parameter (e.g. no TreeView selection) must not prompt for a name.</summary>
    [Fact]
    public async Task RenameCustomCategoryCommand_NullParameter_IsNoOp()
    {
        var mockDialogService = new Mock<IDialogService>();
        var mockWatchlistManager = new Mock<IWatchlistManager>();
        mockWatchlistManager.Setup(w => w.GetAllProfiles()).Returns(new List<StockAnalyzer.Core.Models.Watchlist.WatchlistProfile>());

        var vm = new TickerListViewModel(
            marketDataProvider: null!,
            pythonService: null!,
            messenger: WeakReferenceMessenger.Default,
            dispatcherService: new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService(),
            watchlistManager: mockWatchlistManager.Object,
            portfolioManager: null!,
            dialogService: mockDialogService.Object,
            tickerImportService: null!,
            chartSettingsManager: new MockChartSettingsManager(),
            logger: NullLogger<TickerListViewModel>.Instance);

        try
        {
            await vm.RenameCustomCategoryCommand.ExecuteAsync(null);

            mockDialogService.Verify(d => d.ShowInputAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }
        finally
        {
            vm.Dispose();
        }
    }

    /// <summary>Proves the fix at the heart of this sa_improve round: renaming (via the context menu
    /// on the CustomCategoryNode itself, now that custom categories live directly in Groups instead
    /// of the removed left-column ListBox) updates the actual TreeView node's DisplayName in place,
    /// not just a detached ParentCategories entry.</summary>
    [Fact]
    public async Task RenameCustomCategoryCommand_UpdatesTheCorrespondingGroupsNode()
    {
        var mockDialogService = new Mock<IDialogService>();
        mockDialogService
            .Setup(d => d.ShowInputAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync("Renamed");
        var mockWatchlistManager = new Mock<IWatchlistManager>();
        mockWatchlistManager.Setup(w => w.GetAllProfiles()).Returns(new List<StockAnalyzer.Core.Models.Watchlist.WatchlistProfile>());

        var vm = new TickerListViewModel(
            marketDataProvider: null!,
            pythonService: null!,
            messenger: WeakReferenceMessenger.Default,
            dispatcherService: new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService(),
            watchlistManager: mockWatchlistManager.Object,
            portfolioManager: null!,
            dialogService: mockDialogService.Object,
            tickerImportService: null!,
            chartSettingsManager: new MockChartSettingsManager(),
            logger: NullLogger<TickerListViewModel>.Instance);

        try
        {
            var customCategory = new StockAnalyzer.Core.Models.Settings.TickerParentCategorySettings { Name = "My Sector" };
            vm.ImportCustomTickerCategories(new[] { customCategory });
            var categoryNode = (CustomCategoryNode)vm.Groups.First(n => n.Id == customCategory.Id);

            await vm.RenameCustomCategoryCommand.ExecuteAsync(categoryNode);

            Assert.Equal("Renamed", categoryNode.DisplayName);
            Assert.Equal("Renamed", ((CustomCategoryNode)vm.Groups.First(n => n.Id == customCategory.Id)).DisplayName);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task DeleteCustomCategoryCommand_WhenCurrentlySelected_FallsBackToAllTicker()
    {
        var mockDialogService = new Mock<IDialogService>();
        mockDialogService
            .Setup(d => d.ShowConfirmationAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        var mockWatchlistManager = new Mock<IWatchlistManager>();
        mockWatchlistManager.Setup(w => w.GetAllProfiles()).Returns(new List<StockAnalyzer.Core.Models.Watchlist.WatchlistProfile>());

        var vm = new TickerListViewModel(
            marketDataProvider: null!,
            pythonService: null!,
            messenger: WeakReferenceMessenger.Default,
            dispatcherService: new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService(),
            watchlistManager: mockWatchlistManager.Object,
            portfolioManager: null!,
            dialogService: mockDialogService.Object,
            tickerImportService: null!,
            chartSettingsManager: new MockChartSettingsManager(),
            logger: NullLogger<TickerListViewModel>.Instance);

        try
        {
            var customCategory = new StockAnalyzer.Core.Models.Settings.TickerParentCategorySettings { Name = "My Sector" };
            vm.ImportCustomTickerCategories(new[] { customCategory });
            var categoryNode = (CustomCategoryNode)vm.Groups.First(n => n.Id == customCategory.Id);
            vm.SelectedNode = categoryNode;

            await vm.DeleteCustomCategoryCommand.ExecuteAsync(categoryNode);

            Assert.Empty(vm.Groups.OfType<CustomCategoryNode>());
            Assert.Equal(TickerListViewModel.AllTickersId, vm.SelectedNode?.Id);
            Assert.Empty(vm.ExportCustomTickerCategories());
        }
        finally
        {
            vm.Dispose();
        }
    }

    /// <summary>Regression test (sa_minimal_fix / sa_constraint_check): deleting a category removes
    /// every one of its list folders too, not just the category node itself. The old wasSelected
    /// check in DeleteCustomCategoryAsync only compared SelectedNode against the category's own Id,
    /// so selecting one of its list folders (not the category) and then deleting the category left
    /// SelectedNode pointing at a node no longer present in Groups, with DisplayItems still showing
    /// that now-deleted folder's stale contents.</summary>
    [Fact]
    public async Task DeleteCustomCategoryCommand_WhenAListFolderUnderItIsSelected_FallsBackToAllTicker()
    {
        var mockDialogService = new Mock<IDialogService>();
        mockDialogService
            .Setup(d => d.ShowConfirmationAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        var mockWatchlistManager = new Mock<IWatchlistManager>();
        mockWatchlistManager.Setup(w => w.GetAllProfiles()).Returns(new List<StockAnalyzer.Core.Models.Watchlist.WatchlistProfile>());

        var vm = new TickerListViewModel(
            marketDataProvider: null!,
            pythonService: null!,
            messenger: WeakReferenceMessenger.Default,
            dispatcherService: new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService(),
            watchlistManager: mockWatchlistManager.Object,
            portfolioManager: null!,
            dialogService: mockDialogService.Object,
            tickerImportService: null!,
            chartSettingsManager: new MockChartSettingsManager(),
            logger: NullLogger<TickerListViewModel>.Instance);

        try
        {
            var folder = new StockAnalyzer.Core.Models.Settings.TickerListFolderSettings { Name = "Semis" };
            var customCategory = new StockAnalyzer.Core.Models.Settings.TickerParentCategorySettings
            {
                Name = "My Sector",
                Folders = new List<StockAnalyzer.Core.Models.Settings.TickerListFolderSettings> { folder },
            };
            vm.ImportCustomTickerCategories(new[] { customCategory });
            var categoryNode = (CustomCategoryNode)vm.Groups.First(n => n.Id == customCategory.Id);
            var folderNode = categoryNode.Children!.OfType<TickerListFolderNode>().First(n => n.Id == folder.Id);

            // The list folder - not the category itself - is the current selection.
            vm.SelectedNode = folderNode;

            await vm.DeleteCustomCategoryCommand.ExecuteAsync(categoryNode);

            Assert.Empty(vm.Groups.OfType<CustomCategoryNode>());
            Assert.Equal(TickerListViewModel.AllTickersId, vm.SelectedNode?.Id);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void AssignSelectedTickersToCustomCategoryCommand_AddsCheckedTickers_DeduplicatedCaseInsensitively()
    {
        var vm = CreateViewModel(new Mock<IMarketDataProvider>().Object);

        try
        {
            var folder = new StockAnalyzer.Core.Models.Settings.TickerListFolderSettings
            {
                Name = "My List",
                ChildTickers = new List<string> { "aapl" }, // already present, different case
            };
            var customCategory = new StockAnalyzer.Core.Models.Settings.TickerParentCategorySettings
            {
                Name = "My Sector",
                Folders = new List<StockAnalyzer.Core.Models.Settings.TickerListFolderSettings> { folder },
            };
            vm.ImportCustomTickerCategories(new[] { customCategory });
            var folderEntry = vm.AssignableCategoryGroups.Single().Folders.Single(f => f.FolderId == folder.Id);

            var aapl = new WatchlistItemViewModel("AAPL", "Apple Inc.", "", "", 0, 0, 0, 0, 0, 0) { IsChecked = true };
            var msft = new WatchlistItemViewModel("MSFT", "Microsoft Corp.", "", "", 0, 0, 0, 0, 0, 0) { IsChecked = true };
            vm.DisplayItems.Add(aapl);
            vm.DisplayItems.Add(msft);

            vm.AssignSelectedTickersToCustomCategoryCommand.Execute(folderEntry);

            var exportedFolder = vm.ExportCustomTickerCategories().Single().Folders.Single();
            Assert.Equal(new[] { "aapl", "MSFT" }, exportedFolder.ChildTickers); // AAPL not duplicated
        }
        finally
        {
            vm.Dispose();
        }
    }

    /// <summary>A parent category is a pure grouping container and can never directly hold tickers -
    /// only its list folders can - so it must never appear as a valid "Add To Custom Category"
    /// target itself, no matter how many list folders it has. It appears only as the nested
    /// submenu's group header (<see cref="TickerAssignableCategoryGroup.CategoryName"/>).</summary>
    [Fact]
    public void AssignableCategoryGroups_NeverListsAParentCategorysOwnIdAsAFolder()
    {
        var vm = CreateViewModel(new Mock<IMarketDataProvider>().Object);

        try
        {
            var folder = new StockAnalyzer.Core.Models.Settings.TickerListFolderSettings { Name = "My List" };
            var customCategory = new StockAnalyzer.Core.Models.Settings.TickerParentCategorySettings
            {
                Name = "My Sector",
                Folders = new List<StockAnalyzer.Core.Models.Settings.TickerListFolderSettings> { folder },
            };
            vm.ImportCustomTickerCategories(new[] { customCategory });

            var group = Assert.Single(vm.AssignableCategoryGroups);
            Assert.Equal("My Sector", group.CategoryName);
            Assert.DoesNotContain(group.Folders, f => f.FolderId == customCategory.Id);
            Assert.Contains(group.Folders, f => f.FolderId == folder.Id);
        }
        finally
        {
            vm.Dispose();
        }
    }

    /// <summary>A parent category with zero list folders has nothing valid to assign into, so it
    /// must not appear as a dead-end (empty) submenu entry.</summary>
    [Fact]
    public void AssignableCategoryGroups_ExcludesParentCategoriesWithNoListFolders()
    {
        var vm = CreateViewModel(new Mock<IMarketDataProvider>().Object);

        try
        {
            var emptyCategory = new StockAnalyzer.Core.Models.Settings.TickerParentCategorySettings { Name = "Empty Sector" };
            vm.ImportCustomTickerCategories(new[] { emptyCategory });

            Assert.Empty(vm.AssignableCategoryGroups);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task CreateListFolderCommand_PromptedName_AddsFolderUnderParentAndSelectsIt()
    {
        var mockDialogService = new Mock<IDialogService>();
        mockDialogService
            .Setup(d => d.ShowInputAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync("My List");
        var mockWatchlistManager = new Mock<IWatchlistManager>();
        mockWatchlistManager.Setup(w => w.GetAllProfiles()).Returns(new List<StockAnalyzer.Core.Models.Watchlist.WatchlistProfile>());

        var vm = new TickerListViewModel(
            marketDataProvider: null!,
            pythonService: null!,
            messenger: WeakReferenceMessenger.Default,
            dispatcherService: new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService(),
            watchlistManager: mockWatchlistManager.Object,
            portfolioManager: null!,
            dialogService: mockDialogService.Object,
            tickerImportService: null!,
            chartSettingsManager: new MockChartSettingsManager(),
            logger: NullLogger<TickerListViewModel>.Instance);

        try
        {
            var customCategory = new StockAnalyzer.Core.Models.Settings.TickerParentCategorySettings { Name = "My Sector" };
            vm.ImportCustomTickerCategories(new[] { customCategory });
            var categoryNode = (CustomCategoryNode)vm.Groups.First(n => n.Id == customCategory.Id);

            await vm.CreateListFolderCommand.ExecuteAsync(categoryNode);

            var folderNode = Assert.IsType<TickerListFolderNode>(Assert.Single(categoryNode.Children!));
            Assert.Equal("My List", folderNode.DisplayName);
            Assert.Equal(folderNode.Id, vm.SelectedNode?.Id);
            Assert.Single(vm.ExportCustomTickerCategories().Single().Folders);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task RenameListFolderCommand_UpdatesTheCorrespondingNode()
    {
        var mockDialogService = new Mock<IDialogService>();
        mockDialogService
            .Setup(d => d.ShowInputAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync("Renamed");
        var mockWatchlistManager = new Mock<IWatchlistManager>();
        mockWatchlistManager.Setup(w => w.GetAllProfiles()).Returns(new List<StockAnalyzer.Core.Models.Watchlist.WatchlistProfile>());
        var vm = new TickerListViewModel(
            marketDataProvider: null!,
            pythonService: null!,
            messenger: WeakReferenceMessenger.Default,
            dispatcherService: new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService(),
            watchlistManager: mockWatchlistManager.Object,
            portfolioManager: null!,
            dialogService: mockDialogService.Object,
            tickerImportService: null!,
            chartSettingsManager: new MockChartSettingsManager(),
            logger: NullLogger<TickerListViewModel>.Instance);

        try
        {
            var folder = new StockAnalyzer.Core.Models.Settings.TickerListFolderSettings { Name = "My List" };
            var customCategory = new StockAnalyzer.Core.Models.Settings.TickerParentCategorySettings
            {
                Name = "My Sector",
                Folders = new List<StockAnalyzer.Core.Models.Settings.TickerListFolderSettings> { folder },
            };
            vm.ImportCustomTickerCategories(new[] { customCategory });
            var categoryNode = (CustomCategoryNode)vm.Groups.First(n => n.Id == customCategory.Id);
            var folderNode = (TickerListFolderNode)categoryNode.Children!.First();

            await vm.RenameListFolderCommand.ExecuteAsync(folderNode);

            Assert.Equal("Renamed", folderNode.DisplayName);
            Assert.Equal("Renamed", vm.ExportCustomTickerCategories().Single().Folders.Single().Name);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task DeleteListFolderCommand_RemovesTheFolder_LeavingTheParentCategoryIntact()
    {
        var mockDialogService = new Mock<IDialogService>();
        mockDialogService
            .Setup(d => d.ShowConfirmationAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        var mockWatchlistManager = new Mock<IWatchlistManager>();
        mockWatchlistManager.Setup(w => w.GetAllProfiles()).Returns(new List<StockAnalyzer.Core.Models.Watchlist.WatchlistProfile>());
        var vm = new TickerListViewModel(
            marketDataProvider: null!,
            pythonService: null!,
            messenger: WeakReferenceMessenger.Default,
            dispatcherService: new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService(),
            watchlistManager: mockWatchlistManager.Object,
            portfolioManager: null!,
            dialogService: mockDialogService.Object,
            tickerImportService: null!,
            chartSettingsManager: new MockChartSettingsManager(),
            logger: NullLogger<TickerListViewModel>.Instance);

        try
        {
            var folder = new StockAnalyzer.Core.Models.Settings.TickerListFolderSettings { Name = "My List" };
            var customCategory = new StockAnalyzer.Core.Models.Settings.TickerParentCategorySettings
            {
                Name = "My Sector",
                Folders = new List<StockAnalyzer.Core.Models.Settings.TickerListFolderSettings> { folder },
            };
            vm.ImportCustomTickerCategories(new[] { customCategory });
            var categoryNode = (CustomCategoryNode)vm.Groups.First(n => n.Id == customCategory.Id);
            var folderNode = (TickerListFolderNode)categoryNode.Children!.First();
            vm.SelectedNode = folderNode;

            await vm.DeleteListFolderCommand.ExecuteAsync(folderNode);

            Assert.Empty(categoryNode.Children!);
            Assert.Empty(vm.ExportCustomTickerCategories().Single().Folders);
            Assert.NotNull(vm.Groups.FirstOrDefault(n => n.Id == customCategory.Id)); // parent category still exists
        }
        finally
        {
            vm.Dispose();
        }
    }

    private sealed class FakeScreenerTemplateEvaluator : StockAnalyzer.Core.Services.Screener.IScreenerTemplateEvaluator
    {
        private readonly HashSet<string> _matches;
        public FakeScreenerTemplateEvaluator(HashSet<string> matches) => _matches = matches;

        // Synchronously-completed task: the awaiting caller (TickerListViewModel.
        // EvaluateScreenerTemplateFilterAsync) resumes on the same call stack, so setting
        // SelectedScreenerTemplate from a test observes the resulting ApplyFilter() synchronously.
        public Task<HashSet<string>> EvaluateAsync(
            IReadOnlyList<string> symbols,
            StockAnalyzer.Core.Models.Templates.ScreenerIndicatorTemplate template,
            TimeFrame timeFrame,
            IProgress<int>? progress,
            CancellationToken ct) => Task.FromResult(_matches);
    }

    [Fact]
    public void ScreenerTemplateFilter_CombinesWithCategorySelection_AsIntersection()
    {
        var watchlist = new StockAnalyzer.Core.Models.Watchlist.WatchlistProfile(
            Guid.NewGuid(), "Tech", StockAnalyzer.Core.Models.IndicatorColor.FromRgb(0, 0, 255), isPortfolio: false,
            items: new List<StockAnalyzer.Core.Models.Watchlist.WatchlistItem>
            {
                new("AAPL", DateTimeOffset.UtcNow),
                new("MSFT", DateTimeOffset.UtcNow),
                new("GOOGL", DateTimeOffset.UtcNow),
            });

        var mockWatchlistManager = new Mock<IWatchlistManager>();
        mockWatchlistManager.Setup(w => w.GetAllProfiles())
            .Returns(new List<StockAnalyzer.Core.Models.Watchlist.WatchlistProfile> { watchlist });

        var fakeEvaluator = new FakeScreenerTemplateEvaluator(new HashSet<string> { "AAPL", "MSFT" });

        var mockProvider = new Mock<IMarketDataProvider>();
        mockProvider.Setup(p => p.GetAvailableTickersAsync())
            .ReturnsAsync((IReadOnlyList<string>)new List<string> { "AAPL", "MSFT", "GOOGL" });

        var vm = new TickerListViewModel(
            marketDataProvider: mockProvider.Object,
            pythonService: null!,
            messenger: WeakReferenceMessenger.Default,
            dispatcherService: new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService(),
            watchlistManager: mockWatchlistManager.Object,
            portfolioManager: null!,
            dialogService: null!,
            tickerImportService: null!,
            chartSettingsManager: new MockChartSettingsManager(),
            logger: NullLogger<TickerListViewModel>.Instance,
            templateService: null,
            screenerTemplateEvaluator: fakeEvaluator);

        try
        {
            var watchlistNode = vm.Groups
                .First(n => n.Id == TickerListViewModel.WatchlistsCategoryId).Children!.First();
            vm.SelectedNode = watchlistNode; // DisplayItems = AAPL, GOOGL, MSFT

            // Act: Screener Template filter narrows to its matches, intersected with the active category.
            var template = new StockAnalyzer.Core.Models.Templates.ScreenerIndicatorTemplate { Name = "My Template" };
            vm.SelectedScreenerTemplate = template;
            Assert.Equal(new List<string> { "AAPL", "MSFT" }, vm.DisplayItems.Select(i => i.Symbol).ToList());
            Assert.False(vm.IsEvaluatingScreenerTemplateFilter);

            // Act: turning the Screener Template filter off (back to null) restores the full category scope.
            vm.SelectedScreenerTemplate = null;
            Assert.Equal(new List<string> { "AAPL", "GOOGL", "MSFT" }, vm.DisplayItems.Select(i => i.Symbol).ToList());
        }
        finally
        {
            vm.Dispose();
        }
    }

    // The ctor's fire-and-forget RefreshExistingTagsAsync posts its callback (which rewrites the tag map,
    // ExistingTags and the filter's suggestion list) on a background thread through the synchronous
    // dispatcher double, while production runs that callback and every read/write of that state on the
    // single UI thread. Route such reads and writes through the double's gate so the test keeps that
    // guarantee instead of racing the rebuild.
    private static void SetFilterText(TickerListViewModel vm, StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService dispatcher, string text)
        => dispatcher.Run(() => vm.TickerTagFilter.FilterText = text);

    [Fact]
    public void TickerTagFilter_FilterText_MatchesTickerSymbolSubstring_AndAppliesAsFilter()
    {
        var mockProvider = new Mock<IMarketDataProvider>();
        mockProvider.Setup(p => p.GetAvailableTickersAsync())
            .ReturnsAsync((IReadOnlyList<string>)new List<string> { "AAPL", "MSFT", "GOOGL" });
        mockProvider.Setup(p => p.GetMetadataAsync(It.IsAny<string>()))
            .ReturnsAsync(TickerMetadata.Unknown);

        var dispatcher = new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService();
        var vm = CreateViewModel(mockProvider.Object, dispatcher);

        try
        {
            // Act: substring "GOOG" only matches the GOOGL symbol.
            SetFilterText(vm, dispatcher, "GOOG");
            Assert.Equal(new List<string> { "GOOGL" }, vm.DisplayItems.Select(i => i.Symbol).ToList());

            // Act: clearing the filter text restores the full All-Tickers scope.
            SetFilterText(vm, dispatcher, "");
            Assert.Equal(new List<string> { "AAPL", "GOOGL", "MSFT" }, vm.DisplayItems.Select(i => i.Symbol).ToList());
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task TickerTagFilter_FilterText_MatchesRegisteredTagSubstring_AndAppliesAsFilter()
    {
        var mockProvider = new Mock<IMarketDataProvider>();
        mockProvider.Setup(p => p.GetAvailableTickersAsync())
            .ReturnsAsync((IReadOnlyList<string>)new List<string> { "AAPL", "MSFT", "GOOGL" });
        mockProvider.Setup(p => p.GetMetadataAsync("AAPL"))
            .ReturnsAsync(TickerMetadata.Unknown with { Tag = "Growth,Tech" });
        mockProvider.Setup(p => p.GetMetadataAsync("MSFT"))
            .ReturnsAsync(TickerMetadata.Unknown with { Tag = "Value" });
        mockProvider.Setup(p => p.GetMetadataAsync("GOOGL"))
            .ReturnsAsync(TickerMetadata.Unknown);

        var dispatcher = new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService();
        var vm = CreateViewModel(mockProvider.Object, dispatcher);

        try
        {
            // RefreshExistingTagsAsync (which populates the tag map ApplyTickerTagFilter reads) runs
            // via Task.Run in production code (fire-and-forget from the ctor's ApplyPendingTickers), so
            // it is not guaranteed complete synchronously after construction even with
            // SynchronousDispatcherService. Await it explicitly for a deterministic test.
            await vm.RefreshExistingTagsAsync();

            // Act: substring "grow" (case-insensitive) matches AAPL's "Growth" tag only.
            SetFilterText(vm, dispatcher, "grow");
            Assert.Equal(new List<string> { "AAPL" }, vm.DisplayItems.Select(i => i.Symbol).ToList());
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task TickerTagFilter_AvailableSuggestions_PopulatedWithTickerSymbolsAndTags_AfterLoad()
    {
        var mockProvider = new Mock<IMarketDataProvider>();
        mockProvider.Setup(p => p.GetAvailableTickersAsync())
            .ReturnsAsync((IReadOnlyList<string>)new List<string> { "AAPL", "MSFT" });
        mockProvider.Setup(p => p.GetMetadataAsync("AAPL"))
            .ReturnsAsync(TickerMetadata.Unknown with { Tag = "Growth" });
        mockProvider.Setup(p => p.GetMetadataAsync("MSFT"))
            .ReturnsAsync(TickerMetadata.Unknown);

        var dispatcher = new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService();
        var vm = CreateViewModel(mockProvider.Object, dispatcher);

        try
        {
            // See comment above: await explicitly so ExistingTags/AvailableSuggestions are
            // deterministically populated before asserting.
            await vm.RefreshExistingTagsAsync();

            var suggestionValues = dispatcher.Run(() => vm.TickerTagFilter.AvailableSuggestions.Select(s => s.Value).ToList());
            Assert.Contains("AAPL", suggestionValues);
            Assert.Contains("MSFT", suggestionValues);
            Assert.Contains("Growth", suggestionValues);
        }
        finally
        {
            vm.Dispose();
        }
    }

    private sealed class FakeTemplateService : ITemplateService
    {
        public List<ScreenerIndicatorTemplate> Templates { get; } = new();

        public Task<T?> GetAsync<T>(TemplateType type, Guid id) where T : TemplateBase => throw new NotImplementedException();

        public Task<IReadOnlyList<T>> GetAllAsync<T>(TemplateType type) where T : TemplateBase
        {
            if (typeof(T) != typeof(ScreenerIndicatorTemplate) || type != TemplateType.Screener)
            {
                return Task.FromResult<IReadOnlyList<T>>(Array.Empty<T>());
            }
            return Task.FromResult<IReadOnlyList<T>>(Templates.Cast<T>().ToList());
        }

        public Task SaveAsync<T>(T template) where T : TemplateBase => throw new NotImplementedException();
        public Task<bool> DeleteAsync(TemplateType type, Guid id) => throw new NotImplementedException();
        public Task<TemplateValidationResult> ValidateAsync<T>(T template) where T : TemplateBase => throw new NotImplementedException();
        public Task EnsureMigratedAsync() => Task.CompletedTask;
    }

    [Fact]
    public void AvailableScreenerTemplates_AlwaysStartsWithOffSentinel_SelectedByDefault()
    {
        var vm = CreateViewModel(new Mock<IMarketDataProvider>().Object);

        try
        {
            Assert.Same(vm.ScreenerTemplateOff, vm.AvailableScreenerTemplates.First());
            Assert.Same(vm.ScreenerTemplateOff, vm.SelectedScreenerTemplate);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void SelectingScreenerTemplateOff_ClearsAnActiveScreenerFilter()
    {
        var watchlist = new StockAnalyzer.Core.Models.Watchlist.WatchlistProfile(
            Guid.NewGuid(), "Tech", StockAnalyzer.Core.Models.IndicatorColor.FromRgb(0, 0, 255), isPortfolio: false,
            items: new List<StockAnalyzer.Core.Models.Watchlist.WatchlistItem>
            {
                new("AAPL", DateTimeOffset.UtcNow),
                new("MSFT", DateTimeOffset.UtcNow),
            });
        var mockWatchlistManager = new Mock<IWatchlistManager>();
        mockWatchlistManager.Setup(w => w.GetAllProfiles())
            .Returns(new List<StockAnalyzer.Core.Models.Watchlist.WatchlistProfile> { watchlist });

        var fakeEvaluator = new FakeScreenerTemplateEvaluator(new HashSet<string> { "AAPL" });

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
            logger: NullLogger<TickerListViewModel>.Instance,
            templateService: null,
            screenerTemplateEvaluator: fakeEvaluator);

        try
        {
            var watchlistNode = vm.Groups
                .First(n => n.Id == TickerListViewModel.WatchlistsCategoryId).Children!.First();
            vm.SelectedNode = watchlistNode;

            vm.SelectedScreenerTemplate = new StockAnalyzer.Core.Models.Templates.ScreenerIndicatorTemplate { Name = "T" };
            Assert.Equal(new List<string> { "AAPL" }, vm.DisplayItems.Select(i => i.Symbol).ToList());

            // Act: picking the "off" sentinel (the only way to clear the filter from the UI) restores
            // the full category scope, same as picking null.
            vm.SelectedScreenerTemplate = vm.ScreenerTemplateOff;

            Assert.Equal(new List<string> { "AAPL", "MSFT" }, vm.DisplayItems.Select(i => i.Symbol).ToList());
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void OpeningScreenerTemplatesDropDown_ReloadsFromTemplateService_WithoutRestart()
    {
        var fakeTemplateService = new FakeTemplateService();
        var mockWatchlistManager = new Mock<IWatchlistManager>();
        mockWatchlistManager.Setup(w => w.GetAllProfiles()).Returns(new List<StockAnalyzer.Core.Models.Watchlist.WatchlistProfile>());

        var vm = new TickerListViewModel(
            marketDataProvider: new Mock<IMarketDataProvider>().Object,
            pythonService: null!,
            messenger: WeakReferenceMessenger.Default,
            dispatcherService: new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService(),
            watchlistManager: mockWatchlistManager.Object,
            portfolioManager: null!,
            dialogService: null!,
            tickerImportService: null!,
            chartSettingsManager: new MockChartSettingsManager(),
            logger: NullLogger<TickerListViewModel>.Instance,
            templateService: fakeTemplateService,
            screenerTemplateEvaluator: null);

        try
        {
            // Only the "off" sentinel exists at startup.
            Assert.Single(vm.AvailableScreenerTemplates);

            // A template is saved from the Screener tab (or elsewhere) later in the same run...
            var newTemplate = new StockAnalyzer.Core.Models.Templates.ScreenerIndicatorTemplate { Name = "New Template" };
            fakeTemplateService.Templates.Add(newTemplate);

            // Act: opening the dropdown re-fetches - no app restart needed.
            vm.IsScreenerTemplatesDropDownOpen = true;

            Assert.Equal(2, vm.AvailableScreenerTemplates.Count);
            Assert.Contains(vm.AvailableScreenerTemplates, t => t.Name == "New Template");
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

    [Fact]
    public void BuildFilterTemplateFromNode_CapturesRulesAndNestedChildren()
    {
        var vm = CreateViewModel(new Mock<IMarketDataProvider>().Object);
        try
        {
            var childSettings = new FilterSettings { Name = "Child", Rules = { new FilterRule { Field = "Tag", Value = "Growth" } } };
            var rootSettings = new FilterSettings { Name = "Root", Rules = { new FilterRule { Field = "Sector", Value = "Tech" } } };
            var rootNode = new FilterNode(rootSettings);
            rootNode.Children!.Add(new FilterNode(childSettings));

            var template = vm.BuildFilterTemplateFromNode(rootNode, "My Template");

            Assert.Equal("My Template", template.Name);
            Assert.Single(template.RootSettings.Rules);
            Assert.Equal("Tech", template.RootSettings.Rules[0].Value);
            Assert.Single(template.RootSettings.Children);
            Assert.Equal("Child", template.RootSettings.Children[0].Name);
            Assert.Equal("Growth", template.RootSettings.Children[0].Rules[0].Value);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void ApplyFilterTemplateAsReplace_OverwritesRulesAndChildren_PreservingTargetNodeIdentity()
    {
        var vm = CreateViewModel(new Mock<IMarketDataProvider>().Object);
        try
        {
            var targetSettings = new FilterSettings { Name = "Target", Rules = { new FilterRule { Field = "Tag", Value = "OldValue" } } };
            var targetNode = new FilterNode(targetSettings);
            var staleChild = new FilterNode(new FilterSettings { Name = "StaleChild" });
            targetNode.Children!.Add(staleChild);
            var originalId = targetNode.Id;

            var template = new StockAnalyzer.Core.Models.Templates.FilterTemplate
            {
                Name = "Replacement",
                RootSettings = new FilterSettings
                {
                    Rules = { new FilterRule { Field = "Tag", Value = "NewValue" } },
                    Children = { new FilterSettings { Name = "NewChild" } }
                }
            };

            vm.ApplyFilterTemplateAsReplace(targetNode, template);

            Assert.Equal(originalId, targetNode.Id); // node identity in the tree must not change
            Assert.Single(targetNode.Settings.Rules);
            Assert.Equal("NewValue", targetNode.Settings.Rules[0].Value);
            Assert.Single(targetNode.Children!);
            Assert.Equal("NewChild", ((FilterNode)targetNode.Children![0]).DisplayName);
            Assert.DoesNotContain(staleChild, targetNode.Children!);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void ApplyFilterTemplateAsAppend_AddsNewChild_PreservingExistingChildren()
    {
        var vm = CreateViewModel(new Mock<IMarketDataProvider>().Object);
        try
        {
            var targetSettings = new FilterSettings { Name = "Target" };
            var targetNode = new FilterNode(targetSettings);
            var existingChild = new FilterNode(new FilterSettings { Name = "Existing" });
            targetNode.Children!.Add(existingChild);

            var template = new StockAnalyzer.Core.Models.Templates.FilterTemplate
            {
                Name = "Appended",
                RootSettings = new FilterSettings
                {
                    Name = "AppendedRoot",
                    Rules = { new FilterRule { Field = "Tag", Value = "Appended" } }
                }
            };

            vm.ApplyFilterTemplateAsAppend(targetNode, template);

            Assert.Equal(2, targetNode.Children!.Count);
            Assert.Contains(existingChild, targetNode.Children!);
            var appendedNode = Assert.IsType<FilterNode>(targetNode.Children![1]);
            Assert.Equal("AppendedRoot", appendedNode.DisplayName);
            Assert.NotEqual(template.RootSettings.Id, appendedNode.Settings.Id); // fresh Id assigned, no collision with the template's own Id
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void CreateFilterNodeFromTemplate_AddsNewFilterNodeUnderParent_AndSelectsIt()
    {
        var vm = CreateViewModel(new Mock<IMarketDataProvider>().Object);
        try
        {
            var parentNode = new AllTickersNode("All Tickers");
            var template = new StockAnalyzer.Core.Models.Templates.FilterTemplate
            {
                Name = "FromTemplate",
                RootSettings = new FilterSettings
                {
                    Name = "TemplateRoot",
                    Rules = { new FilterRule { Field = "Tag", Value = "Growth" } }
                }
            };

            vm.CreateFilterNodeFromTemplate(parentNode, template);

            Assert.Single(parentNode.Children!);
            var newNode = Assert.IsType<FilterNode>(parentNode.Children![0]);
            Assert.Equal("TemplateRoot", newNode.DisplayName);
            Assert.Single(newNode.Settings.Rules);
            Assert.Equal("Growth", newNode.Settings.Rules[0].Value);
            Assert.NotEqual(template.RootSettings.Id, newNode.Settings.Id); // fresh Id assigned, no collision with the template's own Id
            Assert.Same(newNode, vm.SelectedNode);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void ExportFilterSettings_ThenImportOnFreshViewModel_RestoresFilterNodesAndRules()
    {
        // Simulates an app restart: session A's FilterNodes are exported (as done by
        // MainWindowViewModel.CaptureWorkspaceSettings), then imported into a brand-new
        // TickerListViewModel instance (as done by WorkspaceCoordinator.RestoreWatchlistAndColumns
        // on the next launch), to verify the round trip preserves both the node and its Rules.
        var watchlist = new StockAnalyzer.Core.Models.Watchlist.WatchlistProfile(
            Guid.NewGuid(), "Tech", StockAnalyzer.Core.Models.IndicatorColor.FromRgb(0, 0, 255), isPortfolio: false,
            items: new List<StockAnalyzer.Core.Models.Watchlist.WatchlistItem> { new("AAPL", DateTimeOffset.UtcNow) });

        var mockWatchlistManagerA = new Mock<IWatchlistManager>();
        mockWatchlistManagerA.Setup(w => w.GetAllProfiles()).Returns(new List<StockAnalyzer.Core.Models.Watchlist.WatchlistProfile> { watchlist });

        var vmA = new TickerListViewModel(
            marketDataProvider: new Mock<IMarketDataProvider>().Object,
            pythonService: null!,
            messenger: WeakReferenceMessenger.Default,
            dispatcherService: new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService(),
            watchlistManager: mockWatchlistManagerA.Object,
            portfolioManager: null!,
            dialogService: null!,
            tickerImportService: null!,
            chartSettingsManager: new MockChartSettingsManager(),
            logger: NullLogger<TickerListViewModel>.Instance);

        List<FilterSettings> exported;
        try
        {
            var allTickersNode = vmA.Groups.OfType<AllTickersNode>().Single();
            allTickersNode.Children!.Add(new FilterNode(new FilterSettings
            {
                Name = "HighPER",
                Rules = { new FilterRule { Field = "PER", Operator = ">", Value = "20" } }
            }));

            var watchlistNode = vmA.Groups.SelectMany(g => g.Children ?? Enumerable.Empty<TickerGroupNode>()).OfType<WatchlistNode>().Single();
            watchlistNode.Children!.Add(new FilterNode(new FilterSettings
            {
                Name = "GrowthTag",
                Rules = { new FilterRule { Field = "Tag", Value = "Growth" } }
            }));

            exported = vmA.ExportFilterSettings();
        }
        finally
        {
            vmA.Dispose();
        }

        Assert.Equal(2, exported.Count);

        var mockWatchlistManagerB = new Mock<IWatchlistManager>();
        mockWatchlistManagerB.Setup(w => w.GetAllProfiles()).Returns(new List<StockAnalyzer.Core.Models.Watchlist.WatchlistProfile> { watchlist });

        var vmB = new TickerListViewModel(
            marketDataProvider: new Mock<IMarketDataProvider>().Object,
            pythonService: null!,
            messenger: WeakReferenceMessenger.Default,
            dispatcherService: new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService(),
            watchlistManager: mockWatchlistManagerB.Object,
            portfolioManager: null!,
            dialogService: null!,
            tickerImportService: null!,
            chartSettingsManager: new MockChartSettingsManager(),
            logger: NullLogger<TickerListViewModel>.Instance);

        try
        {
            vmB.ImportFilterSettings(exported);

            var restoredAllTickersFilter = vmB.Groups.OfType<AllTickersNode>().Single().Children!.OfType<FilterNode>().Single();
            Assert.Equal("HighPER", restoredAllTickersFilter.DisplayName);
            Assert.Single(restoredAllTickersFilter.Settings.Rules);
            Assert.Equal("PER", restoredAllTickersFilter.Settings.Rules[0].Field);

            var restoredWatchlistFilter = vmB.Groups.SelectMany(g => g.Children ?? Enumerable.Empty<TickerGroupNode>())
                .OfType<WatchlistNode>().Single().Children!.OfType<FilterNode>().Single();
            Assert.Equal("GrowthTag", restoredWatchlistFilter.DisplayName);
            Assert.Single(restoredWatchlistFilter.Settings.Rules);
            Assert.Equal("Growth", restoredWatchlistFilter.Settings.Rules[0].Value);
        }
        finally
        {
            vmB.Dispose();
        }
    }

    [Fact]
    public void MultiSyncProgressViewModel_Reopened_ResetsDeleteLatestBarsCountToZero()
    {
        var vm = CreateViewModel(new Mock<IMarketDataProvider>().Object);
        try
        {
            // 1st session: user opens dialog and sets DeleteLatestBarsCount to 5
            var syncVm1 = new MultiSyncProgressViewModel(vm);
            syncVm1.DeleteLatestBarsCount = 5;
            Assert.Equal(5, vm.DeleteLatestBarsCount);

            // Close 1st session
            syncVm1.CloseCommand.Execute(null);

            // Reopening "Sync Progress" (new session created)
            var syncVm2 = new MultiSyncProgressViewModel(vm);
            Assert.Equal(0, syncVm2.DeleteLatestBarsCount);
            Assert.Equal(0, vm.DeleteLatestBarsCount);
        }
        finally
        {
            vm.Dispose();
        }
    }
}
