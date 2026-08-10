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
