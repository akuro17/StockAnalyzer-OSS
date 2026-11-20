using System.Text.Json;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>sa_get_ticker_metadata: the metadata stored on this machine for each symbol. Never fetches anything.</summary>
internal sealed class GetTickerMetadataTool : IMcpTool
{
    private const string SymbolsArgument = "symbols";
    private const string FieldsArgument = "fields";

    /// <summary>The keys every result item carries itself (outside <c>metadata</c>): naming them in <c>fields</c> is accepted and changes nothing.</summary>
    private static readonly string[] AlwaysWrittenNames = { "symbol", "found", "timeSeriesLastUpdated" };

    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly McpLimits _limits;
    private readonly McpResultHandleStore _resultHandles;

    public GetTickerMetadataTool(IReadOnlyStockAnalyzerGateway gateway, McpLimits limits, McpResultHandleStore? resultHandles = null)
    {
        _gateway = gateway;
        _limits = limits;
        _resultHandles = resultHandles ?? new McpResultHandleStore();
    }

    public string Name => "sa_get_ticker_metadata";

    public string Description =>
        $"Returns locally stored metadata and latest bar date for 1..{_limits.MaxSymbolsPerMetadataCall} symbols (via {SymbolsArgument} or {McpResultHandleStore.SymbolsFromResultIdArgument}); supports optional {FieldsArgument} filter to reduce tokens. Read-only; never fetches data. " +
        ToolJson.DocLink(Name);

    public bool IsHeavy => false;

    public string? CompactUsageHint => "Pass fewer fields, fewer symbols, or paginate with resultOffset/resultLimit.";

    public string? ExampleArgumentsJson => "{\"symbols\":[\"7203-T\",\"6758-T\"]}";

    public string InputSchemaJson => ToolSchema.Object()
        .StringArray(SymbolsArgument, $"Ticker symbols (1 to {_limits.MaxSymbolsPerMetadataCall}); '.' and '-' are interchangeable. Mutually exclusive with {McpResultHandleStore.SymbolsFromResultIdArgument}; one of the two is required.", minItems: 1)
        .String(McpResultHandleStore.SymbolsFromResultIdArgument, $"Handle id from sa_run_screener or sa_filter_tickers ({McpResultHandleStore.StoreResultArgument} true). Mutually exclusive with {SymbolsArgument}.")
        .Integer(McpResultHandleStore.ResultOffsetArgument, $"0-based start index within {McpResultHandleStore.SymbolsFromResultIdArgument}. Default 0.", defaultValue: 0, minimum: 0)
        .Integer(McpResultHandleStore.ResultLimitArgument, $"Maximum symbols to read from {McpResultHandleStore.SymbolsFromResultIdArgument} (1..{_limits.MaxSymbolsPerMetadataCall}).", minimum: 1, maximum: _limits.MaxSymbolsPerMetadataCall)
        .StringArray(FieldsArgument, $"Metadata properties to return (camelCase names, for example sector or marketCap); {string.Join(", ", AlwaysWrittenNames)} are always written. Default: every stored property.", minItems: 1)
        .Build();

    /// <summary>
    /// Reads <c>fields</c>: null when the call does not name it; an empty list or an unknown name (case-sensitive) is INVALID_ARGUMENT, the unknown name at <c>fields[i]</c>.
    /// The always-written keys are accepted but select nothing; duplicates are ignored.
    /// </summary>
    private static IReadOnlySet<string>? ReadFields(JsonElement arguments)
    {
        IReadOnlyList<string>? named = ToolArgs.StringArray(arguments, FieldsArgument);
        if (named is null)
        {
            return null;
        }

        if (named.Count == 0)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"'{FieldsArgument}' must name at least one property, or be omitted.", FieldsArgument, $"Omit {FieldsArgument} for every stored property.");
        }

        var selected = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < named.Count; index++)
        {
            string name = named[index];
            if (TickerMetadataJson.SelectableFieldNames.Contains(name, StringComparer.Ordinal))
            {
                selected.Add(name);
            }
            else if (!AlwaysWrittenNames.Contains(name, StringComparer.Ordinal))
            {
                throw new ToolException(
                    ToolErrorCodes.InvalidArgument,
                    $"Unknown metadata property '{name}'.",
                    $"{FieldsArgument}[{index}]",
                    $"Valid names: {string.Join(", ", TickerMetadataJson.SelectableFieldNames.Concat(AlwaysWrittenNames))}.");
            }
        }

        return selected;
    }

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        IReadOnlyList<string>? requested = ToolArgs.StringArray(arguments, SymbolsArgument);
        string? resultId = ToolArgs.String(arguments, McpResultHandleStore.SymbolsFromResultIdArgument);
        if ((requested is not null) == (resultId is not null))
        {
            string parameter = requested is not null ? McpResultHandleStore.SymbolsFromResultIdArgument : SymbolsArgument;
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"Give exactly one of '{SymbolsArgument}' and '{McpResultHandleStore.SymbolsFromResultIdArgument}'.",
                parameter,
                "Name the symbols, or pass a stored result handle.");
        }

        (int sliceOffset, int? sliceLimit) = McpResultHandleStore.ReadSliceArguments(arguments, resultId, 1, _limits.MaxSymbolsPerMetadataCall);
        IReadOnlyList<string> sourceSymbols;
        if (resultId is not null)
        {
            sourceSymbols = _resultHandles.ResolveSymbolsSlice(resultId, 1, _limits.MaxSymbolsPerMetadataCall, sliceOffset, sliceLimit);
        }
        else
        {
            if (requested!.Count > _limits.MaxSymbolsPerMetadataCall)
            {
                throw new ToolException(
                    ToolErrorCodes.LimitExceeded,
                    $"At most {_limits.MaxSymbolsPerMetadataCall} symbols per call.",
                    SymbolsArgument,
                    $"Send {_limits.MaxSymbolsPerMetadataCall} or fewer symbols.");
            }

            sourceSymbols = requested;
        }

        IReadOnlySet<string>? onlyFields = ReadFields(arguments);

        var symbols = new List<string>();
        for (int index = 0; index < sourceSymbols.Count; index++)
        {
            string canonical = SymbolResolver.Canonical(sourceSymbols[index], $"{SymbolsArgument}[{index}]");
            if (!symbols.Contains(canonical, StringComparer.Ordinal))
            {
                symbols.Add(canonical);
            }
        }

        var results = new List<(string Symbol, TickerMetadata? Metadata, DateTimeOffset? LastUpdated)>();
        foreach (string symbol in symbols)
        {
            TickerMetadata? metadata;
            try
            {
                metadata = await _gateway.GetStoredTickerMetadataAsync(symbol, cancellationToken).ConfigureAwait(false);
            }
            catch (DataUnavailableException)
            {
                throw new ToolException(ToolErrorCodes.DataUnavailable, $"Stored metadata of '{symbol}' is temporarily unreadable.", SymbolsArgument, "Retry in a moment.");
            }

            DateTimeOffset? lastUpdated = await _gateway.GetTimeSeriesLastUpdatedAsync(symbol, cancellationToken).ConfigureAwait(false);
            results.Add((symbol, metadata, lastUpdated));
        }

        return ToolJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("requestedCount", sourceSymbols.Count);
            writer.WriteNumber("uniqueCount", symbols.Count);
            writer.WriteStartArray("results");
            foreach (var (symbol, metadata, lastUpdated) in results)
            {
                writer.WriteStartObject();
                writer.WriteString("symbol", symbol);
                writer.WriteBoolean("found", metadata is not null);
                if (lastUpdated is { } updated)
                {
                    writer.WriteString("timeSeriesLastUpdated", ToolJson.FormatDate(updated.UtcDateTime));
                }
                else
                {
                    writer.WriteNull("timeSeriesLastUpdated");
                }

                if (metadata is { } stored)
                {
                    writer.WritePropertyName("metadata");
                    TickerMetadataJson.WriteAll(writer, stored, onlyFields);
                }
                else
                {
                    writer.WriteNull("metadata");
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }
}
