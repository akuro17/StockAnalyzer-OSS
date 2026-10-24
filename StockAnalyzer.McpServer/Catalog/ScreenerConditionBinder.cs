using System.Collections.Immutable;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using StockAnalyzer.McpServer.Tools;
using static StockAnalyzer.McpServer.Catalog.ConditionJson;

namespace StockAnalyzer.McpServer.Catalog;

/// <summary>One comparison of the bound tree, in tree order; <see cref="Id"/> (L0, L1, ...) names it in the result.</summary>
internal sealed record ScreenerLeaf(string Id, string Path, ScreenerIndicatorEntry Entry);

/// <summary>A comparison symbol a condition names (canonical form) and the path of the parameter that names it, for error parameters.</summary>
internal sealed record ScreenerComparisonSymbol(string Symbol, string Path);

/// <summary>The tree as the Screener evaluates it, with its leaves, the comparison symbols it names and the measures the size limits use.</summary>
internal sealed record BoundScreenerCondition(
    ScreenerConditionTreeCondition Condition,
    IReadOnlyList<ScreenerLeaf> Leaves,
    int NodeCount,
    int Depth,
    IReadOnlyList<ScreenerComparisonSymbol> ComparisonSymbols);

/// <summary>
/// Turns the client's condition tree into the objects the Screener tab builds: <see cref="ScreenerConditionGroup"/> and
/// <see cref="ScreenerConditionLeaf"/> over <see cref="ScreenerIndicatorEntry"/>. Every key, type and name is checked here (unknown
/// keys are rejected, indicator parameters go through the strict <see cref="IndicatorSettingsBinder"/>) so a request the Screener would
/// silently evaluate to "no match" is an error instead. The size limits are the application's own settings.
/// </summary>
internal sealed class ScreenerConditionBinder
{
    private static readonly string[] GroupKeys = { "operator", "children" };
    private static readonly string[] LeafKeys = { "left", "comparison", "right" };
    private static readonly string[] SideKeys = { "indicator", "price", "column", "criteria", "parameters", "outputSeriesName", "offset" };
    private static readonly string[] SideKinds = { "indicator", "price", "column", "criteria" };
    private const string ComparisonSymbolName = nameof(ICrossTickerParameter.ComparisonSymbol);
    private static readonly ComparisonOperator[] TextOperators = { ComparisonOperator.Equal, ComparisonOperator.NotEqual, ComparisonOperator.Contains, ComparisonOperator.DoesNotContain };

    private readonly IndicatorSideResolver _indicatorSides;
    private readonly ScreenerCatalog _screenerCatalog;
    private readonly IStockAnalyzerSettings _settings;

    public ScreenerConditionBinder(IndicatorCatalog indicators, IndicatorSettingsBinder binder, ScreenerCatalog screenerCatalog, IStockAnalyzerSettings settings)
    {
        _indicatorSides = new IndicatorSideResolver(indicators, binder);
        _screenerCatalog = screenerCatalog;
        _settings = settings;
    }

    /// <summary>The comparison names a client may use, as the application's enum spells them.</summary>
    public static IReadOnlyList<string> ComparisonNames { get; } = Enum.GetNames<ComparisonOperator>();

    /// <summary>The group operator names.</summary>
    public static IReadOnlyList<string> LogicalOperatorNames { get; } = Enum.GetNames<LogicalOperator>();

    public BoundScreenerCondition Bind(JsonElement tree, string path)
    {
        var state = new State();
        ScreenerConditionGroup root;
        if (IsGroup(tree))
        {
            root = BindGroup(tree, path, 1, state);
        }
        else
        {
            // A single comparison is a tree of one leaf under an And root, as the Screener tab holds it.
            state.CountNode(path, _settings);
            state.Depth = 1;
            root = new ScreenerConditionGroup(LogicalOperator.And, ImmutableArray.Create<IScreenerConditionNode>(BindLeaf(tree, path, state)));
        }

        return new BoundScreenerCondition(new ScreenerConditionTreeCondition(root), state.Leaves, state.Nodes, state.Depth, state.ComparisonSymbols);
    }

    private static bool IsGroup(JsonElement node)
        => node.ValueKind == JsonValueKind.Object && (node.TryGetProperty("children", out _) || node.TryGetProperty("operator", out _));

    private ScreenerConditionGroup BindGroup(JsonElement node, string path, int depth, State state)
    {
        RequireObject(node, path);
        RejectUnknownKeys(node, path, GroupKeys);
        if (depth > _settings.ScreenerMaxFilterTreeDepth)
        {
            throw TreeLimit($"The condition tree is nested deeper than {_settings.ScreenerMaxFilterTreeDepth} levels.", path, "Screener:MaxFilterTreeDepth", _settings.ScreenerMaxFilterTreeDepth);
        }

        state.Depth = Math.Max(state.Depth, depth);
        state.CountNode(path, _settings);

        LogicalOperator op = ParseEnum<LogicalOperator>(Require(node, "operator", path, JsonValueKind.String), $"{path}.operator", LogicalOperatorNames);
        JsonElement children = Require(node, "children", path, JsonValueKind.Array);
        if (children.GetArrayLength() == 0)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, "A group needs at least one child (an empty group never matches).", $"{path}.children");
        }

        var bound = ImmutableArray.CreateBuilder<IScreenerConditionNode>();
        int index = 0;
        foreach (JsonElement child in children.EnumerateArray())
        {
            string childPath = $"{path}.children[{index}]";
            bound.Add(IsGroup(child) ? BindGroup(child, childPath, depth + 1, state) : BindLeaf(child, childPath, state));
            index++;
        }

        return new ScreenerConditionGroup(op, bound.ToImmutable());
    }

    private IScreenerConditionNode BindLeaf(JsonElement node, string path, State state)
    {
        RequireObject(node, path);
        RejectUnknownKeys(node, path, LeafKeys);
        state.CountNode(path, _settings);

        (ScreenerIndicatorSideConfig left, bool leftIsText) = BindSide(Require(node, "left", path, JsonValueKind.Object), $"{path}.left", state);
        ComparisonOperator op = ParseEnum<ComparisonOperator>(Require(node, "comparison", path, JsonValueKind.String), $"{path}.comparison", ComparisonNames);
        JsonElement right = Require(node, "right", path);

        var entry = new ScreenerIndicatorEntry
        {
            LeftHand = left,
            Operator = op,
            CategoryType = left.CategoryType,
            IsEnabled = true,
        };

        string rightPath = $"{path}.right";
        if (leftIsText)
        {
            if (right.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(right.GetString()))
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, "A text column is compared with a non-empty text.", rightPath, "Example: \"right\": \"Technology\".");
            }

            if (!TextOperators.Contains(op))
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"Comparison '{op}' cannot compare text.", $"{path}.comparison", $"Use {string.Join(", ", TextOperators)}.");
            }

            entry.TargetMode = RightHandTargetMode.StringValue;
            entry.RightStringValue = right.GetString()!.Trim();
        }
        else
        {
            if (op is ComparisonOperator.Contains or ComparisonOperator.DoesNotContain)
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"Comparison '{op}' is for text columns only.", $"{path}.comparison", "Use GreaterThan, GreaterThanOrEqual, LessThan, LessThanOrEqual, Equal or NotEqual.");
            }

            switch (right.ValueKind)
            {
                case JsonValueKind.Number:
                    if (!right.TryGetDecimal(out decimal number))
                    {
                        throw new ToolException(ToolErrorCodes.InvalidArgument, "The number is outside the supported range.", rightPath);
                    }

                    entry.TargetMode = RightHandTargetMode.NumericValue;
                    entry.RightNumericValue = number;
                    break;

                case JsonValueKind.Object:
                    (ScreenerIndicatorSideConfig rightSide, bool rightIsText) = BindSide(right, rightPath, state);
                    if (rightIsText)
                    {
                        throw new ToolException(ToolErrorCodes.InvalidArgument, "A text column cannot be the right side of a numeric comparison.", rightPath);
                    }

                    entry.TargetMode = RightHandTargetMode.Indicator;
                    entry.RightHand = rightSide;
                    break;

                default:
                    throw new ToolException(
                        ToolErrorCodes.InvalidArgument,
                        "The right side of a numeric comparison is a number or a side object.",
                        rightPath,
                        "Example: \"right\": 70, or \"right\": {\"indicator\": \"SMA\", \"parameters\": {\"Period\": 50}}.");
            }
        }

        var leaf = new ScreenerLeaf($"L{state.Leaves.Count}", path, entry);
        state.Leaves.Add(leaf);
        return new ScreenerConditionLeaf(entry);
    }

    /// <summary>One side of a comparison; the flag says whether it is a text column.</summary>
    private (ScreenerIndicatorSideConfig Side, bool IsText) BindSide(JsonElement side, string path, State state)
    {
        RequireObject(side, path);
        RejectUnknownKeys(side, path, SideKeys);

        string[] kinds = SideKinds.Where(kind => side.TryGetProperty(kind, out _)).ToArray();
        if (kinds.Length != 1)
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                "A side names exactly one of indicator, price, column or criteria.",
                path,
                "See sa_get_capabilities with includeScreenerCatalog true for the column, criteria and price names.");
        }

        int offset = 0;
        if (side.TryGetProperty("offset", out JsonElement offsetElement))
        {
            if (offsetElement.ValueKind != JsonValueKind.Number || !offsetElement.TryGetInt32(out offset) || offset < 0)
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, "offset is the number of bars back from the latest bar and must be an integer of 0 or more.", $"{path}.offset");
            }

            if (offset > _settings.ScreenerMaxConditionOffset)
            {
                throw TreeLimit($"The offset {offset} is above the maximum {_settings.ScreenerMaxConditionOffset}.", $"{path}.offset", "Screener:MaxConditionOffset", _settings.ScreenerMaxConditionOffset);
            }
        }

        string kind = kinds[0];
        if (kind != "indicator" && (side.TryGetProperty("parameters", out _) || side.TryGetProperty("outputSeriesName", out _)))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, "parameters and outputSeriesName belong to an indicator side only.", path);
        }

        string name = Require(side, kind, path, JsonValueKind.String).GetString()!;
        switch (kind)
        {
            case "indicator":
                return (BindIndicator(side, name, offset, path, state), false);

            case "price":
            {
                ScreenerCatalogItem item = _screenerCatalog.FindPrice(name)
                    ?? throw UnknownName("price", name, $"{path}.price");
                return (new ScreenerIndicatorSideConfig
                {
                    IndicatorType = IndicatorType.Price,
                    OutputName = item.ShortName,
                    CustomDisplayName = item.DisplayName,
                    CategoryType = ScreenerItemCategoryType.Indicator,
                    TimeFrame = TimeFrame.D1,
                    Offset = offset,
                }, false);
            }

            case "column":
            {
                ScreenerCatalogItem item = _screenerCatalog.FindColumn(name)
                    ?? throw UnknownName("column", name, $"{path}.column");
                if (!_screenerCatalog.IsEvaluable(item))
                {
                    throw new ToolException(
                        ToolErrorCodes.InvalidArgument,
                        $"Column '{item.ShortName}' cannot be evaluated by the screener.",
                        $"{path}.column",
                        "It holds descriptive text or a value the screener cannot read reliably; sa_get_capabilities with includeScreenerCatalog true lists the usable columns and unavailableColumns.");
                }

                return (ScreenerCatalog.ColumnSide(item, offset), ScreenerCatalog.IsTextColumn(item.ShortName));
            }

            default:
            {
                ScreenerCatalogItem item = _screenerCatalog.FindCriteria(name)
                    ?? throw UnknownName("criteria", name, $"{path}.criteria");
                return (CriteriaSide(item, offset), false);
            }
        }
    }

    /// <summary>A Criteria side the way the registration dialog stores it: the short name (the pattern identifier) in OutputName, the description in CustomDisplayName.</summary>
    private static ScreenerIndicatorSideConfig CriteriaSide(ScreenerCatalogItem item, int offset)
        => new()
        {
            OutputName = item.ShortName,
            CustomDisplayName = item.DisplayName,
            CategoryType = ScreenerItemCategoryType.Criteria,
            TimeFrame = TimeFrame.D1,
            Offset = offset,
        };

    private ScreenerIndicatorSideConfig BindIndicator(JsonElement side, string typeName, int offset, string path, State state)
    {
        ResolvedIndicatorSide resolved = _indicatorSides.Resolve(side, typeName, "the screener", path, allowCrossTicker: true);

        // An indicator that compares two symbols needs the comparison symbol; it is written back in canonical form, as the other tools do.
        // That it is a stored symbol is checked by the tool, which knows the stored symbols.
        if (resolved.Settings.ParameterObject is ICrossTickerParameter cross)
        {
            string symbolPath = $"{path}.parameters.{ComparisonSymbolName}";
            if (string.IsNullOrWhiteSpace(cross.ComparisonSymbol))
            {
                throw new ToolException(
                    ToolErrorCodes.InvalidArgument,
                    $"{resolved.Type} compares two symbols and needs the {ComparisonSymbolName} parameter.",
                    symbolPath,
                    "Set it to a symbol from sa_list_tickers.");
            }

            cross.ComparisonSymbol = SymbolResolver.Canonical(cross.ComparisonSymbol, symbolPath);
            state.ComparisonSymbols.Add(new ScreenerComparisonSymbol(cross.ComparisonSymbol, symbolPath));
        }

        return new ScreenerIndicatorSideConfig
        {
            IndicatorType = resolved.Type,
            Parameters = IndicatorSideResolver.ParameterValues(resolved.Settings.ParameterObject),
            OutputName = resolved.Series,
            CategoryType = ScreenerItemCategoryType.Indicator,
            TimeFrame = TimeFrame.D1,
            Offset = offset,
        };
    }

    private static ToolException UnknownName(string what, string name, string path)
        => new(
            ToolErrorCodes.InvalidArgument,
            $"Unknown {what} '{name}'.",
            path,
            "Call sa_get_capabilities with includeScreenerCatalog true to list the names.");

    private static ToolException TreeLimit(string message, string path, string setting, int limit)
        => new(ToolErrorCodes.ConditionLimitExceeded, message, path, $"The limit is the application setting {setting} = {limit}.");

    private sealed class State
    {
        public List<ScreenerLeaf> Leaves { get; } = new();

        public List<ScreenerComparisonSymbol> ComparisonSymbols { get; } = new();

        public int Nodes { get; private set; }

        public int Depth { get; set; }

        public void CountNode(string path, IStockAnalyzerSettings settings)
        {
            Nodes++;
            if (Nodes > settings.ScreenerMaxFilterTreeNodes)
            {
                throw TreeLimit($"The condition tree has more than {settings.ScreenerMaxFilterTreeNodes} nodes (groups and comparisons).", path, "Screener:MaxFilterTreeNodes", settings.ScreenerMaxFilterTreeNodes);
            }
        }
    }
}
