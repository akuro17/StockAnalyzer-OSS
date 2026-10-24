using System.Text.Json;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.McpServer.Catalog;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// sa_list_templates: the user's saved templates, so a client can run one by id: Screener templates with sa_run_screener (templateId), backtest-condition
/// templates with sa_run_backtest (templateId). templateType selects the kind; Screener is the default.
/// </summary>
internal sealed class ListTemplatesTool : IMcpTool
{
    private const string TemplateTypeArgument = "templateType";
    private const string ScreenerKind = "screener";
    private const string BacktestConditionKind = "backtestCondition";

    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly ScreenerTemplateAdvisor _advisor;
    private readonly BacktestConditionBinder _backtestConditions;

    public ListTemplatesTool(IReadOnlyStockAnalyzerGateway gateway, ScreenerTemplateAdvisor advisor, BacktestConditionBinder backtestConditions)
    {
        _gateway = gateway;
        _advisor = advisor;
        _backtestConditions = backtestConditions;
    }

    public string Name => "sa_list_templates";

    public string Description =>
        "Lists the templates the user saved in StockAnalyzer, favourites first and then by name. templateType screener (default) lists the Screener templates " +
        "(the combinations of conditions of the Screener tab; run one with sa_run_screener templateId). templateType backtestCondition lists the backtest-condition " +
        "templates (the entry, exit and reverse conditions and the risk settings of the Backtest window; run one with sa_run_backtest templateId). " +
        "Screener templates: each has an id, a name, an optional description, whether it is a favourite, when it was last changed, and its entries: the condition as text, " +
        "whether it is enabled (a disabled entry is ignored) and how it is combined with the next entry (And or Or; entries are combined from the first to the last). " +
        "Backtest-condition templates: each has the same identity fields, the six condition roots as text with their comparison counts, and riskManagement when it is set. " +
        "warnings name what cannot run in this server (for example an indicator that looks ahead, a time frame other than D1, or a template that cannot be converted). " +
        "unreadableFileCount counts template files that could not be read and templates that could not be turned into a listing entry; neither is listed. The tool only reads; it never creates, changes or migrates a template.";

    public bool IsHeavy => false;

    public string InputSchemaJson => ToolSchema.Object()
        .String(TemplateTypeArgument, "Which templates to list. Default screener.", allowedValues: new[] { ScreenerKind, BacktestConditionKind })
        .Build();

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        string kind = ToolArgs.String(arguments, TemplateTypeArgument) ?? ScreenerKind;
        return kind == BacktestConditionKind
            ? await BacktestConditionListingAsync(cancellationToken).ConfigureAwait(false)
            : await ScreenerListingAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ScreenerListingAsync(CancellationToken cancellationToken)
    {
        ScreenerTemplateListing listing = await _gateway.ListScreenerTemplatesAsync(cancellationToken).ConfigureAwait(false);
        return TemplateListing(TemplateType.Screener, listing.Templates, listing.UnreadableFileCount, WriteScreenerTemplate, cancellationToken);
    }

    private async Task<string> BacktestConditionListingAsync(CancellationToken cancellationToken)
    {
        BacktestConditionTemplateListing listing = await _gateway.ListBacktestConditionTemplatesAsync(cancellationToken).ConfigureAwait(false);
        return TemplateListing(TemplateType.BacktestCondition, listing.Templates, listing.UnreadableFileCount, WriteBacktestTemplate, cancellationToken);
    }

    /// <summary>
    /// Each template is rendered on its own before the totals are written, so one template that cannot be rendered does not end the list (D-13 A): it is
    /// left out and counted in unreadableFileCount with the files that could not be read. The exception text is not put into the response (it can name
    /// a path); this tool has no logger. Which failures are absorbed is <see cref="TemplateFailureRule"/>, the same rule the file reader uses.
    /// </summary>
    private static string TemplateListing<T>(TemplateType type, IReadOnlyList<T> templates, int unreadableFileCount, Action<Utf8JsonWriter, T> writeTemplate, CancellationToken cancellationToken)
    {
        var rendered = new List<string>(templates.Count);
        foreach (T template in templates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                rendered.Add(ToolJson.Build(writer => writeTemplate(writer, template)));
            }
            catch (Exception error) when (TemplateFailureRule.IsIsolatable(error))
            {
                unreadableFileCount++;
            }
        }

        return ToolJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("templateType", type.ToString());
            writer.WriteNumber("templateCount", rendered.Count);
            writer.WriteNumber("unreadableFileCount", unreadableFileCount);
            writer.WriteStartArray("templates");
            foreach (string template in rendered)
            {
                writer.WriteRawValue(template);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    private void WriteScreenerTemplate(Utf8JsonWriter writer, ScreenerIndicatorTemplate template)
    {
        writer.WriteStartObject();
        WriteIdentity(writer, template);
        writer.WriteNumber("entryCount", template.Entries.Count);
        writer.WriteNumber("enabledEntryCount", template.Entries.Count(entry => entry.IsEnabled));
        writer.WriteStartArray("entries");
        foreach (ScreenerIndicatorEntry entry in template.Entries)
        {
            writer.WriteStartObject();
            writer.WriteString("text", ScreenerEntryText.Describe(entry));
            writer.WriteBoolean("enabled", entry.IsEnabled);
            writer.WriteString("combinedWithNext", entry.LogicalOperator.ToString());
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        WriteWarnings(writer, _advisor.Warnings(template));
        writer.WriteEndObject();
    }

    private void WriteBacktestTemplate(Utf8JsonWriter writer, BacktestConditionTemplate template)
    {
        writer.WriteStartObject();
        WriteIdentity(writer, template);

        var warnings = new List<string>();
        writer.WriteStartArray("roots");
        try
        {
            BoundBacktestConditions bound = _backtestConditions.FromTemplate(template, "conditionTree");
            foreach (BacktestRootSummary root in bound.Roots)
            {
                writer.WriteStartObject();
                writer.WriteString("root", root.Name);
                writer.WriteNumber("comparisonCount", root.ComparisonCount);
                writer.WriteString("text", root.Text);
                writer.WriteEndObject();
            }

            warnings.AddRange(bound.Warnings);
        }
        catch (ToolException error)
        {
            warnings.Add($"Cannot run through sa_run_backtest: {error.Message}");
        }

        writer.WriteEndArray();

        if (template.RiskManagement is { } risk && (risk.StopLossPercent.HasValue || risk.TakeProfitPercent.HasValue))
        {
            writer.WriteStartObject("riskManagement");
            if (risk.StopLossPercent is { } stop)
            {
                writer.WriteNumber("stopLossRatio", stop);
            }

            if (risk.TakeProfitPercent is { } take)
            {
                writer.WriteNumber("takeProfitRatio", take);
            }

            writer.WriteEndObject();
        }

        WriteWarnings(writer, warnings);
        writer.WriteEndObject();
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
        writer.WriteString("updatedAt", template.UpdatedAt.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void WriteWarnings(Utf8JsonWriter writer, IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0)
        {
            return;
        }

        writer.WriteStartArray("warnings");
        foreach (string warning in warnings)
        {
            writer.WriteStringValue(warning);
        }

        writer.WriteEndArray();
    }
}
