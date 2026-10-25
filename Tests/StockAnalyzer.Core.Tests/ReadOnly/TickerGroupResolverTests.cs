using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Constants;
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

namespace StockAnalyzer.Core.Tests.ReadOnly;

public sealed class TickerGroupResolverTests
{
    private sealed class FakeWorkspaceSource : IReadOnlyWorkspaceSource
    {
        private readonly WorkspaceSettings? _settings;
        public FakeWorkspaceSource(WorkspaceSettings? settings) => _settings = settings;
        public Task<WorkspaceSettings?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_settings);
    }

    private sealed class FakeGateway : IReadOnlyStockAnalyzerGateway
    {
        public List<string> Registry { get; set; } = new();
        public Dictionary<string, TickerMetadata> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<CandleData>> Candles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public int CandlesCallCount { get; private set; }
        public int MetadataCallCount { get; private set; }

        public Task<IReadOnlyList<string>> GetAvailableTickersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(Registry);

        public Task<IReadOnlyList<CandleData>> GetCandlesAsync(string symbol, TimeFrame timeFrame, int count, CancellationToken cancellationToken = default)
        {
            CandlesCallCount++;
            if (Candles.TryGetValue(symbol, out var list))
            {
                return Task.FromResult<IReadOnlyList<CandleData>>(list);
            }
            return Task.FromResult<IReadOnlyList<CandleData>>(Array.Empty<CandleData>());
        }

        public Task<TickerMetadata?> GetStoredTickerMetadataAsync(string symbol, CancellationToken cancellationToken = default)
        {
            MetadataCallCount++;
            Metadata.TryGetValue(symbol, out var meta);
            return Task.FromResult<TickerMetadata?>(meta);
        }

        public Task<IReadOnlyList<CandleData>> GetCandlesUpToAsync(string symbol, TimeFrame timeFrame, DateOnly endDate, int count, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<DateTimeOffset?> GetTimeSeriesLastUpdatedAsync(string symbol, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<Dictionary<string, IIndicatorResult>> CalculateIndicatorsAsync(IReadOnlyList<CoreCandleData> candles, IEnumerable<CoreIndicatorSettings> settings, IReadOnlyDictionary<string, IReadOnlyList<CoreCandleData?>>? secondaryCandlesBySymbol = null)
            => throw new NotImplementedException();
        public ReverseWatchCurveData? CalculateReverseWatch(IReadOnlyList<CoreCandleData> candles, int period, string symbol, bool isMaBased = true, bool isLogScaleVolume = false, int dataCount = 0)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> RunScreenerAsync(IReadOnlyList<string> symbols, IScreeningCondition condition, TimeFrame timeFrame, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<ScreenerTemplateListing> ListScreenerTemplatesAsync(CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<ScreenerIndicatorTemplate?> GetScreenerTemplateAsync(Guid id, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<BacktestConditionTemplateListing> ListBacktestConditionTemplatesAsync(CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<BacktestConditionTemplate?> GetBacktestConditionTemplateAsync(Guid id, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> RunScreenerTemplateAsync(IReadOnlyList<string> symbols, ScreenerIndicatorTemplate template, TimeFrame timeFrame, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<BacktestEvaluationArtifact> RunBacktestAsync(BacktestRunSnapshot snapshot, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }

    [Fact]
    public async Task ResolveAsync_StaticAllTickers_ReturnsSortedRegisteredSymbols()
    {
        var gateway = new FakeGateway { Registry = new() { "MSFT", "AAPL", "NVDA" } };
        var resolver = new TickerGroupResolver(gateway, new FakeWorkspaceSource(null));

        var result = await resolver.ResolveAsync(TickerGroupIds.AllTickers, 1000);

        Assert.Equal(TickerGroupKind.AllTickers, result.Kind);
        Assert.Equal(new[] { "AAPL", "MSFT", "NVDA" }, result.Symbols);
        Assert.Equal(0, result.UnregisteredCount);
        Assert.Empty(result.UnknownRuleFields);
        Assert.Equal(0, gateway.CandlesCallCount);
        Assert.Equal(0, gateway.MetadataCallCount);
    }

    [Fact]
    public async Task ResolveAsync_StaticWatchlist_FiltersToRegistryAndReportsUnregistered()
    {
        var watchlistId = Guid.NewGuid();
        var workspace = new WorkspaceSettings
        {
            WatchlistProfiles = new List<WatchlistProfile>
            {
                new(watchlistId, "Tech", IndicatorColor.FromRgb(0, 0, 255), false, new[]
                {
                    new WatchlistItem("MSFT", DateTimeOffset.UtcNow),
                    new WatchlistItem("UNREGISTERED_A", DateTimeOffset.UtcNow),
                    new WatchlistItem("AAPL", DateTimeOffset.UtcNow),
                    new WatchlistItem("UNREGISTERED_B", DateTimeOffset.UtcNow)
                })
            }
        };

        var gateway = new FakeGateway { Registry = new() { "AAPL", "MSFT", "GOOGL" } };
        var resolver = new TickerGroupResolver(gateway, new FakeWorkspaceSource(workspace));

        var result = await resolver.ResolveAsync(watchlistId, 1000);

        Assert.Equal(TickerGroupKind.Watchlist, result.Kind);
        Assert.Equal(new[] { "AAPL", "MSFT" }, result.Symbols);
        Assert.Equal(2, result.UnregisteredCount);
        Assert.Empty(result.UnknownRuleFields);
    }

    [Fact]
    public async Task ResolveAsync_UnknownGroupId_ThrowsGroupNotFoundException()
    {
        var gateway = new FakeGateway();
        var resolver = new TickerGroupResolver(gateway, new FakeWorkspaceSource(null));

        var unknownId = Guid.NewGuid();
        var ex = await Assert.ThrowsAsync<GroupNotFoundException>(() => resolver.ResolveAsync(unknownId, 1000));
        Assert.Equal(unknownId, ex.GroupId);
    }

    [Fact]
    public async Task ResolveAsync_FilterNode_WithZeroRules_KeepsAllParentMembers()
    {
        var filterId = Guid.NewGuid();
        var workspace = new WorkspaceSettings
        {
            TagFilters = new List<FilterSettings>
            {
                new()
                {
                    Id = filterId,
                    ParentId = TickerGroupIds.AllTickers,
                    Name = "Empty Rules Filter",
                    Rules = new List<FilterRule>()
                }
            }
        };

        var gateway = new FakeGateway { Registry = new() { "AAPL", "MSFT" } };
        var resolver = new TickerGroupResolver(gateway, new FakeWorkspaceSource(workspace));

        var result = await resolver.ResolveAsync(filterId, 1000);

        Assert.Equal(TickerGroupKind.Filter, result.Kind);
        Assert.Equal(new[] { "AAPL", "MSFT" }, result.Symbols);
        Assert.Equal(0, result.UnregisteredCount);
        Assert.Empty(result.UnknownRuleFields);
        Assert.Equal(0, gateway.CandlesCallCount);
    }

    [Fact]
    public async Task ResolveAsync_FilterNode_EvaluatesMetadataAndMarketRules()
    {
        var filterId = Guid.NewGuid();
        var workspace = new WorkspaceSettings
        {
            TagFilters = new List<FilterSettings>
            {
                new()
                {
                    Id = filterId,
                    ParentId = TickerGroupIds.AllTickers,
                    Name = "High Close Tech Filter",
                    Rules = new List<FilterRule>
                    {
                        new() { Field = "Close", Operator = ">", Value = "150" },
                        new() { Field = "Sector", Operator = "==", Value = "Technology" }
                    }
                }
            }
        };

        var gateway = new FakeGateway
        {
            Registry = new() { "AAPL", "MSFT", "F" },
            Metadata = new()
            {
                ["AAPL"] = new TickerMetadata { Sector = "Technology" },
                ["MSFT"] = new TickerMetadata { Sector = "Technology" },
                ["F"] = new TickerMetadata { Sector = "Consumer Cyclical" }
            },
            Candles = new()
            {
                ["AAPL"] = new()
                {
                    new CandleData(new DateTime(2026, 1, 1), 170m, 175m, 169m, 172m, 1000),
                    new CandleData(new DateTime(2026, 1, 2), 172m, 180m, 171m, 178m, 1200)
                },
                ["MSFT"] = new()
                {
                    new CandleData(new DateTime(2026, 1, 1), 140m, 145m, 139m, 142m, 1000),
                    new CandleData(new DateTime(2026, 1, 2), 142m, 148m, 141m, 144m, 1200) // Close <= 150
                },
                ["F"] = new()
                {
                    new CandleData(new DateTime(2026, 1, 1), 160m, 165m, 159m, 162m, 1000),
                    new CandleData(new DateTime(2026, 1, 2), 162m, 168m, 161m, 165m, 1200) // Sector != Technology
                }
            }
        };

        var resolver = new TickerGroupResolver(gateway, new FakeWorkspaceSource(workspace));
        var result = await resolver.ResolveAsync(filterId, 1000);

        Assert.Equal(TickerGroupKind.Filter, result.Kind);
        Assert.Equal(new[] { "AAPL" }, result.Symbols);
        Assert.Equal(0, result.UnregisteredCount);
        Assert.Empty(result.UnknownRuleFields);
    }

    [Fact]
    public async Task ResolveAsync_FilterNode_WithUnknownRuleField_CollectsFieldAndEvaluatesToFalse()
    {
        var filterId = Guid.NewGuid();
        var workspace = new WorkspaceSettings
        {
            TagFilters = new List<FilterSettings>
            {
                new()
                {
                    Id = filterId,
                    ParentId = TickerGroupIds.AllTickers,
                    Name = "Bad Field Filter",
                    Rules = new List<FilterRule>
                    {
                        new() { Field = "NonExistentColumn", Operator = ">", Value = "100" }
                    }
                }
            }
        };

        var gateway = new FakeGateway
        {
            Registry = new() { "AAPL" },
            Metadata = new() { ["AAPL"] = new TickerMetadata() },
            Candles = new() { ["AAPL"] = new() { new CandleData(DateTime.UtcNow, 150m, 160m, 140m, 155m, 100) } }
        };

        var resolver = new TickerGroupResolver(gateway, new FakeWorkspaceSource(workspace));
        var result = await resolver.ResolveAsync(filterId, 1000);

        Assert.Equal(TickerGroupKind.Filter, result.Kind);
        Assert.Empty(result.Symbols);
        Assert.Equal(new[] { "NonExistentColumn" }, result.UnknownRuleFields);
    }

    [Fact]
    public async Task ResolveAsync_FilterNode_ExceedingLimit_ThrowsFilterEvaluationLimitException()
    {
        var filterId = Guid.NewGuid();
        var workspace = new WorkspaceSettings
        {
            TagFilters = new List<FilterSettings>
            {
                new()
                {
                    Id = filterId,
                    ParentId = TickerGroupIds.AllTickers,
                    Name = "Exceeding Filter",
                    Rules = new List<FilterRule>()
                }
            }
        };

        var gateway = new FakeGateway { Registry = new() { "A", "B", "C", "D", "E" } };
        var resolver = new TickerGroupResolver(gateway, new FakeWorkspaceSource(workspace));

        var ex = await Assert.ThrowsAsync<FilterEvaluationLimitException>(() => resolver.ResolveAsync(filterId, maxFilterEvaluationSymbols: 3));
        Assert.Equal(5, ex.Count);
        Assert.Equal(3, ex.Limit);
    }

    [Fact]
    public async Task ResolveAsync_NestedFilters_ApplyCumulatively()
    {
        var parentFilterId = Guid.NewGuid();
        var childFilterId = Guid.NewGuid();

        var workspace = new WorkspaceSettings
        {
            TagFilters = new List<FilterSettings>
            {
                new()
                {
                    Id = parentFilterId,
                    ParentId = TickerGroupIds.AllTickers,
                    Name = "Parent Filter",
                    Rules = new List<FilterRule>
                    {
                        new() { Field = "Close", Operator = ">", Value = "100" }
                    },
                    Children = new List<FilterSettings>
                    {
                        new()
                        {
                            Id = childFilterId,
                            ParentId = parentFilterId,
                            Name = "Child Filter",
                            Rules = new List<FilterRule>
                            {
                                new() { Field = "Close", Operator = ">", Value = "200" }
                            }
                        }
                    }
                }
            }
        };

        var gateway = new FakeGateway
        {
            Registry = new() { "S1", "S2", "S3" },
            Candles = new()
            {
                ["S1"] = new() { new CandleData(DateTime.UtcNow, 50m, 50m, 50m, 50m, 100) },
                ["S2"] = new() { new CandleData(DateTime.UtcNow, 150m, 150m, 150m, 150m, 100) },
                ["S3"] = new() { new CandleData(DateTime.UtcNow, 250m, 250m, 250m, 250m, 100) }
            }
        };

        var resolver = new TickerGroupResolver(gateway, new FakeWorkspaceSource(workspace));

        var parentResult = await resolver.ResolveAsync(parentFilterId, 1000);
        Assert.Equal(new[] { "S2", "S3" }, parentResult.Symbols);

        var childResult = await resolver.ResolveAsync(childFilterId, 1000);
        Assert.Equal(new[] { "S3" }, childResult.Symbols);
    }

    [Fact]
    public async Task ResolveAsync_MetadataOnlyFilter_DoesNotCallGetCandlesAsync()
    {
        var filterId = Guid.NewGuid();
        var workspace = new WorkspaceSettings
        {
            TagFilters = new List<FilterSettings>
            {
                new()
                {
                    Id = filterId,
                    ParentId = TickerGroupIds.AllTickers,
                    Name = "Tech Meta Filter",
                    Rules = new List<FilterRule>
                    {
                        new() { Field = "Sector", Operator = "==", Value = "Technology" },
                        new() { Field = "Tag", Operator = "Contains", Value = "AI" }
                    }
                }
            }
        };

        var gateway = new FakeGateway
        {
            Registry = new() { "AAPL", "MSFT", "XOM" },
            Metadata = new()
            {
                ["AAPL"] = new TickerMetadata { Sector = "Technology", Tag = "AI, Hardware" },
                ["MSFT"] = new TickerMetadata { Sector = "Technology", Tag = "AI, Cloud" },
                ["XOM"] = new TickerMetadata { Sector = "Energy", Tag = "Oil" }
            }
        };

        var resolver = new TickerGroupResolver(gateway, new FakeWorkspaceSource(workspace));
        var result = await resolver.ResolveAsync(filterId, 1000);

        Assert.Equal(new[] { "AAPL", "MSFT" }, result.Symbols);
        Assert.Equal(3, gateway.MetadataCallCount);
        Assert.Equal(0, gateway.CandlesCallCount); // Candles never fetched for metadata-only rules
    }

    [Fact]
    public async Task ResolveAsync_MultiLevelFilters_MemoizesMetadataAndCandlesPerSymbol()
    {
        var level1Id = Guid.NewGuid();
        var level2Id = Guid.NewGuid();
        var level3Id = Guid.NewGuid();

        var workspace = new WorkspaceSettings
        {
            TagFilters = new List<FilterSettings>
            {
                new()
                {
                    Id = level1Id,
                    ParentId = TickerGroupIds.AllTickers,
                    Name = "Level 1 (Sector)",
                    Rules = new List<FilterRule> { new() { Field = "Sector", Operator = "==", Value = "Technology" } },
                    Children = new List<FilterSettings>
                    {
                        new()
                        {
                            Id = level2Id,
                            ParentId = level1Id,
                            Name = "Level 2 (Close > 150)",
                            Rules = new List<FilterRule> { new() { Field = "Close", Operator = ">", Value = "150" } },
                            Children = new List<FilterSettings>
                            {
                                new()
                                {
                                    Id = level3Id,
                                    ParentId = level2Id,
                                    Name = "Level 3 (Close > 175)",
                                    Rules = new List<FilterRule> { new() { Field = "Close", Operator = ">", Value = "175" } }
                                }
                            }
                        }
                    }
                }
            }
        };

        var gateway = new FakeGateway
        {
            Registry = new() { "AAPL", "MSFT", "XOM" },
            Metadata = new()
            {
                ["AAPL"] = new TickerMetadata { Sector = "Technology" },
                ["MSFT"] = new TickerMetadata { Sector = "Technology" },
                ["XOM"] = new TickerMetadata { Sector = "Energy" }
            },
            Candles = new()
            {
                ["AAPL"] = new() { new CandleData(DateTime.UtcNow, 180m, 180m, 180m, 180m, 100) },
                ["MSFT"] = new() { new CandleData(DateTime.UtcNow, 160m, 160m, 160m, 160m, 100) }
            }
        };

        var resolver = new TickerGroupResolver(gateway, new FakeWorkspaceSource(workspace));
        var result = await resolver.ResolveAsync(level3Id, 1000);

        Assert.Equal(new[] { "AAPL" }, result.Symbols);
        // Level 1: 3 metadata calls (AAPL, MSFT, XOM), 0 candles
        // Level 2: 0 metadata calls (cached!), 2 candle calls (AAPL, MSFT)
        // Level 3: 0 metadata calls (cached!), 0 candle calls (cached!)
        Assert.Equal(3, gateway.MetadataCallCount);
        Assert.Equal(2, gateway.CandlesCallCount);
    }

    [Fact]
    public async Task ResolveAsync_MultiLevelFilters_EnforcesCumulativeEvaluationLimit()
    {
        var parentFilterId = Guid.NewGuid();
        var childFilterId = Guid.NewGuid();

        var workspace = new WorkspaceSettings
        {
            TagFilters = new List<FilterSettings>
            {
                new()
                {
                    Id = parentFilterId,
                    ParentId = TickerGroupIds.AllTickers,
                    Name = "Parent",
                    Rules = new List<FilterRule> { new() { Field = "Sector", Operator = "==", Value = "Tech" } },
                    Children = new List<FilterSettings>
                    {
                        new()
                        {
                            Id = childFilterId,
                            ParentId = parentFilterId,
                            Name = "Child",
                            Rules = new List<FilterRule> { new() { Field = "Close", Operator = ">", Value = "100" } }
                        }
                    }
                }
            }
        };

        var gateway = new FakeGateway
        {
            Registry = new() { "S1", "S2", "S3" },
            Metadata = new()
            {
                ["S1"] = new TickerMetadata { Sector = "Tech" },
                ["S2"] = new TickerMetadata { Sector = "Tech" },
                ["S3"] = new TickerMetadata { Sector = "NonTech" }
            },
            Candles = new()
            {
                ["S1"] = new() { new CandleData(DateTime.UtcNow, 150m, 150m, 150m, 150m, 100) },
                ["S2"] = new() { new CandleData(DateTime.UtcNow, 50m, 50m, 50m, 50m, 100) }
            }
        };

        var resolver = new TickerGroupResolver(gateway, new FakeWorkspaceSource(workspace));

        // Parent evaluates 3 symbols. Child evaluates 2 surviving symbols.
        // Cumulative count = 3 + 2 = 5.
        // With limit 4, child resolution should throw FilterEvaluationLimitException(5, 4).
        var ex = await Assert.ThrowsAsync<FilterEvaluationLimitException>(() => resolver.ResolveAsync(childFilterId, maxFilterEvaluationSymbols: 4));
        Assert.Equal(5, ex.Count);
        Assert.Equal(4, ex.Limit);
    }
}
