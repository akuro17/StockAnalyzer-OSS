using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Core.ReadOnly;
using Xunit;

namespace StockAnalyzer.Core.Tests.ReadOnly;

/// <summary>
/// Pins the observable behaviour of the filter branch of <see cref="TickerGroupResolver"/> that the other resolver tests do not
/// cover: gateway read counts, failure propagation, cancellation and the cap order. Written before the filter loop moved into
/// <see cref="TickerFilterEvaluator"/> and kept unchanged afterwards, so a green run on both sides is the proof that the move
/// changed nothing.
/// </summary>
public sealed class TickerGroupResolverCharacterizationTests
{
    private sealed class FixedWorkspaceSource : IReadOnlyWorkspaceSource
    {
        private readonly WorkspaceSettings _settings;
        public FixedWorkspaceSource(WorkspaceSettings settings) => _settings = settings;
        public Task<WorkspaceSettings?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult<WorkspaceSettings?>(_settings);
    }

    private static CandleData Bar(decimal close) => new(new DateTime(2026, 1, 2), close, close, close, close, 100);

    private static FilterSettings Filter(Guid id, Guid parentId, params FilterRule[] rules)
        => new() { Id = id, ParentId = parentId, Name = "F-" + id.ToString("N")[..6], Rules = new List<FilterRule>(rules) };

    private static TickerGroupResolver ResolverFor(CountingTickerGateway gateway, params FilterSettings[] filters)
        => new(gateway, new FixedWorkspaceSource(new WorkspaceSettings { TagFilters = new List<FilterSettings>(filters) }));

    private static FilterRule CloseAbove(string value) => new() { Field = "Close", Operator = ">", Value = value };

    [Fact]
    public async Task CandleRuleAfterMetadataRule_ReadsMetadataOncePerSymbolAndCandlesOnlyForSurvivors()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var gateway = new CountingTickerGateway
        {
            Registry = new() { "S1", "S2", "S3" },
            Metadata = new()
            {
                ["S1"] = new TickerMetadata { Sector = "Technology" },
                ["S2"] = new TickerMetadata { Sector = "Technology" },
                ["S3"] = new TickerMetadata { Sector = "Energy" },
            },
            Candles = new() { ["S1"] = new() { Bar(10m) }, ["S2"] = new() { Bar(200m) }, ["S3"] = new() { Bar(300m) } },
        };
        var resolver = ResolverFor(
            gateway,
            Filter(first, TickerGroupIds.AllTickers, new FilterRule { Field = "Sector", Operator = "==", Value = "Technology" }),
            Filter(second, first, CloseAbove("100")));

        var result = await resolver.ResolveAsync(second, 1000);

        Assert.Equal(new[] { "S2" }, result.Symbols);
        Assert.Equal(new[] { "S1", "S2", "S3" }, gateway.MetadataSymbols);
        Assert.Equal(new[] { "S1", "S2" }, gateway.CandleSymbols);
    }

    [Fact]
    public async Task MetadataRuleAfterCandleRule_ReusesTheRowThatAlreadyHasCandles()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var gateway = new CountingTickerGateway
        {
            Registry = new() { "S1", "S2" },
            Metadata = new()
            {
                ["S1"] = new TickerMetadata { Sector = "Technology" },
                ["S2"] = new TickerMetadata { Sector = "Energy" },
            },
            Candles = new() { ["S1"] = new() { Bar(200m) }, ["S2"] = new() { Bar(300m) } },
        };
        var resolver = ResolverFor(
            gateway,
            Filter(first, TickerGroupIds.AllTickers, CloseAbove("100")),
            Filter(second, first, new FilterRule { Field = "Sector", Operator = "==", Value = "Technology" }));

        var result = await resolver.ResolveAsync(second, 1000);

        Assert.Equal(new[] { "S1" }, result.Symbols);
        Assert.Equal(2, gateway.MetadataCallCount);
        Assert.Equal(2, gateway.CandlesCallCount);
    }

    [Fact]
    public async Task CapExceeded_ThrowsBeforeAnyGatewayRead()
    {
        var id = Guid.NewGuid();
        var gateway = new CountingTickerGateway { Registry = new() { "A", "B", "C", "D", "E" } };
        var resolver = ResolverFor(gateway, Filter(id, TickerGroupIds.AllTickers, CloseAbove("1")));

        var ex = await Assert.ThrowsAsync<FilterEvaluationLimitException>(() => resolver.ResolveAsync(id, 3));

        Assert.Equal(5, ex.Count);
        Assert.Equal(3, ex.Limit);
        Assert.Equal(0, gateway.MetadataCallCount);
        Assert.Equal(0, gateway.CandlesCallCount);
    }

    [Fact]
    public async Task NodeWithoutRules_DoesNotAdvanceTheCumulativeCounter()
    {
        var passThrough = Guid.NewGuid();
        var evaluated = Guid.NewGuid();
        var gateway = new CountingTickerGateway
        {
            Registry = new() { "A", "B", "C" },
            Candles = new() { ["A"] = new() { Bar(5m) }, ["B"] = new() { Bar(50m) }, ["C"] = new() { Bar(500m) } },
        };
        var resolver = ResolverFor(
            gateway,
            Filter(passThrough, TickerGroupIds.AllTickers),
            Filter(evaluated, passThrough, CloseAbove("10")));

        // Limit equals the symbol count: the empty node must not count, otherwise 3 + 3 > 3 would throw.
        var result = await resolver.ResolveAsync(evaluated, 3);

        Assert.Equal(new[] { "B", "C" }, result.Symbols);
    }

    [Fact]
    public async Task ChildOfAnEmptyResult_IsEmptyAndReadsNothingMore()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var gateway = new CountingTickerGateway
        {
            Registry = new() { "A", "B" },
            Candles = new() { ["A"] = new() { Bar(1m) }, ["B"] = new() { Bar(2m) } },
        };
        var resolver = ResolverFor(
            gateway,
            Filter(first, TickerGroupIds.AllTickers, CloseAbove("1000")),
            Filter(second, first, CloseAbove("0")));

        var result = await resolver.ResolveAsync(second, 1000);

        Assert.Empty(result.Symbols);
        Assert.Equal(TickerGroupKind.Filter, result.Kind);
        Assert.Equal(2, gateway.CandlesCallCount);
    }

    [Fact]
    public async Task DataUnavailableFromTheCandleRead_Propagates()
    {
        var id = Guid.NewGuid();
        var gateway = new CountingTickerGateway
        {
            Registry = new() { "A" },
            CandleFailure = _ => new DataUnavailableException("store unreadable"),
        };
        var resolver = ResolverFor(gateway, Filter(id, TickerGroupIds.AllTickers, CloseAbove("1")));

        await Assert.ThrowsAsync<DataUnavailableException>(() => resolver.ResolveAsync(id, 1000));
    }

    [Fact]
    public async Task ArgumentExceptionFromTheCandleRead_IsTreatedAsNoBars()
    {
        var id = Guid.NewGuid();
        var gateway = new CountingTickerGateway
        {
            Registry = new() { "BAD", "OK" },
            Candles = new() { ["OK"] = new() { Bar(5m) } },
            CandleFailure = symbol => symbol == "BAD" ? new ArgumentException("not a symbol") : null,
        };
        var resolver = ResolverFor(gateway, Filter(id, TickerGroupIds.AllTickers, new FilterRule { Field = "Close", Operator = "==", Value = "0" }));

        var result = await resolver.ResolveAsync(id, 1000);

        Assert.Equal(new[] { "BAD" }, result.Symbols);
    }

    [Fact]
    public async Task CancelledToken_ThrowsOperationCanceledBeforeTheFirstSymbolIsRead()
    {
        var id = Guid.NewGuid();
        var gateway = new CountingTickerGateway { Registry = new() { "A", "B" } };
        var resolver = ResolverFor(gateway, Filter(id, TickerGroupIds.AllTickers, CloseAbove("1")));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ResolveAsync(id, 1000, cts.Token));

        Assert.Equal(0, gateway.MetadataCallCount);
        Assert.Equal(0, gateway.CandlesCallCount);
    }

    [Fact]
    public async Task MatchedSymbols_AreInOrdinalOrder()
    {
        var id = Guid.NewGuid();
        var gateway = new CountingTickerGateway
        {
            // Ordinal order: '-' (0x2D) < digits < letters; a culture-aware sort would not have to agree.
            Registry = new() { "Z", "AB", "A-B", "7203-T" },
            Candles = new()
            {
                ["Z"] = new() { Bar(9m) },
                ["AB"] = new() { Bar(9m) },
                ["A-B"] = new() { Bar(9m) },
                ["7203-T"] = new() { Bar(9m) },
            },
        };
        var resolver = ResolverFor(gateway, Filter(id, TickerGroupIds.AllTickers, CloseAbove("1")));

        var result = await resolver.ResolveAsync(id, 1000);

        Assert.Equal(new[] { "7203-T", "A-B", "AB", "Z" }, result.Symbols);
    }

    [Fact]
    public async Task FilterOverAWatchlist_ReportsNoUnregisteredCountAndOnlyEvaluatesRegisteredMembers()
    {
        var watchlistId = Guid.NewGuid();
        var filterId = Guid.NewGuid();
        var workspace = new WorkspaceSettings
        {
            WatchlistProfiles = new List<WatchlistProfile>
            {
                new(watchlistId, "Mine", IndicatorColor.FromRgb(0, 0, 255), false, new[]
                {
                    new WatchlistItem("AAA", DateTimeOffset.UtcNow),
                    new WatchlistItem("GONE", DateTimeOffset.UtcNow),
                }),
            },
            TagFilters = new List<FilterSettings> { Filter(filterId, watchlistId, CloseAbove("1")) },
        };
        var gateway = new CountingTickerGateway
        {
            Registry = new() { "AAA", "BBB" },
            Candles = new() { ["AAA"] = new() { Bar(9m) }, ["BBB"] = new() { Bar(9m) } },
        };
        var resolver = new TickerGroupResolver(gateway, new FixedWorkspaceSource(workspace));

        var result = await resolver.ResolveAsync(filterId, 1000);

        Assert.Equal(new[] { "AAA" }, result.Symbols);
        Assert.Equal(0, result.UnregisteredCount);
        Assert.Equal(new[] { "AAA" }, gateway.CandleSymbols);
    }

    [Fact]
    public async Task CandleRead_AsksForTheLastTwoDailyBars()
    {
        var id = Guid.NewGuid();
        var gateway = new CountingTickerGateway
        {
            Registry = new() { "A" },
            Candles = new() { ["A"] = new() { Bar(1m), Bar(2m) } },
        };
        var resolver = ResolverFor(gateway, Filter(id, TickerGroupIds.AllTickers, CloseAbove("0")));

        await resolver.ResolveAsync(id, 1000);

        Assert.Equal(2, gateway.LastRequestedBarCount);
    }
}
