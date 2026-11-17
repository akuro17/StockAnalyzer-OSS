using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.ViewModels.Watchlist;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// After a sync from the Tickers tab, the data on disk (daily, and the weekly/monthly files rebuilt from it) is new, so every in-memory cache
/// built from the old files must be dropped, exactly as <see cref="TickerSyncService"/> does after a chart-side sync.
/// </summary>
public class TickerListViewModelSyncCacheInvalidationTests
{
    private const string Symbol = "AAPL";

    private sealed class StubSyncSession : IMultiSyncProgressSession
    {
        private readonly Func<MultiSyncProgressViewModel> _create;
        private MultiSyncProgressViewModel? _viewModel;

        public StubSyncSession(Func<MultiSyncProgressViewModel> create) => _create = create;

        public MultiSyncProgressViewModel ViewModel => _viewModel ??= _create();

        public void Show(object? owner = null) { }

        public void Close() { }

        public void Dispose() { }
    }

    private sealed class Harness
    {
        public TickerListViewModel Vm = null!;
        public StubSyncSession Session = null!;
        public Mock<ITimeFrameManager> TimeFrames = new();
        public Mock<IMarketBreadthSeriesProvider> Breadth = new();
        public Mock<IMarketDataProvider> Provider = new();
        public Mock<IPythonService> Python = new();
        public List<string> Calls = new();
    }

    private static Harness CreateHarness()
    {
        var h = new Harness();
        var watchlist = new Mock<IWatchlistManager>();
        watchlist.Setup(w => w.GetAllProfiles()).Returns(new List<StockAnalyzer.Core.Models.Watchlist.WatchlistProfile>());
        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.GetMainWindowOwner()).Returns((object?)null);
        dialog.Setup(d => d.CreateMultiSyncProgressSession()).Returns(() => h.Session);

        h.Python
            .Setup(p => p.RunUpdatePipelineAsync(It.IsAny<string?>(), It.IsAny<SyncSessionConfig>(), It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()))
            .Callback(() => h.Calls.Add("pipeline"))
            .Returns(Task.CompletedTask);
        h.TimeFrames.Setup(t => t.InvalidateCache(It.IsAny<string?>())).Callback(() => h.Calls.Add("timeframes"));
        h.Breadth.Setup(b => b.ClearCache()).Callback(() => h.Calls.Add("breadth"));
        h.Provider.Setup(p => p.InvalidateMetadataCache(It.IsAny<string>())).Callback(() => h.Calls.Add("metadata"));
        h.Provider.Setup(p => p.DeleteLatestTickerDataAsync(It.IsAny<string>(), It.IsAny<int>())).Callback(() => h.Calls.Add("delete")).ReturnsAsync(0);

        h.Vm = new TickerListViewModel(
            marketDataProvider: h.Provider.Object,
            pythonService: h.Python.Object,
            messenger: new WeakReferenceMessenger(),
            dispatcherService: new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService(),
            watchlistManager: watchlist.Object,
            portfolioManager: null!,
            dialogService: dialog.Object,
            tickerImportService: null!,
            chartSettingsManager: new MockChartSettingsManager(),
            logger: NullLogger<TickerListViewModel>.Instance,
            timeFrameManager: h.TimeFrames.Object,
            marketBreadthSeriesProvider: h.Breadth.Object);
        h.Vm.IsAutoSyncEnabled = false; // the test starts the item itself
        h.Session = new StubSyncSession(() => new MultiSyncProgressViewModel(h.Vm));

        var item = new WatchlistItemViewModel(Symbol, "Apple Inc.", "", "", 0, 0, 0, 0, 0, 0) { IsChecked = true };
        h.Vm.DisplayItems.Add(item);
        return h;
    }

    private static async Task SyncTheCheckedTickerAsync(Harness h, int deleteLatestBars = 0)
    {
        await h.Vm.SyncSelectedTickersCommand.ExecuteAsync(null);

        var sync = h.Session.ViewModel;
        sync.DeleteLatestBarsCount = deleteLatestBars;
        var item = Assert.Single(sync.Items);
        await item.RetryCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task SyncFromTickersTab_AfterThePipeline_DropsTimeFrameAndMarketBreadthCaches()
    {
        var h = CreateHarness();
        try
        {
            await SyncTheCheckedTickerAsync(h);

            Assert.Contains("pipeline", h.Calls);
            h.TimeFrames.Verify(t => t.InvalidateCache(Symbol), Times.Once);
            h.Breadth.Verify(b => b.ClearCache(), Times.Once);

            // Order: the caches are dropped after the files were rewritten, not before.
            Assert.True(h.Calls.LastIndexOf("pipeline") < h.Calls.LastIndexOf("timeframes"));
            Assert.True(h.Calls.LastIndexOf("pipeline") < h.Calls.LastIndexOf("breadth"));
        }
        finally
        {
            h.Vm.Dispose();
        }
    }

    [Fact]
    public async Task SyncFromTickersTab_AfterDeletingTheLatestBars_AlsoDropsTheCachesBeforeTheSync()
    {
        var h = CreateHarness();
        try
        {
            await SyncTheCheckedTickerAsync(h, deleteLatestBars: 1);

            int delete = h.Calls.IndexOf("delete");
            int pipeline = h.Calls.IndexOf("pipeline");
            Assert.True(delete >= 0 && pipeline > delete);

            // One drop right after the delete (before the pipeline) and one after the pipeline.
            h.TimeFrames.Verify(t => t.InvalidateCache(Symbol), Times.Exactly(2));
            h.Breadth.Verify(b => b.ClearCache(), Times.Exactly(2));
            Assert.True(h.Calls.IndexOf("timeframes") > delete && h.Calls.IndexOf("timeframes") < pipeline);
        }
        finally
        {
            h.Vm.Dispose();
        }
    }

    [Fact]
    public async Task SyncFromTickersTab_WithoutCacheServices_StillCompletes()
    {
        var h = CreateHarness();
        // Same view model shape as before the caches were injected: both services are optional.
        var watchlist = new Mock<IWatchlistManager>();
        watchlist.Setup(w => w.GetAllProfiles()).Returns(new List<StockAnalyzer.Core.Models.Watchlist.WatchlistProfile>());
        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.GetMainWindowOwner()).Returns((object?)null);
        dialog.Setup(d => d.CreateMultiSyncProgressSession()).Returns(() => h.Session);
        h.Vm.Dispose();
        h.Vm = new TickerListViewModel(
            marketDataProvider: h.Provider.Object,
            pythonService: h.Python.Object,
            messenger: new WeakReferenceMessenger(),
            dispatcherService: new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService(),
            watchlistManager: watchlist.Object,
            portfolioManager: null!,
            dialogService: dialog.Object,
            tickerImportService: null!,
            chartSettingsManager: new MockChartSettingsManager(),
            logger: NullLogger<TickerListViewModel>.Instance);
        h.Vm.IsAutoSyncEnabled = false;
        h.Vm.DisplayItems.Add(new WatchlistItemViewModel(Symbol, "Apple Inc.", "", "", 0, 0, 0, 0, 0, 0) { IsChecked = true });
        try
        {
            await SyncTheCheckedTickerAsync(h);

            Assert.Contains("pipeline", h.Calls);
            Assert.Contains("metadata", h.Calls);
            Assert.DoesNotContain("timeframes", h.Calls);
            Assert.DoesNotContain("breadth", h.Calls);
        }
        finally
        {
            h.Vm.Dispose();
        }
    }
}
