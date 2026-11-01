using System.Text.Json;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// Retrieves stored bar counts, earliest and latest bar timestamps across supported time frames (D1, W1, MN1),
/// and metadata status for the requested symbols without loading full candle datasets.
/// </summary>
internal sealed class GetSymbolCoverageTool : IMcpTool
{
    private const string SymbolsArgument = "symbols";

    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly McpLimits _limits;

    public GetSymbolCoverageTool(IReadOnlyStockAnalyzerGateway gateway, McpLimits limits)
    {
        _gateway = gateway;
        _limits = limits;
    }

    public string Name => "sa_get_symbol_coverage";

    public string Description =>
        "Retrieves time-series data coverage (stored bar counts, earliest and latest timestamps across D1, W1, MN1) " +
        "and metadata status for the given symbols. Duplicate symbols are returned once. " +
        $"At most {_limits.MaxSymbolsPerMetadataCall} symbols per call. Read-only.";

    public string InputSchemaJson => ToolSchema.Object()
        .StringArray(SymbolsArgument, $"The symbols to inspect. Duplicate symbols are folded. At most {_limits.MaxSymbolsPerMetadataCall} symbols per call.", required: true, minItems: 1)
        .Build();

    public bool IsHeavy => false;

    public string? ExampleArgumentsJson => "{\"symbols\":[\"7203-T\",\"6758-T\"]}";

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        IReadOnlyList<string>? rawSymbols = ToolArgs.StringArray(arguments, SymbolsArgument);
        if (rawSymbols is null)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, "Missing required argument 'symbols'.", SymbolsArgument);
        }

        if (rawSymbols.Count > _limits.MaxSymbolsPerMetadataCall)
        {
            throw new ToolException(
                ToolErrorCodes.LimitExceeded,
                $"At most {_limits.MaxSymbolsPerMetadataCall} symbols per call.",
                SymbolsArgument,
                $"Send {_limits.MaxSymbolsPerMetadataCall} or fewer symbols.");
        }

        var uniqueSymbols = new List<string>();
        for (int index = 0; index < rawSymbols.Count; index++)
        {
            string canonical = SymbolResolver.Canonical(rawSymbols[index], $"{SymbolsArgument}[{index}]");
            if (!uniqueSymbols.Contains(canonical, StringComparer.Ordinal))
            {
                uniqueSymbols.Add(canonical);
            }
        }

        var results = new List<SymbolCoverage>();
        foreach (string symbol in uniqueSymbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SymbolCoverage coverage = await _gateway.GetCoverageAsync(symbol, cancellationToken).ConfigureAwait(false);
            results.Add(coverage);
        }

        return ToolJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartArray("results");

            foreach (SymbolCoverage coverage in results)
            {
                writer.WriteStartObject();
                writer.WriteString("symbol", coverage.Symbol);
                writer.WriteBoolean("found", coverage.Found);
                writer.WriteBoolean("hasMetadata", coverage.HasMetadata);

                if (coverage.TimeSeriesLastUpdated is { } updated)
                {
                    writer.WriteString("timeSeriesLastUpdated", ToolJson.FormatDate(updated.UtcDateTime));
                }
                else
                {
                    writer.WriteNull("timeSeriesLastUpdated");
                }

                writer.WriteStartArray("timeFrames");
                foreach (TimeFrameCoverage tf in coverage.TimeFrames)
                {
                    writer.WriteStartObject();
                    writer.WriteString("timeFrame", tf.TimeFrame.ToString());
                    writer.WriteNumber("barCount", tf.BarCount);
                    writer.WriteString("earliestTimestamp", ToolJson.FormatDate(tf.EarliestTimestamp));
                    writer.WriteString("latestTimestamp", ToolJson.FormatDate(tf.LatestTimestamp));
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }
}
