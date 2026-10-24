using System.Collections.Immutable;
using System.Text.Json;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
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
    private const string TimeFrameArgument = "timeFrame";
    private const string ValuesArgument = "includeMatchedValues";
    private const string MaxMatchesArgument = "maxReturnedMatches";

    /// <summary>The Screener evaluates daily bars only: the value extractor returns "no value" for any other frame (plan A-06).</summary>
    internal const TimeFrame ScreenerTimeFrame = TimeFrame.D1;

    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly ScreenerConditionBinder _binder;
    private readonly IScreenerValueExtractor _extractor;
    private readonly ScreenerTemplateAdvisor _advisor;
    private readonly IStockAnalyzerSettings _settings;
    private readonly McpLimits _limits;

    public RunScreenerTool(
        IReadOnlyStockAnalyzerGateway gateway,
        ScreenerConditionBinder binder,
        IScreenerValueExtractor extractor,
        ScreenerTemplateAdvisor advisor,
        IStockAnalyzerSettings settings,
        McpLimits limits)
    {
        _gateway = gateway;
        _binder = binder;
        _extractor = extractor;
        _advisor = advisor;
        _settings = settings;
        _limits = limits;
    }

    public string Name => "sa_run_screener";

    public string Description =>
        "Finds the symbols whose latest daily bar meets a condition, with the application's own Screener (the same evaluation as the Screener tab). " +
        "Call sa_get_capabilities with includeScreenerCatalog true first to learn the column, criteria and price names; indicator types, parameters and series come from sa_get_capabilities. " +
        "Give exactly one of conditionTree (a condition you write) or templateId (a Screener template the user saved; ids come from sa_list_templates). " +
        "conditionTree is a group {operator: And|Or, children: [...]} of groups and comparisons (or one comparison). " +
        "A comparison is {left: side, comparison: GreaterThan|GreaterThanOrEqual|LessThan|LessThanOrEqual|Equal|NotEqual|Contains|DoesNotContain, right: number | text | side}. " +
        "A side names exactly one of indicator (with optional parameters and outputSeriesName), price, column or criteria, plus an optional offset (bars back from the latest bar, default 0). " +
        "Example: {\"operator\":\"And\",\"children\":[{\"left\":{\"indicator\":\"RSI\",\"parameters\":{\"Period\":14}},\"comparison\":\"LessThan\",\"right\":30}," +
        "{\"left\":{\"price\":\"Close\"},\"comparison\":\"GreaterThan\",\"right\":{\"indicator\":\"SMA\",\"parameters\":{\"Period\":200}}}]}. " +
        "A criteria side is 1 when the pattern is present at the bar and 0 otherwise (compare it with Equal 1). " +
        "A text column (Sector, Industry, Region, Name, Tag) is compared with text using Equal, NotEqual, Contains or DoesNotContain. " +
        "A comparison whose value cannot be calculated (too little history, no metadata) does not match. Indicators that look ahead are not allowed. " +
        $"An indicator that compares two symbols (isCrossTicker in sa_get_capabilities, for example Correlation) needs its ComparisonSymbol parameter: a stored symbol, at most {_limits.MaxSecondarySymbolsPerCall} different ones per call; " +
        "a symbol screened against itself is compared with itself, and the comparison symbol's bars are matched to each screened symbol's dates. " +
        $"Omit symbols to screen every available symbol; otherwise list at most {_limits.MaxScreenerSymbols}. The tree size and offset limits are the application's Screener settings (see sa_get_settings). " +
        $"matchedSymbols are in ascending order, at most maxReturnedMatches (default and maximum {_limits.MaxReturnedMatches}); matchedSymbolCount is the true count and truncated says whether the list was cut. " +
        "includeMatchedValues adds the value of each comparison's two sides for every returned symbol. Only the daily time frame is screened. " +
        "A template is run as the user saved it (enabled entries combined from the first to the last by the And or Or of the preceding entry); entries that can never match here are listed in warnings. " +
        "The tool never changes anything and never fetches data.";

    public bool IsHeavy => true;

    public string InputSchemaJson => ToolSchema.Object()
        .FreeObject(ConditionArgument, "The condition tree (see the tool description). Give this or templateId.")
        .String(TemplateArgument, "Id of a saved Screener template (from sa_list_templates). Give this or conditionTree.")
        .StringArray(SymbolsArgument, $"Symbols to screen (at most {_limits.MaxScreenerSymbols}); omit to screen every available symbol.", minItems: 1)
        .String(TimeFrameArgument, $"Time frame of the bars. Only {ScreenerTimeFrame} is supported (default).", allowedValues: Enum.GetNames<TimeFrame>())
        .Boolean(ValuesArgument, "Add the values each comparison read, per returned symbol. Default false.")
        .Integer(MaxMatchesArgument, $"Most matched symbols to return. Default and maximum {_limits.MaxReturnedMatches}.", defaultValue: _limits.MaxReturnedMatches, minimum: 1, maximum: _limits.MaxReturnedMatches)
        .Build();

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        CheckTimeFrame(arguments);
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

        int maxMatches = ToolArgs.PositiveWithin(arguments, MaxMatchesArgument, _limits.MaxReturnedMatches, _limits.MaxReturnedMatches);
        bool includeValues = ToolArgs.Boolean(arguments, ValuesArgument) ?? false;

        // Both modes end in the same pieces: the leaves (for matched values), a condition that sizes the history, and a way to run it.
        IReadOnlyList<ScreenerLeaf> leaves;
        IScreeningCondition sizing;
        Func<IReadOnlyList<string>, Task<IReadOnlyList<string>>> run;
        ScreenerIndicatorTemplate? template = null;
        IReadOnlyList<string> warnings = Array.Empty<string>();
        IReadOnlyList<string> comparisonSymbols;
        int nodeCount = 0, depth = 0;
        if (hasTree)
        {
            BoundScreenerCondition bound = _binder.Bind(tree, ConditionArgument);
            leaves = bound.Leaves;
            sizing = bound.Condition;
            nodeCount = bound.NodeCount;
            depth = bound.Depth;
            comparisonSymbols = await ResolveComparisonSymbolsAsync(bound.ComparisonSymbols, ConditionArgument, cancellationToken).ConfigureAwait(false);
            run = universe => _gateway.RunScreenerAsync(universe, bound.Condition, ScreenerTimeFrame, comparisonSymbols, cancellationToken);
        }
        else
        {
            template = await LoadTemplateAsync(templateText!, cancellationToken).ConfigureAwait(false);
            warnings = _advisor.Warnings(template);
            leaves = template.Entries
                .Select((entry, index) => (entry, index))
                .Where(item => item.entry.IsEnabled)
                .Select((item, position) => new ScreenerLeaf($"L{position}", $"entries[{item.index}]", item.entry))
                .ToList();
            sizing = new ScreenerConditionTreeCondition(new ScreenerConditionGroup(
                LogicalOperator.And, leaves.Select(leaf => (IScreenerConditionNode)new ScreenerConditionLeaf(leaf.Entry)).ToImmutableArray()));
            ScreenerIndicatorTemplate loaded = template;
            comparisonSymbols = await ResolveComparisonSymbolsAsync(TemplateComparisonSymbols(leaves), TemplateArgument, cancellationToken).ConfigureAwait(false);
            run = universe => _gateway.RunScreenerTemplateAsync(universe, loaded, ScreenerTimeFrame, comparisonSymbols, cancellationToken);
        }

        (List<string> universe, bool isCustom) = await ResolveUniverseAsync(arguments, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> matched = universe.Count == 0 ? Array.Empty<string>() : await run(universe).ConfigureAwait(false);

        List<string> ordered = matched.OrderBy(symbol => symbol, StringComparer.Ordinal).ToList();
        List<string> returned = ordered.Take(maxMatches).ToList();
        Dictionary<string, TickerMetadata?>? metadata = null;
        Dictionary<string, IReadOnlyList<CandleData>>? candles = null;
        IScreenerSecondaryCandles? secondary = null;
        if (includeValues)
        {
            (candles, metadata, secondary) = await LoadValueInputsAsync(returned, sizing, comparisonSymbols, cancellationToken).ConfigureAwait(false);
        }

        return ToolJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("timeFrame", ScreenerTimeFrame.ToString());
            writer.WriteString("universe", isCustom ? "Custom" : "AllAvailable");
            if (template is not null)
            {
                writer.WriteStartObject("template");
                writer.WriteString("id", template.Id.ToString());
                writer.WriteString("name", template.Name);
                writer.WriteEndObject();
            }

            writer.WriteNumber("evaluatedSymbolCount", universe.Count);
            writer.WriteNumber("matchedSymbolCount", ordered.Count);
            writer.WriteNumber("returnedMatchCount", returned.Count);
            writer.WriteBoolean("truncated", ordered.Count > returned.Count);
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
                WriteValues(writer, leaves, returned, candles!, metadata!, secondary);
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

    private static void CheckTimeFrame(JsonElement arguments)
    {
        string? name = ToolArgs.String(arguments, TimeFrameArgument);
        if (name is not null && Enum.Parse<TimeFrame>(name) != ScreenerTimeFrame)
        {
            throw new ToolException(ToolErrorCodes.UnsupportedTimeFrame, $"Time frame {name} is not supported by the screener.", TimeFrameArgument, $"Supported: {ScreenerTimeFrame}.");
        }
    }

    private async Task<(List<string> Symbols, bool IsCustom)> ResolveUniverseAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> available = await _gateway.GetAvailableTickersAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string>? requested = ToolArgs.StringArray(arguments, SymbolsArgument);
        if (requested is null)
        {
            return (available.OrderBy(symbol => symbol, StringComparer.Ordinal).ToList(), false);
        }

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
        for (int index = 0; index < requested.Count; index++)
        {
            string path = $"{SymbolsArgument}[{index}]";
            string canonical = SymbolResolver.Canonical(requested[index], path);
            if (!known.Contains(canonical))
            {
                throw new ToolException(ToolErrorCodes.SymbolNotFound, $"Symbol '{canonical}' is not available.", path, "Call sa_list_tickers to see the available symbols.");
            }

            universe.Add(canonical);
        }

        return (universe.ToList(), true);
    }

    /// <summary>
    /// The comparison symbols the condition names, canonical and distinct: each must be a stored symbol (SYMBOL_NOT_FOUND names the parameter that
    /// holds it) and there are at most <see cref="McpLimits.MaxSecondarySymbolsPerCall"/> different ones (LIMIT_EXCEEDED). A symbol that is also
    /// screened is allowed: it is compared with itself.
    /// </summary>
    private async Task<IReadOnlyList<string>> ResolveComparisonSymbolsAsync(
        IReadOnlyList<ScreenerComparisonSymbol> named, string argument, CancellationToken cancellationToken)
    {
        if (named.Count == 0)
        {
            return Array.Empty<string>();
        }

        IReadOnlyList<string> available = await _gateway.GetAvailableTickersAsync(cancellationToken).ConfigureAwait(false);
        var known = new HashSet<string>(available, StringComparer.OrdinalIgnoreCase);
        var symbols = new List<string>();
        foreach (ScreenerComparisonSymbol item in named)
        {
            string canonical = SymbolResolver.Canonical(item.Symbol, item.Path);
            if (!known.Contains(canonical))
            {
                throw new ToolException(ToolErrorCodes.SymbolNotFound, $"Symbol '{canonical}' is not available.", item.Path, "Call sa_list_tickers to see the available symbols.");
            }

            if (!symbols.Contains(canonical, StringComparer.OrdinalIgnoreCase))
            {
                symbols.Add(canonical);
            }
        }

        if (symbols.Count > _limits.MaxSecondarySymbolsPerCall)
        {
            throw new ToolException(
                ToolErrorCodes.LimitExceeded,
                $"At most {_limits.MaxSecondarySymbolsPerCall} different comparison symbols per call.",
                argument,
                "Use fewer different ComparisonSymbol values.");
        }

        return symbols;
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
    private async Task<(Dictionary<string, IReadOnlyList<CandleData>> Candles, Dictionary<string, TickerMetadata?> Metadata, IScreenerSecondaryCandles? Secondary)> LoadValueInputsAsync(
        IReadOnlyList<string> symbols, IScreeningCondition sizing, IReadOnlyList<string> comparisonSymbols, CancellationToken cancellationToken)
    {
        int required = ScreenerHistoryRequirements.GetRequiredCandleCount(sizing);
        int count = required <= 0 ? _limits.MaxCandlesPerRequest : Math.Min(required, _limits.MaxCandlesPerRequest);
        var candles = new Dictionary<string, IReadOnlyList<CandleData>>(StringComparer.Ordinal);
        var metadata = new Dictionary<string, TickerMetadata?>(StringComparer.Ordinal);
        foreach (string symbol in symbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            candles[symbol] = await _gateway.GetCandlesAsync(symbol, ScreenerTimeFrame, count, cancellationToken).ConfigureAwait(false);
            metadata[symbol] = await _gateway.GetStoredTickerMetadataAsync(symbol, cancellationToken).ConfigureAwait(false);
        }

        ScreenerSecondaryCandles? secondary = null;
        if (comparisonSymbols.Count > 0)
        {
            var bySymbol = new Dictionary<string, IReadOnlyList<CoreCandleData?>>(StringComparer.OrdinalIgnoreCase);
            foreach (string comparison in comparisonSymbols)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<CandleData> bars = await _gateway.GetCandlesAsync(comparison, ScreenerTimeFrame, count, cancellationToken).ConfigureAwait(false);
                if (bars.Count > 0)
                {
                    bySymbol[comparison] = bars.Select(bar => (CoreCandleData?)new CoreCandleData(bar.Timestamp, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume)).ToList();
                }
            }

            secondary = new ScreenerSecondaryCandles(bySymbol);
        }

        return (candles, metadata, secondary);
    }

    private void WriteValues(
        System.Text.Json.Utf8JsonWriter writer,
        IReadOnlyList<ScreenerLeaf> leaves,
        IReadOnlyList<string> symbols,
        IReadOnlyDictionary<string, IReadOnlyList<CandleData>> candles,
        IReadOnlyDictionary<string, TickerMetadata?> metadata,
        IScreenerSecondaryCandles? secondary)
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
                WriteSideValue(writer, "left", leaf.Entry, leaf.Entry.LeftHand, candles[symbol], meta, secondary, isRight: false);
                WriteSideValue(writer, "right", leaf.Entry, leaf.Entry.RightHand, candles[symbol], meta, secondary, isRight: true);
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
        IReadOnlyList<CandleData> candles,
        TickerMetadata metadata,
        IScreenerSecondaryCandles? secondary,
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

        if (_extractor.ExtractValueNullable(side, candles, metadata, secondary) is { } value)
        {
            writer.WriteNumber(name, value);
        }
        else
        {
            writer.WriteNull(name);
        }
    }
}
