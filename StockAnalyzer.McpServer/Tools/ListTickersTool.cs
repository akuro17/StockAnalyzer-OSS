using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>sa_list_tickers: the available symbols, paged, optionally filtered and with a short stored-metadata summary.</summary>
internal sealed class ListTickersTool : IMcpTool
{
    private const string OffsetArgument = "offset";
    private const string LimitArgument = "limit";
    private const string SearchArgument = "searchQuery";
    private const string SummaryArgument = "includeMetadataSummary";
    private const string GroupIdArgument = "groupId";

    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly ITickerGroupResolver _groupResolver;
    private readonly McpLimits _limits;

    public ListTickersTool(IReadOnlyStockAnalyzerGateway gateway, McpLimits limits)
        : this(gateway, new TickerGroupResolver(gateway, new FileReadOnlyWorkspaceSource(PathDiscovery.ResolveConfigPathWithoutCreating(LayoutConstants.DefaultWorkspaceFileName))), limits)
    {
    }

    public ListTickersTool(IReadOnlyStockAnalyzerGateway gateway, ITickerGroupResolver groupResolver, McpLimits limits)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _groupResolver = groupResolver ?? throw new ArgumentNullException(nameof(groupResolver));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
    }

    public string Name => "sa_list_tickers";

    public string Description =>
        "Lists the registered ticker symbols (All Tickers), sorted by symbol (ordinal, ascending). " +
        "Symbols are canonical: upper case with '.' replaced by '-' (for example 7203-T). " +
        "searchQuery matches symbol or stored short name, ignoring case. " +
        "includeMetadataSummary adds the stored identity fields (short name, long name, region, sector, industry, currency) to each returned ticker. " +
        "groupId restricts the symbols to the members of a saved ticker group (from sa_list_ticker_groups).";

    public bool IsHeavy => false;

    public string? ExampleArgumentsJson => "{\"limit\":20,\"searchQuery\":\"TOYOTA\"}";

    public string InputSchemaJson => ToolSchema.Object()
        .Integer(OffsetArgument, "Number of matching tickers to skip. Default 0.", defaultValue: 0, minimum: 0)
        .Integer(LimitArgument, $"Page size. Default {_limits.DefaultTickerPageSize}, at most {_limits.MaxTickersPerPage}.", defaultValue: _limits.DefaultTickerPageSize, minimum: 1, maximum: _limits.MaxTickersPerPage)
        .String(SearchArgument, "Case-insensitive text that must occur in the symbol or the stored short name.")
        .Boolean(SummaryArgument, $"Add stored identity fields to each returned ticker. At most {_limits.MaxSymbolsPerMetadataCall} tickers per call.")
        .String(GroupIdArgument, "Guid of a ticker group from sa_list_ticker_groups. When given, lists only members of that group.")
        .Build();

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        long offset = ToolArgs.Integer(arguments, OffsetArgument) ?? 0;
        if (offset < 0)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{OffsetArgument}' must not be negative.", OffsetArgument);
        }

        int limit = ToolArgs.PositiveWithin(arguments, LimitArgument, _limits.DefaultTickerPageSize, _limits.MaxTickersPerPage);
        bool includeSummary = ToolArgs.Boolean(arguments, SummaryArgument) ?? false;
        if (includeSummary && limit > _limits.MaxSymbolsPerMetadataCall)
        {
            throw new ToolException(
                ToolErrorCodes.LimitExceeded,
                $"A summary can be added to at most {_limits.MaxSymbolsPerMetadataCall} tickers per call.",
                LimitArgument,
                $"Use {LimitArgument} {_limits.MaxSymbolsPerMetadataCall} or less with {SummaryArgument}.");
        }

        string? query = ToolArgs.String(arguments, SearchArgument)?.Trim();
        if (query is { Length: 0 })
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{SearchArgument}' must not be empty.", SearchArgument);
        }

        string? groupIdRaw = ToolArgs.String(arguments, GroupIdArgument)?.Trim();
        Guid? groupId = null;
        GroupMembers? groupMembers = null;
        if (groupIdRaw is not null)
        {
            if (!Guid.TryParse(groupIdRaw, out Guid parsedId))
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{GroupIdArgument}' must be a valid Guid.", GroupIdArgument);
            }

            groupId = parsedId;
            try
            {
                groupMembers = await _groupResolver.ResolveAsync(parsedId, _limits.MaxFilterEvaluationSymbols, cancellationToken).ConfigureAwait(false);
            }
            catch (GroupNotFoundException)
            {
                throw new ToolException(ToolErrorCodes.GroupNotFound, $"Group '{parsedId}' was not found in the workspace.", GroupIdArgument, "Use sa_list_ticker_groups to see available group ids.");
            }
            catch (FilterEvaluationLimitException ex)
            {
                throw new ToolException(ToolErrorCodes.LimitExceeded, ex.Message, GroupIdArgument, "Raise AiIntegration:MaxFilterEvaluationSymbols or narrow the group.");
            }
        }

        List<string> tickers;
        if (groupMembers is not null)
        {
            tickers = groupMembers.Symbols.ToList();
        }
        else
        {
            tickers = (await _gateway.GetAvailableTickersAsync(cancellationToken).ConfigureAwait(false))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();
        }

        if (query is not null)
        {
            tickers = await FilterAsync(tickers, query, cancellationToken).ConfigureAwait(false);
        }

        List<string> page = tickers.Skip((int)Math.Min(offset, int.MaxValue)).Take(limit).ToList();
        var summaries = new Dictionary<string, TickerMetadata?>(StringComparer.Ordinal);
        if (includeSummary)
        {
            foreach (string symbol in page)
            {
                summaries[symbol] = await StoredMetadataAsync(symbol, cancellationToken).ConfigureAwait(false);
            }
        }

        return ToolJson.Build(writer =>
        {
            writer.WriteStartObject();
            if (groupId.HasValue && groupMembers is not null)
            {
                writer.WriteString("groupId", groupId.Value.ToString("D").ToLowerInvariant());
                writer.WriteString("groupKind", groupMembers.Kind.ToCamelCase());
                writer.WriteNumber("groupUnregisteredMemberCount", groupMembers.UnregisteredCount);
                writer.WriteStartArray("groupUnknownRuleFields");
                foreach (string field in groupMembers.UnknownRuleFields)
                {
                    writer.WriteStringValue(field);
                }
                writer.WriteEndArray();
            }

            writer.WriteNumber("totalCount", tickers.Count);
            writer.WriteNumber("offset", offset);
            writer.WriteNumber("limit", limit);
            writer.WriteNumber("returnedCount", page.Count);
            writer.WriteBoolean("hasMore", offset + page.Count < tickers.Count);
            writer.WriteStartArray("tickers");
            foreach (string symbol in page)
            {
                writer.WriteStartObject();
                writer.WriteString("symbol", symbol);
                if (includeSummary)
                {
                    TickerMetadata? metadata = summaries[symbol];
                    writer.WriteBoolean("hasMetadata", metadata is not null);
                    if (metadata is { } stored)
                    {
                        TickerMetadataJson.WriteSummaryFields(writer, stored);
                    }
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    private async Task<List<string>> FilterAsync(List<string> tickers, string query, CancellationToken cancellationToken)
    {
        var matches = new List<string>();
        foreach (string symbol in tickers)
        {
            if (symbol.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(symbol);
                continue;
            }

            TickerMetadata? metadata = await StoredMetadataAsync(symbol, cancellationToken).ConfigureAwait(false);
            if (metadata is { ShortName: { } shortName } && shortName.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(symbol);
            }
        }

        return matches;
    }

    private async Task<TickerMetadata?> StoredMetadataAsync(string symbol, CancellationToken cancellationToken)
    {
        // A malformed entry in the user's ticker list has no usable file name, so it has no stored metadata.
        if (!TickerSymbol.IsWellFormed(symbol))
        {
            return null;
        }

        try
        {
            return await _gateway.GetStoredTickerMetadataAsync(symbol, cancellationToken).ConfigureAwait(false);
        }
        catch (DataUnavailableException)
        {
            throw new ToolException(ToolErrorCodes.DataUnavailable, "Stored metadata is temporarily unreadable.", hint: "Retry in a moment.");
        }
    }
}
