using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// Validates a conditionTree (screener) or conditions (backtest) without fetching or evaluating historical price bars.
/// Echoes structural counts, rendered comparison texts, or the first binding error encountered.
/// </summary>
internal sealed class ValidateConditionTreeTool : IMcpTool
{
    private static readonly string[] AllowedKinds = { "screener", "backtest" };

    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly ScreenerConditionBinder _screenerBinder;
    private readonly BacktestConditionBinder _backtestBinder;
    private readonly ScreenerComparisonSymbolResolver _comparisonSymbols;
    private readonly McpLimits _limits;

    public ValidateConditionTreeTool(
        IReadOnlyStockAnalyzerGateway gateway,
        ScreenerConditionBinder screenerBinder,
        BacktestConditionBinder backtestBinder,
        McpLimits limits)
    {
        _gateway = gateway;
        _screenerBinder = screenerBinder;
        _backtestBinder = backtestBinder;
        _limits = limits;
        _comparisonSymbols = new ScreenerComparisonSymbolResolver(gateway, new SymbolResolver(gateway), limits);
    }

    public string Name => "sa_validate_condition_tree";

    public string Description =>
        "Validates a screener conditionTree or backtest conditions without reading market data. " +
        "Returns valid:true with structural metrics (node count, depth, rendered comparisons) on success, " +
        "or valid:false with the first binding error (code, message, path, hint) on invalid input. Read-only.";

    public string InputSchemaJson => ToolSchema.Object()
        .String("kind", "The kind of condition to validate ('screener' or 'backtest').", required: true, allowedValues: AllowedKinds)
        .FreeObject("conditionTree", "The condition tree to validate when kind is 'screener'.", required: false)
        .FreeObject("conditions", "The conditions object to validate when kind is 'backtest'.", required: false)
        .String("timeFrame", "The evaluated timeframe for backtest conditions (D1, W1, MN1). Default is D1.", required: false, allowedValues: Enum.GetNames<TimeFrame>())
        .Build();

    public bool IsHeavy => false;

    public string? ExampleArgumentsJson =>
        "{\"kind\":\"screener\",\"conditionTree\":{\"left\":{\"price\":\"Close\"},\"comparison\":\"GreaterThan\",\"right\":100}}";

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        string? kind = ToolArgs.String(arguments, "kind");
        if (kind is null)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, "kind is required.", "kind");
        }

        if (string.Equals(kind, "screener", StringComparison.OrdinalIgnoreCase))
        {
            if (arguments.TryGetProperty("conditions", out _))
            {
                throw new ToolException(
                    ToolErrorCodes.InvalidArgument,
                    "conditions belongs to backtest only.",
                    "conditions",
                    "Use conditionTree when kind is screener.");
            }

            if (arguments.TryGetProperty("timeFrame", out _))
            {
                throw new ToolException(
                    ToolErrorCodes.InvalidArgument,
                    "timeFrame belongs to backtest only.",
                    "timeFrame",
                    "Omit timeFrame when kind is screener.");
            }

            if (!arguments.TryGetProperty("conditionTree", out JsonElement tree))
            {
                throw new ToolException(
                    ToolErrorCodes.InvalidArgument,
                    "conditionTree is required when kind is screener.",
                    "conditionTree",
                    "Provide a valid conditionTree object.");
            }

            BoundScreenerCondition bound;
            try
            {
                bound = _screenerBinder.Bind(tree, "conditionTree");
                if (bound.ExplicitFrameSides.Count > 0)
                {
                    _screenerBinder.ApplyLiteralFrames(bound, TimeFrame.D1);
                }
                else if (bound.ComparisonSymbols.Count > 0)
                {
                    var warnings = new List<string>();
                    await _comparisonSymbols.ResolveAsync(bound.ComparisonSymbols, "conditionTree", allowUnregistered: false, warnings, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (ToolException ex)
            {
                return ErrorResult("screener", ex);
            }

            return ToolJson.Build(writer =>
            {
                writer.WriteStartObject();
                writer.WriteBoolean("valid", true);
                writer.WriteString("kind", "screener");
                writer.WriteNumber("treeNodeCount", bound.NodeCount);
                writer.WriteNumber("treeDepth", bound.Depth);

                writer.WriteStartArray("comparisons");
                foreach (ScreenerLeaf leaf in bound.Leaves)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", leaf.Id);
                    writer.WriteString("path", leaf.Path);
                    writer.WriteString("text", ScreenerEntryText.Describe(leaf.Entry));
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();

                writer.WriteEndObject();
            });
        }
        else if (string.Equals(kind, "backtest", StringComparison.OrdinalIgnoreCase))
        {
            if (arguments.TryGetProperty("conditionTree", out _))
            {
                throw new ToolException(
                    ToolErrorCodes.InvalidArgument,
                    "conditionTree belongs to screener only.",
                    "conditionTree",
                    "Use conditions when kind is backtest.");
            }

            if (!arguments.TryGetProperty("conditions", out JsonElement conditions))
            {
                throw new ToolException(
                    ToolErrorCodes.InvalidArgument,
                    "conditions is required when kind is backtest.",
                    "conditions",
                    "Provide a valid conditions object with canonical roots (e.g., entryLong).");
            }

            TimeFrame timeFrame = TimeFrame.D1;
            if (arguments.TryGetProperty("timeFrame", out JsonElement tfProp))
            {
                string tfName = tfProp.GetString()!;
                if (!Enum.TryParse(tfName, out TimeFrame parsedTf) || !ReadOnlyGatewayContract.SupportedBacktestTimeFrames.Contains(parsedTf))
                {
                    return ErrorResult("backtest", new ToolException(
                        ToolErrorCodes.UnsupportedTimeFrame,
                        $"Time frame {tfName} is not supported for backtests.",
                        "timeFrame",
                        $"Supported time frames: {string.Join(", ", ReadOnlyGatewayContract.SupportedBacktestTimeFrames)}."));
                }
                timeFrame = parsedTf;
            }

            BoundBacktestConditions bound;
            try
            {
                bound = _backtestBinder.Bind(conditions, "conditions", timeFrame);
            }
            catch (ToolException ex)
            {
                return ErrorResult("backtest", ex);
            }

            var comparisons = new List<(string Id, string Path, string Text)>();
            int compIndex = 0;
            foreach ((BacktestConditionSection section, TradeSide side) in BacktestConditionTree.CanonicalRoots)
            {
                BacktestConditionGroup root = bound.Tree.Root(section, side);
                if (!root.HasLeaf)
                {
                    continue;
                }

                string rootName = BacktestConditionTree.RootName(section, side);
                string rootKey = char.ToLowerInvariant(rootName[0]) + rootName[1..];
                CollectBacktestComparisons(root, $"conditions.{rootKey}", ref compIndex, comparisons);
            }

            return ToolJson.Build(writer =>
            {
                writer.WriteStartObject();
                writer.WriteBoolean("valid", true);
                writer.WriteString("kind", "backtest");

                writer.WriteStartArray("conditionRoots");
                foreach (BacktestRootSummary root in bound.Roots)
                {
                    writer.WriteStartObject();
                    writer.WriteString("root", root.Name);
                    writer.WriteNumber("comparisonCount", root.ComparisonCount);
                    writer.WriteString("text", root.Text);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();

                writer.WriteStartArray("comparisons");
                foreach (var comp in comparisons)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", comp.Id);
                    writer.WriteString("path", comp.Path);
                    writer.WriteString("text", comp.Text);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();

                writer.WriteEndObject();
            });
        }
        else
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"Unsupported kind '{kind}'.",
                "kind",
                $"Allowed values: {string.Join(", ", AllowedKinds)}.");
        }
    }

    private static void CollectBacktestComparisons(
        BacktestConditionGroup group,
        string path,
        ref int index,
        List<(string Id, string Path, string Text)> list)
    {
        int childIndex = 0;
        foreach (IBacktestConditionNode child in group.Children)
        {
            string childPath = group.Children.Length == 1 && (path.EndsWith("Long", StringComparison.Ordinal) || path.EndsWith("Short", StringComparison.Ordinal))
                ? path
                : $"{path}.children[{childIndex}]";

            if (child is BacktestConditionLeaf leaf)
            {
                list.Add(($"C{index++}", childPath, BacktestConditionText.Describe(leaf.Comparison)));
            }
            else if (child is BacktestConditionGroup nested)
            {
                CollectBacktestComparisons(nested, childPath, ref index, list);
            }

            childIndex++;
        }
    }

    private static string ErrorResult(string kind, ToolException ex)
    {
        return ToolJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("valid", false);
            writer.WriteString("kind", kind);

            writer.WriteStartObject("error");
            writer.WriteString("code", ex.Code);
            writer.WriteString("message", ex.Message);
            if (ex.Parameter is not null)
            {
                writer.WriteString("path", ex.Parameter);
            }
            if (ex.Hint is not null)
            {
                writer.WriteString("hint", ex.Hint);
            }
            writer.WriteEndObject();

            writer.WriteEndObject();
        });
    }
}
