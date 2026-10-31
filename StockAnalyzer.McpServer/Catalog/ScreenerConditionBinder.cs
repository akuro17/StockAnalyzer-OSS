using System.Collections.Immutable;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Screener;
using StockAnalyzer.McpServer.Tools;
using static StockAnalyzer.McpServer.Catalog.ConditionJson;

namespace StockAnalyzer.McpServer.Catalog;

/// <summary>One comparison of the bound tree, in tree order; <see cref="Id"/> (L0, L1, ...) names it in the result.</summary>
internal sealed record ScreenerLeaf(string Id, string Path, ScreenerIndicatorEntry Entry);

/// <summary>A comparison symbol a condition names (canonical form) and the path of the parameter that names it, for error parameters.</summary>
internal sealed record ScreenerComparisonSymbol(string Symbol, string Path);

/// <summary>
/// The tree as the Screener evaluates it, with its leaves, the comparison symbols it names and the measures the size limits use.
/// <see cref="ExplicitFrameSides"/> are the sides whose <c>timeFrame</c> the client wrote (compared by reference); while none is, every side still holds the placeholder D1 (the scan frame).
/// </summary>
internal sealed record BoundScreenerCondition(
    ScreenerConditionTreeCondition Condition,
    IReadOnlyList<ScreenerLeaf> Leaves,
    int NodeCount,
    int Depth,
    IReadOnlyList<ScreenerComparisonSymbol> ComparisonSymbols,
    IReadOnlyCollection<ScreenerIndicatorSideConfig> ExplicitFrameSides);

/// <summary>
/// The bound <c>sortBy</c> side: the side, its one-comparison leaf (for the as-of column check), a condition that sizes its history and the comparison symbols it names;
/// <see cref="NamesFrame"/> says whether the client wrote its <c>timeFrame</c>.
/// </summary>
internal sealed record BoundSortSide(
    ScreenerIndicatorSideConfig Side,
    ScreenerLeaf Leaf,
    IScreeningCondition Sizing,
    IReadOnlyList<ScreenerComparisonSymbol> ComparisonSymbols,
    bool NamesFrame);

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
    /// <summary>The optional key of a side that names the time frame whose bars the side reads (D1, W1 or MN1).</summary>
    public const string SideTimeFrameKey = "timeFrame";

    private static readonly string[] SideKeys = { "indicator", "price", "column", "criteria", "parameters", "outputSeriesName", "offset", SideTimeFrameKey };
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

    /// <summary>The columns that stay usable with an as-of date (read from the bars), in catalogue order.</summary>
    public IReadOnlyList<string> AsOfColumnNames => new ScreenerAsOfColumnGuard(_screenerCatalog).AllowedColumns;

    /// <summary>
    /// Owner decision D-02a: with an as-of date no comparison side may read a snapshot column (see <see cref="ScreenerAsOfColumnGuard"/>). The argument path of a refused
    /// side is <c>left.column</c>/<c>right.column</c> under the comparison for a condition tree, <c>leftHand</c>/<c>rightHand</c> under the entry for a saved template.
    /// </summary>
    public void RequireAsOfCompatible(IReadOnlyList<ScreenerLeaf> leaves, bool isTemplate, string asOfArgument)
        => new ScreenerAsOfColumnGuard(_screenerCatalog).Require(
            leaves,
            leaf => isTemplate ? ($"{leaf.Path}.leftHand", $"{leaf.Path}.rightHand") : ($"{leaf.Path}.left.column", $"{leaf.Path}.right.column"),
            asOfArgument);

    /// <summary>
    /// Binds the <c>sortBy</c> side (an indicator, price or numeric column; owner decision D-02e). A criteria side is true or false and a text column has no order, so
    /// both are refused. The side is wrapped in a one-comparison condition only so that the history it needs can be sized the way the Screener sizes a condition.
    /// </summary>
    public BoundSortSide BindSortSide(JsonElement side, string path)
    {
        if (side.ValueKind == JsonValueKind.Object && side.TryGetProperty("criteria", out _))
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                "A criteria side is 1 or 0 (pattern present or not) and cannot rank symbols.",
                $"{path}.criteria",
                "Sort by an indicator, a price or a numeric column.");
        }

        var state = new State();
        (ScreenerIndicatorSideConfig config, bool isText) = BindSide(side, path, state);
        if (isText)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, "A text column has no order to rank symbols by.", $"{path}.column", "Sort by an indicator, a price or a numeric column.");
        }

        var entry = new ScreenerIndicatorEntry
        {
            LeftHand = config,
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 0m,
            IsEnabled = true,
        };
        var sizing = new ScreenerConditionTreeCondition(new ScreenerConditionGroup(
            LogicalOperator.And, ImmutableArray.Create<IScreenerConditionNode>(new ScreenerConditionLeaf(entry))));
        return new BoundSortSide(config, new ScreenerLeaf("S0", path, entry), sizing, state.ComparisonSymbols, state.ExplicitFrameSides.Contains(config));
    }

    /// <summary>
    /// The scan in which each side reads its own time frame: every bar-reading side whose <c>timeFrame</c> the client did not write gets <paramref name="toolFrame"/> (the tool-level
    /// <c>timeFrame</c>, default D1) as its literal frame. That scan evaluates no indicator that compares two symbols (the comparison symbol's bars are daily), so one is refused
    /// wherever it stands; AnchoredVWAP (its anchor date has no definition against a weekly or monthly bar stamp) is refused on a frame other than D1.
    /// </summary>
    public void ApplyLiteralFrames(BoundScreenerCondition bound, TimeFrame toolFrame)
    {
        foreach (ScreenerLeaf leaf in bound.Leaves)
        {
            ApplyLiteralFrame(leaf.Entry.LeftHand, $"{leaf.Path}.left", bound.ExplicitFrameSides, toolFrame);
            if (leaf.Entry.TargetMode == RightHandTargetMode.Indicator)
            {
                ApplyLiteralFrame(leaf.Entry.RightHand, $"{leaf.Path}.right", bound.ExplicitFrameSides, toolFrame);
            }
        }
    }

    /// <summary>The <c>sortBy</c> side of <see cref="ApplyLiteralFrames"/>.</summary>
    public void ApplyLiteralFrame(BoundSortSide sort, TimeFrame toolFrame, string sortArgument)
        => ApplyLiteralFrame(sort.Side, sortArgument, sort.NamesFrame ? new[] { sort.Side } : Array.Empty<ScreenerIndicatorSideConfig>(), toolFrame);

    private static void ApplyLiteralFrame(ScreenerIndicatorSideConfig side, string path, IReadOnlyCollection<ScreenerIndicatorSideConfig> explicitSides, TimeFrame toolFrame)
    {
        if (!explicitSides.Contains(side) && ScreenerSideFrames.ReadsBars(side))
        {
            side.TimeFrame = toolFrame;
        }

        if (ScreenerComparisonSymbols.Of(side) is not null)
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                "An indicator that compares two symbols cannot be used when a side names its own timeFrame: that scan reads no comparison symbol.",
                explicitSides.Contains(side) ? $"{path}.{SideTimeFrameKey}" : $"{path}.indicator",
                "Remove the timeFrame of every side (the tool-level timeFrame then applies to the whole scan), or use an indicator of one symbol.");
        }

        if (side.IndicatorType == IndicatorType.AnchoredVWAP && side.CategoryType == ScreenerItemCategoryType.Indicator && side.TimeFrame != TimeFrame.D1)
        {
            throw NoDefinitionOnFrame(side.TimeFrame, $"{path}.indicator");
        }
    }

    /// <summary>Owner decision D-02a for the <c>sortBy</c> side: with an as-of date it must not read a snapshot column (the refused path is <c>sortBy.column</c>).</summary>
    public void RequireAsOfCompatible(BoundSortSide sortSide, string asOfArgument)
        => new ScreenerAsOfColumnGuard(_screenerCatalog).Require(new[] { sortSide.Leaf }, leaf => ($"{leaf.Path}.column", $"{leaf.Path}.column"), asOfArgument);

    public BoundScreenerCondition Bind(JsonElement tree, string path)
    {
        var state = new State();
        ScreenerConditionGroup root;
        if (IsGroup(tree))
        {
            root = BindGroup(tree, path, 1, state);
        }
        else if (ConditionOperatorExpander.TryGetName(tree, out string expanded))
        {
            // A comparison that expands to several is itself the root And group, as the same comparisons written out would be.
            state.Depth = 1;
            IScreenerConditionNode bound = BindExpanded(tree, expanded, path, state, 1);
            if (bound is ScreenerConditionGroup group)
            {
                root = group;
            }
            else
            {
                state.CountNode(path, _settings);
                root = new ScreenerConditionGroup(LogicalOperator.And, ImmutableArray.Create(bound));
            }
        }
        else
        {
            // A single comparison is a tree of one leaf under an And root, as the Screener tab holds it.
            state.CountNode(path, _settings);
            state.Depth = 1;
            root = new ScreenerConditionGroup(LogicalOperator.And, ImmutableArray.Create<IScreenerConditionNode>(BindLeaf(tree, path, state, state.Depth + 1)));
        }

        return new BoundScreenerCondition(new ScreenerConditionTreeCondition(root), state.Leaves, state.Nodes, state.Depth, state.ComparisonSymbols, state.ExplicitFrameSides);
    }

    private static bool IsGroup(JsonElement node)
        => node.ValueKind == JsonValueKind.Object && (node.TryGetProperty("children", out _) || node.TryGetProperty("operator", out _));

    private ScreenerConditionGroup BindGroup(JsonElement node, string path, int depth, State state)
    {
        RequireObject(node, path);
        RejectUnknownKeys(node, path, GroupKeys);
        RequireDepth(depth, path);
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
            bound.Add(IsGroup(child) ? BindGroup(child, childPath, depth + 1, state) : BindLeaf(child, childPath, state, depth + 1));
            index++;
        }

        return new ScreenerConditionGroup(op, bound.ToImmutable());
    }

    /// <summary>A group created at nesting level <paramref name="depth"/> must stay within the configured depth.</summary>
    private void RequireDepth(int depth, string path)
    {
        if (depth > _settings.ScreenerMaxFilterTreeDepth)
        {
            throw TreeLimit($"The condition tree is nested deeper than {_settings.ScreenerMaxFilterTreeDepth} levels.", path, ScreenerSettingsValidator.MaxFilterTreeDepthKey, _settings.ScreenerMaxFilterTreeDepth);
        }
    }

    /// <param name="depth">The nesting level a group created at this position would have (the level of the parent group plus one).</param>
    private IScreenerConditionNode BindLeaf(JsonElement node, string path, State state, int depth)
    {
        RequireObject(node, path);
        if (ConditionOperatorExpander.TryGetName(node, out string expanded))
        {
            return BindExpanded(node, expanded, path, state, depth);
        }

        RejectUnknownKeys(node, path, LeafKeys);
        state.CountNode(path, _settings);
        return BindComparison(node, path, state);
    }

    /// <summary>
    /// A comparison written with one of <see cref="ConditionOperatorExpander.ExpandedComparisonNames"/>: it becomes the comparisons of <see cref="ConditionOperatorExpander"/>, each bound by the
    /// ordinary rules. The expanded groups and comparisons count against the same tree limits, and every error names the path of the node the client wrote.
    /// </summary>
    private IScreenerConditionNode BindExpanded(JsonElement node, string name, string path, State state, int depth)
    {
        RejectUnknownKeys(node, path, ConditionOperatorExpander.LeafKeys);
        IReadOnlyList<JsonElement> comparisons = ConditionOperatorExpander.Expand(node, name, path, _settings.ScreenerMaxConditionOffset, ScreenerSettingsValidator.MaxConditionOffsetKey);
        if (comparisons.Count == 1)
        {
            state.CountNode(path, _settings);
            return BindComparison(comparisons[0], path, state);
        }

        RequireDepth(depth, path);
        state.Depth = Math.Max(state.Depth, depth);
        state.CountNode(path, _settings);
        var bound = ImmutableArray.CreateBuilder<IScreenerConditionNode>();
        foreach (JsonElement comparison in comparisons)
        {
            state.CountNode(path, _settings);
            bound.Add(BindComparison(comparison, path, state));
        }

        return new ScreenerConditionGroup(LogicalOperator.And, bound.ToImmutable());
    }

    private IScreenerConditionNode BindComparison(JsonElement node, string path, State state)
    {
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

    /// <summary>
    /// One side of a comparison; the flag says whether it is a text column. The side holds the placeholder frame D1 unless the client wrote a <c>timeFrame</c> (recorded in
    /// <see cref="State.ExplicitFrameSides"/>); what a side without one reads is decided by the tool (<see cref="ApplyLiteralFrames"/>).
    /// </summary>
    private (ScreenerIndicatorSideConfig Side, bool IsText) BindSide(JsonElement side, string path, State state)
    {
        (ScreenerIndicatorSideConfig config, bool isText) = BindSideKind(side, path, state);
        if (side.TryGetProperty(SideTimeFrameKey, out JsonElement frameElement))
        {
            config.TimeFrame = ParseSideFrame(frameElement, config, $"{path}.{SideTimeFrameKey}");
            state.ExplicitFrameSides.Add(config);
        }

        return (config, isText);
    }

    /// <summary>
    /// The frame a side names. Refused (INVALID_ARGUMENT naming <paramref name="path"/>): a name that is not a time frame or that the data layer cannot load, any frame on a column
    /// read from the stored metadata (a snapshot, it has no bars), and a frame other than D1 on an indicator that compares two symbols or on AnchoredVWAP.
    /// </summary>
    private static TimeFrame ParseSideFrame(JsonElement element, ScreenerIndicatorSideConfig config, string path)
    {
        string? name = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        if (name is null || !Enum.GetNames<TimeFrame>().Contains(name, StringComparer.Ordinal))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, "timeFrame is a time frame name.", path, $"Supported: {ToolArgs.SupportedTimeFrameNames}.");
        }

        TimeFrame frame = Enum.Parse<TimeFrame>(name);

        if (!ReadOnlyGatewayContract.SupportedTimeFrames.Contains(frame))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Time frame {frame} is not supported.", path, $"Supported: {ToolArgs.SupportedTimeFrameNames}.");
        }

        if (!ScreenerSideFrames.ReadsBars(config))
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                "A column read from the stored metadata (a snapshot) has no bars and so no time frame.",
                path,
                "Remove timeFrame from this side. Open, High, Low, Close, Volume, Change and Change % are the columns read from the bars.");
        }

        if (frame != TimeFrame.D1 && ScreenerComparisonSymbols.Of(config) is not null)
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                "An indicator that compares two symbols reads daily bars only.",
                path,
                "Use timeFrame D1 (or none) on this side.");
        }

        if (frame != TimeFrame.D1 && config.CategoryType == ScreenerItemCategoryType.Indicator && config.IndicatorType == IndicatorType.AnchoredVWAP)
        {
            throw NoDefinitionOnFrame(frame, path);
        }

        return frame;
    }

    private static ToolException NoDefinitionOnFrame(TimeFrame frame, string path)
        => new(ToolErrorCodes.InvalidArgument, $"AnchoredVWAP has no definition on {frame} bars (its anchor date is a day).", path, "Use timeFrame D1 on this side.");

    private (ScreenerIndicatorSideConfig Side, bool IsText) BindSideKind(JsonElement side, string path, State state)
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
                throw TreeLimit($"The offset {offset} is above the maximum {_settings.ScreenerMaxConditionOffset}.", $"{path}.offset", ScreenerSettingsValidator.MaxConditionOffsetKey,_settings.ScreenerMaxConditionOffset);
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

        /// <summary>The bound sides whose <c>timeFrame</c> the client wrote, by reference.</summary>
        public HashSet<ScreenerIndicatorSideConfig> ExplicitFrameSides { get; } = new(ReferenceEqualityComparer.Instance);

        public int Nodes { get; private set; }

        public int Depth { get; set; }

        public void CountNode(string path, IStockAnalyzerSettings settings)
        {
            Nodes++;
            if (Nodes > settings.ScreenerMaxFilterTreeNodes)
            {
                throw TreeLimit($"The condition tree has more than {settings.ScreenerMaxFilterTreeNodes} nodes (groups and comparisons).", path, ScreenerSettingsValidator.MaxFilterTreeNodesKey,settings.ScreenerMaxFilterTreeNodes);
            }
        }
    }
}
