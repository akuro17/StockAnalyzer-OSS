using System.Text.Json;
using StockAnalyzer.McpServer.Catalog;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// sa_list_result_handles: lists active in-memory symbol result handles stored by
/// <c>sa_run_screener</c> or <c>sa_filter_tickers</c> (<c>storeResult: true</c>), ordered most-recently-used first.
/// </summary>
internal sealed class ListResultHandlesTool : IMcpTool
{
    public const string ToolName = "sa_list_result_handles";
    public const int DefaultPreviewCount = 5;
    public const int MaxPreviewCount = 20;
    private const string PreviewCountArgument = "previewCount";

    private readonly McpResultHandleStore _resultHandles;

    public ListResultHandlesTool(McpResultHandleStore resultHandles)
    {
        _resultHandles = resultHandles ?? throw new ArgumentNullException(nameof(resultHandles));
    }

    public string Name => ToolName;

    public string Description =>
        $"Lists active result handles (max {McpResultHandleStore.MaxHandles}) from storeResult: true, most-recently-used first (preserves LRU order). " +
        ToolJson.DocLink(Name);

    public bool IsHeavy => false;

    public bool IsExecution => false;

    public bool IsReadOnly => true;

    public string? CompactUsageHint => "Pass previewCount: 0 to omit previewSymbols.";

    public string? ExampleArgumentsJson => "{\"previewCount\":5}";

    public string InputSchemaJson => ToolSchema.Object()
        .Integer(
            PreviewCountArgument,
            $"Leading symbols per handle (0..{MaxPreviewCount}). Default {DefaultPreviewCount}.",
            defaultValue: DefaultPreviewCount,
            minimum: 0,
            maximum: MaxPreviewCount)
        .Build();

    public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        int previewCount = ToolArgs.NonNegativeWithin(arguments, PreviewCountArgument, DefaultPreviewCount, MaxPreviewCount);
        IReadOnlyList<SymbolListHandleEntry> entries = _resultHandles.SnapshotMostRecentFirst();

        string json = ToolJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("maxHandles", McpResultHandleStore.MaxHandles);
            writer.WriteNumber("activeCount", entries.Count);
            writer.WriteStartArray("handles");
            foreach (SymbolListHandleEntry entry in entries)
            {
                writer.WriteStartObject();
                writer.WriteString("resultId", entry.ResultId);
                writer.WriteString("sourceTool", entry.SourceTool);
                writer.WriteNumber("symbolCount", entry.Symbols.Count);
                writer.WriteStartArray("previewSymbols");
                int previewTake = Math.Min(previewCount, entry.Symbols.Count);
                for (int i = 0; i < previewTake; i++)
                {
                    writer.WriteStringValue(entry.Symbols[i]);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });

        return Task.FromResult(json);
    }
}
