using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Watchlist;

namespace StockAnalyzer.Core.ReadOnly;

public enum TickerGroupKind
{
    AllTickers,
    WatchlistCategory,
    Watchlist,
    PortfolioCategory,
    Portfolio,
    CustomCategory,
    ListFolder,
    Filter
}

public static class TickerGroupKindExtensions
{
    public static string ToCamelCase(this TickerGroupKind kind) => kind switch
    {
        TickerGroupKind.AllTickers => "allTickers",
        TickerGroupKind.WatchlistCategory => "watchlistCategory",
        TickerGroupKind.Watchlist => "watchlist",
        TickerGroupKind.PortfolioCategory => "portfolioCategory",
        TickerGroupKind.Portfolio => "portfolio",
        TickerGroupKind.CustomCategory => "customCategory",
        TickerGroupKind.ListFolder => "listFolder",
        TickerGroupKind.Filter => "filter",
        _ => kind.ToString()
    };
}

public sealed record TickerGroupNodeInfo(
    Guid Id,
    Guid? ParentId,
    TickerGroupKind Kind,
    string Name,
    int Depth,
    IReadOnlyList<string>? Members,
    IReadOnlyList<FilterRule>? Rules);

public sealed record TickerGroupTree(
    IReadOnlyList<TickerGroupNodeInfo> Nodes,
    bool WorkspaceFound,
    int DroppedFilterCount,
    bool TruncatedAtDepth)
{
    public const int DefaultMaxDepth = 16;

    public static TickerGroupTree Build(WorkspaceSettings? settings, IReadOnlyList<string> registry, int maxDepth = DefaultMaxDepth)
    {
        var canonicalRegistry = CanonicalizeSymbols(registry);

        if (settings == null)
        {
            var emptyFixedRoots = new List<TickerGroupNodeInfo>
            {
                new(TickerGroupIds.AllTickers, null, TickerGroupKind.AllTickers, TickerGroupIds.AllTickersName, 0, canonicalRegistry, null),
                new(TickerGroupIds.Watchlists, null, TickerGroupKind.WatchlistCategory, TickerGroupIds.WatchlistsName, 0, Array.Empty<string>(), null),
                new(TickerGroupIds.Portfolios, null, TickerGroupKind.PortfolioCategory, TickerGroupIds.PortfoliosName, 0, Array.Empty<string>(), null)
            };
            return new TickerGroupTree(emptyFixedRoots, WorkspaceFound: false, DroppedFilterCount: 0, TruncatedAtDepth: false);
        }

        var roots = new List<MutableNode>();
        var seenIds = new HashSet<Guid>
        {
            TickerGroupIds.AllTickers,
            TickerGroupIds.Watchlists,
            TickerGroupIds.Portfolios
        };

        // 1. All Tickers root
        var allTickersRoot = new MutableNode
        {
            Id = TickerGroupIds.AllTickers,
            ParentId = null,
            Kind = TickerGroupKind.AllTickers,
            Name = TickerGroupIds.AllTickersName,
            Depth = 0,
            Members = canonicalRegistry,
            Rules = null
        };
        roots.Add(allTickersRoot);

        // 2. Watchlists root and sorted children (!IsPortfolio)
        var watchlistsRoot = new MutableNode
        {
            Id = TickerGroupIds.Watchlists,
            ParentId = null,
            Kind = TickerGroupKind.WatchlistCategory,
            Name = TickerGroupIds.WatchlistsName,
            Depth = 0,
            Rules = null
        };
        var watchlistProfiles = (settings.WatchlistProfiles ?? new List<WatchlistProfile>())
            .Where(p => !p.IsPortfolio)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Id)
            .ToList();

        var watchlistUnion = new List<string>();
        var watchlistUnionSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in watchlistProfiles)
        {
            if (profile.Id == Guid.Empty || !seenIds.Add(profile.Id))
            {
                continue;
            }

            var profileMembers = CanonicalizeSymbols(profile.Items?.Select(i => i.Ticker));
            var profileNode = new MutableNode
            {
                Id = profile.Id,
                ParentId = TickerGroupIds.Watchlists,
                Kind = TickerGroupKind.Watchlist,
                Name = profile.Name,
                Depth = 1,
                Members = profileMembers,
                Rules = null
            };
            watchlistsRoot.Children.Add(profileNode);

            foreach (var sym in profileMembers)
            {
                if (watchlistUnionSet.Add(sym))
                {
                    watchlistUnion.Add(sym);
                }
            }
        }
        watchlistsRoot.Members = watchlistUnion;
        roots.Add(watchlistsRoot);

        // 3. Portfolios root and sorted children (IsPortfolio)
        var portfoliosRoot = new MutableNode
        {
            Id = TickerGroupIds.Portfolios,
            ParentId = null,
            Kind = TickerGroupKind.PortfolioCategory,
            Name = TickerGroupIds.PortfoliosName,
            Depth = 0,
            Rules = null
        };
        var portfolioProfiles = (settings.WatchlistProfiles ?? new List<WatchlistProfile>())
            .Where(p => p.IsPortfolio)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Id)
            .ToList();

        var portfolioUnion = new List<string>();
        var portfolioUnionSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in portfolioProfiles)
        {
            if (profile.Id == Guid.Empty || !seenIds.Add(profile.Id))
            {
                continue;
            }

            var profileMembers = CanonicalizeSymbols(profile.Items?.Select(i => i.Ticker));
            var profileNode = new MutableNode
            {
                Id = profile.Id,
                ParentId = TickerGroupIds.Portfolios,
                Kind = TickerGroupKind.Portfolio,
                Name = profile.Name,
                Depth = 1,
                Members = profileMembers,
                Rules = null
            };
            portfoliosRoot.Children.Add(profileNode);

            foreach (var sym in profileMembers)
            {
                if (portfolioUnionSet.Add(sym))
                {
                    portfolioUnion.Add(sym);
                }
            }
        }
        portfoliosRoot.Members = portfolioUnion;
        roots.Add(portfoliosRoot);

        // 4. Custom categories in order
        if (settings.CustomTickerCategories != null)
        {
            foreach (var customCat in settings.CustomTickerCategories)
            {
                if (customCat.Id == Guid.Empty || !seenIds.Add(customCat.Id))
                {
                    continue;
                }

                var customNode = new MutableNode
                {
                    Id = customCat.Id,
                    ParentId = null,
                    Kind = TickerGroupKind.CustomCategory,
                    Name = customCat.Name,
                    Depth = 0,
                    Rules = null
                };

                var catUnion = new List<string>();
                var catUnionSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (customCat.Folders != null)
                {
                    foreach (var folder in customCat.Folders)
                    {
                        if (folder.Id == Guid.Empty || !seenIds.Add(folder.Id))
                        {
                            continue;
                        }

                        var folderMembers = CanonicalizeSymbols(folder.ChildTickers);
                        var folderNode = new MutableNode
                        {
                            Id = folder.Id,
                            ParentId = customCat.Id,
                            Kind = TickerGroupKind.ListFolder,
                            Name = folder.Name,
                            Depth = 1,
                            Members = folderMembers,
                            Rules = null
                        };
                        customNode.Children.Add(folderNode);

                        foreach (var sym in folderMembers)
                        {
                            if (catUnionSet.Add(sym))
                            {
                                catUnion.Add(sym);
                            }
                        }
                    }
                }
                customNode.Members = catUnion;
                roots.Add(customNode);
            }
        }

        // 5. Append TagFilters
        int droppedFilterCount = 0;
        bool truncatedAtDepth = false;

        if (settings.TagFilters != null)
        {
            foreach (var filter in settings.TagFilters)
            {
                if (filter.ParentId == Guid.Empty)
                {
                    droppedFilterCount++;
                    continue;
                }

                var parent = FindNodeById(roots, filter.ParentId);
                if (parent == null)
                {
                    droppedFilterCount++;
                    continue;
                }

                AddFilterSubtree(parent, filter, seenIds, ref droppedFilterCount, ref truncatedAtDepth, maxDepth);
            }
        }

        // 6. Flatten to pre-order list
        var flatNodes = new List<TickerGroupNodeInfo>();
        foreach (var root in roots)
        {
            FlattenPreOrder(root, flatNodes);
        }

        return new TickerGroupTree(flatNodes, WorkspaceFound: true, DroppedFilterCount: droppedFilterCount, TruncatedAtDepth: truncatedAtDepth);
    }

    private static void AddFilterSubtree(
        MutableNode parent,
        FilterSettings filter,
        HashSet<Guid> seenIds,
        ref int droppedFilterCount,
        ref bool truncatedAtDepth,
        int maxDepth)
    {
        if (filter.Id == Guid.Empty || !seenIds.Add(filter.Id))
        {
            droppedFilterCount++;
            return;
        }

        int childDepth = parent.Depth + 1;
        if (childDepth > maxDepth)
        {
            truncatedAtDepth = true;
            return;
        }

        var filterNode = new MutableNode
        {
            Id = filter.Id,
            ParentId = parent.Id,
            Kind = TickerGroupKind.Filter,
            Name = filter.Name,
            Depth = childDepth,
            Members = null,
            Rules = filter.Rules?.ToList() ?? new List<FilterRule>()
        };
        parent.Children.Add(filterNode);

        if (filter.Children != null)
        {
            foreach (var childFilter in filter.Children)
            {
                AddFilterSubtree(filterNode, childFilter, seenIds, ref droppedFilterCount, ref truncatedAtDepth, maxDepth);
            }
        }
    }

    private static MutableNode? FindNodeById(IEnumerable<MutableNode> nodes, Guid targetId)
    {
        foreach (var node in nodes)
        {
            if (node.Id == targetId)
            {
                return node;
            }

            var found = FindNodeById(node.Children, targetId);
            if (found != null)
            {
                return found;
            }
        }
        return null;
    }

    private static void FlattenPreOrder(MutableNode node, List<TickerGroupNodeInfo> result)
    {
        result.Add(new TickerGroupNodeInfo(
            node.Id,
            node.ParentId,
            node.Kind,
            node.Name,
            node.Depth,
            node.Members,
            node.Rules));

        foreach (var child in node.Children)
        {
            FlattenPreOrder(child, result);
        }
    }

    private static List<string> CanonicalizeSymbols(IEnumerable<string>? symbols)
    {
        if (symbols == null)
        {
            return new List<string>();
        }

        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in symbols)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string canonical = TickerSymbol.Canonicalize(raw);
            if (seen.Add(canonical))
            {
                list.Add(canonical);
            }
        }

        return list;
    }

    private sealed class MutableNode
    {
        public Guid Id { get; init; }
        public Guid? ParentId { get; init; }
        public TickerGroupKind Kind { get; init; }
        public string Name { get; init; } = string.Empty;
        public int Depth { get; set; }
        public List<string>? Members { get; set; }
        public IReadOnlyList<FilterRule>? Rules { get; init; }
        public List<MutableNode> Children { get; } = new();
    }
}
