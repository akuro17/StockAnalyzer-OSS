using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services.Watchlist;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// sa_filter_tickers: the Tickers-tab rule filter for rules that are sent with the call (nothing has to be saved first), optionally followed by a
/// Screener condition. The universe is a list of symbols, a saved ticker group or every registered symbol; the rules are AND-ed by the same
/// evaluator the saved filters use, the condition is run by the same Screener service as sa_run_screener on the symbols the rules kept.
/// </summary>
internal sealed class FilterTickersTool : IMcpTool
{
    private const string SymbolsArgument = "symbols";
    private const string GroupIdArgument = "groupId";
    private const string RulesArgument = "rules";
    private const string ScreenerArgument = "screener";
    private const string ConditionKey = "conditionTree";
    private const string IncludeFieldsArgument = "includeFields";
    private const string OffsetArgument = "offset";
    private const string LimitArgument = "limit";
    private const string FieldKey = "field";
    private const string OperatorKey = "operator";
    private const string ValueKey = "value";
    private const string IsCompareToFieldKey = "isCompareToField";

    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly ITickerGroupResolver _groupResolver;
    private readonly ScreenerConditionBinder _binder;
    private readonly SymbolResolver _symbols;
    private readonly ScreenerComparisonSymbolResolver _comparisonSymbols;
    private readonly McpLimits _limits;

    public FilterTickersTool(IReadOnlyStockAnalyzerGateway gateway, ITickerGroupResolver groupResolver, ScreenerConditionBinder binder, McpLimits limits)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _groupResolver = groupResolver ?? throw new ArgumentNullException(nameof(groupResolver));
        _binder = binder ?? throw new ArgumentNullException(nameof(binder));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _symbols = new SymbolResolver(gateway);
        _comparisonSymbols = new ScreenerComparisonSymbolResolver(gateway, _symbols, limits);
    }

    public string Name => "sa_filter_tickers";

    public string Description =>
        "Filters tickers with rules sent in the call, the same rules and evaluator as a Tickers-tab filter (no saved filter, group or template needed), and optionally a Screener condition. " +
        "Universe: symbols (a list of symbols), or groupId (a saved ticker group), or neither (every registered symbol); symbols and groupId exclude each other. " +
        $"The universe may hold at most {_limits.MaxFilterEvaluationSymbols} symbols (LIMIT_EXCEEDED, never cut). " +
        $"rules: at most {_limits.MaxInlineFilterRules}, all must hold (AND). Each rule is {{field, operator, value, isCompareToField?}}. " +
        "field is a ticker column name or alias as used by the Tickers tab (for example Close, ChangePercent, Sector, Tag, TrailingPE, MarketCap); an unknown field is INVALID_ARGUMENT, never an empty result. " +
        "With isCompareToField true, value is another field of the same kind (number with number, text with text). " +
        $"Numeric fields accept: {string.Join(" ", FilterRuleValidator.OperatorsFor(FilterFieldKind.Numeric))}, and value must be a number. " +
        $"Text fields accept: {string.Join(" ", FilterRuleValidator.OperatorsFor(FilterFieldKind.Text))} (text comparison ignores case). " +
        $"The Tag field accepts: {string.Join(" ", FilterRuleValidator.OperatorsFor(FilterFieldKind.Tag))} (tags are separated by comma or semicolon). " +
        "A field that depends on daily bars (Open, High, Low, Close, Volume, Change, ChangePercent, valuation ratios built on the price) uses the latest stored daily bar and the one before it; a symbol without stored bars has no price, so it matches no such rule. " +
        "Values keep the unit of the Tickers-tab column: ChangePercent is a percent value (1.5 means 1.5 %), prices are in the currency of the stored data. " +
        "screener is {conditionTree} in the grammar of sa_run_screener (see its description and sa_get_capabilities with includeScreenerCatalog true); it is applied after the rules, to the symbols the rules kept, " +
        $"on the latest daily bar exactly as sa_run_screener does, and at most {_limits.MaxScreenerSymbols} symbols may reach it (LIMIT_EXCEEDED: narrow the universe or the rules). " +
        "Matches are sorted by symbol (ordinal, ascending) and paged with offset and limit. includeFields adds those columns to each returned match (null when the value is not available). " +
        "Without rules and screener the whole universe is returned. " +
        $"Without {SymbolResolver.AllowUnregisteredArgument}, symbols that are not in the ticker list are counted in universe.unregisteredCount and left out. " +
        $"{SymbolResolver.AllowUnregisteredArgument} true evaluates the listed symbols that are not in the ticker list but have stored daily bars (named in warnings); a listed symbol without stored bars is SYMBOL_NOT_FOUND. It applies to symbols only, not to groupId or the whole-list universe.";

    // The Screener condition can read long histories of up to MaxScreenerSymbols symbols, so the tool gets the longer time limit of sa_run_screener.
    public bool IsHeavy => true;

    public string? ExampleArgumentsJson =>
        "{\"symbols\":[\"MSFT\",\"AAPL\"],\"rules\":[{\"field\":\"Close\",\"operator\":\">\",\"value\":\"100\"}],\"screener\":{\"conditionTree\":{\"left\":{\"indicator\":\"RSI\",\"parameters\":{\"Period\":14}},\"comparison\":\"LessThan\",\"right\":70}},\"includeFields\":[\"Close\",\"ChangePercent\"],\"limit\":50}";

    public string InputSchemaJson => ToolSchema.Object()
        .StringArray(SymbolsArgument, $"Symbols to filter, 1 to {_limits.MaxFilterEvaluationSymbols}. Excludes {GroupIdArgument}. Omit both to filter every registered symbol.", minItems: 1)
        .String(GroupIdArgument, "Guid of a ticker group from sa_list_ticker_groups; its members are the universe. Excludes symbols.")
        .ObjectArray(
            RulesArgument,
            $"Filter rules, at most {_limits.MaxInlineFilterRules}; all must hold. Omit or send [] for no rule.",
            ToolSchema.Object()
                .String(FieldKey, "Ticker column name or alias, for example Close, Sector, Tag, TrailingPE.", required: true)
                .String(OperatorKey, "Comparison operator valid for the field kind (see the tool description).", required: true)
                .String(ValueKey, "Number or text to compare with; the name of another field when isCompareToField is true.", required: true)
                .Boolean(IsCompareToFieldKey, "True when value names another field of the same kind. Default false."))
        .NestedObject(
            ScreenerArgument,
            "A Screener condition applied after the rules, to the symbols they kept.",
            ToolSchema.Object().FreeObject(ConditionKey, "The condition tree, in the grammar of sa_run_screener.", required: true))
        .StringArray(IncludeFieldsArgument, "Ticker columns to add to each returned match; each must be a known column name or alias.")
        .Integer(OffsetArgument, "Number of matches to skip. Default 0.", defaultValue: 0, minimum: 0)
        .Integer(LimitArgument, $"Page size. Default {_limits.DefaultTickerPageSize}, at most {_limits.MaxTickersPerPage}.", defaultValue: _limits.DefaultTickerPageSize, minimum: 1, maximum: _limits.MaxTickersPerPage)
        .Boolean(SymbolResolver.AllowUnregisteredArgument, "Evaluate listed symbols that are not in the ticker list but have stored daily bars. Applies to symbols only. Default false.")
        .Build();

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        int offset = ToolArgs.NonNegativeWithin(arguments, OffsetArgument, 0, int.MaxValue);
        int limit = ToolArgs.PositiveWithin(arguments, LimitArgument, _limits.DefaultTickerPageSize, _limits.MaxTickersPerPage);
        bool allowUnregistered = ToolArgs.Boolean(arguments, SymbolResolver.AllowUnregisteredArgument) ?? false;
        IReadOnlyList<FilterRule> rules = ReadRules(arguments);
        IReadOnlyList<string> includeFields = ReadIncludeFields(arguments);

        // The shape of everything the client sent, and the symbols its screener condition names, are checked before any rule is evaluated,
        // so a mistake in the screener is reported whatever the rules leave over. Only the number of symbols that reach the screener depends on the data.
        var warnings = new List<string>();
        BoundScreenerCondition? screener = null;
        IReadOnlyList<string> comparisonSymbols = Array.Empty<string>();
        string screenerPath = $"{ScreenerArgument}.{ConditionKey}";
        if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(ScreenerArgument, out JsonElement screenerElement))
        {
            screener = _binder.Bind(screenerElement.GetProperty(ConditionKey), screenerPath);
            comparisonSymbols = await _comparisonSymbols.ResolveAsync(screener.ComparisonSymbols, screenerPath, allowUnregistered, warnings, cancellationToken).ConfigureAwait(false);
        }

        var evaluator = new TickerFilterEvaluator(_gateway);
        Universe universe = await ResolveUniverseAsync(arguments, allowUnregistered, rules, evaluator, warnings, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<string> matches = rules.Count == 0
            ? universe.Symbols
            : await evaluator.FilterAsync(universe.Symbols, rules, cancellationToken).ConfigureAwait(false);

        int? screenerEvaluated = null;
        if (screener is not null && matches.Count > 0)
        {
            if (matches.Count > _limits.MaxScreenerSymbols)
            {
                throw new ToolException(
                    ToolErrorCodes.LimitExceeded,
                    $"{matches.Count} symbols reach the screener, which exceeds the limit of {_limits.MaxScreenerSymbols}.",
                    screenerPath,
                    "Narrow the universe or add rules so that fewer symbols remain for the screener.");
            }

            screenerEvaluated = matches.Count;
            IReadOnlyList<string> screened = await _gateway.RunScreenerAsync(matches, screener.Condition, RunScreenerTool.ScreenerTimeFrame, comparisonSymbols, cancellationToken).ConfigureAwait(false);
            matches = screened.OrderBy(symbol => symbol, StringComparer.Ordinal).ToList();
        }

        List<string> page = matches.Skip(offset).Take(limit).ToList();
        var rows = new Dictionary<string, TickerRow>(StringComparer.Ordinal);
        if (includeFields.Count > 0)
        {
            bool needsCandles = includeFields.Any(TickerRow.RequiresCandles);
            foreach (string symbol in page)
            {
                rows[symbol] = await evaluator.GetRowAsync(symbol, needsCandles, cancellationToken).ConfigureAwait(false);
            }
        }

        return ToolJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartObject("universe");
            writer.WriteString("kind", universe.Kind);
            writer.WriteNumber("inputCount", universe.InputCount);
            writer.WriteNumber("unregisteredCount", universe.UnregisteredCount);
            if (universe.GroupId is not null)
            {
                writer.WriteString("groupId", universe.GroupId);
            }

            writer.WriteNumber("evaluatedCount", universe.Symbols.Count);
            writer.WriteEndObject();
            writer.WriteNumber("ruleCount", rules.Count);
            writer.WriteBoolean("screenerApplied", screener is not null);
            if (screener is not null)
            {
                writer.WriteStartObject("screener");
                writer.WriteNumber("evaluatedSymbolCount", screenerEvaluated ?? 0);
                writer.WriteNumber("treeNodeCount", screener.NodeCount);
                writer.WriteNumber("treeDepth", screener.Depth);
                writer.WriteEndObject();
            }

            writer.WriteNumber("totalMatchCount", matches.Count);
            writer.WriteNumber("offset", offset);
            writer.WriteNumber("limit", limit);
            writer.WriteNumber("returnedCount", page.Count);
            writer.WriteBoolean("hasMore", (long)offset + page.Count < matches.Count);
            writer.WriteStartArray("matches");
            foreach (string symbol in page)
            {
                writer.WriteStartObject();
                writer.WriteString("symbol", symbol);
                if (includeFields.Count > 0)
                {
                    writer.WriteStartObject("fields");
                    foreach (string field in includeFields)
                    {
                        writer.WritePropertyName(field);
                        WriteFieldValue(writer, rows[symbol].GetPropertyValue(field));
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("warnings");
            foreach (string warning in warnings)
            {
                writer.WriteStringValue(warning);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    private IReadOnlyList<FilterRule> ReadRules(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(RulesArgument, out JsonElement array))
        {
            return Array.Empty<FilterRule>();
        }

        if (array.GetArrayLength() > _limits.MaxInlineFilterRules)
        {
            throw new ToolException(
                ToolErrorCodes.LimitExceeded,
                $"At most {_limits.MaxInlineFilterRules} rules per call.",
                RulesArgument,
                $"Send fewer rules, or raise AiIntegration:{nameof(McpLimits.MaxInlineFilterRules)}.");
        }

        var rules = new List<FilterRule>();
        int index = 0;
        foreach (JsonElement item in array.EnumerateArray())
        {
            var rule = new FilterRule
            {
                Field = ToolArgs.String(item, FieldKey)!,
                Operator = ToolArgs.String(item, OperatorKey)!,
                Value = ToolArgs.String(item, ValueKey)!,
                IsCompareToField = ToolArgs.Boolean(item, IsCompareToFieldKey) ?? false,
            };

            FilterRuleProblem? problem = FilterRuleValidator.Validate(rule);
            if (problem is not null)
            {
                string key = problem.Part switch
                {
                    FilterRuleProblemPart.Field => FieldKey,
                    FilterRuleProblemPart.Operator => OperatorKey,
                    _ => ValueKey,
                };
                throw new ToolException(
                    ToolErrorCodes.InvalidArgument,
                    problem.Message,
                    $"{RulesArgument}[{index}].{key}",
                    "Fields and aliases are the Tickers-tab column names; see the operator lists in the tool description.");
            }

            rules.Add(rule);
            index++;
        }

        return rules;
    }

    private static IReadOnlyList<string> ReadIncludeFields(JsonElement arguments)
    {
        IReadOnlyList<string>? requested = ToolArgs.StringArray(arguments, IncludeFieldsArgument);
        if (requested is null || requested.Count == 0)
        {
            return Array.Empty<string>();
        }

        int maximum = TickerRow.KnownFieldNames.Count;
        if (requested.Count > maximum)
        {
            throw new ToolException(
                ToolErrorCodes.LimitExceeded,
                $"At most {maximum} fields (the number of known ticker columns and aliases).",
                IncludeFieldsArgument,
                "Send fewer field names.");
        }

        var distinct = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < requested.Count; index++)
        {
            if (!TickerRow.IsKnownField(requested[index]))
            {
                throw new ToolException(
                    ToolErrorCodes.InvalidArgument,
                    $"'{requested[index]}' is not a known ticker field.",
                    $"{IncludeFieldsArgument}[{index}]",
                    "Use a Tickers-tab column name or alias.");
            }

            if (seen.Add(requested[index]))
            {
                distinct.Add(requested[index]);
            }
        }

        return distinct;
    }

    private async Task<Universe> ResolveUniverseAsync(
        JsonElement arguments, bool allowUnregistered, IReadOnlyList<FilterRule> rules, TickerFilterEvaluator evaluator, List<string> warnings, CancellationToken cancellationToken)
    {
        IReadOnlyList<string>? requested = ToolArgs.StringArray(arguments, SymbolsArgument);
        string? groupIdRaw = ToolArgs.String(arguments, GroupIdArgument)?.Trim();

        if (requested is not null && groupIdRaw is not null)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"'{SymbolsArgument}' and '{GroupIdArgument}' cannot be combined.", GroupIdArgument, "Send one of them, or neither for every registered symbol.");
        }

        IReadOnlyList<string> available = await _gateway.GetAvailableTickersAsync(cancellationToken).ConfigureAwait(false);

        if (requested is not null)
        {
            if (requested.Count > _limits.MaxFilterEvaluationSymbols)
            {
                throw LimitExceeded(requested.Count, SymbolsArgument);
            }

            var known = new HashSet<string>(available, StringComparer.OrdinalIgnoreCase);
            var evaluated = new SortedSet<string>(StringComparer.Ordinal);
            var unregistered = new SortedSet<string>(StringComparer.Ordinal);
            bool rulesReadBars = TickerFilterEvaluator.NeedsCandles(rules);
            for (int index = 0; index < requested.Count; index++)
            {
                string canonical = SymbolResolver.Canonical(requested[index], $"{SymbolsArgument}[{index}]");
                if (known.Contains(canonical))
                {
                    evaluated.Add(canonical);
                    continue;
                }

                if (allowUnregistered && !unregistered.Contains(canonical))
                {
                    // When a rule reads the bars anyway, the evaluator reads and keeps them here, so the symbol is not read a second time.
                    bool hasBars = rulesReadBars
                        ? await evaluator.HasStoredBarsAsync(canonical, cancellationToken).ConfigureAwait(false)
                        : await _symbols.HasStoredBarsAsync(canonical, cancellationToken).ConfigureAwait(false);
                    if (!hasBars)
                    {
                        throw SymbolResolver.NotStored(canonical, $"{SymbolsArgument}[{index}]");
                    }

                    evaluated.Add(canonical);
                }

                unregistered.Add(canonical);
            }

            if (unregistered.Count > 0)
            {
                warnings.Add(allowUnregistered
                    ? SymbolResolver.OffListWarning(unregistered)
                    : $"{unregistered.Count} symbol(s) are not registered and were left out: {SymbolResolver.NameList(unregistered)}.");
            }

            return new Universe("symbols", requested.Count, unregistered.Count, evaluated.ToList(), null);
        }

        if (groupIdRaw is not null)
        {
            (Guid groupId, GroupMembers group) = await GroupMembersLookup.ResolveAsync(_groupResolver, groupIdRaw, GroupIdArgument, _limits.MaxFilterEvaluationSymbols, cancellationToken).ConfigureAwait(false);

            if (group.Symbols.Count > _limits.MaxFilterEvaluationSymbols)
            {
                throw LimitExceeded(group.Symbols.Count, GroupIdArgument);
            }

            if (group.UnregisteredCount > 0)
            {
                warnings.Add($"{group.UnregisteredCount} member(s) of the group are not registered and were left out.");
            }

            if (group.UnknownRuleFields.Count > 0)
            {
                warnings.Add($"The saved group filter names unknown field(s) that match nothing: {string.Join(", ", group.UnknownRuleFields)}.");
            }

            return new Universe("group", group.Symbols.Count + group.UnregisteredCount, group.UnregisteredCount, group.Symbols.ToList(), groupId.ToString("D").ToLowerInvariant());
        }

        List<string> distinct = available.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.Ordinal).ToList();
        // A malformed entry of the user's ticker list has no usable file name, so no row can be built for it.
        List<string> all = distinct.Where(TickerSymbol.IsWellFormed).ToList();
        if (all.Count > _limits.MaxFilterEvaluationSymbols)
        {
            throw LimitExceeded(all.Count, SymbolsArgument);
        }

        if (all.Count < distinct.Count)
        {
            warnings.Add($"{distinct.Count - all.Count} entr{(distinct.Count - all.Count == 1 ? "y" : "ies")} of the ticker list are not well-formed symbols and were left out.");
        }

        return new Universe("all", distinct.Count, 0, all, null);
    }

    private ToolException LimitExceeded(int count, string parameter) => new(
        ToolErrorCodes.LimitExceeded,
        $"The universe has {count} symbols, which exceeds the limit of {_limits.MaxFilterEvaluationSymbols}.",
        parameter,
        $"Send fewer symbols or a smaller group, or raise AiIntegration:{nameof(McpLimits.MaxFilterEvaluationSymbols)}.");

    private static void WriteFieldValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            case double number when double.IsFinite(number):
                writer.WriteNumberValue(number);
                break;
            case double:
                writer.WriteNullValue();
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case DateTimeOffset moment:
                writer.WriteStringValue(ToolJson.FormatDateTime(moment.UtcDateTime));
                break;
            case DateTime moment:
                writer.WriteStringValue(ToolJson.FormatDateTime(moment));
                break;
            default:
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }

    private sealed record Universe(string Kind, int InputCount, int UnregisteredCount, IReadOnlyList<string> Symbols, string? GroupId);
}
