using System.Globalization;
using System.Text.Json;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.McpServer.Catalog;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// sa_get_template_detail: returns the detail of one saved template with its conditions formatted in MCP JSON syntax,
/// ready to be inspected or directly passed to sa_run_screener (conditionTree) or sa_run_backtest (conditions and riskManagement).
/// </summary>
internal sealed class GetTemplateDetailTool : IMcpTool
{
    private const string TemplateTypeArgument = "templateType";
    private const string TemplateIdArgument = "templateId";
    private const string ScreenerKind = "screener";
    private const string BacktestConditionKind = "backtestCondition";

    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly TemplateConditionWriter _writer;

    public GetTemplateDetailTool(IReadOnlyStockAnalyzerGateway gateway, TemplateConditionWriter writer)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    public string Name => "sa_get_template_detail";

    public string Description =>
        "Returns the detail of one saved template: its identity fields (id, name, description, isFavorite, updatedAt) and its conditions in MCP JSON syntax, " +
        "so that the client can inspect them or pass them directly to sa_run_screener (conditionTree) or sa_run_backtest (conditions and riskManagement). " +
        "templateType selects screener (default) or backtestCondition. " +
        "If any template entries cannot be converted to MCP syntax, unconvertibleEntries lists their indices and reasons. " +
        "The tool only reads; it never creates, changes or migrates a template.";

    public bool IsHeavy => false;

    public string? ExampleArgumentsJson => "{\"templateType\":\"screener\",\"templateId\":\"00000000-0000-0000-0000-000000000000\"}";

    public string InputSchemaJson => ToolSchema.Object()
        .String(TemplateTypeArgument, "Which template kind to read. Default screener.", allowedValues: new[] { ScreenerKind, BacktestConditionKind })
        .String(TemplateIdArgument, "Unique identifier (GUID) of the saved template from sa_list_templates.", required: true)
        .Build();

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        string kind = ToolArgs.String(arguments, TemplateTypeArgument) ?? ScreenerKind;
        string templateIdText = ToolArgs.String(arguments, TemplateIdArgument)
            ?? throw new ToolException(ToolErrorCodes.InvalidArgument, "templateId is required.", TemplateIdArgument);

        return kind == BacktestConditionKind
            ? await GetBacktestDetailAsync(templateIdText, cancellationToken).ConfigureAwait(false)
            : await GetScreenerDetailAsync(templateIdText, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> GetScreenerDetailAsync(string templateIdText, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(templateIdText, out Guid id))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, "templateId must be a template id.", TemplateIdArgument, "Ids come from sa_list_templates.");
        }

        ScreenerIndicatorTemplate template = await _gateway.GetScreenerTemplateAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new ToolException(ToolErrorCodes.TemplateNotFound, $"No readable Screener template has the id {id}.", TemplateIdArgument, "Call sa_list_templates to see the saved templates.");

        ScreenerTemplateConversion conversion = _writer.WriteScreenerTemplate(template);

        return ToolJson.Build(writer =>
        {
            writer.WriteStartObject();
            WriteIdentity(writer, template);
            writer.WritePropertyName("conditionTree");
            writer.WriteRawValue(conversion.ConditionTree.ToJsonString());
            WriteUnconvertibleEntries(writer, conversion.UnconvertibleEntries);
            writer.WriteEndObject();
        });
    }

    private async Task<string> GetBacktestDetailAsync(string templateIdText, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(templateIdText, out Guid id))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, "templateId must be a template id (a GUID) from sa_list_templates.", TemplateIdArgument);
        }

        BacktestConditionTemplate template = await _gateway.GetBacktestConditionTemplateAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new ToolException(ToolErrorCodes.TemplateNotFound, $"No backtest-condition template has the id {id}.", TemplateIdArgument, "Call sa_list_templates with templateType backtestCondition.");

        BacktestTemplateConversion conversion = _writer.WriteBacktestTemplate(template);

        return ToolJson.Build(writer =>
        {
            writer.WriteStartObject();
            WriteIdentity(writer, template);
            writer.WritePropertyName("conditions");
            writer.WriteRawValue(conversion.Conditions.ToJsonString());
            if (conversion.RiskManagement is not null)
            {
                writer.WritePropertyName("riskManagement");
                writer.WriteRawValue(conversion.RiskManagement.ToJsonString());
            }

            WriteUnconvertibleEntries(writer, conversion.UnconvertibleEntries);
            writer.WriteEndObject();
        });
    }

    private static void WriteIdentity(Utf8JsonWriter writer, TemplateBase template)
    {
        writer.WriteString("id", template.Id.ToString());
        writer.WriteString("name", template.Name);
        if (!string.IsNullOrWhiteSpace(template.Description))
        {
            writer.WriteString("description", template.Description);
        }

        writer.WriteBoolean("isFavorite", template.IsFavorite);
        writer.WriteString("updatedAt", template.UpdatedAt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
    }

    private static void WriteUnconvertibleEntries(Utf8JsonWriter writer, IReadOnlyList<TemplateUnconvertibleEntry> unconvertible)
    {
        if (unconvertible.Count == 0)
        {
            return;
        }

        writer.WriteStartArray("unconvertibleEntries");
        foreach (TemplateUnconvertibleEntry item in unconvertible)
        {
            writer.WriteStartObject();
            writer.WriteNumber("index", item.Index);
            writer.WriteString("reason", item.Reason);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}
