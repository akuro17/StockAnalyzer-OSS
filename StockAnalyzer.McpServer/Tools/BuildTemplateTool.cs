using System.Text.Json;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.McpServer.Catalog;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// sa_build_template: builds a new StockAnalyzer template (ScreenerIndicatorTemplate or BacktestConditionTemplate) or
/// dynamically merges new conditions into an existing saved template, returning both the MCP representation and the
/// exact persisted StockAnalyzer Template JSON structure that works immediately when saved to Data/Templates/.
/// </summary>
internal sealed class BuildTemplateTool : IMcpTool
{
    private const string TemplateTypeArgument = "templateType";
    private const string NameArgument = "name";
    private const string DescriptionArgument = "description";
    private const string TemplateIdArgument = "templateId";
    private const string BaseTemplateIdArgument = "baseTemplateId";
    private const string MergeModeArgument = "mergeMode";
    private const string TimeFrameArgument = "timeFrame";
    private const string ConditionTreeArgument = "conditionTree";
    private const string ConditionsArgument = "conditions";
    private const string RiskManagementArgument = "riskManagement";

    private const string ScreenerType = "screener";
    private const string BacktestConditionType = "backtestCondition";

    private const string MergeModeAppendAnd = "appendAnd";
    private const string MergeModeAppendOr = "appendOr";
    private const string MergeModeReplace = "replace";

    private static readonly string[] TemplateTypes = { ScreenerType, BacktestConditionType };
    private static readonly string[] MergeModes = { MergeModeAppendAnd, MergeModeAppendOr, MergeModeReplace };

    private readonly TemplateStructureBuilder _builder;

    public BuildTemplateTool(TemplateStructureBuilder builder)
    {
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
    }

    public string Name => "sa_build_template";

    public string Description =>
        "Builds a new StockAnalyzer template (ScreenerIndicatorTemplate or BacktestConditionTemplate) or merges new conditions into an existing saved template (baseTemplateId), " +
        "returning both the normalized MCP query representation and the complete persisted StockAnalyzer Template JSON (template object and templateJson string). " +
        "When saved by the user into Data/Templates/Screener/{id}.json or Data/Templates/BacktestCondition/{id}.json, it functions immediately and seamlessly in the Screener or Backtest modules. " +
        "templateType selects screener (default) or backtestCondition. " +
        "For screener: give conditionTree (and optional baseTemplateId, mergeMode appendAnd|appendOr|replace). " +
        "For backtestCondition: give conditions and/or riskManagement (and optional baseTemplateId, mergeMode appendAnd|appendOr|replace). " +
        "The tool is strictly read-only and never writes to disk.";

    public bool IsHeavy => false;

    public string? ExampleArgumentsJson =>
        "{\"templateType\":\"screener\",\"name\":\"RSI Oversold\",\"conditionTree\":{\"left\":{\"indicator\":\"RSI\",\"parameters\":{\"Period\":14}},\"comparison\":\"LessThan\",\"right\":30}}";

    public string InputSchemaJson => ToolSchema.Object()
        .String(TemplateTypeArgument, "Which template kind to build: screener (default) or backtestCondition.", allowedValues: TemplateTypes)
        .String(NameArgument, "Human-readable name of the template (1..200 characters). Required when baseTemplateId is omitted; defaults to base template name otherwise.")
        .String(DescriptionArgument, "Optional human-readable description (at most 1000 characters).")
        .String(TemplateIdArgument, "Optional GUID for the template. Defaults to baseTemplateId when provided, or a new GUID otherwise.")
        .String(BaseTemplateIdArgument, "Optional GUID of an existing saved template to merge or update conditions into (ids from sa_list_templates).")
        .String(MergeModeArgument, "How new conditions combine with baseTemplateId: appendAnd (default), appendOr, or replace.", allowedValues: MergeModes)
        .String(TimeFrameArgument, "Time frame for conditions that do not specify their own: D1 (default), W1, or MN1.")
        .FreeObject(ConditionTreeArgument, "The Screener condition tree (required when templateType is screener; forbidden for backtestCondition).")
        .FreeObject(ConditionsArgument, "The Backtest conditions object with up to 6 roots (for backtestCondition; forbidden for screener).")
        .FreeObject(RiskManagementArgument, "The Backtest risk management object: {stopLossRatio?: number, takeProfitRatio?: number} (for backtestCondition; forbidden for screener).")
        .Build();

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        string templateType = ToolArgs.String(arguments, TemplateTypeArgument) ?? ScreenerType;
        if (!TemplateTypes.Contains(templateType, StringComparer.OrdinalIgnoreCase))
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"'{templateType}' is not a valid templateType.",
                TemplateTypeArgument,
                $"Allowed values: {string.Join(", ", TemplateTypes)}.");
        }

        string mergeModeStr = ToolArgs.String(arguments, MergeModeArgument) ?? MergeModeAppendAnd;
        TemplateMergeMode mergeMode = mergeModeStr.ToLowerInvariant() switch
        {
            "appendand" => TemplateMergeMode.AppendAnd,
            "appendor" => TemplateMergeMode.AppendOr,
            "replace" => TemplateMergeMode.Replace,
            _ => throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"'{mergeModeStr}' is not a valid mergeMode.",
                MergeModeArgument,
                $"Allowed values: {string.Join(", ", MergeModes)}."),
        };

        BuiltTemplateResult result = string.Equals(templateType, BacktestConditionType, StringComparison.OrdinalIgnoreCase)
            ? await _builder.BuildBacktestTemplateAsync(arguments, mergeMode, mergeModeStr, cancellationToken).ConfigureAwait(false)
            : await _builder.BuildScreenerTemplateAsync(arguments, mergeMode, mergeModeStr, cancellationToken).ConfigureAwait(false);

        return ToolJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("templateType", result.TemplateType);
            writer.WriteString("id", result.Id.ToString());
            writer.WriteString("name", result.Name);
            if (!string.IsNullOrWhiteSpace(result.Description))
            {
                writer.WriteString("description", result.Description);
            }
            else
            {
                writer.WriteNull("description");
            }

            writer.WriteNumber("schemaVersion", result.SchemaVersion);
            if (result.BaseTemplateId is { } baseId)
            {
                writer.WriteString("baseTemplateId", baseId.ToString());
            }

            writer.WriteString("mergeMode", result.MergeMode);
            writer.WriteString("suggestedRelativePath", result.SuggestedRelativePath);
            writer.WriteNumber("nodeCount", result.NodeCount);
            writer.WriteNumber("depth", result.Depth);

            writer.WriteStartArray("warnings");
            foreach (string warning in result.Warnings)
            {
                writer.WriteStringValue(warning);
            }
            writer.WriteEndArray();

            if (result.ConditionTree is not null)
            {
                writer.WritePropertyName("conditionTree");
                writer.WriteRawValue(result.ConditionTree.ToJsonString());
            }

            if (result.Conditions is not null)
            {
                writer.WritePropertyName("conditions");
                writer.WriteRawValue(result.Conditions.ToJsonString());
            }

            if (result.RiskManagement is not null)
            {
                writer.WritePropertyName("riskManagement");
                writer.WriteRawValue(result.RiskManagement.ToJsonString());
            }

            writer.WritePropertyName("template");
            result.TemplateElement.WriteTo(writer);

            writer.WriteString("templateJson", result.TemplateJson);

            writer.WriteEndObject();
        });
    }
}
