using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.ViewModels.TickerList;
using StockAnalyzer.Avalonia.ViewModels.Watchlist;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.Core.Services.Screener;
using StockAnalyzer.Core.Services.Watchlist;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

[Collection("MessengerSharedState")]
public sealed class TickerListViewModelRsRatingTests
{
    private sealed class StubRsRatingClosesProvider : IRsRatingClosesProvider
    {
        private readonly IReadOnlyDictionary<string, RsRatingCloses> _closes;
        private readonly bool _shouldThrow;

        public StubRsRatingClosesProvider(IReadOnlyDictionary<string, RsRatingCloses> closes, bool shouldThrow = false)
        {
            _closes = closes;
            _shouldThrow = shouldThrow;
        }

        public int CallCount { get; private set; }
        public IReadOnlyList<string>? LastUniverse { get; private set; }
        public TaskCompletionSource<bool>? BatchCompleted { get; set; }

        public Task<IReadOnlyDictionary<string, RsRatingCloses>> GetRsRatingClosesAsync(
            IReadOnlyList<string> symbols,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastUniverse = symbols.ToList();
            if (_shouldThrow)
            {
                BatchCompleted?.TrySetResult(true);
                throw new InvalidOperationException("Simulated RS Rating provider failure");
            }

            var result = new Dictionary<string, RsRatingCloses>(StringComparer.OrdinalIgnoreCase);
            foreach (var symbol in symbols)
            {
                if (_closes.TryGetValue(symbol, out var c))
                {
                    result[symbol] = c;
                }
            }

            BatchCompleted?.TrySetResult(true);
            return Task.FromResult<IReadOnlyDictionary<string, RsRatingCloses>>(result);
        }
    }

    private sealed class FakeScreenerService : IScreenerService
    {
        public IScreenerEvaluationContext? CapturedContext { get; private set; }

        public Task<List<string>> ScreenAsync(ScreeningCriteria criteria, IProgress<int> progress, CancellationToken ct)
            => Task.FromResult(new List<string>());

        public Task<List<string>> ScreenAsync(List<string> symbols, IScreeningCondition condition, TimeFrame timeFrame, IProgress<int> progress, CancellationToken ct)
            => Task.FromResult(symbols.ToList());

        public Task<List<string>> ScreenAsync(
            List<string> symbols,
            IScreeningCondition condition,
            TimeFrame timeFrame,
            IScreenerSecondaryCandles? secondary,
            IScreenerEvaluationContext? evaluationContext,
            IProgress<int> progress,
            CancellationToken ct)
        {
            CapturedContext = evaluationContext;
            var hits = new List<string>();
            foreach (var symbol in symbols)
            {
                if (evaluationContext?.GetRsRating(symbol) is int rating && rating > 50)
                {
                    hits.Add(symbol);
                }
            }

            return Task.FromResult(hits);
        }
    }

    private static TickerListViewModel CreateListViewModel(
        IMarketDataProvider marketDataProvider,
        IWatchlistManager watchlistManager,
        IDialogService? dialogService = null,
        IRsRatingClosesProvider? rsProvider = null)
    {
        return new TickerListViewModel(
            marketDataProvider: marketDataProvider,
            pythonService: null!,
            messenger: WeakReferenceMessenger.Default,
            dispatcherService: new SynchronousDispatcherService(),
            watchlistManager: watchlistManager,
            portfolioManager: null!,
            dialogService: dialogService!,
            tickerImportService: null!,
            chartSettingsManager: new MockChartSettingsManager(),
            logger: NullLogger<TickerListViewModel>.Instance,
            rsRatingClosesProvider: rsProvider);
    }

    [Fact]
    public void WatchlistItemViewModel_And_TickerRow_ExposeRsRatingAndDisplayFallback()
    {
        var item = new WatchlistItemViewModel("AAPL", "Apple", "", "", 100m, 101m, 99m, 100m, 1000L, 0d);
        Assert.Equal("-", item.DisplayRsRating);
        Assert.Null(item.GetRawValue("RsRating"));
        Assert.Null(item.GetRawValue("RS Rating"));

        item.RsRating = 87;
        Assert.Equal("87", item.DisplayRsRating);
        Assert.Equal(87, item.GetRawValue("RsRating"));
        Assert.Equal(87, item.GetRawValue("RS Rating"));

        var row = new TickerRow { Symbol = "AAPL", Name = "Apple", RsRating = 87 };
        Assert.Equal(87, row.RsRating);
        Assert.Equal(87, row.GetPropertyValue("RS Rating"));
    }

    [Fact]
    public async Task StartHydration_KeepsRsRatingInvariantAcrossTagFilter_BecauseUniverseIsPreFilter()
    {
        var mockMarket = new Mock<IMarketDataProvider>();
        mockMarket.Setup(m => m.GetAvailableTickersAsync()).ReturnsAsync(new List<string> { "AAPL", "MSFT", "NVDA" });
        mockMarket.Setup(m => m.GetMetadataAsync(It.IsAny<string>())).ReturnsAsync((string s) =>
            s == "MSFT" ? new TickerMetadata("MSFT", "Microsoft", "Tech", "Soft", "US", "EQUITY") { Tag = "Keep" } :
            TickerMetadata.Unknown);
        mockMarket.Setup(m => m.GetTickersDataAsync(It.IsAny<string>(), TimeFrame.D1))
            .ReturnsAsync(new List<CandleData> { new(new DateTime(2024, 1, 2), 100m, 101m, 99m, 100m, 1000L) });
        mockMarket.Setup(m => m.GetTimeSeriesLastUpdatedAsync(It.IsAny<string>()))
            .ReturnsAsync(DateTimeOffset.UtcNow);

        var mockWatchlist = new Mock<IWatchlistManager>();
        mockWatchlist.Setup(w => w.GetAllProfiles()).Returns(new List<WatchlistProfile>());

        var rsProvider = new StubRsRatingClosesProvider(new Dictionary<string, RsRatingCloses>
        {
            ["AAPL"] = new(250m, 100m, 100m, 100m, 100m), // 99
            ["MSFT"] = new(150m, 100m, 100m, 100m, 100m), // 50
            ["NVDA"] = new(90m, 100m, 100m, 100m, 100m)   // 1
        });

        rsProvider.BatchCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var vm = CreateListViewModel(mockMarket.Object, mockWatchlist.Object, rsProvider: rsProvider);

        await WaitForRsRatingHydrationAsync(vm, rsProvider.BatchCompleted.Task, "AAPL");

        Assert.Equal(99, vm.DisplayItems.Single(i => i.Symbol == "AAPL").RsRating);
        Assert.Equal(50, vm.DisplayItems.Single(i => i.Symbol == "MSFT").RsRating);
        Assert.Equal(1, vm.DisplayItems.Single(i => i.Symbol == "NVDA").RsRating);

        // Apply secondary Tag filter that keeps only MSFT visible
        rsProvider.BatchCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.TickerTagFilter.FilterText = "MSFT";
        await WaitForRsRatingHydrationAsync(vm, rsProvider.BatchCompleted.Task, "MSFT");

        Assert.Single(vm.DisplayItems);
        Assert.Equal("MSFT", vm.DisplayItems[0].Symbol);
        Assert.Equal(50, vm.DisplayItems[0].RsRating);
        Assert.Equal(new[] { "AAPL", "MSFT", "NVDA" }, rsProvider.LastUniverse!.OrderBy(s => s, StringComparer.Ordinal));
    }

    [Fact]
    public async Task ExportGroupColumnDataCommand_PopulatesRsRatingAndHandlesProviderFailureGracefully()
    {
        var watchlist = new WatchlistProfile(
            Guid.NewGuid(),
            "Tech",
            IndicatorColor.FromRgb(0, 0, 255),
            isPortfolio: false,
            items: new List<WatchlistItem>
            {
                new("AAPL", DateTimeOffset.UtcNow),
                new("MSFT", DateTimeOffset.UtcNow)
            });

        var tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sa_rs_export_{Guid.NewGuid():N}.csv");
        var mockDialog = new Mock<IDialogService>();
        mockDialog
            .Setup(d => d.ShowSaveFileDialogAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string[]>(), null))
            .ReturnsAsync(tempFile);
        var mockWatchlist = new Mock<IWatchlistManager>();
        mockWatchlist.Setup(w => w.GetAllProfiles()).Returns(new List<WatchlistProfile> { watchlist });

        var mockMarket = new Mock<IMarketDataProvider>();
        mockMarket.Setup(m => m.GetAvailableTickersAsync()).ReturnsAsync(new List<string>());
        mockMarket.Setup(m => m.GetMetadataAsync(It.IsAny<string>())).ReturnsAsync(TickerMetadata.Unknown);
        mockMarket.Setup(m => m.GetTickersDataAsync(It.IsAny<string>(), TimeFrame.D1))
            .ReturnsAsync(new List<CandleData> { new(new DateTime(2024, 1, 2), 100m, 101m, 99m, 100m, 1000L) });
        mockMarket.Setup(m => m.GetTimeSeriesLastUpdatedAsync(It.IsAny<string>())).ReturnsAsync(DateTimeOffset.UtcNow);

        var rsProvider = new StubRsRatingClosesProvider(new Dictionary<string, RsRatingCloses>
        {
            ["AAPL"] = new(200m, 100m, 100m, 100m, 100m), // 99
            ["MSFT"] = new(80m, 100m, 100m, 100m, 100m)   // 1
        });

        using var vm = CreateListViewModel(mockMarket.Object, mockWatchlist.Object, mockDialog.Object, rsProvider);
        try
        {
            vm.SetActiveColumns(new[] { "Symbol", "RsRating" });
            var node = vm.Groups.SelectMany(g => g.Children ?? Enumerable.Empty<TickerGroupNode>()).OfType<WatchlistNode>().Single();

            await vm.ExportGroupColumnDataCommand.ExecuteAsync(node);

            var lines = await System.IO.File.ReadAllLinesAsync(tempFile);
            Assert.Equal(3, lines.Length);
            Assert.Contains("RS Rating", lines[0]);
            Assert.Contains("AAPL,99", lines);
            Assert.Contains("MSFT,1", lines);
        }
        finally
        {
            if (System.IO.File.Exists(tempFile)) System.IO.File.Delete(tempFile);
        }

        // Now verify provider exception clears/leaves null cleanly without throwing
        var failingProvider = new StubRsRatingClosesProvider(new Dictionary<string, RsRatingCloses>(), shouldThrow: true);
        using var failingVm = CreateListViewModel(mockMarket.Object, mockWatchlist.Object, mockDialog.Object, failingProvider);
        try
        {
            failingVm.SetActiveColumns(new[] { "Symbol", "RsRating" });
            var node = failingVm.Groups.SelectMany(g => g.Children ?? Enumerable.Empty<TickerGroupNode>()).OfType<WatchlistNode>().Single();
            await failingVm.ExportGroupColumnDataCommand.ExecuteAsync(node);

            var lines = await System.IO.File.ReadAllLinesAsync(tempFile);
            Assert.Equal(3, lines.Length);
            Assert.Contains("AAPL,", lines);
            Assert.Equal(1, failingProvider.CallCount);
        }
        finally
        {
            if (System.IO.File.Exists(tempFile)) System.IO.File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ScreenerViewModel_ScanCommand_OnDemandZeroCallVsPopulatedRsRating()
    {
        var mockMarket = new Mock<IMarketDataProvider>();
        mockMarket.Setup(m => m.GetAvailableTickersAsync()).ReturnsAsync(new List<string> { "AAPL", "MSFT" });
        mockMarket.Setup(m => m.GetMetadataAsync(It.IsAny<string>())).ReturnsAsync(TickerMetadata.Unknown);
        mockMarket.Setup(m => m.GetTickersDataAsync(It.IsAny<string>(), TimeFrame.D1))
            .ReturnsAsync(new List<CandleData>
            {
                new(new DateTime(2024, 1, 1), 100m, 101m, 99m, 100m, 1000L),
                new(new DateTime(2024, 1, 2), 105m, 106m, 104m, 105m, 1000L)
            });

        var rsProvider = new StubRsRatingClosesProvider(new Dictionary<string, RsRatingCloses>
        {
            ["AAPL"] = new(220m, 100m, 100m, 100m, 100m), // 99
            ["MSFT"] = new(90m, 100m, 100m, 100m, 100m)   // 1
        });
        var fakeScreener = new FakeScreenerService();

        using var vm = new ScreenerViewModel(
            screenerService: fakeScreener,
            marketDataProvider: mockMarket.Object,
            rsRatingClosesProvider: rsProvider);
        vm.TargetSymbols.ReplaceRange(new[] { "AAPL", "MSFT" });

        // 1. Condition does NOT reference RS Rating and RsRating is not in ActiveColumns -> 0 calls
        vm.IndicatorRegistrationViewModel.RegisteredEntries.Clear();
        vm.IndicatorRegistrationViewModel.RegisteredEntries.Add(new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig { CategoryType = ScreenerItemCategoryType.Column, OutputName = "Close" },
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 50m,
            IsEnabled = true
        });
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.Equal(0, rsProvider.CallCount);
        Assert.Null(fakeScreener.CapturedContext);

        // 2. Replace condition with RS Rating > 50 -> computes once and sets ResultItems[0].RsRating
        vm.IndicatorRegistrationViewModel.RegisteredEntries.Clear();
        vm.IndicatorRegistrationViewModel.RegisteredEntries.Add(new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig
            {
                CategoryType = ScreenerItemCategoryType.Column,
                CustomDisplayName = ScreenerRsRatingInspector.ColumnDisplayName,
                OutputName = ScreenerRsRatingInspector.ColumnMemberName
            },
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 50m,
            IsEnabled = true
        });
        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Equal(1, rsProvider.CallCount);
        Assert.NotNull(fakeScreener.CapturedContext);
        Assert.Single(vm.ResultItems);
        Assert.Equal("AAPL", vm.ResultItems[0].Symbol);
        Assert.Equal(99, vm.ResultItems[0].RsRating);
        Assert.Equal("99", vm.ResultItems[0].DisplayRsRating);
    }

    [Fact]
    public async Task ScreenerViewModel_RightHandRsRatingColumn_PopulatesRightHandResultColumn()
    {
        var mockMarket = new Mock<IMarketDataProvider>();
        mockMarket.Setup(m => m.GetAvailableTickersAsync()).ReturnsAsync(new List<string> { "AAPL", "MSFT" });
        mockMarket.Setup(m => m.GetMetadataAsync(It.IsAny<string>())).ReturnsAsync(TickerMetadata.Unknown);
        mockMarket.Setup(m => m.GetTickersDataAsync(It.IsAny<string>(), TimeFrame.D1))
            .ReturnsAsync(new List<CandleData>
            {
                new(new DateTime(2024, 1, 1), 100m, 101m, 99m, 100m, 1000L),
                new(new DateTime(2024, 1, 2), 105m, 106m, 104m, 105m, 1000L)
            });

        var rsProvider = new StubRsRatingClosesProvider(new Dictionary<string, RsRatingCloses>
        {
            ["AAPL"] = new(220m, 100m, 100m, 100m, 100m), // 99
            ["MSFT"] = new(90m, 100m, 100m, 100m, 100m)   // 1
        });
        var realScreener = new FakeScreenerService();

        using var vm = new ScreenerViewModel(
            screenerService: realScreener,
            marketDataProvider: mockMarket.Object,
            rsRatingClosesProvider: rsProvider);
        vm.TargetSymbols.ReplaceRange(new[] { "AAPL", "MSFT" });

        // Place RS Rating on the RightHand side of a comparison (LeftHand = Close, RightHand = RS Rating Column).
        vm.IndicatorRegistrationViewModel.RegisteredEntries.Clear();
        vm.IndicatorRegistrationViewModel.RegisteredEntries.Add(new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig { CategoryType = ScreenerItemCategoryType.Column, OutputName = "Close" },
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.Indicator,
            RightHand = new ScreenerIndicatorSideConfig
            {
                CategoryType = ScreenerItemCategoryType.Column,
                CustomDisplayName = ScreenerRsRatingInspector.ColumnDisplayName,
                OutputName = ScreenerRsRatingInspector.ColumnMemberName
            },
            CategoryType = ScreenerItemCategoryType.Column,
            IsEnabled = true
        });

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Single(vm.ResultItems);
        var resultItem = vm.ResultItems[0];
        Assert.Equal("AAPL", resultItem.Symbol);
        var rightHandCol = vm.ActiveColumns.Single(c => c.MemberName.StartsWith("Indicator_", StringComparison.Ordinal) && c.HeaderKey == ScreenerRsRatingInspector.ColumnDisplayName);
        Assert.Equal(99m, resultItem.GetRawValue(rightHandCol.MemberName));
    }

    [Fact]
    public async Task SwitchingWatchlist_ClearsStaleRsRatingBeforeNewBatchSettles()
    {
        var watchlistA = new WatchlistProfile(
            Guid.NewGuid(),
            "TechA",
            IndicatorColor.FromRgb(0, 0, 255),
            isPortfolio: false,
            items: new List<WatchlistItem>
            {
                new("AAPL", DateTimeOffset.UtcNow),
                new("MSFT", DateTimeOffset.UtcNow)
            });
        var watchlistB = new WatchlistProfile(
            Guid.NewGuid(),
            "TechB",
            IndicatorColor.FromRgb(0, 128, 0),
            isPortfolio: false,
            items: new List<WatchlistItem>
            {
                new("AAPL", DateTimeOffset.UtcNow),
                new("IBM", DateTimeOffset.UtcNow),
                new("NVDA", DateTimeOffset.UtcNow)
            });

        var mockMarket = new Mock<IMarketDataProvider>();
        mockMarket.Setup(m => m.GetAvailableTickersAsync()).ReturnsAsync(new List<string> { "AAPL", "MSFT", "IBM", "NVDA" });
        mockMarket.Setup(m => m.GetMetadataAsync(It.IsAny<string>())).ReturnsAsync(TickerMetadata.Unknown);

        var gateB = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool blockHydration = false;
        mockMarket.Setup(m => m.GetTickersDataAsync(It.IsAny<string>(), TimeFrame.D1))
            .Returns(async () =>
            {
                if (blockHydration)
                {
                    await gateB.Task;
                }
                return new List<CandleData> { new(new DateTime(2024, 1, 2), 100m, 101m, 99m, 100m, 1000L) };
            });
        mockMarket.Setup(m => m.GetTimeSeriesLastUpdatedAsync(It.IsAny<string>())).ReturnsAsync(DateTimeOffset.UtcNow);

        var mockWatchlist = new Mock<IWatchlistManager>();
        mockWatchlist.Setup(w => w.GetAllProfiles()).Returns(new List<WatchlistProfile> { watchlistA, watchlistB });

        var rsProvider = new StubRsRatingClosesProvider(new Dictionary<string, RsRatingCloses>
        {
            ["AAPL"] = new(150m, 100m, 100m, 100m, 100m),
            ["MSFT"] = new(200m, 100m, 100m, 100m, 100m), // MSFT > AAPL in A -> AAPL = 1 in A
            ["IBM"] = new(90m, 100m, 100m, 100m, 100m),
            ["NVDA"] = new(80m, 100m, 100m, 100m, 100m)    // AAPL > IBM > NVDA in B -> AAPL = 99 in B
        });

        rsProvider.BatchCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var vm = CreateListViewModel(mockMarket.Object, mockWatchlist.Object, rsProvider: rsProvider);
        var watchlists = vm.Groups.SelectMany(g => g.Children ?? Enumerable.Empty<TickerGroupNode>()).OfType<WatchlistNode>().ToList();
        var nodeA = watchlists.Single(w => w.DisplayName == "TechA");
        var nodeB = watchlists.Single(w => w.DisplayName == "TechB");

        rsProvider.BatchCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.SelectedNode = nodeA;
        await WaitForRsRatingHydrationAsync(vm, rsProvider.BatchCompleted.Task, "AAPL");
        Assert.Equal(1, vm.DisplayItems.Single(i => i.Symbol == "AAPL").RsRating);

        // Switching to TechB (different universe) must clear AAPL's stale rating of 1 immediately while TechB hydrates.
        blockHydration = true;
        rsProvider.BatchCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.SelectedNode = nodeB;
        var aaplDuringHydration = vm.DisplayItems.Single(i => i.Symbol == "AAPL");
        Assert.Null(aaplDuringHydration.RsRating);
        Assert.Equal("-", aaplDuringHydration.DisplayRsRating);

        // Releasing hydration settles TechB's own universe rank (99).
        gateB.TrySetResult(true);
        await WaitForRsRatingHydrationAsync(vm, rsProvider.BatchCompleted.Task, "AAPL");
        Assert.Equal(99, vm.DisplayItems.Single(i => i.Symbol == "AAPL").RsRating);
    }

    [Fact]
    public async Task ExportGroupColumnDataCommand_DoesNotOverwriteDisplayedWatchlistRsRating()
    {
        var watchlistA = new WatchlistProfile(
            Guid.NewGuid(),
            "TechA",
            IndicatorColor.FromRgb(0, 0, 255),
            isPortfolio: false,
            items: new List<WatchlistItem>
            {
                new("AAPL", DateTimeOffset.UtcNow),
                new("MSFT", DateTimeOffset.UtcNow)
            });
        var watchlistB = new WatchlistProfile(
            Guid.NewGuid(),
            "TechB",
            IndicatorColor.FromRgb(0, 128, 0),
            isPortfolio: false,
            items: new List<WatchlistItem>
            {
                new("AAPL", DateTimeOffset.UtcNow),
                new("IBM", DateTimeOffset.UtcNow),
                new("NVDA", DateTimeOffset.UtcNow)
            });

        var tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sa_rs_group_isolation_{Guid.NewGuid():N}.csv");
        var mockDialog = new Mock<IDialogService>();
        mockDialog
            .Setup(d => d.ShowSaveFileDialogAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string[]>(), null))
            .ReturnsAsync(tempFile);

        var mockMarket = new Mock<IMarketDataProvider>();
        mockMarket.Setup(m => m.GetAvailableTickersAsync()).ReturnsAsync(new List<string> { "AAPL", "MSFT", "IBM", "NVDA" });
        mockMarket.Setup(m => m.GetMetadataAsync(It.IsAny<string>())).ReturnsAsync(TickerMetadata.Unknown);
        mockMarket.Setup(m => m.GetTickersDataAsync(It.IsAny<string>(), TimeFrame.D1))
            .ReturnsAsync(new List<CandleData> { new(new DateTime(2024, 1, 2), 100m, 101m, 99m, 100m, 1000L) });
        mockMarket.Setup(m => m.GetTimeSeriesLastUpdatedAsync(It.IsAny<string>())).ReturnsAsync(DateTimeOffset.UtcNow);

        var mockWatchlist = new Mock<IWatchlistManager>();
        mockWatchlist.Setup(w => w.GetAllProfiles()).Returns(new List<WatchlistProfile> { watchlistA, watchlistB });

        var rsProvider = new StubRsRatingClosesProvider(new Dictionary<string, RsRatingCloses>
        {
            ["AAPL"] = new(150m, 100m, 100m, 100m, 100m),
            ["MSFT"] = new(200m, 100m, 100m, 100m, 100m), // In TechA: AAPL = 1
            ["IBM"] = new(90m, 100m, 100m, 100m, 100m),
            ["NVDA"] = new(80m, 100m, 100m, 100m, 100m)    // In TechB: AAPL = 99
        });

        using var vm = CreateListViewModel(mockMarket.Object, mockWatchlist.Object, mockDialog.Object, rsProvider);
        try
        {
            vm.SetActiveColumns(new[] { "Symbol", "RsRating" });
            var watchlists = vm.Groups.SelectMany(g => g.Children ?? Enumerable.Empty<TickerGroupNode>()).OfType<WatchlistNode>().ToList();
            var nodeA = watchlists.Single(w => w.DisplayName == "TechA");
            var nodeB = watchlists.Single(w => w.DisplayName == "TechB");

            rsProvider.BatchCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.SelectedNode = nodeA;
            await WaitForRsRatingHydrationAsync(vm, rsProvider.BatchCompleted.Task, "AAPL");
            Assert.Equal(1, vm.DisplayItems.Single(i => i.Symbol == "AAPL").RsRating);

            // Export unselected group TechB: CSV must contain TechB rank (99), while displayed TechA item keeps rank 1.
            await vm.ExportGroupColumnDataCommand.ExecuteAsync(nodeB);

            var lines = await System.IO.File.ReadAllLinesAsync(tempFile);
            Assert.Contains("AAPL,99", lines);
            Assert.Equal(1, vm.DisplayItems.Single(i => i.Symbol == "AAPL").RsRating);
        }
        finally
        {
            if (System.IO.File.Exists(tempFile)) System.IO.File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ScreenerViewModel_RsRatingWithPositiveOffset_ReturnsNullInDynamicColumn()
    {
        var mockMarket = new Mock<IMarketDataProvider>();
        mockMarket.Setup(m => m.GetAvailableTickersAsync()).ReturnsAsync(new List<string> { "AAPL", "MSFT" });
        mockMarket.Setup(m => m.GetMetadataAsync(It.IsAny<string>())).ReturnsAsync(TickerMetadata.Unknown);
        mockMarket.Setup(m => m.GetTickersDataAsync(It.IsAny<string>(), TimeFrame.D1))
            .ReturnsAsync(new List<CandleData>
            {
                new(new DateTime(2024, 1, 1), 100m, 101m, 99m, 100m, 1000L),
                new(new DateTime(2024, 1, 2), 105m, 106m, 104m, 105m, 1000L)
            });

        var rsProvider = new StubRsRatingClosesProvider(new Dictionary<string, RsRatingCloses>
        {
            ["AAPL"] = new(220m, 100m, 100m, 100m, 100m), // 99
            ["MSFT"] = new(90m, 100m, 100m, 100m, 100m)   // 1
        });
        var realScreener = new FakeScreenerService();

        using var vm = new ScreenerViewModel(
            screenerService: realScreener,
            marketDataProvider: mockMarket.Object,
            rsRatingClosesProvider: rsProvider);
        vm.TargetSymbols.ReplaceRange(new[] { "AAPL", "MSFT" });

        // OR combination: entry 1 matches all symbols (Close > 50); entry 2 puts Offset = 1 on RS Rating.
        vm.IndicatorRegistrationViewModel.RegisteredEntries.Clear();
        vm.IndicatorRegistrationViewModel.RegisteredEntries.Add(new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig { CategoryType = ScreenerItemCategoryType.Column, OutputName = "Close" },
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 50m,
            CategoryType = ScreenerItemCategoryType.Column,
            LogicalOperator = LogicalOperator.Or,
            IsEnabled = true
        });
        vm.IndicatorRegistrationViewModel.RegisteredEntries.Add(new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig
            {
                CategoryType = ScreenerItemCategoryType.Column,
                CustomDisplayName = ScreenerRsRatingInspector.ColumnDisplayName,
                OutputName = ScreenerRsRatingInspector.ColumnMemberName,
                Offset = 1
            },
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 50m,
            CategoryType = ScreenerItemCategoryType.Column,
            IsEnabled = true
        });

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Single(vm.ResultItems);
        var rsOffsetCol = vm.ActiveColumns.Single(c =>
            c.MemberName.StartsWith("Indicator_", StringComparison.Ordinal) &&
            c.HeaderKey.Contains(ScreenerRsRatingInspector.ColumnDisplayName, StringComparison.Ordinal));
        var aapl = vm.ResultItems[0];
        Assert.Equal("AAPL", aapl.Symbol);
        Assert.Equal(99, aapl.RsRating);
        Assert.Null(aapl.GetRawValue(rsOffsetCol.MemberName));
    }

    private static async Task WaitForRsRatingHydrationAsync(TickerListViewModel vm, Task batchCompletedTask, string probeSymbol)
    {
        await Task.WhenAny(batchCompletedTask, Task.Delay(5000));
        for (int i = 0; i < 200; i++)
        {
            var probe = vm.DisplayItems.FirstOrDefault(x => string.Equals(x.Symbol, probeSymbol, StringComparison.OrdinalIgnoreCase));
            if (!vm.IsHydrating && probe?.RsRating != null)
            {
                return;
            }
            await Task.Delay(25);
        }
    }
}
