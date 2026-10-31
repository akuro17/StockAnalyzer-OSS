using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// sa_calculate_indicators: runs the application's own analysis pipeline on the stored bars of one symbol. The tool only maps the
/// request to indicator settings and the result to JSON; it contains no indicator formula.
/// </summary>
internal sealed class CalculateIndicatorsTool : IMcpTool
{
    private const string SymbolArgument = "symbol";
    private const string TimeFrameArgument = "timeFrame";
    private const string IndicatorsArgument = "indicators";
    private const string DerivedArgument = "derived";
    private const string LookbackArgument = "lookbackBars";
    private const string TailArgument = "outputTailCount";
    private const string EndDateArgument = "endDate";
    private const string AsOfDateArgument = "asOfDate";
    private const string ComparisonSymbolName = "ComparisonSymbol";
    private const string IndicatorTimeFrameKey = "timeFrame";
    private const string AlignmentKey = "alignment";
    private const string DerivedType = "Derived";

    /// <summary>The alignment of an indicator calculated on a coarser time frame: the value of the last period that had closed when the bar began (<see cref="TimeframeAlignment"/>).</summary>
    internal const string PreviousClosedPeriod = "PreviousClosedPeriod";

    /// <summary>The reason of a result in which every value is null (plan S-10): the history may be shorter than the warm-up, or a signal-type indicator found nothing.</summary>
    internal const string AllNullMessage = "No value could be calculated: every value is null (the stored history may be shorter than the indicator's warm-up, or the indicator found nothing to report).";

    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly SymbolResolver _symbols;
    private readonly CandleWindowLoader _loader;
    private readonly IndicatorCatalog _catalog;
    private readonly IndicatorSettingsBinder _binder;
    private readonly IndicatorChainPlan _chain;
    private readonly ErrorMessageSanitizer _sanitizer;
    private readonly McpLimits _limits;

    public CalculateIndicatorsTool(
        IReadOnlyStockAnalyzerGateway gateway,
        SymbolResolver symbols,
        IndicatorCatalog catalog,
        IndicatorSettingsBinder binder,
        IndicatorChainPlan chain,
        ErrorMessageSanitizer sanitizer,
        McpLimits limits)
    {
        _gateway = gateway;
        _symbols = symbols;
        _loader = new CandleWindowLoader(gateway);
        _catalog = catalog;
        _binder = binder;
        _chain = chain;
        _sanitizer = sanitizer;
        _limits = limits;
    }

    public string Name => "sa_calculate_indicators";

    public string Description =>
        "Calculates technical indicators on the stored bars of one symbol with the application's own indicator pipeline (the same values the chart shows). " +
        "Call sa_get_capabilities with indicatorTypeFilter first to learn the exact type names, parameter names with their ranges, and output series names. " +
        $"Up to {_limits.MaxIndicatorsPerCall} indicators per call; each has a client-chosen unique id and the results are keyed by that id. " +
        "Omitted parameters use the application defaults. All periods are in bars of the indicator's own time frame (the call's time frame unless the indicator carries its own timeFrame, then bars of that time frame, for example 5 weekly bars for W1; periodUnit is always bars). " +
        $"The calculation runs over up to lookbackBars stored bars (default and maximum {_limits.MaxCandlesPerRequest}) so that long-period indicators are warmed up; " +
        $"only the last outputTailCount bars (default {_limits.DefaultOutputTailBars}, at most {_limits.MaxOutputTailBars}) are returned, aligned with the timestamps array. " +
        $"endDate ({ToolJson.DateFormat}, inclusive) ends the calculation at the last bar dated on or before it, so the latest bar and every value are as they were known on that date " +
        "(for W1 and MN1 the daily bars are cut at endDate before the period bars are built; the window is then the latest lookbackBars bars up to endDate); " +
        "comparison symbols are cut at the same date, no bar after it is read, and endDate is echoed in the result only when given. " +
        "A leading null means the indicator has not accumulated enough bars yet (leadingNullCount counts them over the whole calculated history). " +
        "Some indicators (for example Ichimoku SenkouSpanA and SenkouSpanB) also project values beyond the last bar; those are returned in projectedValues and belong to no bar yet. " +
        "nonCausalSeries lists series whose value at a bar depends on later bars. " +
        "Indicator on indicator: sourceIndicatorId feeds one output series (sourceOutputSeriesName) of another indicator of the same call as the price input, " +
        "dynamicPeriodIndicatorId feeds one as adaptive periods; one upstream indicator can be asked for one series only. " +
        "Cross-ticker indicators need the ComparisonSymbol parameter. " +
        $"An indicator may carry its own {IndicatorTimeFrameKey} ({ToolArgs.SupportedTimeFrameNames}; the call's time frame or a coarser one, a finer one is UNSUPPORTED_TIMEFRAME): it is then calculated on the bars of that time frame " +
        "(up to the last day of the call's window, at most the maximum number of bars; lookbackBars applies to the call's bars only) and aligned to the call's timestamps, so the value shown at a bar is that of the previous CLOSED period containing the bar " +
        "(a W1 value at a daily bar is last week's, never the running week's; before the first closed period it is null). " +
        $"One call can therefore return D1, W1 and MN1 values of an indicator on the daily timestamps. {AlignmentKey} {PreviousClosedPeriod} and the indicator's {IndicatorTimeFrameKey} are echoed for such an indicator; it has no projectedValues. " +
        $"An indicator and the indicators it reads (sourceIndicatorId, dynamicPeriodIndicatorId) must have the same {IndicatorTimeFrameKey}, and a cross-ticker indicator keeps the call's time frame. " +
        "allowUnregistered true also accepts a symbol (and comparison symbols) that is not in the ticker list but has stored daily bars; it is named in warnings, and a symbol without stored bars is SYMBOL_NOT_FOUND. " +
        $"{DerivedArgument} (optional, at most {_limits.MaxDerivedSeriesPerCall}) adds series computed bar by bar from two series of the same call: {{{DerivedSeriesPlan.IdKey}, {DerivedSeriesPlan.OperationKey}, {DerivedSeriesPlan.LeftKey}, {DerivedSeriesPlan.RightKey}}} with {DerivedSeriesPlan.OperationKey} one of {string.Join(", ", Enum.GetNames<SeriesOperation>())} (exact names) " +
        $"and each side {{\"{DerivedSeriesPlan.ReferenceKey}\":\"<id>\"}} (an indicator id, or a derived id declared before; {{\"{DerivedSeriesPlan.ReferenceKey}\":\"<id>\",\"{DerivedSeriesPlan.SeriesNameKey}\":\"<name>\"}} picks another output series of an indicator, default its main series) or {{\"{DerivedSeriesPlan.ConstantKey}\":<number>}}. " +
        "A bar has no value (null) when either side has none, and Divide by zero gives null and is counted in divisionByZeroCount (never an infinite value). A derived series is returned in results under its id with type Derived, " +
        "one series Main, latestValues, leadingNullCount, nonCausalSeries (Main when an input looks ahead) and unit null (the unit of a derived series is not defined); one whose input did not succeed reports isSuccessful false and names that input, the call itself still succeeds. " +
        "Derived series are output only: they cannot be used in screener or backtest conditions. " +
        $"{SeriesOutputOptions.SummaryArgument} true adds, per series, count (non-null values), latest (the last bar), mean, stdDev (population), zScoreOfLatest ((latest - mean) / stdDev; null when stdDev is below {ComparisonSeriesDefaults.FlatWindowDeviationEpsilon.ToString("0.#######E+0", CultureInfo.InvariantCulture)}, an absolute value, or latest is null), " +
        "percentileRankOfLatest (percent, 0-100: earlier non-null values strictly smaller than latest / earlier non-null values; null without earlier values), trendSlope (least-squares slope per bar over the last trendBars bars, each value at its own bar position, null below two values) and trendBars; " +
        "mean, stdDev, latest and trendSlope are in the series' own unit, zScoreOfLatest has none and percentileRankOfLatest is a percentage; they are computed over the whole calculated window, not only the returned tail. " +
        "trendBars and the offsets count bars of the call's time frame (an indicator with its own timeFrame is already aligned to those bars); a series listed in nonCausalSeries keeps its look-ahead in its summary and pastValues. " +
        $"{SeriesOutputOptions.LookbackOffsetsArgument} adds pastValues (value k bars before the last calculated bar), {SeriesOutputOptions.IncludeSeriesArgument} false returns only the compact figures (latestValues, summary, pastValues) to save tokens. " +
        "Prices are in the instrument's units; the application stores no output unit per indicator (see unitUndefined in sa_get_capabilities).";

    public bool IsHeavy => false;

    public IReadOnlyDictionary<string, string>? MisnamedArgumentHints { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [AsOfDateArgument] = $"This tool names the last-bar date '{EndDateArgument}' (sa_run_screener uses '{AsOfDateArgument}').",
    };

    public string? ExampleArgumentsJson => "{\"symbol\":\"7203-T\",\"timeFrame\":\"D1\",\"indicators\":[{\"id\":\"sma25\",\"type\":\"SMA\",\"parameters\":{\"Period\":25}}],\"outputTailCount\":200}";

    public string InputSchemaJson => ToolSchema.Object()
        .String(SymbolArgument, "Ticker symbol; '.' and '-' are interchangeable (7203.T and 7203-T are the same).", required: true)
        .String(TimeFrameArgument, $"Time frame of the bars. Supported: {ToolArgs.SupportedTimeFrameNames}.", required: true, allowedValues: Enum.GetNames<TimeFrame>())
        .ObjectArray(
            IndicatorsArgument,
            $"The indicators to calculate (1 to {_limits.MaxIndicatorsPerCall}).",
            ToolSchema.Object()
                .String("id", "Unique id chosen by the client; the result is keyed by it.", required: true)
                .String("type", "Indicator type name from sa_get_capabilities (case-insensitive), for example RSI.", required: true)
                .FreeObject("parameters", "Parameter values by name (case-insensitive), for example {\"Period\": 14}. Names, types and ranges: sa_get_capabilities.")
                .String(IndicatorTimeFrameKey, $"Time frame of the bars this indicator is calculated on: the call's timeFrame (same as omitted) or a coarser one ({ToolArgs.SupportedTimeFrameNames}). A coarser one is aligned to the call's timestamps as the value of the previous closed period.", allowedValues: Enum.GetNames<TimeFrame>())
                .String("priceSource", "Price the indicator reads (for example Close). Affects indicators that work on one price series. Default: the indicator's default.", allowedValues: Enum.GetNames<PriceType>())
                .String("sourceIndicatorId", "Id of another indicator in this call whose output series is this indicator's input instead of the price.")
                .String("sourceOutputSeriesName", "Output series of the source indicator (default: its main series).")
                .String("dynamicPeriodIndicatorId", "Id of another indicator in this call whose output series drives this indicator's adaptive period.")
                .String("dynamicPeriodOutputSeriesName", "Output series of the dynamic-period indicator (default: its main series)."),
            required: true,
            minItems: 1)
        .ObjectArray(
            DerivedArgument,
            $"Optional series computed from two series of this call (at most {_limits.MaxDerivedSeriesPerCall}); a derived series may read the ones declared before it. Omit or send [] for none.",
            ToolSchema.Object()
                .String(DerivedSeriesPlan.IdKey, "Unique id chosen by the client (unique across indicators and derived series); the result is keyed by it.", required: true)
                .String(DerivedSeriesPlan.OperationKey, $"The operation (exact name): {string.Join(", ", Enum.GetNames<SeriesOperation>())}.", required: true, allowedValues: Enum.GetNames<SeriesOperation>())
                .FreeObject(DerivedSeriesPlan.LeftKey, $"{{\"{DerivedSeriesPlan.ReferenceKey}\":\"<indicator or earlier derived id>\"}} (+ optional \"{DerivedSeriesPlan.SeriesNameKey}\" for an indicator) or {{\"{DerivedSeriesPlan.ConstantKey}\":<number>}}.", required: true)
                .FreeObject(DerivedSeriesPlan.RightKey, $"Same forms as {DerivedSeriesPlan.LeftKey}.", required: true))
        .Integer(LookbackArgument, $"Stored bars the calculation runs over, newest last. Default and maximum {_limits.MaxCandlesPerRequest}.", defaultValue: _limits.MaxCandlesPerRequest, minimum: 1, maximum: _limits.MaxCandlesPerRequest)
        .Integer(TailArgument, $"Number of trailing bars returned per series. Default {_limits.DefaultOutputTailBars}, at most {_limits.MaxOutputTailBars}.", defaultValue: _limits.DefaultOutputTailBars, minimum: 1, maximum: _limits.MaxOutputTailBars)
        .String(EndDateArgument, $"Last date to include ({ToolJson.DateFormat}). If specified, the latest bar is the last bar dated on or before it. Default: the latest stored bar.")
        .Boolean(SeriesOutputOptions.SummaryArgument, "true adds summary to every returned series of every result (indicators and derived): count, latest, mean, stdDev (population, divisor n), zScoreOfLatest, percentileRankOfLatest, trendSlope, trendBars, all over the whole calculated window. Default false.")
        .Integer(SeriesOutputOptions.TrendBarsArgument, $"Trend window, in bars, of the summary's trendSlope (needs summary true). Default {McpLimitsContract.EffectiveDefaultTrendBars(_limits)}, from {SeriesSummaryStatistics.MinTrendBars} to {McpLimitsContract.EffectiveMaxTrendBars(_limits)}.", defaultValue: McpLimitsContract.EffectiveDefaultTrendBars(_limits), minimum: SeriesSummaryStatistics.MinTrendBars, maximum: McpLimitsContract.EffectiveMaxTrendBars(_limits))
        .IntegerArray(SeriesOutputOptions.LookbackOffsetsArgument, $"Offsets (0 or more, at most {_limits.MaxLookbackOffsets}) of bars before the last calculated bar: adds pastValues with the value of every returned series that many bars back (0 = the last bar). A bar that does not exist or has no value gives null.")
        .Boolean(SeriesOutputOptions.IncludeSeriesArgument, "false leaves out series, projectedValues and timestamps (latestValues, leadingNullCount, summary and pastValues stay); it needs summary true or lookbackOffsets, cannot be combined with outputTailCount, and returnedBarCount is then 0. Default true.")
        .Boolean(SymbolResolver.AllowUnregisteredArgument, "Accept a symbol (and comparison symbols) that is not in the ticker list but has stored daily bars. Default false.")
        .Build();

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        bool allowUnregistered = ToolArgs.Boolean(arguments, SymbolResolver.AllowUnregisteredArgument) ?? false;
        var warnings = new List<string>();
        ResolvedSymbol resolved = await _symbols.ResolveAsync(ToolArgs.String(arguments, SymbolArgument)!, SymbolArgument, allowUnregistered, cancellationToken).ConfigureAwait(false);
        string symbol = resolved.Symbol;
        if (!resolved.IsRegistered)
        {
            warnings.Add(SymbolResolver.OffListWarning(new[] { symbol }));
        }

        TimeFrame timeFrame = ToolArgs.SupportedTimeFrame(arguments, TimeFrameArgument);
        int lookback = ToolArgs.PositiveWithin(arguments, LookbackArgument, _limits.MaxCandlesPerRequest, _limits.MaxCandlesPerRequest);
        int tail = ToolArgs.PositiveWithin(arguments, TailArgument, _limits.DefaultOutputTailBars, _limits.MaxOutputTailBars);
        DateOnly? endDate = ToolArgs.Date(arguments, EndDateArgument);

        JsonElement indicators = arguments.GetProperty(IndicatorsArgument);
        if (indicators.GetArrayLength() > _limits.MaxIndicatorsPerCall)
        {
            throw new ToolException(
                ToolErrorCodes.LimitExceeded,
                $"At most {_limits.MaxIndicatorsPerCall} indicators per call.",
                IndicatorsArgument,
                $"Send {_limits.MaxIndicatorsPerCall} or fewer indicators, or split the call.");
        }

        SeriesOutputOptions output = SeriesOutputOptions.Read(arguments, _limits);
        if (!output.IncludeSeries && ToolArgs.Integer(arguments, TailArgument) is not null)
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"'{TailArgument}' chooses how many bars the series arrays hold and has no effect when '{SeriesOutputOptions.IncludeSeriesArgument}' is false.",
                TailArgument,
                $"Set {SeriesOutputOptions.IncludeSeriesArgument} to true, or omit {TailArgument}.");
        }

        List<RequestedIndicator> requests = Bind(indicators, timeFrame);
        _chain.Apply(requests);
        IReadOnlyList<DerivedSeriesRequest> derived = arguments.TryGetProperty(DerivedArgument, out JsonElement derivedElement)
            ? DerivedSeriesPlan.Parse(derivedElement, requests, _chain, _limits)
            : Array.Empty<DerivedSeriesRequest>();
        List<string> comparisonSymbols = await ResolveComparisonSymbolsAsync(requests, symbol, allowUnregistered, warnings, cancellationToken).ConfigureAwait(false);

        List<CandleData> loaded;
        bool droppedLeadingBar;
        if (endDate is { } asOf)
        {
            // The gateway reads the whole stored daily history and cuts it at the date, so no leading week or month is truncated by the load.
            loaded = await _loader.LoadUpToAsync(symbol, timeFrame, asOf, lookback, cancellationToken).ConfigureAwait(false);
            droppedLeadingBar = false;
            if (loaded.Count == 0)
            {
                throw new ToolException(
                    ToolErrorCodes.InsufficientMarketData,
                    $"No stored {timeFrame} bars exist on or before {asOf.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture)} for {symbol}.",
                    EndDateArgument);
            }
        }
        else
        {
            (loaded, droppedLeadingBar) = await _loader.LoadAsync(symbol, timeFrame, lookback, cancellationToken).ConfigureAwait(false);
            if (loaded.Count == 0)
            {
                throw new ToolException(ToolErrorCodes.InsufficientMarketData, $"No stored {timeFrame} bars exist for {symbol}.", SymbolArgument);
            }
        }

        List<CoreCandleData> candles = loaded.Select(ToCore).ToList();
        IReadOnlyDictionary<string, IReadOnlyList<CoreCandleData?>>? secondary =
            await AlignComparisonAsync(symbol, comparisonSymbols, timeFrame, candles.Count, endDate, warnings, cancellationToken).ConfigureAwait(false);

        var excluded = new HashSet<string>(
            comparisonSymbols.Where(c => secondary is null || !secondary.ContainsKey(c)),
            StringComparer.OrdinalIgnoreCase);

        cancellationToken.ThrowIfCancellationRequested();

        await LoadOwnBarsAsync(symbol, requests, DateOnly.FromDateTime(loaded[^1].Timestamp), cancellationToken).ConfigureAwait(false);

        // The pipeline cannot be cancelled: the call is awaited without the token so that a calculation that outlives its time limit
        // keeps the turn until it ends (the executor has already answered EXECUTION_TIMEOUT by then).
        // The indicators of the call's own bars are one calculation exactly as before; each coarser time frame is one more calculation on its own bars.
        var results = new Dictionary<string, IIndicatorResult>();
        List<RequestedIndicator> ownBarRequests = requests.Where(r => r.OwnTimeFrame is null).ToList();
        if (ownBarRequests.Count > 0)
        {
            Merge(results, await _gateway.CalculateIndicatorsAsync(candles, ownBarRequests.Select(r => r.Settings).ToList(), secondary).ConfigureAwait(false));
        }

        foreach (IGrouping<TimeFrame, RequestedIndicator> frame in requests.Where(r => r.OwnTimeFrame is not null).GroupBy(r => r.OwnTimeFrame!.Value))
        {
            List<CoreCandleData> frameCandles = frame.First().OwnBars!.Select(ToCore).ToList();
            Merge(results, await _gateway.CalculateIndicatorsAsync(frameCandles, frame.Select(r => r.Settings).ToList(), null).ConfigureAwait(false));
        }

        return ToolJson.Build(writer => WriteResult(writer, symbol, timeFrame, endDate, lookback, loaded, droppedLeadingBar, tail, requests, derived, output, results, warnings, excluded));
    }

    private static void Merge(Dictionary<string, IIndicatorResult> into, Dictionary<string, IIndicatorResult> part)
    {
        foreach ((string id, IIndicatorResult result) in part)
        {
            into[id] = result;
        }
    }

    /// <summary>
    /// Loads, once per coarser time frame, the bars its indicators are calculated on: the latest bars of the frame dated on or before the last day of the call's window (the gateway cuts the
    /// daily bars at that day before it builds the period bars, so nothing after the window is read), at most <see cref="McpLimits.MaxCandlesPerRequest"/> of them.
    /// </summary>
    private async Task LoadOwnBarsAsync(string symbol, List<RequestedIndicator> requests, DateOnly windowEnd, CancellationToken cancellationToken)
    {
        foreach (IGrouping<TimeFrame, RequestedIndicator> frame in requests.Where(r => r.OwnTimeFrame is not null).GroupBy(r => r.OwnTimeFrame!.Value))
        {
            List<CandleData> bars = await _loader.LoadUpToAsync(symbol, frame.Key, windowEnd, _limits.MaxCandlesPerRequest, cancellationToken).ConfigureAwait(false);
            if (bars.Count == 0)
            {
                throw new ToolException(
                    ToolErrorCodes.InsufficientMarketData,
                    $"No stored {frame.Key} bars exist on or before {windowEnd.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture)} for {symbol}.",
                    $"{frame.First().Path}.{IndicatorTimeFrameKey}");
            }

            foreach (RequestedIndicator request in frame)
            {
                request.OwnBars = bars;
            }
        }
    }

    /// <summary>
    /// The time frame an indicator is calculated on. Naming the call's own frame is the same as naming none (null); a finer frame is UNSUPPORTED_TIMEFRAME, because the alignment reads
    /// the previous closed period of a coarser frame and has no meaning for finer bars.
    /// </summary>
    private static (TimeFrame? Written, TimeFrame? Own) ReadTimeFrame(JsonElement item, TimeFrame callFrame, string path)
    {
        if (!item.TryGetProperty(IndicatorTimeFrameKey, out JsonElement element))
        {
            return (null, null);
        }

        string keyPath = $"{path}.{IndicatorTimeFrameKey}";
        TimeFrame written = ToolArgs.SupportedTimeFrame(element.GetString()!, keyPath);
        if (ToolArgs.IsFinerThan(written, callFrame))
        {
            throw new ToolException(
                ToolErrorCodes.UnsupportedTimeFrame,
                $"An indicator on {written} bars is finer than the call's {callFrame}.",
                keyPath,
                $"Give {callFrame} or a coarser time frame, or omit {IndicatorTimeFrameKey}.");
        }

        return (written, written == callFrame ? null : written);
    }

    private List<RequestedIndicator> Bind(JsonElement indicators, TimeFrame callFrame)
    {
        var requests = new List<RequestedIndicator>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        int index = 0;
        foreach (JsonElement item in indicators.EnumerateArray())
        {
            string path = $"{IndicatorsArgument}[{index}]";
            string id = item.GetProperty("id").GetString()!;
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, "An indicator id must not be empty.", $"{path}.id");
            }

            if (!ids.Add(id))
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"Indicator id '{id}' is used more than once.", $"{path}.id", "Ids must be unique within one call.");
            }

            string typeName = item.GetProperty("type").GetString()!;
            IndicatorCatalogEntry entry = _catalog.Find(typeName)
                ?? throw new ToolException(ToolErrorCodes.UnknownIndicator, $"Unknown indicator type '{typeName}'.", $"{path}.type", "Call sa_get_capabilities to list the type names.");
            if (!entry.IsRegistered)
            {
                throw new ToolException(
                    ToolErrorCodes.UnknownIndicator,
                    $"Indicator type '{entry.Type}' is defined but cannot be calculated.",
                    $"{path}.type",
                    "Types with isRegistered false in sa_get_capabilities are not available.");
            }

            PriceType? priceSource = Optional(item, "priceSource") is { } priceName ? Enum.Parse<PriceType>(priceName) : null;
            item.TryGetProperty("parameters", out JsonElement parameters);
            CoreIndicatorSettings settings = _binder.Bind(entry, parameters, priceSource, path);
            (TimeFrame? written, TimeFrame? own) = ReadTimeFrame(item, callFrame, path);
            if (own is not null && settings.ParameterObject is ICrossTickerParameter)
            {
                throw new ToolException(
                    ToolErrorCodes.InvalidArgument,
                    $"{entry.Type} compares two symbols, whose bars are matched on the call's time frame, so it cannot have its own {IndicatorTimeFrameKey}.",
                    $"{path}.{IndicatorTimeFrameKey}",
                    $"Omit {IndicatorTimeFrameKey} on this indicator (it then uses the call's {callFrame}).");
            }

            requests.Add(new RequestedIndicator(
                index,
                id,
                entry,
                settings,
                Optional(item, "sourceIndicatorId"),
                Optional(item, "sourceOutputSeriesName"),
                Optional(item, "dynamicPeriodIndicatorId"),
                Optional(item, "dynamicPeriodOutputSeriesName"))
            {
                WrittenTimeFrame = written,
                OwnTimeFrame = own,
            });
            index++;
        }

        return requests;
    }

    private static string? Optional(JsonElement item, string name)
        => item.TryGetProperty(name, out JsonElement value) ? value.GetString() : null;

    /// <summary>
    /// Cross-ticker indicators need a comparison symbol: it must be a stored ticker and is written back in canonical form.
    /// Returns the distinct comparison symbols, the primary symbol included when an indicator compares a symbol with itself.
    /// </summary>
    private async Task<List<string>> ResolveComparisonSymbolsAsync(
        List<RequestedIndicator> requests, string primary, bool allowUnregistered, List<string> warnings, CancellationToken cancellationToken)
    {
        var symbols = new List<string>();
        var offList = new List<string>();
        foreach (RequestedIndicator request in requests)
        {
            if (request.Settings.ParameterObject is not ICrossTickerParameter cross)
            {
                continue;
            }

            string path = $"{request.Path}.parameters.{ComparisonSymbolName}";
            if (string.IsNullOrWhiteSpace(cross.ComparisonSymbol))
            {
                throw new ToolException(
                    ToolErrorCodes.InvalidIndicatorParameter,
                    $"{request.Entry.Type} compares two tickers and needs the {ComparisonSymbolName} parameter.",
                    path,
                    "Set it to a symbol from sa_list_tickers.");
            }

            ResolvedSymbol comparison = await _symbols.ResolveAsync(cross.ComparisonSymbol, path, allowUnregistered, cancellationToken).ConfigureAwait(false);
            string canonical = comparison.Symbol;
            cross.ComparisonSymbol = canonical;
            if (!comparison.IsRegistered && !offList.Contains(canonical, StringComparer.OrdinalIgnoreCase))
            {
                offList.Add(canonical);
            }

            if (!symbols.Contains(canonical, StringComparer.OrdinalIgnoreCase))
            {
                symbols.Add(canonical);
            }
        }

        // The primary symbol itself costs no extra load (the aligner binds it to the primary bars), so it does not count against the limit.
        if (symbols.Count(c => !SymbolHelper.IsSameSymbol(c, primary)) > _limits.MaxSecondarySymbolsPerCall)
        {
            throw new ToolException(
                ToolErrorCodes.LimitExceeded,
                $"At most {_limits.MaxSecondarySymbolsPerCall} different comparison symbols per call.",
                IndicatorsArgument,
                "Split the call.");
        }

        if (offList.Count > 0)
        {
            warnings.Add(SymbolResolver.OffListWarning(offList));
        }

        return symbols;
    }

    /// <summary>Aligns the comparison symbols to the primary timeline exactly as the chart does (carry-forward of gaps, fragmented series excluded).</summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<CoreCandleData?>>?> AlignComparisonAsync(
        string symbol,
        List<string> comparisonSymbols,
        TimeFrame timeFrame,
        int barCount,
        DateOnly? endDate,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (comparisonSymbols.Count == 0)
        {
            return null;
        }

        // With an end date every symbol is cut at the same day; without one the loader is the unchanged latest-bars path.
        IDataService data = endDate is null ? new WindowedDataService(_loader) : new EndDateAwareDataService(_loader, endDate);
        var aligner = new ComparisonDataAligner(data) { MaxComparisonSymbols = _limits.MaxSecondarySymbolsPerCall };
        ComparisonAlignedData aligned = await aligner.AlignAsync(symbol, comparisonSymbols, timeFrame, barCount, cancellationToken).ConfigureAwait(false);
        warnings.AddRange(aligned.Warnings.Select(_sanitizer.Sanitize));

        var secondary = new Dictionary<string, IReadOnlyList<CoreCandleData?>>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, CandleData?[] bars) in aligned.Series)
        {
            secondary[name] = bars.Select(b => b is { } bar ? ToCore(bar) : null).ToList();
        }

        return secondary;
    }

    private static CoreCandleData ToCore(CandleData bar) => new(bar.Timestamp, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume);

    private void WriteResult(
        Utf8JsonWriter writer,
        string symbol,
        TimeFrame timeFrame,
        DateOnly? endDate,
        int lookback,
        List<CandleData> loaded,
        bool droppedLeadingBar,
        int tail,
        List<RequestedIndicator> requests,
        IReadOnlyList<DerivedSeriesRequest> derived,
        SeriesOutputOptions output,
        Dictionary<string, IIndicatorResult> results,
        List<string> warnings,
        HashSet<string> excludedComparisons)
    {
        int barCount = loaded.Count;
        int returned = output.IncludeSeries ? Math.Min(tail, barCount) : 0;
        int firstReturned = barCount - returned;

        writer.WriteStartObject();
        writer.WriteString("symbol", symbol);
        writer.WriteString("timeFrame", timeFrame.ToString());
        if (endDate is not null)
        {
            writer.WriteString("endDate", endDate.Value.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture));
        }

        writer.WriteString("periodUnit", GetCapabilitiesTool.PeriodUnit);
        writer.WriteString("timestampFormat", ToolJson.DateFormat);
        writer.WriteNumber("lookbackBars", lookback);
        writer.WriteNumber("calculatedBarCount", barCount);
        writer.WriteNumber("returnedBarCount", returned);
        writer.WriteString("earliestCalculatedTimestamp", ToolJson.FormatDate(loaded[0].Timestamp));
        writer.WriteString("latestTimestamp", ToolJson.FormatDate(loaded[^1].Timestamp));
        writer.WriteBoolean("droppedLeadingBar", droppedLeadingBar);
        if (output.IncludeSeries)
        {
            writer.WriteStartArray("timestamps");
            for (int i = firstReturned; i < barCount; i++)
            {
                writer.WriteStringValue(ToolJson.FormatDate(loaded[i].Timestamp));
            }

            writer.WriteEndArray();
        }

        WriteStrings(writer, "warnings", warnings);
        ImmutableArray<CandleData> callBars = loaded.ToImmutableArray();
        var seriesById = new Dictionary<string, List<(string Name, IReadOnlyList<decimal?> Values)>>(StringComparer.Ordinal);
        foreach (RequestedIndicator request in requests)
        {
            results.TryGetValue(request.Settings.Id, out IIndicatorResult? result);
            seriesById[request.Id] = BuildSeries(request, result, callBars);
        }

        writer.WriteStartObject("results");
        foreach (RequestedIndicator request in requests)
        {
            writer.WritePropertyName(request.Id);
            WriteIndicator(writer, request, requests, results, seriesById[request.Id], output, barCount, firstReturned, excludedComparisons);
        }

        if (derived.Count > 0)
        {
            IReadOnlyList<DerivedSeriesOutcome> outcomes = DerivedSeriesPlan.Evaluate(derived, id => DerivedSource(id, requests, results, seriesById), barCount);
            foreach (DerivedSeriesOutcome outcome in outcomes)
            {
                writer.WritePropertyName(outcome.Request.Id);
                WriteDerived(writer, outcome, output, barCount, firstReturned);
            }
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    /// <summary>The returned output series of one indicator, by listed name and aligned to the call's bars (empty when the indicator did not succeed).</summary>
    private static List<(string Name, IReadOnlyList<decimal?> Values)> BuildSeries(RequestedIndicator request, IIndicatorResult? result, ImmutableArray<CandleData> callBars)
    {
        CoreIndicatorSettings settings = request.Settings;
        IndicatorType type = Enum.Parse<IndicatorType>(request.Entry.Type);
        var series = new List<(string Name, IReadOnlyList<decimal?> Values)>();
        if (result is { IsSuccessful: true })
        {
            foreach (string name in request.Entry.OutputSeriesNames)
            {
                if (IndicatorOutputSeriesResolver.TryResolveSeries(result, type, name, settings.PriceSource, out IReadOnlyList<decimal?> values))
                {
                    series.Add((name, request.OwnBars is { } ownBars ? AlignToCall(callBars, ownBars, values) : values));
                }
            }
        }

        return series;
    }

    private static bool HasAnyValue(IEnumerable<(string Name, IReadOnlyList<decimal?> Values)> series) => series.Any(s => s.Values.Any(v => v is not null));

    private DerivedSeriesSource DerivedSource(
        string id,
        List<RequestedIndicator> requests,
        Dictionary<string, IIndicatorResult> results,
        Dictionary<string, List<(string Name, IReadOnlyList<decimal?> Values)>> seriesById)
    {
        RequestedIndicator request = requests.First(r => string.Equals(r.Id, id, StringComparison.Ordinal));
        results.TryGetValue(request.Settings.Id, out IIndicatorResult? result);
        List<(string Name, IReadOnlyList<decimal?> Values)> series = seriesById[id];
        bool successful = result is { IsSuccessful: true } && HasAnyValue(series);
        return new DerivedSeriesSource(successful, series, _chain.NonCausalSeries(request, requests));
    }

    private static void WriteDerived(Utf8JsonWriter writer, DerivedSeriesOutcome outcome, SeriesOutputOptions output, int barCount, int firstReturned)
    {
        const string main = IndicatorResult.MainSeriesName;
        writer.WriteStartObject();
        writer.WriteString("type", DerivedType);
        writer.WriteString("op", outcome.Request.Operation.ToString());
        writer.WriteBoolean("isSuccessful", outcome.IsSuccessful);
        if (outcome.ErrorMessage is not null)
        {
            writer.WriteString("errorMessage", outcome.ErrorMessage);
        }

        WriteStrings(writer, "warnings", Array.Empty<string>());
        WriteStrings(writer, "nonCausalSeries", outcome.IsNonCausal ? new[] { main } : Array.Empty<string>());
        writer.WriteNull("unit"); // the unit of a derived series depends on the operation and its inputs and is not defined (D15-A: no unit is invented)
        if (outcome.DivisionByZeroCount is { } byZero)
        {
            writer.WriteNumber("divisionByZeroCount", byZero);
        }

        if (outcome.IsSuccessful && outcome.Values is { } values)
        {
            if (output.IncludeSeries)
            {
                writer.WriteStartObject("series");
                writer.WritePropertyName(main);
                writer.WriteStartArray();
                for (int i = firstReturned; i < barCount; i++)
                {
                    WriteNullable(writer, values[i]);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteStartObject("latestValues");
            writer.WritePropertyName(main);
            WriteNullable(writer, values[barCount - 1]);
            writer.WriteEndObject();

            int leading = 0;
            while (leading < barCount && values[leading] is null)
            {
                leading++;
            }

            writer.WriteStartObject("leadingNullCount");
            writer.WriteNumber(main, leading);
            writer.WriteEndObject();

            output.WriteStatistics(writer, new[] { (main, values) }, barCount);
        }

        writer.WriteEndObject();
    }

    private void WriteIndicator(
        Utf8JsonWriter writer,
        RequestedIndicator request,
        List<RequestedIndicator> requests,
        Dictionary<string, IIndicatorResult> results,
        List<(string Name, IReadOnlyList<decimal?> Values)> series,
        SeriesOutputOptions output,
        int barCount,
        int firstReturned,
        HashSet<string> excludedComparisons)
    {
        CoreIndicatorSettings settings = request.Settings;
        results.TryGetValue(settings.Id, out IIndicatorResult? result);

        bool anyValue = HasAnyValue(series);
        bool successful = result is { IsSuccessful: true } && anyValue;
        string? error = null;
        var indicatorWarnings = new List<string>();
        if (!successful && settings.ParameterObject is ICrossTickerParameter { ComparisonSymbol: { Length: > 0 } comparison } && excludedComparisons.Contains(comparison))
        {
            error = $"Comparison symbol '{comparison}' was excluded (no stored data, or too many gaps against the primary symbol); see warnings.";
        }
        else if (result is null)
        {
            error = "The pipeline produced no result for this indicator.";
        }
        else if (!result.IsSuccessful)
        {
            error = result.ErrorMessage ?? settings.ErrorMessage ?? "The calculation failed.";
        }
        else if (!anyValue)
        {
            error = AllNullMessage;
        }
        else if (!string.IsNullOrWhiteSpace(settings.ErrorMessage))
        {
            indicatorWarnings.Add(settings.ErrorMessage);
        }

        writer.WriteStartObject();
        writer.WriteString("type", request.Entry.Type);
        if (request.WrittenTimeFrame is { } written)
        {
            writer.WriteString(IndicatorTimeFrameKey, written.ToString());
            if (request.OwnTimeFrame is not null)
            {
                writer.WriteString(AlignmentKey, PreviousClosedPeriod);
            }
        }

        writer.WriteString("displayName", settings.GetFormattedDisplayName());
        writer.WriteBoolean("isSuccessful", successful);
        if (error is not null)
        {
            writer.WriteString("errorMessage", _sanitizer.Sanitize(error));
        }

        WriteStrings(writer, "warnings", indicatorWarnings.Select(_sanitizer.Sanitize));
        writer.WriteString("priceSource", settings.PriceSource.ToString());
        WriteStrings(writer, "nonCausalSeries", _chain.NonCausalSeries(request, requests));

        if (successful)
        {
            if (output.IncludeSeries)
            {
                writer.WriteStartObject("series");
                foreach ((string name, IReadOnlyList<decimal?> values) in series)
                {
                    writer.WritePropertyName(name);
                    writer.WriteStartArray();
                    for (int i = firstReturned; i < barCount; i++)
                    {
                        WriteNullable(writer, i < values.Count ? values[i] : null);
                    }

                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }

            writer.WriteStartObject("latestValues");
            foreach ((string name, IReadOnlyList<decimal?> values) in series)
            {
                writer.WritePropertyName(name);
                WriteNullable(writer, barCount - 1 < values.Count ? values[barCount - 1] : null);
            }

            writer.WriteEndObject();

            writer.WriteStartObject("leadingNullCount");
            foreach ((string name, IReadOnlyList<decimal?> values) in series)
            {
                int leading = 0;
                while (leading < Math.Min(values.Count, barCount) && values[leading] is null)
                {
                    leading++;
                }

                writer.WriteNumber(name, leading);
            }

            writer.WriteEndObject();

            output.WriteStatistics(writer, series, barCount);

            var projected = series.Where(s => s.Values.Count > barCount).ToList();
            if (output.IncludeSeries && projected.Count > 0)
            {
                writer.WriteStartObject("projectedValues");
                foreach ((string name, IReadOnlyList<decimal?> values) in projected)
                {
                    writer.WritePropertyName(name);
                    writer.WriteStartArray();
                    for (int i = barCount; i < values.Count; i++)
                    {
                        WriteNullable(writer, values[i]);
                    }

                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// A series calculated on <paramref name="ownBars"/> (a coarser time frame) as the call's bars see it: <see cref="TimeframeAlignment.Align"/>, the value of the previous closed period.
    /// A projection past the last bar (Ichimoku's Senkou spans) belongs to no bar and is cut, as the backtest engine cuts it for the same alignment.
    /// </summary>
    private static IReadOnlyList<decimal?> AlignToCall(ImmutableArray<CandleData> callBars, IReadOnlyList<CandleData> ownBars, IReadOnlyList<decimal?> values)
        => TimeframeAlignment.Align(
            callBars,
            ownBars.ToImmutableArray(),
            values.Take(ownBars.Count).Concat(Enumerable.Repeat<decimal?>(null, Math.Max(0, ownBars.Count - values.Count))).ToImmutableArray());

    private static void WriteNullable(Utf8JsonWriter writer, decimal? value)
    {
        if (value is { } number)
        {
            writer.WriteNumberValue(number);
        }
        else
        {
            writer.WriteNullValue();
        }
    }

    private static void WriteStrings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WriteStartArray(name);
        foreach (string value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }
}
