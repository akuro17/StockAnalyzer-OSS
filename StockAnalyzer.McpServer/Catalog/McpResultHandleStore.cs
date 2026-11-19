using StockAnalyzer.McpServer.Tools;

namespace StockAnalyzer.McpServer.Catalog;

/// <summary>One stored screener/filter symbol list identified by an opaque handle within the current server runtime.</summary>
internal sealed record SymbolListHandleEntry(string ResultId, string SourceTool, IReadOnlyList<string> Symbols);

/// <summary>
/// Thread-safe volatile in-memory LRU cache of symbol lists emitted by <c>sa_run_screener</c> and <c>sa_filter_tickers</c>
/// when <c>storeResult: true</c>, allowing downstream tools to consume symbols without round-tripping large arrays through chat context.
/// </summary>
internal sealed class McpResultHandleStore
{
    public const int MaxHandles = 32;
    public const string HandlePrefix = "res_";
    public const string StoreResultArgument = "storeResult";
    public const string SymbolsFromResultIdArgument = "symbolsFromResultId";
    public const string ResultIdKey = "resultId";

    private readonly object _sync = new();
    private readonly LinkedList<SymbolListHandleEntry> _lru = new();
    private readonly Dictionary<string, LinkedListNode<SymbolListHandleEntry>> _byId = new(StringComparer.Ordinal);
    private long _nextSequence;

    /// <summary>The number of currently cached result handles.</summary>
    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _byId.Count;
            }
        }
    }

    /// <summary>Stores a defensive snapshot of <paramref name="symbols"/> under a new handle id, evicting the least-recently-used handle when full.</summary>
    public SymbolListHandleEntry Store(string sourceTool, IReadOnlyList<string> symbols)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceTool);
        ArgumentNullException.ThrowIfNull(symbols);

        string[] snapshot = symbols.ToArray();
        lock (_sync)
        {
            _nextSequence++;
            string resultId = $"{HandlePrefix}{_nextSequence:D4}";
            var entry = new SymbolListHandleEntry(resultId, sourceTool, snapshot);

            if (_byId.Count >= MaxHandles && _lru.Last is { } oldest)
            {
                _lru.RemoveLast();
                _byId.Remove(oldest.Value.ResultId);
            }

            LinkedListNode<SymbolListHandleEntry> node = _lru.AddFirst(entry);
            _byId.Add(resultId, node);
            return entry;
        }
    }

    /// <summary>Looks up a handle by id and refreshes its recency position when present.</summary>
    public bool TryGet(string resultId, out SymbolListHandleEntry? entry)
    {
        if (string.IsNullOrWhiteSpace(resultId))
        {
            entry = null;
            return false;
        }

        lock (_sync)
        {
            if (!_byId.TryGetValue(resultId, out LinkedListNode<SymbolListHandleEntry>? node))
            {
                entry = null;
                return false;
            }

            if (node != _lru.First)
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
            }

            entry = node.Value;
            return true;
        }
    }

    /// <summary>
    /// Resolves <paramref name="resultId"/> and verifies that its symbol count fits <paramref name="minCount"/>..<paramref name="maxCount"/>,
    /// throwing structured <see cref="ToolException"/> failures otherwise.
    /// </summary>
    public IReadOnlyList<string> ResolveSymbols(string? resultId, int minCount, int maxCount)
    {
        string normalized = resultId?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(normalized) || !TryGet(normalized, out SymbolListHandleEntry? entry) || entry is null)
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"Result handle '{normalized}' was not found or has expired.",
                SymbolsFromResultIdArgument,
                "Run sa_run_screener or sa_filter_tickers with storeResult: true first.");
        }

        if (entry.Symbols.Count < minCount)
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"Result handle '{normalized}' contains {entry.Symbols.Count} symbols, fewer than the minimum required ({minCount}).",
                SymbolsFromResultIdArgument);
        }

        if (entry.Symbols.Count > maxCount)
        {
            throw new ToolException(
                ToolErrorCodes.LimitExceeded,
                $"Result handle '{normalized}' contains {entry.Symbols.Count} symbols, exceeding the limit of {maxCount}.",
                SymbolsFromResultIdArgument);
        }

        return entry.Symbols;
    }
}
