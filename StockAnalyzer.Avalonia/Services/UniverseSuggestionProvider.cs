using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.ViewModels.TickerList;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>
/// Categorizes workspace ticker groups eligible for <c>UniverseGroupId</c> suggestions.
/// Values define deterministic display group ordering: Lists before Filters.
/// </summary>
public enum UniverseSuggestionKind
{
    Watchlist = 0,
    Portfolio = 1,
    CustomList = 2,
    Filter = 3
}

/// <summary>
/// AutoComplete suggestion item representing a <see cref="Guid"/>-backed workspace List or Filter group.
/// Implements <see cref="ISuggestionRow"/> so <c>SuggestionRowTemplate</c> renders identically to Tickers tab suggestions.
/// </summary>
public sealed class UniverseGroupSuggestion : ISuggestionRow
{
    public UniverseGroupSuggestion(
        Guid groupId,
        string displayName,
        UniverseSuggestionKind kind,
        string iconGeometry,
        string kindLabel,
        string? parentDisplayName = null,
        string? displayText = null)
    {
        GroupId = groupId;
        CanonicalId = groupId.ToString("D");
        DisplayName = displayName ?? string.Empty;
        Kind = kind;
        IconGeometry = iconGeometry ?? string.Empty;
        KindLabel = kindLabel ?? string.Empty;
        ParentDisplayName = parentDisplayName;
        DisplayText = string.IsNullOrEmpty(displayText) ? DisplayName : displayText;
    }

    public Guid GroupId { get; }

    public string CanonicalId { get; }

    public string DisplayName { get; }

    public string? ParentDisplayName { get; }

    public UniverseSuggestionKind Kind { get; }

    public string IconGeometry { get; }

    public string KindLabel { get; }

    public string DisplayText { get; internal set; }

    public override string ToString() => DisplayText;
}

/// <summary>
/// Provides workspace List/Filter AutoComplete suggestions and resolves target group IDs
/// when the user clicks a ticker row in the Tickers tab.
/// </summary>
public interface IUniverseSuggestionProvider
{
    IReadOnlyList<UniverseGroupSuggestion> GetSuggestions();

    UniverseGroupSuggestion? FindByGroupId(Guid groupId);

    Guid? ResolveTargetGroupId(TickerRowClickedMessage message);
}

/// <summary>
/// In-memory synchronous provider over <see cref="ITickerStateStore.Groups"/> for <c>UniverseGroupId</c>.
/// Excludes <see cref="AllTickersNode"/>, <see cref="CategoryNode"/>, <see cref="CustomCategoryNode"/>, <see cref="ActionNode"/>,
/// and ticker metadata Tags per owner specification.
/// </summary>
public sealed class UniverseSuggestionProvider : IUniverseSuggestionProvider
{
    private readonly ITickerStateStore _tickerStateStore;
    private readonly Func<string, string?> _localizer;

    public UniverseSuggestionProvider(
        ITickerStateStore tickerStateStore,
        Func<string, string?>? localizer = null)
    {
        _tickerStateStore = tickerStateStore ?? throw new ArgumentNullException(nameof(tickerStateStore));
        _localizer = localizer ?? (key => LocalizationManager.Instance[key]);
    }

    public IReadOnlyList<UniverseGroupSuggestion> GetSuggestions()
    {
        var items = new List<UniverseGroupSuggestion>();
        var visited = new HashSet<Guid>();

        CollectNodes(_tickerStateStore.Groups, parentNode: null, items, visited);

        if (items.Count == 0)
        {
            return items;
        }

        ApplyDuplicateDisambiguation(items);

        items.Sort(static (a, b) =>
        {
            int kindCmp = ((int)a.Kind).CompareTo((int)b.Kind);
            if (kindCmp != 0) return kindCmp;

            int nameCmp = StringComparer.CurrentCultureIgnoreCase.Compare(a.DisplayName, b.DisplayName);
            if (nameCmp != 0) return nameCmp;

            int textCmp = StringComparer.CurrentCultureIgnoreCase.Compare(a.DisplayText, b.DisplayText);
            if (textCmp != 0) return textCmp;

            return StringComparer.Ordinal.Compare(a.CanonicalId, b.CanonicalId);
        });

        return items;
    }

    public UniverseGroupSuggestion? FindByGroupId(Guid groupId)
    {
        if (groupId == Guid.Empty) return null;

        foreach (var suggestion in GetSuggestions())
        {
            if (suggestion.GroupId == groupId)
            {
                return suggestion;
            }
        }

        return null;
    }

    public Guid? ResolveTargetGroupId(TickerRowClickedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var suggestions = GetSuggestions();
        if (suggestions.Count == 0)
        {
            return null;
        }

        var availableIds = new HashSet<Guid>(suggestions.Count);
        foreach (var suggestion in suggestions)
        {
            availableIds.Add(suggestion.GroupId);
        }

        if (message.SelectedNodeId is Guid selectedId && availableIds.Contains(selectedId))
        {
            return selectedId;
        }

        foreach (var candidateId in message.ContainingGroupIds)
        {
            if (availableIds.Contains(candidateId))
            {
                return candidateId;
            }
        }

        return null;
    }

    private void CollectNodes(
        IEnumerable<TickerGroupNode>? nodes,
        TickerGroupNode? parentNode,
        List<UniverseGroupSuggestion> target,
        HashSet<Guid> visited)
    {
        if (nodes == null) return;

        foreach (var node in nodes)
        {
            if (node == null) continue;

            if (TryCreateSuggestion(node, parentNode, out var suggestion) && visited.Add(node.Id))
            {
                target.Add(suggestion);
            }

            if (node.Children is { Count: > 0 })
            {
                CollectNodes(node.Children, node, target, visited);
            }
        }
    }

    private bool TryCreateSuggestion(
        TickerGroupNode node,
        TickerGroupNode? parentNode,
        out UniverseGroupSuggestion suggestion)
    {
        string? parentLabel = parentNode is CategoryNode ? null : parentNode?.DisplayName;

        switch (node)
        {
            case WatchlistNode:
                suggestion = new UniverseGroupSuggestion(
                    node.Id,
                    node.DisplayName,
                    UniverseSuggestionKind.Watchlist,
                    TickerGroupIconGeometries.Watchlist,
                    ResolveKindLabel("TickerList_TypeName_Watchlist", "Watchlist"),
                    parentDisplayName: null);
                return true;

            case PortfolioNode:
                suggestion = new UniverseGroupSuggestion(
                    node.Id,
                    node.DisplayName,
                    UniverseSuggestionKind.Portfolio,
                    TickerGroupIconGeometries.Portfolio,
                    ResolveKindLabel("TickerList_TypeName_Portfolio", "Portfolio"),
                    parentDisplayName: null);
                return true;

            case TickerListFolderNode:
                suggestion = new UniverseGroupSuggestion(
                    node.Id,
                    node.DisplayName,
                    UniverseSuggestionKind.CustomList,
                    TickerGroupIconGeometries.Watchlist,
                    ResolveKindLabel("Prl.Source.Kind.ListFolder", "List"),
                    parentLabel);
                return true;

            case FilterNode:
                suggestion = new UniverseGroupSuggestion(
                    node.Id,
                    node.DisplayName,
                    UniverseSuggestionKind.Filter,
                    TickerGroupIconGeometries.Filter,
                    ResolveKindLabel("Note_SearchSuggestion_TypeName_Filter", "Filter"),
                    parentLabel);
                return true;

            default:
                suggestion = null!;
                return false;
        }
    }

    private string ResolveKindLabel(string key, string fallback)
    {
        var localized = _localizer(key);
        if (!string.IsNullOrEmpty(localized) && !localized.StartsWith('['))
        {
            return localized;
        }

        return fallback;
    }

    private static void ApplyDuplicateDisambiguation(List<UniverseGroupSuggestion> items)
    {
        var nameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            nameCounts[item.DisplayName] = nameCounts.TryGetValue(item.DisplayName, out var c) ? c + 1 : 1;
        }

        foreach (var item in items)
        {
            if (nameCounts.TryGetValue(item.DisplayName, out var count) && count > 1)
            {
                item.DisplayText = !string.IsNullOrWhiteSpace(item.ParentDisplayName)
                    ? $"{item.DisplayName} ({item.ParentDisplayName} · {item.KindLabel})"
                    : $"{item.DisplayName} ({item.KindLabel})";
            }
            else
            {
                item.DisplayText = item.DisplayName;
            }
        }

        var textCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            textCounts[item.DisplayText] = textCounts.TryGetValue(item.DisplayText, out var c) ? c + 1 : 1;
        }

        foreach (var item in items)
        {
            if (textCounts.TryGetValue(item.DisplayText, out var count) && count > 1)
            {
                string shortId = item.CanonicalId[..Math.Min(8, item.CanonicalId.Length)];
                item.DisplayText = !string.IsNullOrWhiteSpace(item.ParentDisplayName)
                    ? $"{item.DisplayName} ({item.ParentDisplayName} · {item.KindLabel} #{shortId})"
                    : $"{item.DisplayName} ({item.KindLabel} #{shortId})";
            }
        }
    }
}
