using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Configuration;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.McpServer.Tools;
using static StockAnalyzer.McpServer.Catalog.ConditionJson;

namespace StockAnalyzer.McpServer.Catalog;

/// <summary>One of the six condition roots of the bound tree, as the result echoes it.</summary>
internal sealed record BacktestRootSummary(string Name, int ComparisonCount, string Text);

/// <summary>The tree as the engine runs it, the roots that carry conditions, and the advisory notes for the client.</summary>
internal sealed record BoundBacktestConditions(BacktestConditionTree Tree, IReadOnlyList<BacktestRootSummary> Roots, IReadOnlyList<string> Warnings);

/// <summary>
/// Turns the client's conditions object into the engine's <see cref="BacktestConditionTree"/> (six roots, groups and comparisons).
/// Every key, type and name is checked here; the result then passes the application's own
/// <see cref="BacktestConditionValidator.SnapshotTree"/> with the configured Backtest limits, so the tool accepts exactly what the
/// Backtest window accepts. The six roots are the persisted tree's property names (entryLong ... reverseShort).
/// </summary>
internal sealed class BacktestConditionBinder
{
    private static readonly string[] GroupKeys = { "operator", "children" };
    private static readonly string[] LeafKeys = { "left", "comparison", "right" };
    private static readonly string[] SideKeys = { "indicator", "price", "parameters", "outputSeriesName", "offset" };
    private static readonly string[] SideKinds = { "indicator", "price" };

    /// <summary>The comparisons the backtest evaluates (the screener's text comparisons do not apply to numeric series).</summary>
    public static IReadOnlyList<string> ComparisonNames { get; } = new[]
    {
        nameof(ComparisonOperator.GreaterThan),
        nameof(ComparisonOperator.GreaterThanOrEqual),
        nameof(ComparisonOperator.LessThan),
        nameof(ComparisonOperator.LessThanOrEqual),
        nameof(ComparisonOperator.Equal),
        nameof(ComparisonOperator.NotEqual),
    };

    /// <summary>
    /// The comparisons the backtest deliberately does not accept, with the reason. Together with <see cref="ComparisonNames"/> this covers every
    /// <see cref="ComparisonOperator"/>: a new operator in neither list fails <c>McpParityGuardTests</c> instead of being silently missing.
    /// </summary>
    public static IReadOnlyDictionary<ComparisonOperator, string> ExcludedComparisons { get; } = new Dictionary<ComparisonOperator, string>
    {
        [ComparisonOperator.Contains] = "Text comparisons are not used on numeric series.",
        [ComparisonOperator.DoesNotContain] = "Text comparisons are not used on numeric series.",
    };

    /// <summary>The persisted tree's root names in the client's spelling (camelCase), in the engine's canonical order.</summary>
    public static IReadOnlyList<string> RootKeys { get; } = BacktestConditionTree.CanonicalRoots
        .Select(root => ToKey(BacktestConditionTree.RootName(root.Section, root.Side)))
        .ToArray();

    private readonly IndicatorCatalog _indicators;
    private readonly IndicatorSideResolver _indicatorSides;
    private readonly IStockAnalyzerSettings _settings;

    public BacktestConditionBinder(IndicatorCatalog indicators, IndicatorSettingsBinder binder, IStockAnalyzerSettings settings)
    {
        _indicators = indicators;
        _indicatorSides = new IndicatorSideResolver(indicators, binder);
        _settings = settings;
    }

    public BoundBacktestConditions Bind(JsonElement conditions, string path)
    {
        RequireObject(conditions, path);
        RejectUnknownKeys(conditions, path, RootKeys.ToArray());

        var summaries = new List<BacktestRootSummary>();
        BacktestConditionTree tree = BacktestConditionTree.Create((section, side) =>
        {
            string name = BacktestConditionTree.RootName(section, side);
            string key = ToKey(name);
            if (!conditions.TryGetProperty(key, out JsonElement root))
            {
                return BacktestConditionGroup.EmptyAnd;
            }

            string rootPath = $"{path}.{key}";
            var state = new State();
            BacktestConditionGroup group = IsGroup(root)
                ? BindGroup(root, rootPath, 1, state)
                : SingleLeafRoot(root, rootPath, state);
            summaries.Add(new BacktestRootSummary(name, state.Leaves, BacktestConditionText.Describe(group)));
            return group;
        });

        if (!tree.HasAnyLeaf)
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                "conditions needs at least one comparison.",
                path,
                $"Give at least one of: {string.Join(", ", RootKeys)}. A backtest without conditions never trades.");
        }

        ValidateTree(tree, path);

        return new BoundBacktestConditions(tree, summaries, EntryWarnings(tree));
    }

    /// <summary>
    /// The conditions of a saved backtest-condition template, under the same rules as the client's tree: the template's tree is converted with the
    /// application's own mapper, checked against the configured limits, and refused when a side names a time frame other than the backtest's (D1).
    /// </summary>
    /// <exception cref="ToolException">The template cannot run through this tool.</exception>
    public BoundBacktestConditions FromTemplate(BacktestConditionTemplate template, string path)
    {
        ArgumentNullException.ThrowIfNull(template);
        BacktestConditionTree tree;
        try
        {
            tree = BacktestConditionTreeDtoMapper.ToTree(template.ConditionTree, _settings.BacktestMaxConditionTreeDepth);
        }
        catch (ArgumentException error)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"The template's conditions cannot be read: {error.Message.Split(Environment.NewLine)[0]}", path);
        }

        foreach (BacktestConditionEntry entry in AllComparisons(tree))
        {
            foreach (TimeFrame? frame in new[] { entry.Left.Frame, entry.Right?.Frame })
            {
                if (frame is { } named && named != ReadOnlyGatewayContract.BacktestTimeFrame)
                {
                    throw new ToolException(
                        ToolErrorCodes.UnsupportedTimeFrame,
                        $"The template compares a {named} side; the backtest supports only {ReadOnlyGatewayContract.BacktestTimeFrame}.",
                        path,
                        "Use a template whose sides all use the evaluated time frame.");
                }
            }
        }

        if (!tree.HasAnyLeaf)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, "The template has no comparison, so it never trades.", path);
        }

        CheckIndicatorParameters(tree, path);
        ValidateTree(tree, path);
        return Summarize(tree);
    }

    /// <summary>
    /// A saved template carries typed indicator parameters that never passed the client's range checks (the file can be edited by hand), so
    /// every indicator side gets the same checks as a client value (<see cref="IndicatorParameterRules"/>). The error names the template and the
    /// position of the side inside it, for example <c>entryLong.children[0].left</c>.
    /// </summary>
    private void CheckIndicatorParameters(BacktestConditionTree tree, string path)
    {
        foreach ((BacktestConditionSection section, TradeSide side) in BacktestConditionTree.CanonicalRoots)
        {
            CheckIndicatorParameters(tree.Root(section, side), ToKey(BacktestConditionTree.RootName(section, side)), path);
        }
    }

    private void CheckIndicatorParameters(BacktestConditionGroup group, string location, string path)
    {
        int index = 0;
        foreach (IBacktestConditionNode child in group.Children)
        {
            string childLocation = $"{location}.children[{index}]";
            switch (child)
            {
                case BacktestConditionGroup nested:
                    CheckIndicatorParameters(nested, childLocation, path);
                    break;
                case BacktestConditionLeaf leaf:
                    CheckSideParameters(leaf.Comparison.Left, $"{childLocation}.left", path);
                    if (leaf.Comparison.TargetMode == RightHandTargetMode.Indicator && leaf.Comparison.Right is { } right)
                    {
                        CheckSideParameters(right, $"{childLocation}.right", path);
                    }

                    break;
            }

            index++;
        }
    }

    private void CheckSideParameters(BacktestConditionSide side, string location, string path)
    {
        if (side.IndicatorType != IndicatorType.Price
            && side.Parameters is { } parameters
            && _indicators.Find(side.IndicatorType.ToString()) is { } entry)
        {
            IndicatorParameterRules.CheckTyped(entry, parameters, path, location);
        }
    }

    /// <summary>The six roots of a tree in the client's spelling, each with the comparisons it holds (roots without comparisons are omitted).</summary>
    public BoundBacktestConditions Summarize(BacktestConditionTree tree)
    {
        var summaries = new List<BacktestRootSummary>();
        foreach ((BacktestConditionSection section, TradeSide side) in BacktestConditionTree.CanonicalRoots)
        {
            BacktestConditionGroup root = tree.Root(section, side);
            if (!root.HasLeaf)
            {
                continue;
            }

            string name = BacktestConditionTree.RootName(section, side);
            summaries.Add(new BacktestRootSummary(name, AllComparisons(root).Count(), BacktestConditionText.Describe(root)));
        }

        return new BoundBacktestConditions(tree, summaries, EntryWarnings(tree));
    }

    /// <summary>Checks a tree against the configured Backtest limits (offset, depth, node count), as the client's conditions are checked.</summary>
    public void ValidateTree(BacktestConditionTree tree, string path)
    {
        try
        {
            BacktestConditionValidator.SnapshotTree(tree, _settings.BacktestMaxConditionOffset, _settings.BacktestMaxConditionTreeDepth, _settings.BacktestMaxConditionTreeNodes);
        }
        catch (ArgumentOutOfRangeException error)
        {
            throw new ToolException(ToolErrorCodes.ConditionLimitExceeded, error.Message.Split(Environment.NewLine)[0], path);
        }
        catch (ArgumentException error)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, error.Message.Split(Environment.NewLine)[0], path);
        }
    }

    private static IReadOnlyList<string> EntryWarnings(BacktestConditionTree tree)
    {
        var warnings = new List<string>();
        if (!tree.EntryLong.HasLeaf && !tree.EntryShort.HasLeaf)
        {
            warnings.Add("No entry condition (entryLong or entryShort): the strategy cannot open a position, because a reverse condition acts only while a position is held.");
        }

        return warnings;
    }

    private static IEnumerable<BacktestConditionEntry> AllComparisons(BacktestConditionTree tree)
        => CanonicalRootsOf(tree).SelectMany(AllComparisons);

    private static IEnumerable<BacktestConditionGroup> CanonicalRootsOf(BacktestConditionTree tree)
        => BacktestConditionTree.CanonicalRoots.Select(root => tree.Root(root.Section, root.Side));

    private static IEnumerable<BacktestConditionEntry> AllComparisons(BacktestConditionGroup group)
    {
        foreach (IBacktestConditionNode child in group.Children)
        {
            switch (child)
            {
                case BacktestConditionLeaf leaf:
                    yield return leaf.Comparison;
                    break;
                case BacktestConditionGroup nested:
                    foreach (BacktestConditionEntry entry in AllComparisons(nested))
                    {
                        yield return entry;
                    }

                    break;
            }
        }
    }

    private static string ToKey(string rootName) => char.ToLowerInvariant(rootName[0]) + rootName[1..];

    private static bool IsGroup(JsonElement node)
        => node.ValueKind == JsonValueKind.Object && (node.TryGetProperty("children", out _) || node.TryGetProperty("operator", out _));

    /// <summary>A root given as one comparison is an And group of one leaf (a tree root is always a group).</summary>
    private BacktestConditionGroup SingleLeafRoot(JsonElement node, string path, State state)
    {
        state.CountNode(path, _settings);
        state.Depth = 1;
        return new BacktestConditionGroup(LogicalOperator.And, ImmutableArray.Create<IBacktestConditionNode>(BindLeaf(node, path, state)));
    }

    private BacktestConditionGroup BindGroup(JsonElement node, string path, int depth, State state)
    {
        RequireObject(node, path);
        RejectUnknownKeys(node, path, GroupKeys);
        if (depth > _settings.BacktestMaxConditionTreeDepth)
        {
            throw TreeLimit($"The condition tree is nested deeper than {_settings.BacktestMaxConditionTreeDepth} levels.", path, "Backtest:MaxConditionTreeDepth", _settings.BacktestMaxConditionTreeDepth);
        }

        state.Depth = Math.Max(state.Depth, depth);
        state.CountNode(path, _settings);

        LogicalOperator op = ParseEnum<LogicalOperator>(Require(node, "operator", path, JsonValueKind.String), $"{path}.operator", ScreenerConditionBinder.LogicalOperatorNames);
        JsonElement children = Require(node, "children", path, JsonValueKind.Array);
        if (children.GetArrayLength() == 0)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, "A group needs at least one child (an empty group never matches).", $"{path}.children");
        }

        var bound = ImmutableArray.CreateBuilder<IBacktestConditionNode>();
        int index = 0;
        foreach (JsonElement child in children.EnumerateArray())
        {
            string childPath = $"{path}.children[{index}]";
            bound.Add(IsGroup(child) ? BindGroup(child, childPath, depth + 1, state) : BindLeaf(child, childPath, state));
            index++;
        }

        return new BacktestConditionGroup(op, bound.ToImmutable());
    }

    private IBacktestConditionNode BindLeaf(JsonElement node, string path, State state)
    {
        RequireObject(node, path);
        RejectUnknownKeys(node, path, LeafKeys);
        state.CountNode(path, _settings);
        state.Leaves++;

        BacktestConditionSide left = BindSide(Require(node, "left", path, JsonValueKind.Object), $"{path}.left");
        ComparisonOperator op = ParseEnum<ComparisonOperator>(Require(node, "comparison", path, JsonValueKind.String), $"{path}.comparison", ComparisonNames);
        JsonElement right = Require(node, "right", path);
        string rightPath = $"{path}.right";

        switch (right.ValueKind)
        {
            case JsonValueKind.Number:
                if (!right.TryGetDecimal(out decimal number))
                {
                    throw new ToolException(ToolErrorCodes.InvalidArgument, "The number is outside the supported range.", rightPath);
                }

                return new BacktestConditionLeaf(new BacktestConditionEntry
                {
                    Left = left,
                    Operator = op,
                    TargetMode = RightHandTargetMode.NumericValue,
                    RightNumericValue = number,
                });

            case JsonValueKind.Object:
                return new BacktestConditionLeaf(new BacktestConditionEntry
                {
                    Left = left,
                    Operator = op,
                    TargetMode = RightHandTargetMode.Indicator,
                    Right = BindSide(right, rightPath),
                });

            default:
                throw new ToolException(
                    ToolErrorCodes.InvalidArgument,
                    "The right side of a comparison is a number or a side object.",
                    rightPath,
                    "Example: \"right\": 70, or \"right\": {\"indicator\": \"SMA\", \"parameters\": {\"Period\": 50}}.");
        }
    }

    private BacktestConditionSide BindSide(JsonElement side, string path)
    {
        RequireObject(side, path);
        RejectUnknownKeys(side, path, SideKeys);

        string[] kinds = SideKinds.Where(kind => side.TryGetProperty(kind, out _)).ToArray();
        if (kinds.Length != 1)
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                "A side names exactly one of indicator or price.",
                path,
                "Example: {\"indicator\": \"RSI\", \"parameters\": {\"Period\": 14}} or {\"price\": \"Close\"}.");
        }

        int offset = 0;
        if (side.TryGetProperty("offset", out JsonElement offsetElement))
        {
            if (offsetElement.ValueKind != JsonValueKind.Number || !offsetElement.TryGetInt32(out offset) || !BacktestConditionOffsetRule.IsValid(offset))
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, "offset is the number of bars back from the evaluated bar and must be an integer of 0 or more.", $"{path}.offset");
            }

            if (!BacktestConditionOffsetRule.IsWithinLimit(offset, _settings.BacktestMaxConditionOffset))
            {
                throw TreeLimit($"The offset {offset} is above the maximum {_settings.BacktestMaxConditionOffset}.", $"{path}.offset", "Backtest:MaxConditionOffset", _settings.BacktestMaxConditionOffset);
            }
        }

        string kind = kinds[0];
        string name = Require(side, kind, path, JsonValueKind.String).GetString()!;
        if (kind == "price")
        {
            if (side.TryGetProperty("parameters", out _) || side.TryGetProperty("outputSeriesName", out _))
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, "parameters and outputSeriesName belong to an indicator side only.", path);
            }

            PriceType price = ParseEnum<PriceType>(Require(side, "price", path, JsonValueKind.String), $"{path}.price", Enum.GetNames<PriceType>());
            return new BacktestConditionSide
            {
                IndicatorType = IndicatorType.Price,
                PriceSource = price,
                OutputName = IndicatorResult.MainSeriesName,
                Offset = offset,
            };
        }

        ResolvedIndicatorSide resolved = _indicatorSides.Resolve(side, name, "a backtest", path);
        return new BacktestConditionSide
        {
            IndicatorType = resolved.Type,
            Parameters = resolved.Settings.ParameterObject?.Clone(),
            OutputName = resolved.Series,
            Offset = offset,
        };
    }

    /// <summary>The application's own validation, with the configured limits: the same call the Backtest window makes before a run.</summary>
    private static ToolException TreeLimit(string message, string path, string setting, int limit)
        => new(ToolErrorCodes.ConditionLimitExceeded, message, path, $"The limit is the application setting {setting} = {limit}.");

    private sealed class State
    {
        public int Nodes { get; private set; }

        public int Depth { get; set; }

        public int Leaves { get; set; }

        public void CountNode(string path, IStockAnalyzerSettings settings)
        {
            Nodes++;
            if (Nodes > settings.BacktestMaxConditionTreeNodes)
            {
                throw TreeLimit(
                    $"A condition root has more than {settings.BacktestMaxConditionTreeNodes} nodes (groups and comparisons).",
                    path,
                    "Backtest:MaxConditionTreeNodes",
                    settings.BacktestMaxConditionTreeNodes);
            }
        }
    }
}

/// <summary>The one way a backtest condition is written as text for a client (the echo of the roots).</summary>
internal static class BacktestConditionText
{
    public static string Describe(BacktestConditionGroup group)
    {
        IEnumerable<string> parts = group.Children.Select(child => child switch
        {
            BacktestConditionGroup nested => $"({Describe(nested)})",
            BacktestConditionLeaf leaf => Describe(leaf.Comparison),
            _ => string.Empty,
        });
        return string.Join($" {group.Operator} ", parts);
    }

    public static string Describe(BacktestConditionEntry entry)
    {
        string right = entry.TargetMode == RightHandTargetMode.NumericValue
            ? entry.RightNumericValue.ToString(CultureInfo.InvariantCulture)
            : DescribeSide(entry.Right!);
        return $"{DescribeSide(entry.Left)} {entry.Operator.ToSymbolString()} {right}";
    }

    public static string DescribeSide(BacktestConditionSide side)
    {
        string title;
        if (side.IndicatorType == IndicatorType.Price && side.PriceSource is { } price)
        {
            title = price.ToString();
        }
        else
        {
            // Only the parameters the catalogue lists (the object also carries state such as HasErrors).
            IReadOnlyDictionary<string, object> values = IndicatorSideResolver.ParameterValues(side.Parameters);
            string parameters = side.Parameters is null
                ? string.Empty
                : string.Join(", ", IndicatorParameterReader.Read(side.Parameters)
                    .Where(descriptor => values.ContainsKey(descriptor.Name))
                    .Select(descriptor => $"{descriptor.Name}={Convert.ToString(values[descriptor.Name], CultureInfo.InvariantCulture)}"));
            string outputName = BacktestConditionValidator.NormalizeOutputName(side);
            string series = string.Equals(outputName, IndicatorResult.MainSeriesName, StringComparison.Ordinal) ? string.Empty : $".{outputName}";
            title = $"{side.IndicatorType}({parameters}){series}";
        }

        return side.Offset > 0 ? $"{title} [{side.Offset} bars back]" : title;
    }
}
