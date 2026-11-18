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

    public CalculateIndicatorsBatchTool(CalculateIndicatorsTool indicators, SymbolResolver symbols, ITickerGroupResolver groupResolver, ErrorMessageSanitizer sanitizer, McpLimits limits)
    {
        _indicators = indicators;
        _symbols = symbols;
        _groupResolver = groupResolver;
        _sanitizer = sanitizer;
        _limits = limits;
    }

    public string Name => "sa_calculate_indicators_batch";

    public string Description =>
        "Calculates the same technical indicators on many symbols in one call, with exactly the pipeline, arguments and per-indicator results of sa_calculate_indicators " +
        "(call sa_get_capabilities first for type names, parameters and output series). " +
        $"Name the symbols (1 to {_limits.MaxBatchIndicatorSymbols}) or one saved ticker group (groupId); not both. " +
        $"The answer is compact by default: per symbol only the latest values (latestValues, leadingNullCount) are returned; includeSeries true adds the last outputTailCount bars (default {DefaultTailBars}, at most {_limits.MaxBatchIndicatorTailBars}) per series. " +
        $"The calculation runs over lookbackBars stored bars per symbol (default {Math.Min(DefaultLookbackBars, _limits.MaxCandlesPerRequest)}, at most {_limits.MaxCandlesPerRequest}). " +
        "Items come in the order of the request. A symbol that is not available or has no stored bars is reported in its own item (isSuccessful false, errorCode, errorMessage) and does not fail the call; " +
        "an indicator that cannot be calculated is reported inside its result, as in sa_calculate_indicators. A malformed request (arguments, indicator parameters, a missing comparison symbol) fails the whole call before any symbol is evaluated. " +
        $"Cross-ticker indicators (ComparisonSymbol): at most {_limits.MaxSecondarySymbolsPerCall} different comparison symbols per call; each is read once and shared by all symbols. " +
        "Indicators with their own timeFrame, derived series, summary, trendBars, lookbackOffsets, decimalPlaces, endDate, completeBarsOnly and allowUnregistered work as in sa_calculate_indicators. " +
        "Read-only; heavy (it uses the exclusive tool turn).";

    public bool IsHeavy => true;

    public string? ExampleArgumentsJson =>
        "{\"symbols\":[\"MSFT\",\"7203-T\"],\"timeFrame\":\"D1\",\"indicators\":[{\"id\":\"rsi\",\"type\":\"RSI\",\"parameters\":{\"Period\":14}}]}";

    public string InputSchemaJson => ToolSchema.Object()
        .StringArray(SymbolsArgument, $"Symbols to evaluate (1 to {_limits.MaxBatchIndicatorSymbols}); '.' and '-' are interchangeable. Mutually exclusive with {GroupIdArgument}; one of them is required.", minItems: 1)
        .String(GroupIdArgument, "Guid of a saved ticker group (from sa_list_ticker_groups) whose members are evaluated. Mutually exclusive with symbols.")
        .String(TimeFrameArgument, $"Time frame of the bars. Supported: {ToolArgs.SupportedTimeFrameNames}.", required: true, allowedValues: Enum.GetNames<TimeFrame>())
        .ObjectArray(IndicatorsArgument, $"The indicators to calculate on every symbol (1 to {_limits.MaxIndicatorsPerCall}); the entries are those of sa_calculate_indicators.", IndicatorItemSchema(), required: true, minItems: 1)
        .ObjectArray(DerivedArgument, $"Optional series computed from two series of the call (at most {_limits.MaxDerivedSeriesPerCall}), as in sa_calculate_indicators. Omit or send [] for none.", DerivedItemSchema())
        .Integer(LookbackArgument, $"Stored bars each symbol's calculation runs over, newest last. Default {Math.Min(DefaultLookbackBars, _limits.MaxCandlesPerRequest)}, maximum {_limits.MaxCandlesPerRequest}.", defaultValue: Math.Min(DefaultLookbackBars, _limits.MaxCandlesPerRequest), minimum: 1, maximum: _limits.MaxCandlesPerRequest)
        .Integer(TailArgument, $"Trailing bars returned per series (needs includeSeries true). Default {DefaultTailBars}, at most {_limits.MaxBatchIndicatorTailBars}.", defaultValue: DefaultTailBars, minimum: 1, maximum: _limits.MaxBatchIndicatorTailBars)
        .Boolean(SeriesOutputOptions.IncludeSeriesArgument, "true adds series, projectedValues and timestamps (the last outputTailCount bars) to every symbol. Default false: only latestValues, leadingNullCount (and summary, pastValues when asked for).")
        .Boolean(SeriesOutputOptions.SummaryArgument, "true adds the summary of every series, as in sa_calculate_indicators. Default false.")
        .Integer(SeriesOutputOptions.TrendBarsArgument, $"Trend window, in bars, of the summary's trendSlope (needs summary true). Default {McpLimitsContract.EffectiveDefaultTrendBars(_limits)}, from {StockAnalyzer.Core.MathUtils.SeriesSummaryStatistics.MinTrendBars} to {McpLimitsContract.EffectiveMaxTrendBars(_limits)}.", defaultValue: McpLimitsContract.EffectiveDefaultTrendBars(_limits), minimum: StockAnalyzer.Core.MathUtils.SeriesSummaryStatistics.MinTrendBars, maximum: McpLimitsContract.EffectiveMaxTrendBars(_limits))
        .Boolean(SeriesOutputOptions.SummaryDistributionArgument, SeriesOutputOptions.SummaryDistributionDescription)
        .IntegerArray(SeriesOutputOptions.LookbackOffsetsArgument, $"Offsets (0 or more, at most {_limits.MaxLookbackOffsets}) of bars before the last calculated bar: adds pastValues, as in sa_calculate_indicators.")
        .Integer(SeriesOutputOptions.DecimalPlacesArgument, $"Round every decimal number of the result to this many decimal places (0 to {SeriesOutputOptions.MaxDecimalPlaces}; away from zero at the midpoint). Counts are never rounded. Default: numbers as calculated.", minimum: 0, maximum: SeriesOutputOptions.MaxDecimalPlaces)
        .String(EndDateArgument, $"Last date to include ({ToolJson.DateFormat}). If specified, the latest bar of every symbol is the last bar dated on or before it. Default: the latest stored bar.")
        .Boolean(PartialBarOutput.CompleteBarsOnlyArgument, "W1 and MN1 only (ignored for D1): true leaves out a running last period; either value adds the partial-bar keys to every item, as in sa_calculate_indicators. Not given: nothing changes.")
        .Boolean(SymbolResolver.AllowUnregisteredArgument, "Accept symbols (and comparison symbols) that are not in the ticker list but have stored daily bars. Default false.")
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

        SeriesOutputOptions output = SeriesOutputOptions.Read(arguments, _limits, includeSeriesDefault: false);
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

    /// <summary>The symbols of the call in request order, without repeats: the named ones (shape checked) or the members of the group. Exactly one of the two must be given.</summary>
    private async Task<(List<string> Symbols, string? GroupId)> ResolveRequestedSymbolsAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        IReadOnlyList<string>? named = ToolArgs.StringArray(arguments, SymbolsArgument);
        string? groupIdText = ToolArgs.String(arguments, GroupIdArgument)?.Trim();
        if ((named is null) == (groupIdText is null))
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"Send exactly one of '{SymbolsArgument}' and '{GroupIdArgument}'.",
                SymbolsArgument,
                "Name the symbols, or the group whose members are evaluated.");
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
