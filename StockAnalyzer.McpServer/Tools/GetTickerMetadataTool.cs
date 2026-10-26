using System.Text.Json;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>sa_get_ticker_metadata: the metadata stored on this machine for each symbol. Never fetches anything.</summary>
internal sealed class GetTickerMetadataTool : IMcpTool
{
    private const string SymbolsArgument = "symbols";

    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly McpLimits _limits;

    public GetTickerMetadataTool(IReadOnlyStockAnalyzerGateway gateway, McpLimits limits)
    {
        _gateway = gateway;
        _limits = limits;
    }

    public string Name => "sa_get_ticker_metadata";

    public string Description =>
        "Returns the metadata stored locally for each symbol (company names, region, sector, industry, currency, prices, fundamentals) and the date of the newest stored bar. " +
        "Nothing is fetched from the internet: found is false when nothing is stored. Fields without a stored value are omitted. " +
        "The record also contains the user's own entries (tag, notes, reminder and strategy prices such as long, exitLong and stopLossLong) when the user has made them; " +
        "they are the user's personal annotations. " +
        "timeSeriesLastUpdated is the date of the newest stored daily bar, or the date the data file was last written when the bar dates cannot be read. " +
        $"Duplicate symbols (after canonicalisation) are returned once. At most {_limits.MaxSymbolsPerMetadataCall} symbols per call.";

    public bool IsHeavy => false;

    public string? ExampleArgumentsJson => "{\"symbols\":[\"7203-T\",\"6758-T\"]}";

    public string InputSchemaJson => ToolSchema.Object()
        .StringArray(SymbolsArgument, "Ticker symbols; '.' and '-' are interchangeable.", required: true, minItems: 1)
        .Build();

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> requested = ToolArgs.StringArray(arguments, SymbolsArgument)!;
        if (requested.Count > _limits.MaxSymbolsPerMetadataCall)
        {
            throw new ToolException(
                ToolErrorCodes.LimitExceeded,
                $"At most {_limits.MaxSymbolsPerMetadataCall} symbols per call.",
                SymbolsArgument,
                $"Send {_limits.MaxSymbolsPerMetadataCall} or fewer symbols.");
        }

        var symbols = new List<string>();
        for (int index = 0; index < requested.Count; index++)
        {
            string canonical = SymbolResolver.Canonical(requested[index], $"{SymbolsArgument}[{index}]");
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
            writer.WriteNumber("requestedCount", requested.Count);
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
                    TickerMetadataJson.WriteAll(writer, stored);
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
