using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.ViewModels.TickerList;
using StockAnalyzer.Avalonia.ViewModels.Watchlist;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Evaluation;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

[Collection("MessengerSharedState")]
public sealed class TickerGroupParityTests
{
    private sealed class MemoryWorkspaceSource : IReadOnlyWorkspaceSource
    {
        private readonly WorkspaceSettings _settings;
        public MemoryWorkspaceSource(WorkspaceSettings settings) => _settings = settings;
        public Task<WorkspaceSettings?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult<WorkspaceSettings?>(_settings);
    }

    private sealed class MemoryGateway : IReadOnlyStockAnalyzerGateway
    {
        private readonly List<string> _registry;
        private readonly Dictionary<string, TickerMetadata> _metadata;
        private readonly Dictionary<string, List<CandleData>> _candles;

        public MemoryGateway(List<string> registry, Dictionary<string, TickerMetadata> metadata, Dictionary<string, List<CandleData>> candles)
        {
            _registry = registry;
            _metadata = metadata;
            _candles = candles;
        }

        public Task<IReadOnlyList<string>> GetAvailableTickersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(_registry);

        public Task<TickerMetadata?> GetStoredTickerMetadataAsync(string symbol, CancellationToken cancellationToken = default)
        {
            _metadata.TryGetValue(symbol, out var meta);
            return Task.FromResult<TickerMetadata?>(meta);
        }

        public Task<IReadOnlyList<CandleData>> GetCandlesAsync(string symbol, TimeFrame timeFrame, int count, CancellationToken cancellationToken = default)
        {
            if (_candles.TryGetValue(symbol, out var list))
            {
                return Task.FromResult<IReadOnlyList<CandleData>>(list);
            }
            return Task.FromResult<IReadOnlyList<CandleData>>(Array.Empty<CandleData>());
        }

        public Task<IReadOnlyList<CandleData>> GetCandlesUpToAsync(string symbol, TimeFrame timeFrame, DateOnly endDate, int count, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<DateTimeOffset?> GetTimeSeriesLastUpdatedAsync(string symbol, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<Dictionary<string, IIndicatorResult>> CalculateIndicatorsAsync(IReadOnlyList<CoreCandleData> candles, IEnumerable<CoreIndicatorSettings> settings, IReadOnlyDictionary<string, IReadOnlyList<CoreCandleData?>>? secondaryCandlesBySymbol = null) => throw new NotImplementedException();
        public ReverseWatchCurveData? CalculateReverseWatch(IReadOnlyList<CoreCandleData> candles, int period, string symbol, bool isMaBased = true, bool isLogScaleVolume = false, int dataCount = 0) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> RunScreenerAsync(IReadOnlyList<string> symbols, IScreeningCondition condition, TimeFrame timeFrame, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ScreenerTemplateListing> ListScreenerTemplatesAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ScreenerIndicatorTemplate?> GetScreenerTemplateAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BacktestConditionTemplateListing> ListBacktestConditionTemplatesAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BacktestConditionTemplate?> GetBacktestConditionTemplateAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> RunScreenerTemplateAsync(IReadOnlyList<string> symbols, ScreenerIndicatorTemplate template, TimeFrame timeFrame, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BacktestEvaluationArtifact> RunBacktestAsync(BacktestRunSnapshot snapshot, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private static readonly MethodInfo ApplyMetadataMethod = typeof(TickerListViewModel).GetMethod(
        "ApplyMetadataToItem",
        BindingFlags.NonPublic | BindingFlags.Instance,
        null,
        new[] { typeof(WatchlistItemViewModel), typeof(TickerMetadata?) },
        null)!;

    private static readonly MethodInfo ApplyTimeSeriesMethod = typeof(TickerListViewModel).GetMethod(
        "ApplyTimeSeriesDataToItem",
        BindingFlags.NonPublic | BindingFlags.Instance,
        null,
        new[] { typeof(WatchlistItemViewModel), typeof(IReadOnlyList<CandleData>) },
        null)!;

    [Fact]
    public async Task TabEquivalence_AcrossAllWorkspaceNodeTypes_ProducesIdenticalMemberSets()
    {
        var registeredSymbols = new List<string> { "AAPL", "AMZN", "GOOGL", "MSFT", "NVDA", "TSLA" };

        var metadataDict = new Dictionary<string, TickerMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["MSFT"] = new TickerMetadata { Sector = "Technology", Tag = "Growth" },
            ["AAPL"] = new TickerMetadata { Sector = "Technology", Tag = "Growth" },
            ["NVDA"] = new TickerMetadata { Sector = "Technology", Tag = "Value" },
            ["GOOGL"] = new TickerMetadata { Sector = "Communication", Tag = "Growth" },
            ["TSLA"] = new TickerMetadata { Sector = "Consumer Cyclical", Tag = "Growth" },
            ["AMZN"] = new TickerMetadata { Sector = "Consumer Cyclical", Tag = "Growth" }
        };

        var d1 = new DateTime(2026, 1, 1);
        var d2 = new DateTime(2026, 1, 2);
        var candlesDict = new Dictionary<string, List<CandleData>>(StringComparer.OrdinalIgnoreCase)
        {
            ["MSFT"] = new() { new(d1, 195m, 202m, 194m, 198m, 1000), new(d2, 198m, 205m, 197m, 200m, 1200) },
            ["AAPL"] = new() { new(d1, 175m, 182m, 174m, 178m, 1000), new(d2, 178m, 185m, 177m, 180m, 1200) },
            ["NVDA"] = new() { new(d1, 115m, 122m, 114m, 118m, 1000), new(d2, 118m, 125m, 117m, 120m, 1200) },
            ["GOOGL"] = new() { new(d1, 135m, 142m, 134m, 138m, 1000), new(d2, 138m, 145m, 137m, 140m, 1200) },
            ["TSLA"] = new() { new(d1, 215m, 222m, 214m, 218m, 1000), new(d2, 218m, 225m, 217m, 220m, 1200) },
            ["AMZN"] = new() { new(d1, 165m, 172m, 164m, 168m, 1000), new(d2, 168m, 175m, 167m, 170m, 1200) }
        };

        var watchlistId = Guid.NewGuid();
        var portfolioId = Guid.NewGuid();
        var customCatId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        var filter1Id = Guid.NewGuid();
        var filter2Id = Guid.NewGuid();
        var filter3Id = Guid.NewGuid();
        var filter4Id = Guid.NewGuid();

        var workspace = new WorkspaceSettings
        {
            WatchlistProfiles = new List<WatchlistProfile>
            {
                new(watchlistId, "Tech Watchlist", IndicatorColor.FromRgb(0, 0, 255), isPortfolio: false, new[]
                {
                    new WatchlistItem("MSFT", DateTimeOffset.UtcNow),
                    new WatchlistItem("AAPL", DateTimeOffset.UtcNow),
                    new WatchlistItem("NVDA", DateTimeOffset.UtcNow),
                    new WatchlistItem("UNREG1", DateTimeOffset.UtcNow)
                }),
                new(portfolioId, "Core Portfolio", IndicatorColor.FromRgb(0, 255, 0), isPortfolio: true, new[]
                {
                    new WatchlistItem("GOOGL", DateTimeOffset.UtcNow),
                    new WatchlistItem("TSLA", DateTimeOffset.UtcNow),
                    new WatchlistItem("UNREG2", DateTimeOffset.UtcNow)
                })
            },
            CustomTickerCategories = new List<TickerParentCategorySettings>
            {
                new()
                {
                    Id = customCatId,
                    Name = "Sectors",
                    Folders = new List<TickerListFolderSettings>
                    {
                        new()
                        {
                            Id = folderId,
                            Name = "Consumer",
                            ChildTickers = new List<string> { "AMZN", "AAPL" }
                        }
                    }
                }
            },
            TagFilters = new List<FilterSettings>
            {
                new()
                {
                    Id = filter1Id,
                    ParentId = TickerGroupIds.AllTickers,
                    Name = "Filter 1 (Tech)",
                    Rules = new List<FilterRule> { new() { Field = "Sector", Operator = "==", Value = "Technology" } },
                    Children = new List<FilterSettings>
                    {
                        new()
                        {
                            Id = filter2Id,
                            ParentId = filter1Id,
                            Name = "Filter 2 (Close > 150)",
                            Rules = new List<FilterRule> { new() { Field = "Close", Operator = ">", Value = "150" } },
                            Children = new List<FilterSettings>
                            {
                                new()
                                {
                                    Id = filter3Id,
                                    ParentId = filter2Id,
                                    Name = "Filter 3 (Growth)",
                                    Rules = new List<FilterRule> { new() { Field = "Tag", Operator = "Contains", Value = "Growth" } }
                                }
                            }
                        }
                    }
                },
                new()
                {
                    Id = filter4Id,
                    ParentId = watchlistId,
                    Name = "Filter 4 (Under Watchlist)",
                    Rules = new List<FilterRule> { new() { Field = "Close", Operator = ">", Value = "150" } }
                }
            }
        };

        // 1. Build TickerGroupResolver
        var gateway = new MemoryGateway(registeredSymbols, metadataDict, candlesDict);
        var resolver = new TickerGroupResolver(gateway, new MemoryWorkspaceSource(workspace));

        // 2. Build TickerListViewModel
        var mockProvider = new Mock<IMarketDataProvider>();
        mockProvider.Setup(p => p.GetAvailableTickersAsync()).ReturnsAsync(registeredSymbols);
        foreach (var sym in registeredSymbols)
        {
            mockProvider.Setup(p => p.GetMetadataAsync(sym)).ReturnsAsync(metadataDict[sym]);
            mockProvider.Setup(p => p.GetTickersDataAsync(sym, TimeFrame.D1)).ReturnsAsync(candlesDict[sym]);
        }

        var mockWatchlistManager = new Mock<IWatchlistManager>();
        mockWatchlistManager.Setup(w => w.GetAllProfiles()).Returns(workspace.WatchlistProfiles ?? new List<WatchlistProfile>());

        using var vm = new TickerListViewModel(
            marketDataProvider: mockProvider.Object,
            pythonService: null!,
            messenger: CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default,
            dispatcherService: new Services.SynchronousDispatcherService(),
            watchlistManager: mockWatchlistManager.Object,
            portfolioManager: null!,
            dialogService: null!,
            tickerImportService: null!,
            chartSettingsManager: new MockChartSettingsManager(),
            logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<TickerListViewModel>.Instance);

        vm.ImportCustomTickerCategories(workspace.CustomTickerCategories);
        vm.ImportFilterSettings(workspace.TagFilters);

        // Pre-hydrate all symbols in vm's cache using reflection so vm evaluation matches fully-hydrated state
        var cacheField = typeof(TickerListViewModel).GetField("_viewModelCache", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var cache = (Dictionary<string, WatchlistItemViewModel>)cacheField.GetValue(vm)!;
        foreach (var sym in registeredSymbols)
        {
            var item = new WatchlistItemViewModel(sym, sym, "", "", 0, 0, 0, 0, 0, 0);
            ApplyMetadataMethod.Invoke(vm, new object?[] { item, metadataDict[sym] });
            ApplyTimeSeriesMethod.Invoke(vm, new object?[] { item, candlesDict[sym] });
            item.IsMetadataLoaded = true;
            cache[sym] = item;
        }

        // 3. Flatten all nodes in vm.Groups and compare against resolver
        var allVmNodes = new List<TickerGroupNode>();
        void CollectNodes(IEnumerable<TickerGroupNode> nodes)
        {
            foreach (var n in nodes)
            {
                allVmNodes.Add(n);
                if (n.Children != null) CollectNodes(n.Children);
            }
        }
        CollectNodes(vm.Groups);

        var regSet = new HashSet<string>(registeredSymbols, StringComparer.OrdinalIgnoreCase);

        foreach (var node in allVmNodes)
        {
            // Skip action nodes (Add Watchlist, Add Filter, etc.)
            if (node is ActionNode) continue;

            var vmTickers = vm.GetTickersForNode(node);
            var resolved = await resolver.ResolveAsync(node.Id, 1000);

            if (node is FilterNode)
            {
                var expected = vmTickers.OrderBy(s => s, StringComparer.Ordinal).ToList();
                Assert.Equal(expected, resolved.Symbols);
            }
            else
            {
                // Static group: resolver returns registered members
                var expected = vmTickers.Where(regSet.Contains).OrderBy(s => s, StringComparer.Ordinal).ToList();
                Assert.Equal(expected, resolved.Symbols);
            }
        }
    }
}
