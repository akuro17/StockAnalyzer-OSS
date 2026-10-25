using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Core.ReadOnly;
using Xunit;

namespace StockAnalyzer.Core.Tests.ReadOnly;

public sealed class TickerGroupTreeTests
{
    [Fact]
    public void Build_WhenWorkspaceNull_ReturnsThreeFixedRoots()
    {
        var registry = new[] { "MSFT", "AAPL" };
        var tree = TickerGroupTree.Build(null, registry);

        Assert.False(tree.WorkspaceFound);
        Assert.Equal(0, tree.DroppedFilterCount);
        Assert.False(tree.TruncatedAtDepth);
        Assert.Equal(3, tree.Nodes.Count);

        var allTickers = tree.Nodes[0];
        Assert.Equal(TickerGroupIds.AllTickers, allTickers.Id);
        Assert.Null(allTickers.ParentId);
        Assert.Equal(TickerGroupKind.AllTickers, allTickers.Kind);
        Assert.Equal(new[] { "MSFT", "AAPL" }, allTickers.Members);

        var watchlists = tree.Nodes[1];
        Assert.Equal(TickerGroupIds.Watchlists, watchlists.Id);
        Assert.Equal(TickerGroupKind.WatchlistCategory, watchlists.Kind);
        Assert.Empty(watchlists.Members!);

        var portfolios = tree.Nodes[2];
        Assert.Equal(TickerGroupIds.Portfolios, portfolios.Id);
        Assert.Equal(TickerGroupKind.PortfolioCategory, portfolios.Kind);
        Assert.Empty(portfolios.Members!);
    }

    [Fact]
    public void Build_SortsWatchlistsAndPortfoliosByNameWithIdTieBreak()
    {
        var id1 = Guid.Parse("00000000-0000-0000-0000-000000000010");
        var id2 = Guid.Parse("00000000-0000-0000-0000-000000000020");
        var id3 = Guid.Parse("00000000-0000-0000-0000-000000000030");

        var settings = new WorkspaceSettings
        {
            WatchlistProfiles = new List<WatchlistProfile>
            {
                new(id2, "Alpha", IndicatorColor.FromRgb(0, 0, 255), isPortfolio: false, new[] { new WatchlistItem("MSFT", DateTimeOffset.UtcNow) }),
                new(id1, "alpha", IndicatorColor.FromRgb(0, 0, 255), isPortfolio: false, new[] { new WatchlistItem("AAPL", DateTimeOffset.UtcNow) }),
                new(id3, "Beta", IndicatorColor.FromRgb(0, 0, 255), isPortfolio: true, new[] { new WatchlistItem("NVDA", DateTimeOffset.UtcNow) })
            }
        };

        var tree = TickerGroupTree.Build(settings, new[] { "MSFT", "AAPL", "NVDA" });
        Assert.True(tree.WorkspaceFound);

        // Pre-order nodes under Watchlists: Watchlists root -> Alpha (id1) -> Alpha (id2)
        var watchlistChildren = tree.Nodes.Where(n => n.Kind == TickerGroupKind.Watchlist).ToList();
        Assert.Equal(2, watchlistChildren.Count);
        Assert.Equal(id1, watchlistChildren[0].Id);
        Assert.Equal(id2, watchlistChildren[1].Id);

        var watchlistsRoot = tree.Nodes.Single(n => n.Kind == TickerGroupKind.WatchlistCategory);
        Assert.Equal(new[] { "AAPL", "MSFT" }, watchlistsRoot.Members);

        var portfoliosRoot = tree.Nodes.Single(n => n.Kind == TickerGroupKind.PortfolioCategory);
        Assert.Equal(new[] { "NVDA" }, portfoliosRoot.Members);
    }

    [Fact]
    public void Build_HandlesCustomCategoriesAndFolders()
    {
        var catId = Guid.NewGuid();
        var folderId = Guid.NewGuid();

        var settings = new WorkspaceSettings
        {
            CustomTickerCategories = new List<TickerParentCategorySettings>
            {
                new()
                {
                    Id = catId,
                    Name = "My Custom Category",
                    Folders = new List<TickerListFolderSettings>
                    {
                        new()
                        {
                            Id = folderId,
                            Name = "My Folder",
                            ChildTickers = new List<string> { "7203.T", "msft" }
                        }
                    }
                }
            }
        };

        var tree = TickerGroupTree.Build(settings, Array.Empty<string>());

        var customCat = tree.Nodes.Single(n => n.Kind == TickerGroupKind.CustomCategory);
        Assert.Equal(catId, customCat.Id);
        Assert.Equal("My Custom Category", customCat.Name);
        Assert.Equal(new[] { "7203-T", "MSFT" }, customCat.Members);

        var folder = tree.Nodes.Single(n => n.Kind == TickerGroupKind.ListFolder);
        Assert.Equal(folderId, folder.Id);
        Assert.Equal(catId, folder.ParentId);
        Assert.Equal(1, folder.Depth);
        Assert.Equal(new[] { "7203-T", "MSFT" }, folder.Members);
    }

    [Fact]
    public void Build_AttachesFiltersAndTracksDroppedAndDepthTruncation()
    {
        var validFilterId = Guid.NewGuid();
        var childFilterId = Guid.NewGuid();
        var unparentedFilterId = Guid.NewGuid();
        var emptyParentFilterId = Guid.NewGuid();

        var settings = new WorkspaceSettings
        {
            TagFilters = new List<FilterSettings>
            {
                new()
                {
                    Id = validFilterId,
                    Name = "Root Filter",
                    ParentId = TickerGroupIds.AllTickers,
                    Rules = new List<FilterRule>
                    {
                        new() { Field = "Close", Operator = ">", Value = "100" }
                    },
                    Children = new List<FilterSettings>
                    {
                        new()
                        {
                            Id = childFilterId,
                            Name = "Child Filter",
                            Rules = new List<FilterRule>()
                        }
                    }
                },
                new()
                {
                    Id = unparentedFilterId,
                    Name = "Orphan Filter",
                    ParentId = Guid.NewGuid() // Does not exist
                },
                new()
                {
                    Id = emptyParentFilterId,
                    Name = "Empty Parent Filter",
                    ParentId = Guid.Empty
                }
            }
        };

        var tree = TickerGroupTree.Build(settings, Array.Empty<string>());

        Assert.Equal(2, tree.DroppedFilterCount); // orphan and Guid.Empty
        Assert.False(tree.TruncatedAtDepth);

        var rootFilter = tree.Nodes.Single(n => n.Id == validFilterId);
        Assert.Equal(TickerGroupKind.Filter, rootFilter.Kind);
        Assert.Equal(TickerGroupIds.AllTickers, rootFilter.ParentId);
        Assert.Equal(1, rootFilter.Depth);
        Assert.Null(rootFilter.Members);
        Assert.Single(rootFilter.Rules!);

        var childFilter = tree.Nodes.Single(n => n.Id == childFilterId);
        Assert.Equal(TickerGroupKind.Filter, childFilter.Kind);
        Assert.Equal(validFilterId, childFilter.ParentId);
        Assert.Equal(2, childFilter.Depth);
        Assert.Null(childFilter.Members);
    }

    [Fact]
    public void Build_TruncatesSubtreeBeyondMaxDepth()
    {
        var settings = new WorkspaceSettings
        {
            TagFilters = new List<FilterSettings>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    Name = "Filter 1",
                    ParentId = TickerGroupIds.AllTickers, // Depth 1
                    Children = new List<FilterSettings>
                    {
                        new()
                        {
                            Id = Guid.NewGuid(),
                            Name = "Filter 2", // Depth 2
                            Children = new List<FilterSettings>
                            {
                                new()
                                {
                                    Id = Guid.NewGuid(),
                                    Name = "Filter 3" // Depth 3 (exceeds maxDepth = 2)
                                }
                            }
                        }
                    }
                }
            }
        };

        var tree = TickerGroupTree.Build(settings, Array.Empty<string>(), maxDepth: 2);
        Assert.True(tree.TruncatedAtDepth);
        Assert.DoesNotContain(tree.Nodes, n => n.Name == "Filter 3");
    }

    [Fact]
    public void Build_WithDuplicateOrEmptyGuids_SkipsDuplicatesAndPreservesFirstWins()
    {
        var dupWatchlistId = Guid.NewGuid();
        var validCatId = Guid.NewGuid();
        var dupFolderId = Guid.NewGuid();
        var validFilterId = Guid.NewGuid();

        var settings = new WorkspaceSettings
        {
            WatchlistProfiles = new List<WatchlistProfile>
            {
                new(dupWatchlistId, "FirstWatchlist", IndicatorColor.FromRgb(0, 0, 255), isPortfolio: false, new[] { new WatchlistItem("AAPL", DateTimeOffset.UtcNow) }),
                new(dupWatchlistId, "SecondWatchlistDuplicate", IndicatorColor.FromRgb(0, 0, 255), isPortfolio: false, new[] { new WatchlistItem("MSFT", DateTimeOffset.UtcNow) }),
                new(TickerGroupIds.AllTickers, "CollisionWithRootAll", IndicatorColor.FromRgb(0, 0, 255), isPortfolio: false)
            },
            CustomTickerCategories = new List<TickerParentCategorySettings>
            {
                new()
                {
                    Id = validCatId,
                    Name = "ValidCat",
                    Folders = new List<TickerListFolderSettings>
                    {
                        new() { Id = dupFolderId, Name = "FirstFolder" },
                        new() { Id = dupFolderId, Name = "DuplicateFolder" },
                        new() { Id = Guid.Empty, Name = "EmptyFolder" }
                    }
                },
                new()
                {
                    Id = validCatId,
                    Name = "DuplicateCat"
                },
                new()
                {
                    Id = Guid.Empty,
                    Name = "EmptyCat"
                }
            },
            TagFilters = new List<FilterSettings>
            {
                new()
                {
                    Id = validFilterId,
                    ParentId = TickerGroupIds.AllTickers,
                    Name = "ValidFilter",
                    Children = new List<FilterSettings>
                    {
                        new() { Id = validFilterId, Name = "DuplicateChildFilter" },
                        new() { Id = Guid.Empty, Name = "EmptyChildFilter" }
                    }
                },
                new()
                {
                    Id = validFilterId,
                    ParentId = TickerGroupIds.AllTickers,
                    Name = "DuplicateTopFilter"
                }
            }
        };

        var tree = TickerGroupTree.Build(settings, new[] { "AAPL", "MSFT", "NVDA" });

        var ids = tree.Nodes.Select(n => n.Id).ToList();
        Assert.All(ids, id => Assert.NotEqual(Guid.Empty, id));
        Assert.Equal(ids.Distinct().Count(), ids.Count);

        Assert.Single(tree.Nodes.Where(n => n.Kind == TickerGroupKind.Watchlist));
        Assert.Equal("FirstWatchlist", tree.Nodes.Single(n => n.Kind == TickerGroupKind.Watchlist).Name);

        Assert.Single(tree.Nodes.Where(n => n.Kind == TickerGroupKind.CustomCategory));
        Assert.Single(tree.Nodes.Where(n => n.Kind == TickerGroupKind.ListFolder));

        Assert.Single(tree.Nodes.Where(n => n.Kind == TickerGroupKind.Filter));
        Assert.Equal("ValidFilter", tree.Nodes.Single(n => n.Kind == TickerGroupKind.Filter).Name);

        Assert.Equal(3, tree.DroppedFilterCount);
    }
}
