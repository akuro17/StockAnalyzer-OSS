using System.Globalization;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.Limits;
using static StockAnalyzer.McpServer.Tools.CalculateIndicatorsTool;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// sa_calculate_indicators_batch: the indicators of sa_calculate_indicators on many symbols in one call. Every symbol is evaluated by the same code as the single-symbol tool
/// (<see cref="CalculateIndicatorsTool.EvaluateAsync"/>), so a value is the value the single call gives; what is new is the compact default answer (the latest values only),
/// one failed symbol not failing the call, and the comparison symbols of cross-ticker indicators being read once for the whole call (owner decisions U-04 and U-05).
/// </summary>
internal sealed class CalculateIndicatorsBatchTool : IMcpTool
{
    /// <summary>The window every symbol is calculated over when the call does not say (owner decision U-05), within <see cref="McpLimits.MaxCandlesPerRequest"/>.</summary>
    internal const int DefaultLookbackBars = 500;

    /// <summary>The trailing bars per series a call returns when it does not say, with <c>includeSeries</c> true (owner decision U-05).</summary>
    internal const int DefaultTailBars = 1;

    private const string SymbolsArgument = "symbols";
    private const string GroupIdArgument = "groupId";
    private const string TimeFrameArgument = "timeFrame";
    private const string IndicatorsArgument = "indicators";
    private const string DerivedArgument = "derived";
    private const string LookbackArgument = "lookbackBars";
    private const string TailArgument = "outputTailCount";
    private const string EndDateArgument = "endDate";

    private readonly CalculateIndicatorsTool _indicators;
    private readonly SymbolResolver _symbols;
    private readonly ITickerGroupResolver _groupResolver;
    private readonly ErrorMessageSanitizer _sanitizer;
    private readonly McpLimits _limits;
    private readonly McpResultHandleStore _resultHandles;
    private readonly bool _compactDefaults;

    public CalculateIndicatorsBatchTool(
        CalculateIndicatorsTool indicators,
        SymbolResolver symbols,
        ITickerGroupResolver groupResolver,
        ErrorMessageSanitizer sanitizer,
        McpLimits limits,
        McpResultHandleStore? resultHandles = null,
        bool compactDefaults = false)
    {
        _indicators = indicators;
        _symbols = symbols;
        _groupResolver = groupResolver;
        _sanitizer = sanitizer;
        _limits = limits;
        _resultHandles = resultHandles ?? new McpResultHandleStore();
        _compactDefaults = compactDefaults;
    }

    public string Name => "sa_calculate_indicators_batch";

    public string Description =>
        $"Calculates technical indicators of sa_calculate_indicators across 1..{_limits.MaxBatchIndicatorSymbols} symbols (via symbols, groupId, or {McpResultHandleStore.SymbolsFromResultIdArgument}; call sa_get_capabilities first). " +
        "Returns compact latestValues/leadingNullCount per symbol by default (includeSeries true adds trailing bars). Read-only (heavy). " +
        ToolJson.DocLink(Name);

    public bool IsHeavy => true;

    public string? CompactUsageHint => "Pass includeSeries: false, decimalPlaces: 4, fewer indicators, fewer symbols, or paginate with resultOffset/resultLimit.";

    public string? ExampleArgumentsJson =>
        "{\"symbols\":[\"MSFT\",\"7203-T\"],\"timeFrame\":\"D1\",\"indicators\":[{\"id\":\"rsi\",\"type\":\"RSI\",\"parameters\":{\"Period\":14}}]}";

    public string InputSchemaJson => ToolSchema.Object()
        .StringArray(SymbolsArgument, $"Symbols (1..{_limits.MaxBatchIndicatorSymbols}; '.' == '-'). Exclusive with {GroupIdArgument}/{McpResultHandleStore.SymbolsFromResultIdArgument}.", minItems: 1)
        .String(GroupIdArgument, $"Ticker group GUID. Exclusive with {SymbolsArgument}/{McpResultHandleStore.SymbolsFromResultIdArgument}.")
        .String(McpResultHandleStore.SymbolsFromResultIdArgument, $"Result handle id (from storeResult: true). Exclusive with {SymbolsArgument}/{GroupIdArgument}.")
        .Integer(McpResultHandleStore.ResultOffsetArgument, "0-based handle slice offset. Default 0.", defaultValue: 0, minimum: 0)
        .Integer(McpResultHandleStore.ResultLimitArgument, $"Max handle symbols to evaluate (1..{_limits.MaxBatchIndicatorSymbols}).", minimum: 1, maximum: _limits.MaxBatchIndicatorSymbols)
        .String(TimeFrameArgument, $"Bar timeframe ({ToolArgs.SupportedTimeFrameNames}).", required: true, allowedValues: Enum.GetNames<TimeFrame>())
        .ObjectArray(IndicatorsArgument, $"Indicators per symbol (1..{_limits.MaxIndicatorsPerCall}; see sa_calculate_indicators).", IndicatorItemSchema(), required: true, minItems: 1)
        .ObjectArray(DerivedArgument, $"Derived series (max {_limits.MaxDerivedSeriesPerCall}; see sa_calculate_indicators).", DerivedItemSchema())
        .Integer(LookbackArgument, $"Stored bars per symbol (1..{_limits.MaxCandlesPerRequest}). Default {Math.Min(DefaultLookbackBars, _limits.MaxCandlesPerRequest)}.", defaultValue: Math.Min(DefaultLookbackBars, _limits.MaxCandlesPerRequest), minimum: 1, maximum: _limits.MaxCandlesPerRequest)
        .Integer(TailArgument, $"Trailing bars returned when includeSeries is true (1..{_limits.MaxBatchIndicatorTailBars}). Default {DefaultTailBars}.", defaultValue: DefaultTailBars, minimum: 1, maximum: _limits.MaxBatchIndicatorTailBars)
        .Boolean(SeriesOutputOptions.IncludeSeriesArgument, "true adds series/projectedValues/timestamps. Default false (latestValues only).")
        .Boolean(SeriesOutputOptions.SummaryArgument, "true adds per-series summary. Default false.")
        .Integer(SeriesOutputOptions.TrendBarsArgument, $"Summary trendBars ({StockAnalyzer.Core.MathUtils.SeriesSummaryStatistics.MinTrendBars}..{McpLimitsContract.EffectiveMaxTrendBars(_limits)}; needs summary true). Default {McpLimitsContract.EffectiveDefaultTrendBars(_limits)}.", defaultValue: McpLimitsContract.EffectiveDefaultTrendBars(_limits), minimum: StockAnalyzer.Core.MathUtils.SeriesSummaryStatistics.MinTrendBars, maximum: McpLimitsContract.EffectiveMaxTrendBars(_limits))
        .Integer(SeriesOutputOptions.SummaryBarsArgument, SeriesOutputOptions.SummaryBarsDescription(_limits), minimum: StockAnalyzer.Core.MathUtils.SeriesSummaryStatistics.MinTrendBars, maximum: McpLimitsContract.EffectiveMaxSummaryBars(_limits))
        .Boolean(SeriesOutputOptions.SummaryDistributionArgument, SeriesOutputOptions.SummaryDistributionDescription)
        .IntegerArray(SeriesOutputOptions.LookbackOffsetsArgument, $"Non-negative bar offsets back from last bar for pastValues (max {_limits.MaxLookbackOffsets}).")
        .Integer(SeriesOutputOptions.DecimalPlacesArgument, SeriesOutputOptions.DecimalPlacesDescription("output decimals"), minimum: 0, maximum: SeriesOutputOptions.MaxDecimalPlaces)
        .String(EndDateArgument, $"Inclusive end date ({ToolJson.DateFormat}). Default: latest bar.")
        .Boolean(PartialBarOutput.CompleteBarsOnlyArgument, "W1/MN1 only: true drops incomplete last period; both values add partial-bar keys.")
        .Boolean(SymbolResolver.AllowUnregisteredArgument, "Allow unregistered symbols with stored daily bars. Default false.")
        .Build();

    /// <summary>The outcome of one requested symbol: its evaluation, or the reason it has none.</summary>
    private sealed record Item(string Symbol, IndicatorEvaluation? Evaluation, List<string> Warnings, string? ErrorCode, string? ErrorMessage);

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        bool allowUnregistered = ToolArgs.Boolean(arguments, SymbolResolver.AllowUnregisteredArgument) ?? false;
        TimeFrame timeFrame = ToolArgs.SupportedTimeFrame(arguments, TimeFrameArgument);
        int lookback = ToolArgs.PositiveWithin(arguments, LookbackArgument, Math.Min(DefaultLookbackBars, _limits.MaxCandlesPerRequest), _limits.MaxCandlesPerRequest);
        int tail = ToolArgs.PositiveWithin(arguments, TailArgument, DefaultTailBars, _limits.MaxBatchIndicatorTailBars);
        DateOnly? endDate = ToolArgs.Date(arguments, EndDateArgument);
        bool? completeBarsOnly = ToolArgs.Boolean(arguments, PartialBarOutput.CompleteBarsOnlyArgument);

        JsonElement indicators = arguments.GetProperty(IndicatorsArgument);
        _indicators.CheckIndicatorCount(indicators);

        SeriesOutputOptions output = SeriesOutputOptions.Read(arguments, _limits, includeSeriesDefault: false, compactDefaults: _compactDefaults);
        if (!output.IncludeSeries && ToolArgs.Integer(arguments, TailArgument) is not null)
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"'{TailArgument}' chooses how many bars the series arrays hold and has no effect when '{SeriesOutputOptions.IncludeSeriesArgument}' is false.",
                TailArgument,
                $"Set {SeriesOutputOptions.IncludeSeriesArgument} to true, or omit {TailArgument}.");
        }

        (List<string> requested, string? groupId) = await ResolveRequestedSymbolsAsync(arguments, cancellationToken).ConfigureAwait(false);

        // One bind of the indicators and of the derived series, only to refuse a malformed request (and to find the comparison symbols) before any symbol is read.
        var callWarnings = new List<string>();
        (List<RequestedIndicator> probeRequests, _) = _indicators.PlanIndicators(arguments, indicators, timeFrame);
        List<string> comparisonSymbols = await _indicators.ResolveComparisonSymbolsAsync(probeRequests, string.Empty, allowUnregistered, callWarnings, cancellationToken).ConfigureAwait(false);

        // The comparison symbols are read once per window end for the whole call (U-04). The call's own window end is read now; a completeBarsOnly cut of another end is read when a symbol needs it.
        var caches = new Dictionary<(bool HasEnd, DateOnly End), CachedComparisonDataService>();
        Func<DateOnly?, IDataService>? comparisonData = null;
        if (comparisonSymbols.Count > 0)
        {
            IDataService ServiceFor(DateOnly? end)
            {
                var key = (end.HasValue, end ?? default);
                if (!caches.TryGetValue(key, out CachedComparisonDataService? cache))
                {
                    cache = new CachedComparisonDataService(_indicators.CreateComparisonDataService(end), timeFrame, lookback, comparisonSymbols);
                    caches[key] = cache;
                }

                return cache;
            }

            comparisonData = ServiceFor;
            await ((CachedComparisonDataService)ServiceFor(endDate)).PreloadAsync().ConfigureAwait(false);
        }

        var items = new List<Item>(requested.Count);
        foreach (string requestedSymbol in requested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(await EvaluateSymbolAsync(
                requestedSymbol, arguments, indicators, timeFrame, lookback, endDate, completeBarsOnly, allowUnregistered, comparisonData, cancellationToken).ConfigureAwait(false));
        }

        int succeeded = items.Count(item => item.Evaluation is not null);
        return ToolJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("timeFrame", timeFrame.ToString());
            if (endDate is not null)
            {
                writer.WriteString("endDate", endDate.Value.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture));
            }

            if (groupId is not null)
            {
                writer.WriteString("groupId", groupId);
            }

            writer.WriteString("periodUnit", GetCapabilitiesTool.PeriodUnit);
            writer.WriteString("timestampFormat", ToolJson.DateFormat);
            writer.WriteNumber("lookbackBars", lookback);
            writer.WriteNumber("symbolCount", items.Count);
            writer.WriteNumber("succeededCount", succeeded);
            writer.WriteNumber("failedCount", items.Count - succeeded);
            writer.WriteStartArray("warnings");
            foreach (string warning in callWarnings)
            {
                writer.WriteStringValue(warning);
            }

            writer.WriteEndArray();
            writer.WriteStartArray("items");
            foreach (Item item in items)
            {
                writer.WriteStartObject();
                writer.WriteString("symbol", item.Symbol);
                writer.WriteBoolean("isSuccessful", item.Evaluation is not null);
                if (item.Evaluation is { } evaluation)
                {
                    _indicators.WriteEvaluation(writer, evaluation, tail, output, item.Warnings);
                }
                else
                {
                    writer.WriteString("errorCode", item.ErrorCode);
                    writer.WriteString("errorMessage", item.ErrorMessage);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    /// <summary>
    /// One symbol: resolved, its indicators bound afresh (the settings are changed by the calculation) and evaluated by the single-symbol code. A refusal that belongs to this symbol
    /// (it is not available, it has no stored bars) becomes its item; anything else, and a cancellation, ends the call.
    /// </summary>
    private async Task<Item> EvaluateSymbolAsync(
        string requestedSymbol,
        JsonElement arguments,
        JsonElement indicators,
        TimeFrame timeFrame,
        int lookback,
        DateOnly? endDate,
        bool? completeBarsOnly,
        bool allowUnregistered,
        Func<DateOnly?, IDataService>? comparisonData,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        try
        {
            ResolvedSymbol resolved = await _symbols.ResolveAsync(requestedSymbol, SymbolsArgument, allowUnregistered, cancellationToken).ConfigureAwait(false);
            if (!resolved.IsRegistered)
            {
                warnings.Add(SymbolResolver.OffListWarning(new[] { resolved.Symbol }));
            }

            (List<RequestedIndicator> requests, IReadOnlyList<DerivedSeriesRequest> derived) = _indicators.PlanIndicators(arguments, indicators, timeFrame);
            List<string> comparisonSymbols = await _indicators.ResolveComparisonSymbolsAsync(requests, resolved.Symbol, allowUnregistered, new List<string>(), cancellationToken).ConfigureAwait(false);
            IndicatorEvaluation evaluation = await _indicators.EvaluateAsync(
                resolved.Symbol, timeFrame, lookback, endDate, completeBarsOnly, requests, derived, comparisonSymbols, warnings, comparisonData, cancellationToken).ConfigureAwait(false);
            return new Item(resolved.Symbol, evaluation, warnings, null, null);
        }
        catch (ToolException error) when (error.Code is ToolErrorCodes.SymbolNotFound or ToolErrorCodes.InsufficientMarketData)
        {
            return new Item(requestedSymbol, null, warnings, error.Code, _sanitizer.Sanitize(error.Message));
        }
    }

    /// <summary>The symbols of the call in request order, without repeats: named, group members, or a stored result handle. Exactly one of the three must be given.</summary>
    private async Task<(List<string> Symbols, string? GroupId)> ResolveRequestedSymbolsAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        IReadOnlyList<string>? named = ToolArgs.StringArray(arguments, SymbolsArgument);
        string? groupIdText = ToolArgs.String(arguments, GroupIdArgument)?.Trim();
        string? resultId = ToolArgs.String(arguments, McpResultHandleStore.SymbolsFromResultIdArgument);
        int sourceCount = (named is not null ? 1 : 0) + (groupIdText is not null ? 1 : 0) + (resultId is not null ? 1 : 0);
        if (sourceCount != 1)
        {
            string parameter = sourceCount > 1 && resultId is not null ? McpResultHandleStore.SymbolsFromResultIdArgument : SymbolsArgument;
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"Send exactly one of '{SymbolsArgument}', '{GroupIdArgument}' and '{McpResultHandleStore.SymbolsFromResultIdArgument}'.",
                parameter,
                "Name the symbols, the group, or a stored result handle.");
        }

        (int sliceOffset, int? sliceLimit) = McpResultHandleStore.ReadSliceArguments(arguments, resultId, 1, _limits.MaxBatchIndicatorSymbols);
        if (resultId is not null)
        {
            IReadOnlyList<string> stored = _resultHandles.ResolveSymbolsSlice(resultId, 1, _limits.MaxBatchIndicatorSymbols, sliceOffset, sliceLimit);
            return (stored.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), null);
        }

        var distinct = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (named is not null)
        {
            if (named.Count > _limits.MaxBatchIndicatorSymbols)
            {
                throw new ToolException(
                    ToolErrorCodes.LimitExceeded,
                    $"At most {_limits.MaxBatchIndicatorSymbols} symbols per call.",
                    SymbolsArgument,
                    $"Send {_limits.MaxBatchIndicatorSymbols} or fewer symbols, or split the call.");
            }

            for (int i = 0; i < named.Count; i++)
            {
                string canonical = SymbolResolver.Canonical(named[i], $"{SymbolsArgument}[{i}]");
                if (seen.Add(canonical))
                {
                    distinct.Add(canonical);
                }
            }

            return (distinct, null);
        }

        ResolvedGroup group = await GroupMembersLookup.ResolveAsync(_groupResolver, groupIdText!, GroupIdArgument, _limits.MaxFilterEvaluationSymbols, cancellationToken).ConfigureAwait(false);
        if (group.Members.Symbols.Count > _limits.MaxBatchIndicatorSymbols)
        {
            throw new ToolException(
                ToolErrorCodes.LimitExceeded,
                $"The group contains {group.Members.Symbols.Count} symbols, more than the limit of {_limits.MaxBatchIndicatorSymbols}.",
                GroupIdArgument,
                "Use a smaller group, or name the symbols in several calls.");
        }

        foreach (string symbol in group.Members.Symbols)
        {
            if (seen.Add(symbol))
            {
                distinct.Add(symbol);
            }
        }

        if (distinct.Count == 0)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, "The group contains no symbols.", GroupIdArgument);
        }

        return (distinct, group.Id.ToString());
    }
}
