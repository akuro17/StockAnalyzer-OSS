using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Avalonia.ViewModels.TickerList;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>One named ticker list (a watchlist, portfolio, category or list folder) a replay page can offer as a suggestion set.</summary>
public sealed record RlTickerListChoice(Guid Id, string Name);

/// <summary>
/// The seam from the RL Playback page to the Tickers tab lists: names to choose from and the tickers of one list. Read on the UI thread
/// (the ticker tree is UI-owned); it never changes the tree or its selection.
/// </summary>
public interface IRlTickerListSource
{
    IReadOnlyList<RlTickerListChoice> GetLists();
    IReadOnlyList<string> GetTickers(Guid id);
}

/// <summary>Reads the lists from the ticker tree: watchlists, portfolios, user categories and their list folders.</summary>
public sealed class TickerStateRlListSource : IRlTickerListSource
{
    private readonly ITickerStateStore _store;

    public TickerStateRlListSource(ITickerStateStore store) => _store = store ?? throw new ArgumentNullException(nameof(store));

    public IReadOnlyList<RlTickerListChoice> GetLists()
    {
        var result = new List<RlTickerListChoice>();
        var seen = new HashSet<Guid>();
        void Visit(TickerGroupNode node, string? path)
        {
            if (node is ActionNode) return;
            bool isList = node is WatchlistNode or PortfolioNode or CustomCategoryNode or TickerListFolderNode;
            string name = path is null ? node.DisplayName : path + " / " + node.DisplayName;
            if (isList && seen.Add(node.Id)) result.Add(new RlTickerListChoice(node.Id, name));
            if (node.Children is not null) foreach (var child in node.Children) Visit(child, isList ? name : path);
        }
        foreach (var group in _store.Groups) Visit(group, null);
        return result;
    }

    public IReadOnlyList<string> GetTickers(Guid id)
    {
        TickerGroupNode? Find(TickerGroupNode node)
        {
            if (node is ActionNode) return null;
            if (node.Id == id && node is WatchlistNode or PortfolioNode or CustomCategoryNode or TickerListFolderNode) return node;
            return node.Children?.Select(Find).FirstOrDefault(found => found is not null);
        }
        var target = _store.Groups.Select(Find).FirstOrDefault(found => found is not null);
        return target is null ? Array.Empty<string>() : _store.GetTickersForNode(target);
    }
}
