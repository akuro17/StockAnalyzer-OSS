using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.Tests.ViewModels;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.ViewModels.TickerList;
using StockAnalyzer.Avalonia.ViewModels.Watchlist;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Watchlist;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Services;

/// <summary>Real settings objects, the real ticker tree and real watchlist profiles; only the market-data and manager ports are fixed doubles.</summary>
[Collection("MessengerSharedState")]
public sealed class TickerSourceResolverTests : IDisposable
{
    private readonly List<TickerListViewModel> _stores = new();
    private static WatchlistProfile Profile(string name, bool portfolio, params string[] tickers)
        => new(Guid.NewGuid(), name, IndicatorColor.Gray, portfolio, tickers.Select(t => new WatchlistItem(t, DateTimeOffset.UtcNow)).ToList());

    private (TickerSourceResolver Resolver, TickerListViewModel Store) Create(IReadOnlyList<string> all, IReadOnlyDictionary<string, string> tags,
        params WatchlistProfile[] profiles)
    {
        var market = new FakeMarketDataProvider(all, tags);
        // A private messenger keeps this tree isolated from tests that publish on the shared default messenger.
        var store = TickerListViewModelTests.CreateViewModel(market, new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService(), new CommunityToolkit.Mvvm.Messaging.StrongReferenceMessenger());
        _stores.Add(store);
        return (new TickerSourceResolver(market, new FakeWatchlistManager(profiles), store), store);
    }
    private static Dictionary<string, string> NoTags() => new();
    public void Dispose() { foreach (var store in _stores) store.Dispose(); }

    [Fact]
    public async Task AllTickersComeFromTheProviderSortedByOrdinalComparison()
    {
        var (resolver, _) = Create(new[] { "MSFT", "AAPL", "TSLA" }, NoTags());
        var result = await resolver.ResolveAsync(new[] { RlSourceDescriptor.AllTickers() });
        Assert.Equal(new[] { "AAPL", "MSFT", "TSLA" }, result.Symbols);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task WatchlistAndPortfolioResolveByProfileOrWholeCategory()
    {
        var wl1 = Profile("W1", false, "AAPL", "MSFT"); var wl2 = Profile("W2", false, "GOOGL");
        var pf = Profile("P", true, "TSLA");
        var (resolver, _) = Create(Array.Empty<string>(), NoTags(), wl1, wl2, pf);
        Assert.Equal(new[] { "AAPL", "MSFT" }, (await resolver.ResolveAsync(new[] { RlSourceDescriptor.Watchlist(wl1.Id) })).Symbols);
        Assert.Equal(new[] { "AAPL", "GOOGL", "MSFT" }, (await resolver.ResolveAsync(new[] { RlSourceDescriptor.Watchlist() })).Symbols);
        Assert.Equal(new[] { "TSLA" }, (await resolver.ResolveAsync(new[] { RlSourceDescriptor.Portfolio() })).Symbols);
        Assert.Equal(new[] { "TSLA" }, (await resolver.ResolveAsync(new[] { RlSourceDescriptor.Portfolio(pf.Id) })).Symbols);
    }

    [Fact]
    public async Task MissingOrWrongKindProfilesYieldADiagnosticAndNoSymbolsWithoutThrowing()
    {
        var wl = Profile("W", false, "AAPL"); var pf = Profile("P", true, "TSLA");
        var (resolver, _) = Create(Array.Empty<string>(), NoTags(), wl, pf);
        var missing = RlSourceDescriptor.Watchlist(Guid.NewGuid()); var wrongKind = RlSourceDescriptor.Watchlist(pf.Id);
        var result = await resolver.ResolveAsync(new[] { missing, wrongKind, RlSourceDescriptor.Watchlist(wl.Id) });
        Assert.Equal(new[] { "AAPL" }, result.Symbols);
        Assert.Equal(2, result.Diagnostics.Length);
        Assert.All(result.Diagnostics, d => Assert.Equal(RlSourceDiagnosticCode.SourceNotFound, d.Code));
        Assert.Contains(result.Diagnostics, d => d.Source == missing); Assert.Contains(result.Diagnostics, d => d.Source == wrongKind);
    }

    [Fact]
    public async Task TagMatchesWholeTagsCaseInsensitivelyOverCommaAndSemicolonLists()
    {
        var wl = Profile("W", false, "NVDA");
        var tags = new Dictionary<string, string> { ["AAPL"] = "Growth, Tech", ["MSFT"] = "tech;Value", ["ORCL"] = "Technology", ["NVDA"] = " TECH ", ["TSLA"] = "Auto" };
        var (resolver, _) = Create(new[] { "AAPL", "MSFT", "ORCL", "TSLA" }, tags, wl);
        var result = await resolver.ResolveAsync(new[] { RlSourceDescriptor.Tag("  Tech ") });
        // NVDA is only in a watchlist: the candidate set is provider tickers + profile tickers + displayed rows.
        Assert.Equal(new[] { "AAPL", "MSFT", "NVDA" }, result.Symbols);
    }

    [Fact]
    public async Task CustomCategoryIsTheUnionOfItsListFoldersAndAListFolderIsItsOwnTickers()
    {
        var (resolver, store) = Create(Array.Empty<string>(), NoTags());
        var folderA = new TickerListFolderSettings { Name = "A", ChildTickers = new() { "GOOGL", "msft" } };
        var folderB = new TickerListFolderSettings { Name = "B", ChildTickers = new() { "MSFT", "AMZN" } };
        var category = new TickerParentCategorySettings { Name = "Mine", Folders = new() { folderA, folderB } };
        store.ImportCustomTickerCategories(new[] { category });
        var union = await resolver.ResolveAsync(new[] { RlSourceDescriptor.CustomCategory(category.Id) });
        Assert.Equal(new[] { "AMZN", "GOOGL", "msft" }, union.Symbols);
        var single = await resolver.ResolveAsync(new[] { RlSourceDescriptor.ListFolder(folderB.Id) });
        Assert.Equal(new[] { "AMZN", "MSFT" }, single.Symbols);
        var deleted = RlSourceDescriptor.ListFolder(Guid.NewGuid());
        var gone = await resolver.ResolveAsync(new[] { RlSourceDescriptor.CustomCategory(Guid.NewGuid()), deleted });
        Assert.Empty(gone.Symbols); Assert.Equal(2, gone.Diagnostics.Length);
        // A folder id is not a category id and vice versa.
        Assert.Single((await resolver.ResolveAsync(new[] { RlSourceDescriptor.CustomCategory(folderA.Id) })).Diagnostics);
    }

    [Fact]
    public async Task SavedFilterAppliesItsRulesToItsParentProfile()
    {
        var wl = Profile("W", false, "AAPL", "MSFT", "TSLA");
        var tags = new Dictionary<string, string> { ["AAPL"] = "Growth, Tech", ["MSFT"] = "Tech", ["TSLA"] = "Growth", ["GOOGL"] = "Growth" };
        var (resolver, store) = Create(new[] { "AAPL", "MSFT", "TSLA", "GOOGL" }, tags, wl);
        var filter = new FilterSettings { Name = "Growth", ParentId = wl.Id, Rules = new() { new FilterRule { Field = "Tag", Operator = "Contains", Value = "Growth" } } };
        store.Groups.Add(new FilterNode(filter));
        var result = await resolver.ResolveAsync(new[] { RlSourceDescriptor.SavedFilter(filter.Id) });
        Assert.Equal(new[] { "AAPL", "TSLA" }, result.Symbols);
        var everything = new FilterSettings { Name = "All Growth", ParentId = AllTickersNode.StaticId, Rules = filter.Rules };
        store.Groups.Add(new FilterNode(everything));
        Assert.Equal(new[] { "AAPL", "GOOGL", "TSLA" }, (await resolver.ResolveAsync(new[] { RlSourceDescriptor.SavedFilter(everything.Id) })).Symbols);
        Assert.Single((await resolver.ResolveAsync(new[] { RlSourceDescriptor.SavedFilter(Guid.NewGuid()) })).Diagnostics);
    }

    [Fact]
    public async Task SavedFilterUnderACustomCategoryUsesThatCategorysTickersNotEveryTicker()
    {
        var tags = new Dictionary<string, string> { ["AAPL"] = "Growth", ["GOOGL"] = "Growth", ["TSLA"] = "Growth" };
        var (resolver, store) = Create(new[] { "AAPL", "GOOGL", "TSLA" }, tags);
        var folder = new TickerListFolderSettings { Name = "F", ChildTickers = new() { "GOOGL", "TSLA" } };
        var category = new TickerParentCategorySettings { Name = "C", Folders = new() { folder } };
        store.ImportCustomTickerCategories(new[] { category });
        var node = (CustomCategoryNode)store.Groups.First(n => n.Id == category.Id);
        var filter = new FilterSettings { Name = "G", ParentId = category.Id, Rules = new() { new FilterRule { Field = "Tag", Operator = "Contains", Value = "Growth" } } };
        node.Children!.Add(new FilterNode(filter));
        Assert.Equal(new[] { "GOOGL", "TSLA" }, (await resolver.ResolveAsync(new[] { RlSourceDescriptor.SavedFilter(filter.Id) })).Symbols);
    }

    private static FakeTickerStateStore RowStore(TickerGroupNode node, IEnumerable<IFilterableSymbol> rows)
    {
        var store = new FakeTickerStateStore();
        store.Groups.Add(node);
        store.Rows.AddRange(rows);
        return store;
    }

    [Fact]
    public async Task DisplayedRowsWithUnfinishedHydrationNeverChangeASavedFilterOrTagResult()
    {
        var wl = Profile("W", false, "AAPL", "TSLA");
        var tags = new Dictionary<string, string> { ["AAPL"] = "Growth", ["TSLA"] = "Growth", ["MSFT"] = "Value" };
        var filter = new FilterSettings { Name = "G", ParentId = wl.Id, Rules = new() { new FilterRule { Field = "Tag", Operator = "Contains", Value = "Growth" } } };
        // Unhydrated rows (no tag yet) exist for TSLA and MSFT; a hydrated row carries a live tag for AAPL.
        var rows = new[] { new WatchlistItemViewModel("TSLA", "TSLA", "", "", 0, 0, 0, 0, 0, 0, 0), new WatchlistItemViewModel("MSFT", "MSFT", "", "", 0, 0, 0, 0, 0, 0, 0),
            new WatchlistItemViewModel("AAPL", "AAPL", "", "", 0, 0, 0, 0, 0, 0, 0) { Tag = "Growth, Live" } };
        var resolver = new TickerSourceResolver(new FakeMarketDataProvider(new[] { "AAPL", "TSLA", "MSFT" }, tags), new FakeWatchlistManager(new[] { wl }),
            RowStore(new FilterNode(filter), rows));
        Assert.Equal(new[] { "AAPL", "TSLA" }, (await resolver.ResolveAsync(new[] { RlSourceDescriptor.SavedFilter(filter.Id) })).Symbols);
        Assert.Equal(new[] { "AAPL", "TSLA" }, (await resolver.ResolveAsync(new[] { RlSourceDescriptor.Tag("growth") })).Symbols);
        Assert.Equal(new[] { "AAPL" }, (await resolver.ResolveAsync(new[] { RlSourceDescriptor.Tag("live") })).Symbols);
    }

    [Fact]
    public async Task CombinationIsCanonicalCaseInsensitivelyDeduplicatedAndExcludable()
    {
        var wl = Profile("W", false, "AAPL", "NVDA");
        var (resolver, store) = Create(new[] { "AAPL", "TSLA" }, NoTags(), wl);
        var folder = new TickerListFolderSettings { Name = "F", ChildTickers = new() { "Nvda", "AMZN", "  ", " msft " } };
        store.ImportCustomTickerCategories(new[] { new TickerParentCategorySettings { Name = "C", Folders = new() { folder } } });
        var sources = new[] { RlSourceDescriptor.ListFolder(folder.Id), RlSourceDescriptor.Watchlist(wl.Id), RlSourceDescriptor.AllTickers() };
        var forward = await resolver.ResolveAsync(sources);
        var reversed = await resolver.ResolveAsync(sources.Reverse().ToArray());
        // All Tickers is processed first, then the watchlist, then the folder, whatever the selection order: first spelling wins.
        Assert.Equal(new[] { "AAPL", "AMZN", "NVDA", "TSLA", "msft" }, forward.Symbols);
        Assert.Equal(forward.Symbols.ToArray(), reversed.Symbols.ToArray());
        Assert.Equal(forward.Symbols.ToArray(), (await resolver.ResolveAsync(sources.Concat(sources).ToArray())).Symbols.ToArray());
        var excluded = await resolver.ResolveAsync(sources, new[] { " amzn ", "MSFT", "UNKNOWN" });
        Assert.Equal(new[] { "AAPL", "NVDA", "TSLA" }, excluded.Symbols);
    }

    [Fact]
    public async Task EmptyInputsGiveAnEmptyArrayAndBadArgumentsAreRejected()
    {
        var (resolver, _) = Create(Array.Empty<string>(), NoTags());
        var empty = await resolver.ResolveAsync(Array.Empty<RlSourceDescriptor>());
        Assert.False(empty.Symbols.IsDefault); Assert.Empty(empty.Symbols); Assert.Empty(empty.Diagnostics);
        Assert.Empty((await resolver.ResolveAsync(new[] { RlSourceDescriptor.AllTickers() })).Symbols);
        await Assert.ThrowsAsync<ArgumentNullException>(() => resolver.ResolveAsync(null!));
        Assert.Throws<ArgumentNullException>(() => new TickerSourceResolver(null!, new FakeWatchlistManager(), new FakeTickerStateStore()));
        Assert.Throws<ArgumentNullException>(() => new TickerSourceResolver(new FakeMarketDataProvider(), null!, new FakeTickerStateStore()));
        Assert.Throws<ArgumentNullException>(() => new TickerSourceResolver(new FakeMarketDataProvider(), new FakeWatchlistManager(), null!));
        Assert.Throws<ArgumentException>(() => RlSourceDescriptor.Tag("  "));
        Assert.Throws<ArgumentException>(() => RlSourceDescriptor.SavedFilter(Guid.Empty));
        Assert.Throws<ArgumentException>(() => RlSourceDescriptor.CustomCategory(Guid.Empty));
        Assert.Throws<ArgumentException>(() => RlSourceDescriptor.ListFolder(Guid.Empty));
        Assert.Throws<ArgumentException>(() => RlSourceDescriptor.Watchlist(Guid.Empty));
        Assert.Equal(RlSourceDescriptor.Tag("Tech"), RlSourceDescriptor.Tag(" Tech "));
    }

    [Fact]
    public async Task CancellationIsObservedBeforeAnySourceIsRead()
    {
        var (resolver, _) = Create(new[] { "AAPL" }, NoTags());
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ResolveAsync(new[] { RlSourceDescriptor.AllTickers() }, null, cancelled.Token));
    }

    [Fact]
    public async Task ResultsEqualTheScreenersResolutionForTheSharedKinds()
    {
        var wl = Profile("W", false, "AAPL", "MSFT"); var pf = Profile("P", true, "TSLA", "MSFT");
        var tags = new Dictionary<string, string> { ["AAPL"] = "Growth, Tech", ["MSFT"] = "Tech", ["TSLA"] = "Growth", ["GOOGL"] = "growth" };
        var all = new[] { "AAPL", "MSFT", "TSLA", "GOOGL" };
        var (resolver, store) = Create(all, tags, wl, pf);
        var filter = new FilterSettings { Name = "G", ParentId = wl.Id, Rules = new() { new FilterRule { Field = "Tag", Operator = "Contains", Value = "Tech" } } };
        var market = new FakeMarketDataProvider(all, tags); var manager = new FakeWatchlistManager(new[] { wl, pf });
        var screener = new ScreenerViewModel(null, null, market, manager);
        var cases = new (RlSourceDescriptor Source, ScreenerTargetSource Screener)[]
        {
            (RlSourceDescriptor.AllTickers(), new(TargetSourceType.AllTickers, "all")),
            (RlSourceDescriptor.Watchlist(wl.Id), new(TargetSourceType.Watchlist, "w", profileId: wl.Id)),
            (RlSourceDescriptor.Portfolio(), new(TargetSourceType.Portfolio, "p")),
            (RlSourceDescriptor.Watchlist(), new(TargetSourceType.Watchlist, "w")),
            (RlSourceDescriptor.Tag("growth"), new(TargetSourceType.TagFilter, "t", tagName: "growth")),
            (RlSourceDescriptor.SavedFilter(filter.Id), new(TargetSourceType.SavedFilter, "f", profileId: filter.ParentId, filterSettings: filter)),
        };
        store.Groups.Add(new FilterNode(filter));
        foreach (var (source, screenerSource) in cases)
        {
            await screener.UpdateTargetSymbolsAsync(screenerSource);
            var expected = screener.TargetSymbols.OrderBy(s => s, StringComparer.Ordinal).ToArray();
            Assert.Equal(expected, (await resolver.ResolveAsync(new[] { source })).Symbols);
        }
    }
}
