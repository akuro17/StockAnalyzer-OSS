using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// sa_list_ticker_groups: lists the saved workspace ticker groups (All Tickers, watchlists,
/// portfolios, custom categories, list folders, and Tickers-tab filters).
/// </summary>
internal sealed class ListTickerGroupsTool : IMcpTool
{
    private const string IncludeRulesArgument = "includeRules";

    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly IReadOnlyWorkspaceSource _workspaceSource;
    private readonly McpLimits _limits;

    public ListTickerGroupsTool(
        IReadOnlyStockAnalyzerGateway gateway,
        IReadOnlyWorkspaceSource workspaceSource,
        McpLimits limits)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _workspaceSource = workspaceSource ?? throw new ArgumentNullException(nameof(workspaceSource));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
    }

    public string Name => "sa_list_ticker_groups";

    public string Description =>
        "Lists ticker groups (All Tickers, watchlists, portfolios, custom categories, list folders, and Tickers-tab filters) from the saved workspace.";

    public bool IsHeavy => false;

    public string InputSchemaJson => ToolSchema.Object()
        .Boolean(IncludeRulesArgument, "When true, each filter node includes its configured filter rules. Default false.")
        .Build();

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        bool includeRules = ToolArgs.Boolean(arguments, IncludeRulesArgument) ?? false;

        IReadOnlyList<string> registry = await _gateway.GetAvailableTickersAsync(cancellationToken).ConfigureAwait(false);
        WorkspaceSettings? workspaceSettings = await _workspaceSource.LoadAsync(cancellationToken).ConfigureAwait(false);

        TickerGroupTree tree = TickerGroupTree.Build(workspaceSettings, registry, McpLimitsContract.MaxGroupTreeDepth);

        if (tree.Nodes.Count > _limits.MaxGroupNodes)
        {
            throw new ToolException(
                ToolErrorCodes.LimitExceeded,
                $"The workspace contains {tree.Nodes.Count} groups, which exceeds the limit of {_limits.MaxGroupNodes}.",
                hint: "Raise AiIntegration:MaxGroupNodes or remove groups.");
        }

        var registrySet = new HashSet<string>(registry, StringComparer.OrdinalIgnoreCase);

        return ToolJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("workspaceFound", tree.WorkspaceFound);
            writer.WriteNumber("nodeCount", tree.Nodes.Count);
            writer.WriteNumber("droppedFilterCount", tree.DroppedFilterCount);
            writer.WriteBoolean("treeTruncatedAtDepth", tree.TruncatedAtDepth);
            writer.WriteStartArray("groups");

            foreach (var node in tree.Nodes)
            {
                writer.WriteStartObject();
                writer.WriteString("id", node.Id.ToString("D").ToLowerInvariant());

                if (node.ParentId.HasValue)
                {
                    writer.WriteString("parentId", node.ParentId.Value.ToString("D").ToLowerInvariant());
                }
                else
                {
                    writer.WriteNull("parentId");
                }

                writer.WriteString("kind", ToCamelCase(node.Kind));
                writer.WriteString("name", node.Name);
                writer.WriteNumber("depth", node.Depth);

                if (node.Kind == TickerGroupKind.Filter)
                {
                    writer.WriteNull("memberCount");
                    writer.WriteNull("unregisteredMemberCount");
                    int ruleCount = node.Rules?.Count ?? 0;
                    writer.WriteNumber("ruleCount", ruleCount);

                    if (includeRules)
                    {
                        writer.WriteStartArray("rules");
                        if (node.Rules != null)
                        {
                            foreach (var rule in node.Rules)
                            {
                                writer.WriteStartObject();
                                writer.WriteString("field", rule.Field);
                                writer.WriteString("operator", rule.Operator);
                                writer.WriteString("value", rule.Value);
                                writer.WriteBoolean("isCompareToField", rule.IsCompareToField);
                                writer.WriteString("compareMode", rule.CompareMode);
                                writer.WriteEndObject();
                            }
                        }
                        writer.WriteEndArray();
                    }
                }
                else
                {
                    int memberCount = 0;
                    int unregisteredCount = 0;
                    if (node.Members != null)
                    {
                        foreach (var symbol in node.Members)
                        {
                            if (registrySet.Contains(symbol))
                            {
                                memberCount++;
                            }
                            else
                            {
                                unregisteredCount++;
                            }
                        }
                    }

                    writer.WriteNumber("memberCount", memberCount);
                    writer.WriteNumber("unregisteredMemberCount", unregisteredCount);
                    writer.WriteNull("ruleCount");
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    private static string ToCamelCase(TickerGroupKind kind) => kind switch
    {
        TickerGroupKind.AllTickers => "allTickers",
        TickerGroupKind.WatchlistCategory => "watchlistCategory",
        TickerGroupKind.Watchlist => "watchlist",
        TickerGroupKind.PortfolioCategory => "portfolioCategory",
        TickerGroupKind.Portfolio => "portfolio",
        TickerGroupKind.CustomCategory => "customCategory",
        TickerGroupKind.ListFolder => "listFolder",
        TickerGroupKind.Filter => "filter",
        _ => kind.ToString()
    };
}
