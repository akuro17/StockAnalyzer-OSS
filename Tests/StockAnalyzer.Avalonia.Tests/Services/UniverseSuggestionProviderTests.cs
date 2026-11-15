using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.TickerList;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Watchlist;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Services;

public sealed class UniverseSuggestionProviderTests
{
    private sealed class StubTickerStateStore : ITickerStateStore
    {
        public StubTickerStateStore(IEnumerable<TickerGroupNode> groups)
        {
            Groups = groups;
        }

        public IEnumerable<TickerGroupNode> Groups { get; }
        public IEnumerable<IFilterableSymbol> DisplayItems => Array.Empty<IFilterableSymbol>();
        public IReadOnlyList<string> GetTickersForNode(TickerGroupNode? node) => Array.Empty<string>();
    }

    private sealed class NoOpCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) { }
    }

    private static string FakeLocalizer(string key) => key switch
    {
        "TickerList_TypeName_Watchlist" => "Watchlist",
        "TickerList_TypeName_Portfolio" => "Portfolio",
        "Prl.Source.Kind.ListFolder" => "List",
        "Note_SearchSuggestion_TypeName_Filter" => "Filter",
        _ => $"[{key}]"
    };

    private static WatchlistNode MakeWatchlist(Guid id, string name) =>
        new(new WatchlistProfile(id, name, IndicatorColor.FromRgb(0, 0, 255), isPortfolio: false, Array.Empty<WatchlistItem>()));

    private static PortfolioNode MakePortfolio(Guid id, string name) =>
        new(new WatchlistProfile(id, name, IndicatorColor.FromRgb(255, 0, 0), isPortfolio: true, Array.Empty<WatchlistItem>()));

    [Fact]
    public void GetSuggestions_IncludesOnlyListsAndFilters_SortedByKindThenDisplayName()
    {
        var wlB = MakeWatchlist(Guid.NewGuid(), "Beta");
        var wlA = MakeWatchlist(Guid.NewGuid(), "Alpha");
        var pfZ = MakePortfolio(Guid.NewGuid(), "Zeta");
        var folder = new TickerListFolderNode(new TickerListFolderSettings { Id = Guid.NewGuid(), Name = "Semi" });
        var filter = new FilterNode(new FilterSettings { Id = Guid.NewGuid(), Name = "Momentum" });

        var allTickers = new AllTickersNode("All Tickers");
        allTickers.Children!.Add(filter);

        var watchlistsCat = new CategoryNode(new Guid("00000000-0000-0000-0000-000000000002"), "Watchlists");
        watchlistsCat.Children!.Add(wlB);
        watchlistsCat.Children.Add(wlA);
        watchlistsCat.Children.Add(new ActionNode(Guid.NewGuid(), "New Watchlist", new NoOpCommand()));

        var portfoliosCat = new CategoryNode(new Guid("00000000-0000-0000-0000-000000000003"), "Portfolios");
        portfoliosCat.Children!.Add(pfZ);

        var customCat = new CustomCategoryNode(new TickerParentCategorySettings { Id = Guid.NewGuid(), Name = "Sectors" });
        customCat.Children!.Add(folder);

        var provider = new UniverseSuggestionProvider(
            new StubTickerStateStore(new TickerGroupNode[] { allTickers, watchlistsCat, portfoliosCat, customCat }),
            FakeLocalizer);

        var suggestions = provider.GetSuggestions();

        Assert.Equal(5, suggestions.Count);
        Assert.Equal(
            new[]
            {
                (UniverseSuggestionKind.Watchlist, "Alpha", "Alpha"),
                (UniverseSuggestionKind.Watchlist, "Beta", "Beta"),
                (UniverseSuggestionKind.Portfolio, "Zeta", "Zeta"),
                (UniverseSuggestionKind.CustomList, "Semi", "Semi"),
                (UniverseSuggestionKind.Filter, "Momentum", "Momentum")
            },
            suggestions.Select(s => (s.Kind, s.DisplayName, s.DisplayText)));

        Assert.DoesNotContain(suggestions, s => s.GroupId == allTickers.Id || s.GroupId == watchlistsCat.Id || s.GroupId == customCat.Id);
    }

    [Fact]
    public void GetSuggestions_DisambiguatesDuplicateNames_OnlyForDuplicates()
    {
        var wlCore = MakeWatchlist(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Core");
        var folderCore = new TickerListFolderNode(new TickerListFolderSettings { Id = Guid.Parse("22222222-2222-2222-2222-222222222222"), Name = "Core" });
        var uniqueWl = MakeWatchlist(Guid.Parse("33333333-3333-3333-3333-333333333333"), "Unique");

        var watchlistsCat = new CategoryNode(new Guid("00000000-0000-0000-0000-000000000002"), "Watchlists");
        watchlistsCat.Children!.Add(wlCore);
        watchlistsCat.Children.Add(uniqueWl);

        var customCat = new CustomCategoryNode(new TickerParentCategorySettings { Id = Guid.NewGuid(), Name = "Strategies" });
        customCat.Children!.Add(folderCore);

        var provider = new UniverseSuggestionProvider(
            new StubTickerStateStore(new TickerGroupNode[] { watchlistsCat, customCat }),
            FakeLocalizer);

        var suggestions = provider.GetSuggestions();

        var coreWatchlist = suggestions.Single(s => s.GroupId == wlCore.Id);
        var coreFolder = suggestions.Single(s => s.GroupId == folderCore.Id);
        var unique = suggestions.Single(s => s.GroupId == uniqueWl.Id);

        Assert.Equal("Core (Watchlist)", coreWatchlist.DisplayText);
        Assert.Equal("Core (Strategies · List)", coreFolder.DisplayText);
        Assert.Equal("Unique", unique.DisplayText);
    }

    [Fact]
    public void ResolveTargetGroupId_PrioritizesSelectedNode_ThenContainingStaticLists()
    {
        var wl = MakeWatchlist(Guid.NewGuid(), "WL");
        var pf = MakePortfolio(Guid.NewGuid(), "PF");
        var filter = new FilterNode(new FilterSettings { Id = Guid.NewGuid(), Name = "HighVol" });

        var provider = new UniverseSuggestionProvider(
            new StubTickerStateStore(new TickerGroupNode[] { wl, pf, filter }),
            FakeLocalizer);

        Assert.Equal(filter.Id, provider.ResolveTargetGroupId(new TickerRowClickedMessage("AAPL", filter.Id, new[] { wl.Id, pf.Id })));
        Assert.Equal(wl.Id, provider.ResolveTargetGroupId(new TickerRowClickedMessage("AAPL", null, new[] { wl.Id, pf.Id })));
        Assert.Null(provider.ResolveTargetGroupId(new TickerRowClickedMessage("MSFT", AllTickersNode.StaticId, Array.Empty<Guid>())));
    }
}
