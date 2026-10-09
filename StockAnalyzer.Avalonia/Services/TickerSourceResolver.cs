using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.ViewModels.TickerList;
using StockAnalyzer.Avalonia.ViewModels.Watchlist;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>Source kinds a symbol list can be built from. The ordinal order is the canonical processing order.</summary>
public enum RlSourceKind
{
    AllTickers = 0,
    Watchlist = 1,
    Portfolio = 2,
    Tag = 3,
    SavedFilter = 4,
    CustomCategory = 5,
    ListFolder = 6,
}

/// <summary>
/// One selectable source. <see cref="Id"/> is the profile id (Watchlist/Portfolio; null = every profile of that kind),
/// the saved-filter id, the custom-category id or the list-folder id; <see cref="TagName"/> is only used by Tag.
/// </summary>
public sealed record RlSourceDescriptor
{
    public RlSourceKind Kind { get; }
    public Guid? Id { get; }
    public string? TagName { get; }

    private RlSourceDescriptor(RlSourceKind kind, Guid? id, string? tagName) { Kind = kind; Id = id; TagName = tagName; }

    public static RlSourceDescriptor AllTickers() => new(RlSourceKind.AllTickers, null, null);
    public static RlSourceDescriptor Watchlist(Guid? profileId = null) => new(RlSourceKind.Watchlist, RequireNonEmpty(profileId, nameof(profileId)), null);
    public static RlSourceDescriptor Portfolio(Guid? profileId = null) => new(RlSourceKind.Portfolio, RequireNonEmpty(profileId, nameof(profileId)), null);
    public static RlSourceDescriptor Tag(string tagName)
    {
        ArgumentNullException.ThrowIfNull(tagName);
        string trimmed = tagName.Trim();
        if (trimmed.Length == 0) throw new ArgumentException("A tag name is required.", nameof(tagName));
        return new(RlSourceKind.Tag, null, trimmed);
    }
    public static RlSourceDescriptor SavedFilter(Guid filterId) => new(RlSourceKind.SavedFilter, RequireId(filterId, nameof(filterId)), null);
    public static RlSourceDescriptor CustomCategory(Guid categoryId) => new(RlSourceKind.CustomCategory, RequireId(categoryId, nameof(categoryId)), null);
    public static RlSourceDescriptor ListFolder(Guid folderId) => new(RlSourceKind.ListFolder, RequireId(folderId, nameof(folderId)), null);

    private static Guid RequireId(Guid id, string name) => id != Guid.Empty ? id : throw new ArgumentException("A non-empty id is required.", name);
    private static Guid? RequireNonEmpty(Guid? id, string name) => id is { } value && value == Guid.Empty ? throw new ArgumentException("An empty id is not a profile.", name) : id;
}

public enum RlSourceDiagnosticCode { SourceNotFound }

/// <summary>A source that could not be resolved; it contributes no symbols and never throws.</summary>
public sealed record RlSourceDiagnostic(RlSourceDiagnosticCode Code, RlSourceDescriptor Source);

public sealed record RlSourceResolution(ImmutableArray<string> Symbols, ImmutableArray<RlSourceDiagnostic> Diagnostics);

/// <summary>The seam the portfolio dialog depends on, so its tests can fix the resolved symbols exactly.</summary>
public interface ITickerSourceResolver
{
    Task<RlSourceResolution> ResolveAsync(IReadOnlyList<RlSourceDescriptor> sources, IReadOnlyCollection<string>? excluded = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Turns selected sources (All Tickers, watchlists, portfolios, tags, saved filters, user-created categories and their list folders)
/// into one de-duplicated, canonically ordered symbol list. UI-independent: it reads the ticker tree through
/// <see cref="ITickerStateStore"/>, profiles through <see cref="IWatchlistManager"/> and metadata through <see cref="IMarketDataProvider"/>
/// (local reads only; the explicit network fetch of the provider is never called).
/// Call it from the context that owns the ticker tree (the UI thread): node lookups read UI-owned collections.
/// </summary>
public sealed class TickerSourceResolver : ITickerSourceResolver
{
    private readonly IMarketDataProvider _market;
    private readonly IWatchlistManager _watchlists;
    private readonly ITickerStateStore _store;
    private readonly WatchlistFilterEngine _filters = new();

    public TickerSourceResolver(IMarketDataProvider market, IWatchlistManager watchlists, ITickerStateStore store)
    {
        _market = market ?? throw new ArgumentNullException(nameof(market));
        _watchlists = watchlists ?? throw new ArgumentNullException(nameof(watchlists));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// Sources are processed in canonical order (kind, then id or tag) so the same selection always yields the same list and spelling.
    /// Symbols are trimmed, empty ones dropped, de-duplicated case-insensitively keeping the first spelling, the explicit exclusions
    /// removed (same comparison) and the result sorted ascending by ordinal comparison. An unknown id yields a diagnostic, not an exception.
    /// </summary>
    public async Task<RlSourceResolution> ResolveAsync(IReadOnlyList<RlSourceDescriptor> sources,
        IReadOnlyCollection<string>? excluded = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var ordered = sources.Distinct().OrderBy(s => s.Kind).ThenBy(s => s.Id ?? Guid.Empty)
            .ThenBy(s => s.TagName, StringComparer.OrdinalIgnoreCase).ToArray();
        var diagnostics = ImmutableArray.CreateBuilder<RlSourceDiagnostic>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<string>();
        foreach (var source in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var symbols = await ResolveOneAsync(source, cancellationToken);
            if (symbols is null) { diagnostics.Add(new(RlSourceDiagnosticCode.SourceNotFound, source)); continue; }
            foreach (string raw in symbols)
            {
                string symbol = raw?.Trim() ?? "";
                if (symbol.Length > 0 && seen.Add(symbol)) kept.Add(symbol);
            }
        }
        if (excluded is not null)
        {
            var removed = new HashSet<string>(excluded.Where(e => e is not null).Select(e => e.Trim()), StringComparer.OrdinalIgnoreCase);
            kept.RemoveAll(removed.Contains);
        }
        kept.Sort(StringComparer.Ordinal);
        return new(kept.ToImmutableArray(), diagnostics.ToImmutable());
    }

    /// <summary>Null means the source does not exist; an empty list means it exists and holds nothing.</summary>
    private async Task<IReadOnlyList<string>?> ResolveOneAsync(RlSourceDescriptor source, CancellationToken ct)
    {
        switch (source.Kind)
        {
            case RlSourceKind.AllTickers:
                return (await _market.GetAvailableTickersAsync()).ToList();
            case RlSourceKind.Watchlist:
            case RlSourceKind.Portfolio:
                return ResolveProfiles(source);
            case RlSourceKind.Tag:
                return await ResolveTagAsync(source.TagName!, ct);
            case RlSourceKind.SavedFilter:
                return FindNode<FilterNode>(source.Id!.Value) is { } filter ? await ResolveFilterAsync(filter.Settings, ct) : null;
            case RlSourceKind.CustomCategory:
                return FindNode<CustomCategoryNode>(source.Id!.Value) is { } category ? _store.GetTickersForNode(category) : null;
            case RlSourceKind.ListFolder:
                return FindNode<TickerListFolderNode>(source.Id!.Value) is { } folder ? _store.GetTickersForNode(folder) : null;
            default:
                throw new ArgumentOutOfRangeException(nameof(source));
        }
    }

    private IReadOnlyList<string>? ResolveProfiles(RlSourceDescriptor source)
    {
        bool portfolio = source.Kind == RlSourceKind.Portfolio;
        if (source.Id is { } id)
        {
            var profile = _watchlists.GetProfileById(id);
            return profile is not null && profile.IsPortfolio == portfolio ? profile.Items.Select(i => i.Ticker).ToList() : null;
        }
        return _watchlists.GetAllProfiles().Where(p => p.IsPortfolio == portfolio).SelectMany(p => p.Items.Select(i => i.Ticker)).ToList();
    }

    /// <summary>The Screener's tag candidate set: provider tickers, every profile's tickers and the displayed rows; a live row tag wins over stored metadata.</summary>
    private async Task<IReadOnlyList<string>> ResolveTagAsync(string tag, CancellationToken ct)
    {
        var display = DisplayBySymbol();
        var result = new List<string>();
        foreach (string ticker in await CandidatesAsync())
        {
            ct.ThrowIfCancellationRequested();
            string? live = display.TryGetValue(ticker, out var row) ? row.Tag : null;
            string? final = !string.IsNullOrEmpty(live) ? live : (await _market.GetMetadataAsync(ticker)).Tag;
            if (!string.IsNullOrEmpty(final) && WatchlistFilterEngine.ContainsTag(final, tag)) result.Add(ticker);
        }
        return result;
    }

    /// <summary>
    /// Candidate set of a saved filter: its parent profile; else the parent node's tickers (custom category, list folder, category);
    /// else every known ticker. (The Screener silently widens a filter under a custom category to every ticker; the Tickers tab does not.)
    /// </summary>
    private async Task<IReadOnlyList<string>> ResolveFilterAsync(FilterSettings settings, CancellationToken ct)
    {
        IEnumerable<string> candidates;
        var parentProfile = settings.ParentId != Guid.Empty && settings.ParentId != AllTickersNode.StaticId ? _watchlists.GetProfileById(settings.ParentId) : null;
        var parentNode = parentProfile is null && settings.ParentId != Guid.Empty && settings.ParentId != AllTickersNode.StaticId
            ? FindNode<TickerGroupNode>(settings.ParentId) : null;
        if (parentProfile is not null) candidates = parentProfile.Items.Select(i => i.Ticker).ToList();
        else if (parentNode is not null) candidates = _store.GetTickersForNode(parentNode);
        else candidates = await CandidatesAsync();
        var candidateSet = new HashSet<string>(candidates, StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, decimal>? latest = null;
        try { latest = await _market.GetLatestPricesAsync(candidateSet); }
        catch (Exception ex) when (ex is not OperationCanceledException) { latest = null; }
        var display = DisplayBySymbol();
        var result = new List<string>();
        foreach (string ticker in candidateSet)
        {
            ct.ThrowIfCancellationRequested();
            var meta = await _market.GetMetadataAsync(ticker);
            // Rules are always evaluated on stored metadata and latest prices, never on a displayed row: a row whose
            // asynchronous hydration has not finished would silently fail every rule. Only a live, non-empty row tag overrides the stored one.
            string? liveTag = display.TryGetValue(ticker, out var row) ? row.Tag : null;
            var item = new WatchlistItemViewModel(ticker, meta.ShortName ?? ticker, meta.Sector ?? "", meta.Industry ?? "", 0, 0, 0, 0, 0, 0, 0)
            {
                Tag = !string.IsNullOrEmpty(liveTag) ? liveTag : meta.Tag,
                Sector = meta.Sector ?? "", Industry = meta.Industry ?? "",
                ReturnOnEquity = meta.ReturnOnEquity, ReturnOnAssets = meta.ReturnOnAssets, GrossMargins = meta.GrossMargins,
                OperatingMargins = meta.OperatingMargins, ProfitMargins = meta.ProfitMargins, CurrentRatio = meta.CurrentRatio,
                QuickRatio = meta.QuickRatio, DebtToEquity = meta.DebtToEquity, MarketCap = meta.MarketCap,
                EnterpriseValue = meta.EnterpriseValue, TrailingPE = meta.TrailingPE, ForwardPE = meta.ForwardPE,
                PegRatio = meta.PegRatio, PriceToSalesTrailing12Months = meta.PriceToSalesTrailing12Months, PriceToBook = meta.PriceToBook,
            };
            if (latest is not null && latest.TryGetValue(ticker, out decimal price) && price > 0) item.Close = price;
            if (_filters.EvaluateSettings(item, settings)) result.Add(ticker);
        }
        return result;
    }

    private async Task<IEnumerable<string>> CandidatesAsync()
    {
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string ticker in await _market.GetAvailableTickersAsync()) all.Add(ticker);
        foreach (var profile in _watchlists.GetAllProfiles())
            foreach (var item in profile.Items) all.Add(item.Ticker);
        foreach (var row in _store.DisplayItems) all.Add(row.Symbol);
        return all;
    }

    private Dictionary<string, IFilterableSymbol> DisplayBySymbol()
    {
        var map = new Dictionary<string, IFilterableSymbol>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _store.DisplayItems) map.TryAdd(row.Symbol, row);
        return map;
    }

    private T? FindNode<T>(Guid id) where T : TickerGroupNode
    {
        var pending = new Stack<TickerGroupNode>(_store.Groups.Reverse());
        var visited = new HashSet<TickerGroupNode>(ReferenceEqualityComparer.Instance);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (!visited.Add(node)) continue;
            if (node.Id == id && node is T match) return match;
            if (node.Children is not null)
                foreach (var child in node.Children.Reverse()) pending.Push(child);
        }
        return null;
    }
}
