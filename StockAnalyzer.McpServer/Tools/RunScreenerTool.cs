using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Screener;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// sa_run_screener: evaluates a condition tree on the stored daily bars of many symbols with the application's own Screener service
/// and returns the symbols that meet it. The tool only maps the request to the Screener's condition objects and the result to JSON.
/// </summary>
internal sealed class RunScreenerTool : IMcpTool
{
    private const string ConditionArgument = "conditionTree";
    private const string TemplateArgument = "templateId";
    private const string SymbolsArgument = "symbols";
    private const string GroupIdArgument = "groupId";
    private const string TimeFrameArgument = "timeFrame";
    private const string ValuesArgument = "includeMatchedValues";
    private const string MaxMatchesArgument = "maxReturnedMatches";
    private const string AsOfDateArgument = "asOfDate";
    private const string EndDateArgument = "endDate";
    private const string SortByArgument = "sortBy";
    private const string SortDirectionArgument = "sortDirection";
    private const string SortAscending = "Ascending";
    private const string SortDescending = "Descending";

    /// <summary>The time frame of a scan that names none, and the one the Screener tab and the other tools that screen (sa_filter_tickers) use.</summary>
    internal const TimeFrame ScreenerTimeFrame = TimeFrame.D1;

    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly ITickerGroupResolver _groupResolver;
    private readonly ScreenerConditionBinder _binder;
    private readonly IScreenerValueExtractor _extractor;
    private readonly ScreenerTemplateAdvisor _advisor;
    private readonly IStockAnalyzerSettings _settings;
    private readonly McpLimits _limits;
    private readonly McpResultHandleStore _resultHandles;
    private readonly SymbolResolver _symbols;
    private readonly ScreenerComparisonSymbolResolver _comparisonSymbols;

    public RunScreenerTool(
        IReadOnlyStockAnalyzerGateway gateway,
        ScreenerConditionBinder binder,
        IScreenerValueExtractor extractor,
        ScreenerTemplateAdvisor advisor,
        IStockAnalyzerSettings settings,
        McpLimits limits,
        McpResultHandleStore? resultHandles = null)
        : this(gateway, new TickerGroupResolver(gateway, new FileReadOnlyWorkspaceSource(PathDiscovery.ResolveConfigPathWithoutCreating(LayoutConstants.DefaultWorkspaceFileName))), binder, extractor, advisor, settings, limits, resultHandles)
    {
    }

    public RunScreenerTool(
        IReadOnlyStockAnalyzerGateway gateway,
        ITickerGroupResolver groupResolver,
        ScreenerConditionBinder binder,
        IScreenerValueExtractor extractor,
        ScreenerTemplateAdvisor advisor,
        IStockAnalyzerSettings settings,
        McpLimits limits,
        McpResultHandleStore? resultHandles = null)
    {
        _gateway = gateway;
        _groupResolver = groupResolver ?? throw new ArgumentNullException(nameof(groupResolver));
        _binder = binder;
        _extractor = extractor;
        _advisor = advisor;
        _settings = settings;
        _limits = limits;
        _resultHandles = resultHandles ?? new McpResultHandleStore();
        _symbols = new SymbolResolver(gateway);
        _comparisonSymbols = new ScreenerComparisonSymbolResolver(gateway, _symbols, limits);
    }

    public string Name => "sa_run_screener";

    public string Description =>
        "Finds symbols whose bars meet a Screener condition tree or saved templateId (via sa_list_templates; calls sa_get_capabilities for columns, criteria, price sources and indicator parameters). " +
        $"Supports {string.Join(", ", ConditionOperatorExpander.ExpandedComparisonNames)}, per-side timeFrame, sortBy ranking, asOfDate, and {McpResultHandleStore.StoreResultArgument} ({McpResultHandleStore.ResultIdKey}). " +
        "Read-only; never mutates Data or fetches remote data. " +
        ToolJson.DocLink(Name);

    public bool IsHeavy => true;

    public IReadOnlyDictionary<string, string>? MisnamedArgumentHints { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [EndDateArgument] = $"This tool names the evaluation date '{AsOfDateArgument}' (sa_calculate_indicators uses '{EndDateArgument}').",
    };

    public string? ExampleArgumentsJson => "{\"timeFrame\":\"D1\",\"conditionTree\":{\"left\":{\"price\":\"Close\"},\"comparison\":\"GreaterThan\",\"right\":{\"indicator\":\"SMA\",\"parameters\":{\"Period\":10},\"timeFrame\":\"W1\"}},\"maxReturnedMatches\":50}";

    public string InputSchemaJson => ToolSchema.Object()
        .FreeObject(ConditionArgument, "The condition tree (see the tool description). Give this or templateId.")
        .String(TemplateArgument, "Id of a saved Screener template (from sa_list_templates). Give this or conditionTree.")
        .StringArray(SymbolsArgument, $"Symbols to screen (at most {_limits.MaxScreenerSymbols}); omit to screen every available symbol. Mutually exclusive with groupId.", minItems: 1)
        .String(GroupIdArgument, "Guid of a saved ticker group (from sa_list_ticker_groups) to screen. Mutually exclusive with symbols.")
        .String(TimeFrameArgument, $"Time frame of the bars of every side that names no timeFrame of its own. Supported: {ToolArgs.SupportedTimeFrameNames}. Default {ScreenerTimeFrame}.", allowedValues: Enum.GetNames<TimeFrame>())
        .Boolean(PartialBarOutput.CompleteBarsOnlyArgument, "W1 and MN1 only (ignored for D1): true (default) evaluates the last complete period when the latest one has not ended; false evaluates the partial period and says so (partialLastBar).")
        .FreeObject(SortByArgument, "Rank the matched symbols by one side: {indicator|price|column: name, parameters?, outputSeriesName?, offset?} (the same side object as in conditionTree; not criteria or a text column).")
        .String(SortDirectionArgument, $"{SortDescending} (default: larger value first) or {SortAscending}. Needs sortBy.", allowedValues: new[] { SortDescending, SortAscending })
        .String(AsOfDateArgument, $"Evaluation date ({ToolJson.DateFormat}, inclusive): each symbol is evaluated on its last bar on or before it. Default: the latest stored bar. Columns other than those read from the bars are refused with it.")
        .Boolean(ValuesArgument, "Add the values each comparison read, per returned symbol. Default false.")
        .Boolean(SymbolResolver.AllowUnregisteredArgument, "Accept symbols that are not in the ticker list but have stored daily bars. Default false.")
        .Integer(MaxMatchesArgument, $"Most matched symbols to return. Default and maximum {_limits.MaxReturnedMatches}.", defaultValue: _limits.MaxReturnedMatches, minimum: 1, maximum: _limits.MaxReturnedMatches)
        .Boolean(McpResultHandleStore.StoreResultArgument, $"Store returned matchedSymbols in memory and return {McpResultHandleStore.ResultIdKey} for downstream tools. Default false.")
        .Build();

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        TimeFrame timeFrame = arguments.TryGetProperty(TimeFrameArgument, out _) ? ToolArgs.SupportedTimeFrame(arguments, TimeFrameArgument) : ScreenerTimeFrame;
        DateOnly? asOf = ToolArgs.Date(arguments, AsOfDateArgument);
        bool isPeriodFrame = TimeFrameAggregator.IsPeriodFrame(timeFrame);
        bool completeBarsOnly = ToolArgs.Boolean(arguments, PartialBarOutput.CompleteBarsOnlyArgument) ?? ScreenerBarSelection.DefaultCompleteBarsOnly;

        // The plain latest-daily-bar scan keeps its own path (unchanged); a date or a weekly / monthly frame reads a selection of bars built from the daily history.
        var source = new BarSource(timeFrame, asOf is not null || timeFrame != ScreenerTimeFrame ? new ScreenerBarSelection(asOf, completeBarsOnly) : null);

        // A side may name its own timeFrame. While none does, the whole scan is on the tool-level frame (the path above, unchanged). As soon as one does (a side of the
        // condition, of the sortBy, or of the saved template), every side reads its own literal frame and the scan runs on a bar set per symbol.
        var sideSelection = new ScreenerBarSelection(asOf, completeBarsOnly);
        bool sortNamesFrame = arguments.TryGetProperty(SortByArgument, out JsonElement sortProbe)
            && sortProbe.ValueKind == JsonValueKind.Object
            && sortProbe.TryGetProperty(ScreenerConditionBinder.SideTimeFrameKey, out _);
        bool sideFrames;
        Func<IReadOnlyList<string>, IScreenerEvaluationContext?, Task<ScreenerSideFramesResult>>? runSideFrames = null;
        bool hasTree = arguments.TryGetProperty(ConditionArgument, out JsonElement tree);
        string? templateText = ToolArgs.String(arguments, TemplateArgument);
        if (hasTree == (templateText is not null))
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                "Give exactly one of conditionTree or templateId.",
                hasTree ? TemplateArgument : ConditionArgument,
                "Use sa_list_templates to find a template id, or write a conditionTree.");
        }

        bool hasSymbols = arguments.TryGetProperty(SymbolsArgument, out _);
        string? groupIdText = ToolArgs.String(arguments, GroupIdArgument);
        bool hasGroupId = groupIdText is not null;
        if (hasSymbols && hasGroupId)
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                "Give at most one of symbols or groupId.",
                GroupIdArgument);
        }

        int maxMatches = ToolArgs.PositiveWithin(arguments, MaxMatchesArgument, _limits.MaxReturnedMatches, _limits.MaxReturnedMatches);
        bool includeValues = ToolArgs.Boolean(arguments, ValuesArgument) ?? false;
        bool allowUnregistered = ToolArgs.Boolean(arguments, SymbolResolver.AllowUnregisteredArgument) ?? false;
        bool storeResult = ToolArgs.Boolean(arguments, McpResultHandleStore.StoreResultArgument) ?? false;

        // Both modes end in the same pieces: the leaves (for matched values), a condition that sizes the history, and a way to run it.
        IReadOnlyList<ScreenerLeaf> leaves;
        IScreeningCondition sizing;
        Func<IReadOnlyList<string>, IScreenerEvaluationContext?, Task<ScreenerBarsResult>>? run = null;
        ScreenerIndicatorTemplate? template = null;
        var warnings = new List<string>();
        IReadOnlyList<string> comparisonSymbols;
        int nodeCount = 0, depth = 0;
        if (hasTree)
        {
            BoundScreenerCondition bound = _binder.Bind(tree, ConditionArgument);
            leaves = bound.Leaves;
            sizing = bound.Condition;
            nodeCount = bound.NodeCount;
            depth = bound.Depth;
            if (asOf is not null)
            {
                _binder.RequireAsOfCompatible(bound.Leaves, isTemplate: false, AsOfDateArgument);
            }

            sideFrames = bound.ExplicitFrameSides.Count > 0 || sortNamesFrame;
            if (sideFrames)
            {
                _binder.ApplyLiteralFrames(bound, timeFrame);
                comparisonSymbols = Array.Empty<string>();
                runSideFrames = (universe, evalContext) => evalContext is not null
                    ? _gateway.RunScreenerWithSideFramesAsync(universe, bound.Condition, sideSelection, evalContext, cancellationToken)
                    : _gateway.RunScreenerWithSideFramesAsync(universe, bound.Condition, sideSelection, cancellationToken);
            }
            else
            {
                comparisonSymbols = await _comparisonSymbols.ResolveAsync(bound.ComparisonSymbols, ConditionArgument, allowUnregistered, warnings, cancellationToken).ConfigureAwait(false);
                IReadOnlyList<string> symbolsToCompare = comparisonSymbols;
                run = source.Selection is { } treeSelection
                    ? (universe, evalContext) => evalContext is not null
                        ? _gateway.RunScreenerOnBarsAsync(universe, bound.Condition, timeFrame, symbolsToCompare, treeSelection, evalContext, cancellationToken)
                        : _gateway.RunScreenerOnBarsAsync(universe, bound.Condition, timeFrame, symbolsToCompare, treeSelection, cancellationToken)
                    : async (universe, evalContext) => PlainResult(evalContext is not null
                        ? await _gateway.RunScreenerAsync(universe, bound.Condition, timeFrame, symbolsToCompare, evalContext, cancellationToken).ConfigureAwait(false)
                        : await _gateway.RunScreenerAsync(universe, bound.Condition, timeFrame, symbolsToCompare, cancellationToken).ConfigureAwait(false));
            }
        }
        else
        {
            template = await LoadTemplateAsync(templateText!, cancellationToken).ConfigureAwait(false);
            warnings.AddRange(_advisor.Warnings(template));
            leaves = template.Entries
                .Select((entry, index) => (entry, index))
                .Where(item => item.entry.IsEnabled)
                .Select((item, position) => new ScreenerLeaf($"L{position}", $"entries[{item.index}]", item.entry))
                .ToList();
            sizing = new ScreenerConditionTreeCondition(new ScreenerConditionGroup(
                LogicalOperator.And, leaves.Select(leaf => (IScreenerConditionNode)new ScreenerConditionLeaf(leaf.Entry)).ToImmutableArray()));
            ScreenerIndicatorTemplate loaded = template;
            if (asOf is not null)
            {
                _binder.RequireAsOfCompatible(leaves, isTemplate: true, AsOfDateArgument);
            }

            // A template stores a frame on every side, so one that reads a weekly or monthly bar is run on those frames (the tool-level frame has nothing left to say).
            sideFrames = ScreenerSideFrames.UsesNonDailyBars(template.Entries) || sortNamesFrame;
            if (sideFrames)
            {
                if (timeFrame != ScreenerTimeFrame)
                {
                    throw new ToolException(
                        ToolErrorCodes.InvalidArgument,
                        "This scan reads the time frames stored in the template (or named by sortBy), so a tool-level timeFrame other than D1 has no side to apply to.",
                        TimeFrameArgument,
                        "Omit timeFrame, and give the frame of the ranking as sortBy.timeFrame.");
                }

                comparisonSymbols = Array.Empty<string>();
                runSideFrames = (universe, evalContext) => evalContext is not null
                    ? _gateway.RunScreenerTemplateWithSideFramesAsync(universe, loaded, sideSelection, evalContext, cancellationToken)
                    : _gateway.RunScreenerTemplateWithSideFramesAsync(universe, loaded, sideSelection, cancellationToken);
            }
            else
            {
                comparisonSymbols = await _comparisonSymbols.ResolveAsync(TemplateComparisonSymbols(leaves), TemplateArgument, allowUnregistered, warnings, cancellationToken).ConfigureAwait(false);
                IReadOnlyList<string> symbolsToCompare = comparisonSymbols;
                run = source.Selection is { } templateSelection
                    ? (universe, evalContext) => evalContext is not null
                        ? _gateway.RunScreenerTemplateOnBarsAsync(universe, loaded, timeFrame, symbolsToCompare, templateSelection, evalContext, cancellationToken)
                        : _gateway.RunScreenerTemplateOnBarsAsync(universe, loaded, timeFrame, symbolsToCompare, templateSelection, cancellationToken)
                    : async (universe, evalContext) => PlainResult(evalContext is not null
                        ? await _gateway.RunScreenerTemplateAsync(universe, loaded, timeFrame, symbolsToCompare, evalContext, cancellationToken).ConfigureAwait(false)
                        : await _gateway.RunScreenerTemplateAsync(universe, loaded, timeFrame, symbolsToCompare, cancellationToken).ConfigureAwait(false));
            }
        }

        // sortBy and sortDirection are checked, like the condition, before anything is scanned.
        BoundSortSide? sortSide = null;
        IReadOnlyList<string> sortComparisonSymbols = Array.Empty<string>();
        bool descending = true;
        if (arguments.TryGetProperty(SortByArgument, out JsonElement sortBy))
        {
            sortSide = _binder.BindSortSide(sortBy, SortByArgument);
            if (sideFrames)
            {
                _binder.ApplyLiteralFrame(sortSide, timeFrame, SortByArgument);
            }

            if (asOf is not null)
            {
                _binder.RequireAsOfCompatible(sortSide, AsOfDateArgument);
            }

            sortComparisonSymbols = await _comparisonSymbols.ResolveAsync(sortSide.ComparisonSymbols, SortByArgument, allowUnregistered, warnings, cancellationToken).ConfigureAwait(false);
            descending = !string.Equals(ToolArgs.String(arguments, SortDirectionArgument), SortAscending, StringComparison.Ordinal);
        }
        else if (arguments.TryGetProperty(SortDirectionArgument, out _))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, "sortDirection needs sortBy.", SortDirectionArgument, "Give sortBy, or omit sortDirection.");
        }

        (List<string> universe, bool isCustom, string? resolvedGroupId) = await ResolveUniverseAsync(arguments, allowUnregistered, warnings, cancellationToken).ConfigureAwait(false);
        if (asOf is not null && resolvedGroupId is not null)
        {
            warnings.Add("The members of the group are those of today (a group defined by filter rules is resolved on today's stored data), not those on asOfDate.");
        }

        bool needsRsRating = asOf is null
            && universe.Count > 0
            && (ScreenerRsRatingInspector.UsesRsRating(leaves.Select(static leaf => leaf.Entry))
                || (sortSide is not null && ScreenerRsRatingInspector.IsRsRatingColumn(sortSide.Side)));
        IScreenerEvaluationContext? evaluationContext = asOf is { } asOfDate
            ? ScreenerEvaluationContext.ForAsOfDate(asOfDate)
            : needsRsRating
                ? await _gateway.EvaluateScreenerContextAsync(universe, asOf, cancellationToken).ConfigureAwait(false)
                : null;

        if (universe.Count > 0)
        {
            var breadthKeys = CollectUniverseBreadthKeys(leaves, sortSide, timeFrame, sideFrames, asOf);
            if (breadthKeys.Count > 0)
            {
                evaluationContext = await _gateway.EvaluateUniverseBreadthContextAsync(
                    breadthKeys,
                    evaluationContext,
                    sideFrames ? null : timeFrame,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        ScreenerBarsResult screened;
        ScreenerSideFramesResult? framesResult = null;
        if (runSideFrames is not null)
        {
            framesResult = universe.Count == 0
                ? new ScreenerSideFramesResult(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<ScreenerFrameReport>())
                : await runSideFrames(universe, evaluationContext).ConfigureAwait(false);
            screened = new ScreenerBarsResult(framesResult.Matched, framesResult.SymbolsWithoutDailyBars, Array.Empty<DateOnly>(), 0);
        }
        else
        {
            screened = universe.Count == 0 ? PlainResult(Array.Empty<string>()) : await run!(universe, evaluationContext).ConfigureAwait(false);
        }

        IReadOnlyList<string> matched = screened.Matched;
        int noDataAtAsOf = asOf is not null ? screened.SymbolsWithoutBars.Count : 0;

        List<string> ordered;
        Dictionary<string, decimal?>? sortValues = null;
        if (sortSide is not null)
        {
            sortValues = sideFrames
                ? await SortValuesOnSideFrameAsync(matched, sortSide, sideSelection, evaluationContext, cancellationToken).ConfigureAwait(false)
                : await SortValuesAsync(matched, sortSide, sortComparisonSymbols, source, evaluationContext, cancellationToken).ConfigureAwait(false);
            ordered = Rank(sortValues, descending);
        }
        else
        {
            ordered = matched.OrderBy(symbol => symbol, StringComparer.Ordinal).ToList();
        }

        List<string> returned = ordered.Take(maxMatches).ToList();
        string? storedResultId = storeResult ? _resultHandles.Store(Name, returned).ResultId : null;
        Dictionary<string, TickerMetadata?>? metadata = null;
        ValueBars? candles = null;
        IScreenerSecondaryCandles? secondary = null;
        if (includeValues)
        {
            (candles, metadata, secondary) = sideFrames
                ? await LoadSideFrameValueInputsAsync(returned, sizing, sideSelection, cancellationToken).ConfigureAwait(false)
                : await LoadValueInputsAsync(returned, sizing, comparisonSymbols, source, cancellationToken).ConfigureAwait(false);
        }

        // The old path reports one frame for the whole scan; the side-frame path reports each weekly or monthly frame a side reads.
        bool writeCompleteBarsOnly = sideFrames ? framesResult!.Frames.Count > 0 : isPeriodFrame;

        return ToolJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("timeFrame", timeFrame.ToString());
            if (asOf is { } asOfDate)
            {
                writer.WriteString("asOfDate", asOfDate.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture));
            }

            if (writeCompleteBarsOnly)
            {
                writer.WriteBoolean(PartialBarOutput.CompleteBarsOnlyArgument, completeBarsOnly);
            }

            writer.WriteString("universe", isCustom ? "Custom" : "AllAvailable");
            if (resolvedGroupId is not null)
            {
                writer.WriteString("groupId", resolvedGroupId);
            }
            else
            {
                writer.WriteNull("groupId");
            }
            if (template is not null)
            {
                writer.WriteStartObject("template");
                writer.WriteString("id", template.Id.ToString());
                writer.WriteString("name", template.Name);
                writer.WriteEndObject();
            }

            writer.WriteNumber("evaluatedSymbolCount", universe.Count - noDataAtAsOf);
            if (asOf is not null)
            {
                writer.WriteNumber("noDataAtAsOfCount", noDataAtAsOf);
            }

            if (sideFrames)
            {
                WriteSideFrames(writer, framesResult!.Frames, completeBarsOnly, maxMatches);
            }
            else if (isPeriodFrame)
            {
                // A partial last period (its last calendar day is after the reference date) is either left out (completeBarsOnly) or evaluated and named here.
                writer.WriteBoolean("partialLastBar", screened.PartialLastBarPeriodStarts.Count > 0);
                if (screened.PartialLastBarPeriodStarts.Count > 0)
                {
                    writer.WriteStartArray("partialPeriodStarts");
                    foreach (DateOnly start in screened.PartialLastBarPeriodStarts)
                    {
                        writer.WriteStringValue(start.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture));
                    }

                    writer.WriteEndArray();
                }

                if (completeBarsOnly)
                {
                    writer.WriteNumber("skippedPartialBarCount", screened.SkippedPartialBarCount);
                }
            }

            writer.WriteNumber("matchedSymbolCount", ordered.Count);
            writer.WriteNumber("returnedMatchCount", returned.Count);
            writer.WriteBoolean("truncated", ordered.Count > returned.Count);
            if (storedResultId is not null)
            {
                writer.WriteString(McpResultHandleStore.ResultIdKey, storedResultId);
            }
            if (template is null)
            {
                writer.WriteNumber("treeNodeCount", nodeCount);
                writer.WriteNumber("treeDepth", depth);
            }

            writer.WriteStartArray("matchedSymbols");
            foreach (string symbol in returned)
            {
                writer.WriteStringValue(symbol);
            }

            writer.WriteEndArray();
            if (sortValues is not null)
            {
                writer.WriteStartArray("ranking");
                for (int index = 0; index < returned.Count; index++)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("rank", index + 1);
                    writer.WriteString("symbol", returned[index]);
                    if (sortValues[returned[index]] is { } sortValue)
                    {
                        writer.WriteNumber("sortValue", sortValue);
                    }
                    else
                    {
                        writer.WriteNull("sortValue");
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            if (warnings.Count > 0)
            {
                writer.WriteStartArray("warnings");
                foreach (string warning in warnings)
                {
                    writer.WriteStringValue(warning);
                }

                writer.WriteEndArray();
            }

            if (includeValues)
            {
                WriteValues(writer, leaves, returned, candles!, metadata!, secondary, evaluationContext);
            }

            writer.WriteEndObject();
        });
    }

    /// <summary>The saved template with that id; the offsets of its entries must respect the application's own limit (the Screener service refuses them otherwise).</summary>
    private async Task<ScreenerIndicatorTemplate> LoadTemplateAsync(string idText, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(idText, out Guid id))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, "templateId must be a template id.", TemplateArgument, "Ids come from sa_list_templates.");
        }

        ScreenerIndicatorTemplate template = await _gateway.GetScreenerTemplateAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new ToolException(ToolErrorCodes.TemplateNotFound, $"No readable Screener template has the id {id}.", TemplateArgument, "Call sa_list_templates to see the saved templates.");

        int maxOffset = template.Entries.Where(entry => entry.IsEnabled).Select(ScreenerHistoryRequirements.GetMaxConditionOffset).DefaultIfEmpty(0).Max();
        if (maxOffset > _settings.ScreenerMaxConditionOffset)
        {
            throw new ToolException(
                ToolErrorCodes.ConditionLimitExceeded,
                $"The template uses an offset of {maxOffset} bars, above the maximum {_settings.ScreenerMaxConditionOffset}.",
                TemplateArgument,
                $"The limit is the application setting Screener:MaxConditionOffset = {_settings.ScreenerMaxConditionOffset}.");
        }

        return template;
    }

    private async Task<(List<string> Symbols, bool IsCustom, string? GroupId)> ResolveUniverseAsync(JsonElement arguments, bool allowUnregistered, List<string> warnings, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> available = await _gateway.GetAvailableTickersAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string>? requested = ToolArgs.StringArray(arguments, SymbolsArgument);
        string? groupIdRaw = ToolArgs.String(arguments, GroupIdArgument)?.Trim();

        if (requested is not null)
        {
            if (requested.Count > _limits.MaxScreenerSymbols)
            {
                throw new ToolException(
                    ToolErrorCodes.LimitExceeded,
                    $"At most {_limits.MaxScreenerSymbols} symbols per call.",
                    SymbolsArgument,
                    "Send fewer symbols, or omit symbols to screen every available symbol.");
            }

            var known = new HashSet<string>(available, StringComparer.OrdinalIgnoreCase);
            var universe = new SortedSet<string>(StringComparer.Ordinal);
            var offList = new SortedSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < requested.Count; index++)
            {
                string path = $"{SymbolsArgument}[{index}]";
                string canonical = SymbolResolver.Canonical(requested[index], path);
                if (!known.Contains(canonical))
                {
                    if (!allowUnregistered)
                    {
                        throw new ToolException(ToolErrorCodes.SymbolNotFound, $"Symbol '{canonical}' is not available.", path, "Call sa_list_tickers to see the available symbols.");
                    }

                    if (!offList.Contains(canonical))
                    {
                        await _symbols.RequireStoredBarsAsync(canonical, path, cancellationToken).ConfigureAwait(false);
                        offList.Add(canonical);
                    }
                }

                universe.Add(canonical);
            }

            if (offList.Count > 0)
            {
                warnings.Add(SymbolResolver.OffListWarning(offList));
            }

            return (universe.ToList(), true, null);
        }

        if (groupIdRaw is not null)
        {
            (Guid groupId, GroupMembers group) = await GroupMembersLookup.ResolveAsync(_groupResolver, groupIdRaw, GroupIdArgument, _limits.MaxFilterEvaluationSymbols, cancellationToken).ConfigureAwait(false);

            if (group.Symbols.Count > _limits.MaxScreenerSymbols)
            {
                throw new ToolException(
                    ToolErrorCodes.LimitExceeded,
                    $"The group contains {group.Symbols.Count} symbols, exceeding the limit of {_limits.MaxScreenerSymbols}.",
                    GroupIdArgument,
                    "Narrow the group or filter.");
            }

            return (group.Symbols.ToList(), true, groupId.ToString("D").ToLowerInvariant());
        }

        return (available.OrderBy(symbol => symbol, StringComparer.Ordinal).ToList(), false, null);
    }

    private static IReadOnlyList<StockAnalyzer.Core.Models.Analysis.UniverseBreadthKey> CollectUniverseBreadthKeys(
        IReadOnlyList<ScreenerLeaf> leaves,
        BoundSortSide? sortSide,
        TimeFrame defaultTimeFrame,
        bool sideFramesMode,
        DateOnly? asOfDate)
    {
        var keys = new List<StockAnalyzer.Core.Models.Analysis.UniverseBreadthKey>();
        var seen = new HashSet<StockAnalyzer.Core.Models.Analysis.UniverseBreadthKey>();

        foreach (var key in ScreenerUniverseBreadthInspector.Inspect(leaves.Select(static leaf => leaf.Entry), defaultTimeFrame, sideFramesMode, asOfDate))
        {
            if (seen.Add(key))
            {
                keys.Add(key);
            }
        }

        if (sortSide is not null)
        {
            foreach (var key in ScreenerUniverseBreadthInspector.InspectSides(new[] { sortSide.Side }, defaultTimeFrame, sideFramesMode, asOfDate))
            {
                if (seen.Add(key))
                {
                    keys.Add(key);
                }
            }
        }

        return keys;
    }

    /// <summary>The comparison symbols the enabled entries of a saved template name, with the path of the entry side that holds each.</summary>
    private static IReadOnlyList<ScreenerComparisonSymbol> TemplateComparisonSymbols(IReadOnlyList<ScreenerLeaf> leaves)
    {
        var named = new List<ScreenerComparisonSymbol>();
        foreach (ScreenerLeaf leaf in leaves)
        {
            if (ScreenerComparisonSymbols.Of(leaf.Entry.LeftHand) is { } left)
            {
                named.Add(new ScreenerComparisonSymbol(left, $"{leaf.Path}.leftHand"));
            }

            if (leaf.Entry.TargetMode == RightHandTargetMode.Indicator && ScreenerComparisonSymbols.Of(leaf.Entry.RightHand) is { } right)
            {
                named.Add(new ScreenerComparisonSymbol(right, $"{leaf.Path}.rightHand"));
            }
        }

        return named;
    }

    /// <summary>
    /// The bars and stored metadata the screener read for each returned symbol, loaded the way the screener loads them (the history the condition needs),
    /// and the bars of the comparison symbols (null when there are none).
    /// </summary>
    private async Task<(ValueBars Candles, Dictionary<string, TickerMetadata?> Metadata, IScreenerSecondaryCandles? Secondary)> LoadValueInputsAsync(
        IReadOnlyList<string> symbols, IScreeningCondition sizing, IReadOnlyList<string> comparisonSymbols, BarSource source, CancellationToken cancellationToken)
    {
        int count = HistoryCount(sizing);
        var candles = new Dictionary<string, IReadOnlyList<CandleData>>(StringComparer.Ordinal);
        var metadata = new Dictionary<string, TickerMetadata?>(StringComparer.Ordinal);
        foreach (string symbol in symbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            candles[symbol] = await LoadBarsAsync(source, symbol, count, cancellationToken).ConfigureAwait(false);
            metadata[symbol] = await _gateway.GetStoredTickerMetadataAsync(symbol, cancellationToken).ConfigureAwait(false);
        }

        return (ValueBars.OfOneFrame(candles), metadata, await LoadSecondaryAsync(source, comparisonSymbols, count, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// <see cref="LoadValueInputsAsync"/> for a scan in which each side reads its own time frame: for every returned symbol the bars of each frame the condition reads, cut and
    /// aggregated exactly as the scan cut and aggregated them, and the stored metadata. There are no comparison symbols in this scan.
    /// </summary>
    private async Task<(ValueBars Candles, Dictionary<string, TickerMetadata?> Metadata, IScreenerSecondaryCandles? Secondary)> LoadSideFrameValueInputsAsync(
        IReadOnlyList<string> symbols, IScreeningCondition sizing, ScreenerBarSelection selection, CancellationToken cancellationToken)
    {
        int count = HistoryCount(sizing);
        IReadOnlyList<TimeFrame> frames = ScreenerSideFrames.Collect(sizing);
        var byFrame = new Dictionary<string, Dictionary<TimeFrame, IReadOnlyList<CandleData>>>(StringComparer.Ordinal);
        var metadata = new Dictionary<string, TickerMetadata?>(StringComparer.Ordinal);
        foreach (string symbol in symbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bars = new Dictionary<TimeFrame, IReadOnlyList<CandleData>>();
            foreach (TimeFrame frame in frames)
            {
                bars[frame] = await _gateway.GetScreenerBarsAsync(symbol, frame, selection, count, cancellationToken).ConfigureAwait(false);
            }

            byFrame[symbol] = bars;
            metadata[symbol] = await _gateway.GetStoredTickerMetadataAsync(symbol, cancellationToken).ConfigureAwait(false);
        }

        return (ValueBars.OfSideFrames(byFrame), metadata, null);
    }

    /// <summary>The number of bars the Screener loads for a condition (0 = the whole history), capped by the request limit.</summary>
    private int HistoryCount(IScreeningCondition sizing)
    {
        int required = ScreenerHistoryRequirements.GetRequiredCandleCount(sizing);
        return required <= 0 ? _limits.MaxCandlesPerRequest : Math.Min(required, _limits.MaxCandlesPerRequest);
    }

    /// <summary>The bars of the comparison symbols, read the way the scan read them; null when there are none.</summary>
    private async Task<IScreenerSecondaryCandles?> LoadSecondaryAsync(BarSource source, IReadOnlyList<string> comparisonSymbols, int count, CancellationToken cancellationToken)
    {
        if (comparisonSymbols.Count == 0)
        {
            return null;
        }

        var bySymbol = new Dictionary<string, IReadOnlyList<CoreCandleData?>>(StringComparer.OrdinalIgnoreCase);
        foreach (string comparison in comparisonSymbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<CandleData> bars = await LoadBarsAsync(source, comparison, count, cancellationToken).ConfigureAwait(false);
            if (bars.Count > 0)
            {
                bySymbol[comparison] = bars.Select(bar => (CoreCandleData?)new CoreCandleData(bar.Timestamp, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume)).ToList();
            }
        }

        return new ScreenerSecondaryCandles(bySymbol);
    }

    /// <summary>The latest <paramref name="count"/> bars of the symbol that the scan evaluated: those of the selection when there is one (date, frame, partial-period rule), else the latest stored bars.</summary>
    private async Task<IReadOnlyList<CandleData>> LoadBarsAsync(BarSource source, string symbol, int count, CancellationToken cancellationToken)
        => source.Selection is { } selection
            ? await _gateway.GetScreenerBarsAsync(symbol, source.TimeFrame, selection, count, cancellationToken).ConfigureAwait(false)
            : await _gateway.GetCandlesAsync(symbol, source.TimeFrame, count, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// The value of the sort side for every matched symbol, each read from the bars the scan evaluated with the extractor the comparisons use (offset included);
    /// null where it cannot be calculated. One symbol is held at a time.
    /// </summary>
    private async Task<Dictionary<string, decimal?>> SortValuesAsync(
        IReadOnlyList<string> symbols, BoundSortSide sort, IReadOnlyList<string> comparisonSymbols, BarSource source, IScreenerEvaluationContext? evaluationContext, CancellationToken cancellationToken)
    {
        int count = HistoryCount(sort.Sizing);
        IScreenerSecondaryCandles? secondary = await LoadSecondaryAsync(source, comparisonSymbols, count, cancellationToken).ConfigureAwait(false);
        bool readsMetadata = sort.Side.CategoryType == ScreenerItemCategoryType.Column;
        var values = new Dictionary<string, decimal?>(StringComparer.Ordinal);
        foreach (string symbol in symbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<CandleData> bars = await LoadBarsAsync(source, symbol, count, cancellationToken).ConfigureAwait(false);
            TickerMetadata metadata = readsMetadata
                ? await _gateway.GetStoredTickerMetadataAsync(symbol, cancellationToken).ConfigureAwait(false) ?? TickerMetadata.Unknown
                : TickerMetadata.Unknown;
            values[symbol] = _extractor.ExtractValueNullable(sort.Side, bars, metadata, secondary, symbol, evaluationContext);
        }

        return values;
    }

    /// <summary>
    /// <see cref="SortValuesAsync"/> for a scan in which each side reads its own time frame: the sort side is read from the bars of ITS frame (cut and aggregated as the scan did).
    /// There are no comparison symbols in this scan.
    /// </summary>
    private async Task<Dictionary<string, decimal?>> SortValuesOnSideFrameAsync(
        IReadOnlyList<string> symbols, BoundSortSide sort, ScreenerBarSelection selection, IScreenerEvaluationContext? evaluationContext, CancellationToken cancellationToken)
    {
        int count = HistoryCount(sort.Sizing);
        TimeFrame frame = ScreenerSideFrames.FrameOf(sort.Side);
        ScreenerIndicatorSideConfig onItsBars = ValueBars.ForItsOwnBars(sort.Side);
        bool readsMetadata = sort.Side.CategoryType == ScreenerItemCategoryType.Column;
        var values = new Dictionary<string, decimal?>(StringComparer.Ordinal);
        foreach (string symbol in symbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<CandleData> bars = await _gateway.GetScreenerBarsAsync(symbol, frame, selection, count, cancellationToken).ConfigureAwait(false);
            TickerMetadata metadata = readsMetadata
                ? await _gateway.GetStoredTickerMetadataAsync(symbol, cancellationToken).ConfigureAwait(false) ?? TickerMetadata.Unknown
                : TickerMetadata.Unknown;
            values[symbol] = _extractor.ExtractValueNullable(onItsBars, bars, metadata, null, symbol, evaluationContext);
        }

        return values;
    }

    /// <summary>
    /// The symbols in rank order (plan 02 section 6.2): the larger value first for Descending, the smaller for Ascending; a symbol without a value after all that have one
    /// whichever the direction; equal values (and all symbols without a value) by ordinal order of the symbol.
    /// </summary>
    internal static List<string> Rank(IReadOnlyDictionary<string, decimal?> values, bool descending)
    {
        IOrderedEnumerable<string> byPresence = values.Keys.OrderBy(symbol => values[symbol] is null ? 1 : 0);
        IOrderedEnumerable<string> byValue = descending
            ? byPresence.ThenByDescending(symbol => values[symbol] ?? 0m)
            : byPresence.ThenBy(symbol => values[symbol] ?? 0m);
        return byValue.ThenBy(symbol => symbol, StringComparer.Ordinal).ToList();
    }

    /// <summary>Where the bars a request reads come from: the time frame, and the selection (date, partial-period rule) when the scan was not the plain latest-bar scan.</summary>
    private sealed record BarSource(TimeFrame TimeFrame, ScreenerBarSelection? Selection);

    /// <summary>The answer of a plain scan (no selection): nothing is without bars and no period is partial.</summary>
    private static ScreenerBarsResult PlainResult(IReadOnlyList<string> matched) => new(matched, Array.Empty<string>(), Array.Empty<DateOnly>(), 0);

    /// <summary>
    /// The bars each returned symbol's values are read from: one list per symbol when the whole scan is on one frame, one list per symbol and frame when each side reads its own.
    /// </summary>
    private sealed class ValueBars
    {
        private readonly Dictionary<string, IReadOnlyList<CandleData>>? _oneFrame;
        private readonly Dictionary<string, Dictionary<TimeFrame, IReadOnlyList<CandleData>>>? _bySideFrame;

        private ValueBars(Dictionary<string, IReadOnlyList<CandleData>>? oneFrame, Dictionary<string, Dictionary<TimeFrame, IReadOnlyList<CandleData>>>? bySideFrame)
        {
            _oneFrame = oneFrame;
            _bySideFrame = bySideFrame;
        }

        public static ValueBars OfOneFrame(Dictionary<string, IReadOnlyList<CandleData>> bars) => new(bars, null);

        public static ValueBars OfSideFrames(Dictionary<string, Dictionary<TimeFrame, IReadOnlyList<CandleData>>> bars) => new(null, bars);

        /// <summary>The bars <paramref name="side"/> reads for <paramref name="symbol"/>: those of its own frame when each side reads its own.</summary>
        public IReadOnlyList<CandleData> Of(string symbol, ScreenerIndicatorSideConfig side)
            => _oneFrame is not null ? _oneFrame[symbol] : _bySideFrame![symbol][ScreenerSideFrames.FrameOf(side)];

        /// <summary>The side as the extractor is given it with <see cref="Of"/>'s bars: when each side reads its own frame the list is already of that frame, so the side is read as a D1 side (the extractor refuses a weekly side of the one-frame scan).</summary>
        public ScreenerIndicatorSideConfig Read(ScreenerIndicatorSideConfig side) => _bySideFrame is not null ? ForItsOwnBars(side) : side;

        /// <summary>A copy of <paramref name="side"/> on D1 when it reads weekly or monthly bars (the list it is given is already of its frame), else the side itself.</summary>
        public static ScreenerIndicatorSideConfig ForItsOwnBars(ScreenerIndicatorSideConfig side)
            => ScreenerSideFrames.ReadsBars(side) ? side.WithTimeFrame(TimeFrame.D1) : side;
    }

    /// <summary>The report of each weekly or monthly frame a scan read: sideFrames [{timeFrame, partialLastBar, partialPeriodStarts?, skippedPartialBarCount?, symbolsWithoutBarsCount, symbolsWithoutBars}].</summary>
    private static void WriteSideFrames(System.Text.Json.Utf8JsonWriter writer, IReadOnlyList<ScreenerFrameReport> frames, bool completeBarsOnly, int listLimit)
    {
        if (frames.Count == 0)
        {
            return;
        }

        writer.WriteStartArray("sideFrames");
        foreach (ScreenerFrameReport report in frames)
        {
            writer.WriteStartObject();
            writer.WriteString("timeFrame", report.Frame.ToString());
            writer.WriteBoolean("partialLastBar", report.PartialLastBarPeriodStarts.Count > 0);
            if (report.PartialLastBarPeriodStarts.Count > 0)
            {
                writer.WriteStartArray("partialPeriodStarts");
                foreach (DateOnly start in report.PartialLastBarPeriodStarts)
                {
                    writer.WriteStringValue(start.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture));
                }

                writer.WriteEndArray();
            }

            if (completeBarsOnly)
            {
                writer.WriteNumber("skippedPartialBarCount", report.SkippedPartialBarCount);
            }

            writer.WriteNumber("symbolsWithoutBarsCount", report.SymbolsWithoutBars.Count);
            writer.WriteStartArray("symbolsWithoutBars");
            foreach (string symbol in report.SymbolsWithoutBars.Take(listLimit))
            {
                writer.WriteStringValue(symbol);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private void WriteValues(
        System.Text.Json.Utf8JsonWriter writer,
        IReadOnlyList<ScreenerLeaf> leaves,
        IReadOnlyList<string> symbols,
        ValueBars candles,
        IReadOnlyDictionary<string, TickerMetadata?> metadata,
        IScreenerSecondaryCandles? secondary,
        IScreenerEvaluationContext? evaluationContext)
    {
        writer.WriteStartArray("comparisons");
        foreach (ScreenerLeaf leaf in leaves)
        {
            writer.WriteStartObject();
            writer.WriteString("id", leaf.Id);
            writer.WriteString("path", leaf.Path);
            writer.WriteString("text", ScreenerEntryText.Describe(leaf.Entry));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartObject("matchedValues");
        foreach (string symbol in symbols)
        {
            TickerMetadata meta = metadata[symbol] ?? TickerMetadata.Unknown;
            writer.WriteStartObject(symbol);
            foreach (ScreenerLeaf leaf in leaves)
            {
                writer.WriteStartObject(leaf.Id);
                WriteSideValue(writer, "left", leaf.Entry, leaf.Entry.LeftHand, candles, symbol, meta, secondary, evaluationContext, isRight: false);
                WriteSideValue(writer, "right", leaf.Entry, leaf.Entry.RightHand, candles, symbol, meta, secondary, evaluationContext, isRight: true);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private void WriteSideValue(
        System.Text.Json.Utf8JsonWriter writer,
        string name,
        ScreenerIndicatorEntry entry,
        ScreenerIndicatorSideConfig side,
        ValueBars bars,
        string symbol,
        TickerMetadata metadata,
        IScreenerSecondaryCandles? secondary,
        IScreenerEvaluationContext? evaluationContext,
        bool isRight)
    {
        if (isRight && entry.TargetMode == RightHandTargetMode.NumericValue)
        {
            writer.WriteNumber(name, entry.RightNumericValue);
            return;
        }

        if (isRight && entry.TargetMode == RightHandTargetMode.StringValue)
        {
            writer.WriteString(name, entry.RightStringValue);
            return;
        }

        if (entry.TargetMode == RightHandTargetMode.StringValue)
        {
            if (_extractor.ExtractStringValue(side, metadata) is { } text)
            {
                writer.WriteString(name, text);
            }
            else
            {
                writer.WriteNull(name);
            }

            return;
        }

        if (_extractor.ExtractValueNullable(bars.Read(side), bars.Of(symbol, side), metadata, secondary, symbol, evaluationContext) is { } value)
        {
            writer.WriteNumber(name, value);
        }
        else
        {
            writer.WriteNull(name);
        }
    }
}
