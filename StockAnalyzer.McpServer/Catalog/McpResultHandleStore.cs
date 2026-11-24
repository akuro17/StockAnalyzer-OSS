using System.Text.Json;
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
    public const string ResultOffsetArgument = "resultOffset";
    public const string ResultLimitArgument = "resultLimit";
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

    /// <summary>Returns a point-in-time snapshot of active handles ordered most-recently-used first without altering LRU recency.</summary>
    public IReadOnlyList<SymbolListHandleEntry> SnapshotMostRecentFirst()
    {
        lock (_sync)
        {
            if (_lru.Count == 0)
            {
                return Array.Empty<SymbolListHandleEntry>();
            }

            var snapshot = new SymbolListHandleEntry[_lru.Count];
            int index = 0;
            for (LinkedListNode<SymbolListHandleEntry>? node = _lru.First; node is not null; node = node.Next)
            {
                snapshot[index++] = node.Value;
            }

            return snapshot;
        }
    }

    /// <summary>
    /// Resolves <paramref name="resultId"/> and verifies that its symbol count fits <paramref name="minCount"/>..<paramref name="maxCount"/>,
    /// throwing structured <see cref="ToolException"/> failures otherwise.
    /// </summary>
    public IReadOnlyList<string> ResolveSymbols(string? resultId, int minCount, int maxCount)
        => ResolveSymbolsSlice(resultId, minCount, maxCount, offset: 0, limit: null);

    /// <summary>
    /// Reads and validates optional <c>resultOffset</c> / <c>resultLimit</c> slice arguments for a consumer tool.
    /// Rejects slice arguments when <paramref name="resultId"/> is absent.
    /// </summary>
    public static (int Offset, int? Limit) ReadSliceArguments(JsonElement arguments, string? resultId, int minLimit, int maxLimit)
    {
        long? rawOffset = ToolArgs.Integer(arguments, ResultOffsetArgument);
        long? rawLimit = ToolArgs.Integer(arguments, ResultLimitArgument);

        if (resultId is null)
        {
            if (rawOffset is not null)
            {
                throw new ToolException(
                    ToolErrorCodes.InvalidArgument,
                    $"'{ResultOffsetArgument}' requires '{SymbolsFromResultIdArgument}'.",
                    ResultOffsetArgument);
            }

            if (rawLimit is not null)
            {
                throw new ToolException(
                    ToolErrorCodes.InvalidArgument,
                    $"'{ResultLimitArgument}' requires '{SymbolsFromResultIdArgument}'.",
                    ResultLimitArgument);
            }

            return (0, null);
        }

        int offset = 0;
        if (rawOffset is { } sentOffset)
        {
            if (sentOffset < 0 || sentOffset > int.MaxValue)
            {
                throw new ToolException(
                    ToolErrorCodes.InvalidArgument,
                    $"'{ResultOffsetArgument}' must be between 0 and {int.MaxValue}.",
                    ResultOffsetArgument);
            }

            offset = (int)sentOffset;
        }

        int? limit = null;
        if (rawLimit is { } sentLimit)
        {
            if (sentLimit < minLimit)
            {
                throw new ToolException(
                    ToolErrorCodes.InvalidArgument,
                    $"'{ResultLimitArgument}' must be at least {minLimit}.",
                    ResultLimitArgument);
            }

            if (sentLimit > maxLimit)
            {
                throw new ToolException(
                    ToolErrorCodes.LimitExceeded,
                    $"'{ResultLimitArgument}' ({sentLimit}) exceeds the limit of {maxLimit}.",
                    ResultLimitArgument,
                    $"Use {maxLimit} or less.");
            }

            limit = (int)sentLimit;
        }

        return (offset, limit);
    }

    /// <summary>
    /// Resolves a slice of <paramref name="resultId"/> starting at <paramref name="offset"/> (up to <paramref name="limit"/> items when provided)
    /// and verifies that the resulting symbol count fits <paramref name="minCount"/>..<paramref name="maxCount"/> without silent truncation.
    /// </summary>
    public IReadOnlyList<string> ResolveSymbolsSlice(string? resultId, int minCount, int maxCount, int offset = 0, int? limit = null)
    {
        if (offset < 0)
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"'{ResultOffsetArgument}' must be 0 or greater.",
                ResultOffsetArgument);
        }

        if (limit is { } requestedLimit)
        {
            if (requestedLimit < minCount)
            {
                throw new ToolException(
                    ToolErrorCodes.InvalidArgument,
                    $"'{ResultLimitArgument}' must be at least {minCount}.",
                    ResultLimitArgument);
            }

            if (requestedLimit > maxCount)
            {
                throw new ToolException(
                    ToolErrorCodes.LimitExceeded,
                    $"'{ResultLimitArgument}' ({requestedLimit}) exceeds the limit of {maxCount}.",
                    ResultLimitArgument,
                    $"Use {maxCount} or less.");
            }
        }

        string normalized = resultId?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(normalized) || !TryGet(normalized, out SymbolListHandleEntry? entry) || entry is null)
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"Result handle '{normalized}' was not found or has expired.",
                SymbolsFromResultIdArgument,
                "Run sa_run_screener or sa_filter_tickers with storeResult: true first.");
        }

        if (entry.Symbols.Count == 0)
        {
            if (offset > 0)
            {
                throw new ToolException(
                    ToolErrorCodes.InvalidArgument,
                    $"'{ResultOffsetArgument}' ({offset}) is at or beyond the 0 symbols in result handle '{normalized}'.",
                    ResultOffsetArgument,
                    "Use resultOffset 0 when the result handle is empty.");
            }

            if (minCount > 0)
            {
                throw new ToolException(
                    ToolErrorCodes.InvalidArgument,
                    $"Result handle '{normalized}' contains 0 symbols, fewer than the minimum required ({minCount}).",
                    SymbolsFromResultIdArgument);
            }

            return Array.Empty<string>();
        }

        if (offset >= entry.Symbols.Count)
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"'{ResultOffsetArgument}' ({offset}) is at or beyond the {entry.Symbols.Count} symbols in result handle '{normalized}'.",
                ResultOffsetArgument,
                $"Use an offset from 0 to {entry.Symbols.Count - 1}.");
        }

        int remaining = entry.Symbols.Count - offset;
        if (limit is null)
        {
            if (remaining < minCount)
            {
                throw new ToolException(
                    ToolErrorCodes.InvalidArgument,
                    $"Result handle '{normalized}' contains {remaining} symbol(s) from offset {offset}, fewer than the minimum required ({minCount}).",
                    SymbolsFromResultIdArgument);
            }

            if (remaining > maxCount)
            {
                throw new ToolException(
                    ToolErrorCodes.LimitExceeded,
                    $"Result handle '{normalized}' contains {remaining} symbols from offset {offset}, exceeding the limit of {maxCount}.",
                    SymbolsFromResultIdArgument,
                    $"Pass {ResultOffsetArgument} (0-based) and {ResultLimitArgument} (at most {maxCount}) to process a slice.");
            }

            return offset == 0 ? entry.Symbols : entry.Symbols.Skip(offset).ToArray();
        }

        int take = Math.Min(limit.Value, remaining);
        if (take < minCount)
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"Result handle '{normalized}' yields {take} symbol(s) at offset {offset}, fewer than the minimum required ({minCount}).",
                SymbolsFromResultIdArgument);
        }

        return entry.Symbols.Skip(offset).Take(take).ToArray();
    }
}
