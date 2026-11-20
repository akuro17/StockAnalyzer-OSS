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
using StockAnalyzer.Core.Services.Analysis;
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
    private readonly IndicatorRequestBinder _requestBinder;
    private readonly IndicatorChainPlan _chain;
    private readonly ErrorMessageSanitizer _sanitizer;
    private readonly McpLimits _limits;
    private readonly bool _compactDefaults;

    public CalculateIndicatorsTool(
        IReadOnlyStockAnalyzerGateway gateway,
        SymbolResolver symbols,
        IndicatorCatalog catalog,
        IndicatorSettingsBinder binder,
        IndicatorChainPlan chain,
        ErrorMessageSanitizer sanitizer,
        McpLimits limits,
        bool compactDefaults = false)
    {
        _gateway = gateway;
        _symbols = symbols;
        _loader = new CandleWindowLoader(gateway);
        _catalog = catalog;
        _requestBinder = new IndicatorRequestBinder(catalog, binder);
        _chain = chain;
        _sanitizer = sanitizer;
        _limits = limits;
        _compactDefaults = compactDefaults;
    }

    public string Name => "sa_calculate_indicators";

    public string Description =>
        "Calculates technical indicators (and optional derived series) on one symbol's stored bars via the application's indicator pipeline. " +
        "Call sa_get_capabilities first for type names, parameter ranges and output series. " +
        $"Supports up to {_limits.MaxIndicatorsPerCall} indicators, per-indicator timeFrame (coarser alignment), endDate, completeBarsOnly, summary/pastValues, includeSeries, and decimalPlaces. " +
        ToolJson.DocLink(Name);

    public bool IsHeavy => false;

    public string? CompactUsageHint => "Pass includeSeries: false with summary: true, decimalPlaces: 4, or a smaller outputTailCount / lookbackBars.";

    public IReadOnlyDictionary<string, string>? MisnamedArgumentHints { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [AsOfDateArgument] = $"This tool names the last-bar date '{EndDateArgument}' (sa_run_screener uses '{AsOfDateArgument}').",
    };

    public string? ExampleArgumentsJson => "{\"symbol\":\"7203-T\",\"timeFrame\":\"D1\",\"indicators\":[{\"id\":\"sma25\",\"type\":\"SMA\",\"parameters\":{\"Period\":25}}],\"outputTailCount\":200}";

    public string InputSchemaJson => ToolSchema.Object()
        .String(SymbolArgument, "Ticker symbol ('.' == '-', e.g. 7203-T).", required: true)
        .String(TimeFrameArgument, $"Bar timeframe ({ToolArgs.SupportedTimeFrameNames}).", required: true, allowedValues: Enum.GetNames<TimeFrame>())
        .ObjectArray(
            IndicatorsArgument,
            $"Indicators to calculate (1..{_limits.MaxIndicatorsPerCall}).",
            IndicatorItemSchema(),
            required: true,
            minItems: 1)
        .ObjectArray(
            DerivedArgument,
            $"Derived series computed from earlier series (max {_limits.MaxDerivedSeriesPerCall}).",
            DerivedItemSchema())
        .Integer(LookbackArgument, $"Stored bars loaded (1..{_limits.MaxCandlesPerRequest}, newest last).", defaultValue: _limits.MaxCandlesPerRequest, minimum: 1, maximum: _limits.MaxCandlesPerRequest)
        .Integer(TailArgument, $"Trailing bars returned per series (1..{_limits.MaxOutputTailBars}). Default {_limits.DefaultOutputTailBars}.", defaultValue: _limits.DefaultOutputTailBars, minimum: 1, maximum: _limits.MaxOutputTailBars)
        .String(EndDateArgument, $"Inclusive end date ({ToolJson.DateFormat}). Default: latest bar.")
        .Boolean(PartialBarOutput.CompleteBarsOnlyArgument, "W1/MN1 only: true drops incomplete last period, false keeps it; both add partial-bar keys.")
        .Boolean(SeriesOutputOptions.SummaryArgument, _compactDefaults
            ? "Add per-series stats (count, latest, mean, stdDev, zScoreOfLatest, percentileRankOfLatest, trendSlope, trendBars). Default true (unless includeSeries is true or lookbackOffsets is given)."
            : "Add per-series stats (count, latest, mean, stdDev, zScoreOfLatest, percentileRankOfLatest, trendSlope, trendBars). Default false.")
        .Integer(SeriesOutputOptions.TrendBarsArgument, $"Summary trendBars window ({SeriesSummaryStatistics.MinTrendBars}..{McpLimitsContract.EffectiveMaxTrendBars(_limits)}; needs summary true). Default {McpLimitsContract.EffectiveDefaultTrendBars(_limits)}.", defaultValue: McpLimitsContract.EffectiveDefaultTrendBars(_limits), minimum: SeriesSummaryStatistics.MinTrendBars, maximum: McpLimitsContract.EffectiveMaxTrendBars(_limits))
        .Integer(SeriesOutputOptions.SummaryBarsArgument, SeriesOutputOptions.SummaryBarsDescription(_limits), minimum: SeriesSummaryStatistics.MinTrendBars, maximum: McpLimitsContract.EffectiveMaxSummaryBars(_limits))
        .Boolean(SeriesOutputOptions.SummaryDistributionArgument, SeriesOutputOptions.SummaryDistributionDescription)
        .IntegerArray(SeriesOutputOptions.LookbackOffsetsArgument, $"Non-negative bar offsets back from last bar for pastValues (max {_limits.MaxLookbackOffsets}).")
        .Boolean(SeriesOutputOptions.IncludeSeriesArgument, _compactDefaults
            ? "false omits series/projectedValues/timestamps (needs summary true or lookbackOffsets; excludes outputTailCount). Default false (true when outputTailCount is given)."
            : "false omits series/projectedValues/timestamps (needs summary true or lookbackOffsets; excludes outputTailCount). Default true.")
        .Boolean(SymbolResolver.AllowUnregisteredArgument, "Allow unregistered symbols with stored daily bars. Default false.")
        .Integer(SeriesOutputOptions.DecimalPlacesArgument, SeriesOutputOptions.DecimalPlacesDescription("output decimals", _compactDefaults), defaultValue: _compactDefaults ? SeriesOutputOptions.DefaultCompactDecimalPlaces : null, minimum: 0, maximum: SeriesOutputOptions.MaxDecimalPlaces)
        .Build();

    /// <summary>The schema of one entry of <c>indicators</c>: the one definition shared by sa_calculate_indicators and sa_calculate_indicators_batch.</summary>
    internal static ToolSchema IndicatorItemSchema()
        => ToolSchema.Object()
        .String("id", "Unique client id keying this result.", required: true)
        .String("type", "Indicator type name (case-insensitive, from sa_get_capabilities).", required: true)
        .FreeObject("parameters", "Parameter map, e.g. {\"Period\":14} (names/ranges: sa_get_capabilities).")
        .String(IndicatorTimeFrameKey, $"Indicator timeframe (call's or coarser: {ToolArgs.SupportedTimeFrameNames}); coarser aligns via PreviousClosedPeriod.", allowedValues: Enum.GetNames<TimeFrame>())
        .String("priceSource", "Input price field (e.g. Close). Default: indicator default.", allowedValues: Enum.GetNames<PriceType>())
        .String("sourceIndicatorId", "Upstream indicator id feeding price input.")
        .String("sourceOutputSeriesName", "Output series of sourceIndicatorId (default: main).")
        .String("dynamicPeriodIndicatorId", "Upstream indicator id driving adaptive period.")
        .String("dynamicPeriodOutputSeriesName", "Output series of dynamicPeriodIndicatorId (default: main).");

    /// <summary>The schema of one entry of <c>derived</c>: shared by sa_calculate_indicators and sa_calculate_indicators_batch.</summary>
    internal static ToolSchema DerivedItemSchema()
        => ToolSchema.Object()
        .String(DerivedSeriesPlan.IdKey, "Unique client id across indicators and derived series.", required: true)
        .String(DerivedSeriesPlan.OperationKey, "Binary operation.", required: true, allowedValues: Enum.GetNames<SeriesOperation>())
        .FreeObject(DerivedSeriesPlan.LeftKey, $"{{\"{DerivedSeriesPlan.ReferenceKey}\":\"<id>\",\"{DerivedSeriesPlan.SeriesNameKey}\"?:\"<series>\"}} or {{\"{DerivedSeriesPlan.ConstantKey}\":<num>}}.", required: true)
        .FreeObject(DerivedSeriesPlan.RightKey, $"Same forms as {DerivedSeriesPlan.LeftKey}.", required: true);

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
        CheckIndicatorCount(indicators);

        bool hasExplicitTail = ToolArgs.Integer(arguments, TailArgument) is not null;
        SeriesOutputOptions output = SeriesOutputOptions.Read(arguments, _limits, includeSeriesDefault: true, compactDefaults: _compactDefaults, hasExplicitOutputTail: hasExplicitTail);
        if (!output.IncludeSeries && hasExplicitTail)
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"'{TailArgument}' chooses how many bars the series arrays hold and has no effect when '{SeriesOutputOptions.IncludeSeriesArgument}' is false.",
                TailArgument,
                $"Set {SeriesOutputOptions.IncludeSeriesArgument} to true, or omit {TailArgument}.");
        }

        (List<RequestedIndicator> requests, IReadOnlyList<DerivedSeriesRequest> derived) = PlanIndicators(arguments, indicators, timeFrame);
        List<string> comparisonSymbols = await ResolveComparisonSymbolsAsync(requests, symbol, allowUnregistered, warnings, cancellationToken).ConfigureAwait(false);

        // completeBarsOnly concerns the bars of the call's own W1 or MN1 frame; given (true or false) it adds the partial-bar keys, not given nothing changes (the answer stays byte-identical).
        bool? completeBarsOnly = ToolArgs.Boolean(arguments, PartialBarOutput.CompleteBarsOnlyArgument);
        IndicatorEvaluation evaluation = await EvaluateAsync(symbol, timeFrame, lookback, endDate, completeBarsOnly, requests, derived, comparisonSymbols, warnings, null, cancellationToken).ConfigureAwait(false);
        return ToolJson.Build(writer => WriteResult(writer, timeFrame, endDate, lookback, evaluation, tail, output, warnings));
    }

    /// <summary>The refusal of a call that names more indicators than <see cref="McpLimits.MaxIndicatorsPerCall"/> (the list is never cut).</summary>
    internal void CheckIndicatorCount(JsonElement indicators)
    {
        if (indicators.GetArrayLength() > _limits.MaxIndicatorsPerCall)
        {
            throw new ToolException(
                ToolErrorCodes.LimitExceeded,
                $"At most {_limits.MaxIndicatorsPerCall} indicators per call.",
                IndicatorsArgument,
                $"Send {_limits.MaxIndicatorsPerCall} or fewer indicators, or split the call.");
        }
    }

    /// <summary>
    /// Binds the call's indicators (and its optional derived series) to fresh settings. The settings are changed by the call that uses them (a comparison symbol is written back
    /// in canonical form), so a tool that evaluates several symbols plans once per symbol.
    /// </summary>
    internal (List<RequestedIndicator> Requests, IReadOnlyList<DerivedSeriesRequest> Derived) PlanIndicators(JsonElement arguments, JsonElement indicators, TimeFrame timeFrame)
    {
        List<RequestedIndicator> requests = _requestBinder.Bind(indicators.EnumerateArray().Select((item, i) => (item, $"{IndicatorsArgument}[{i}]")), timeFrame);
        _chain.Apply(requests);
        IReadOnlyList<DerivedSeriesRequest> derived = arguments.TryGetProperty(DerivedArgument, out JsonElement derivedElement)
            ? DerivedSeriesPlan.Parse(derivedElement, requests, _chain, _limits)
            : Array.Empty<DerivedSeriesRequest>();
        return (requests, derived);
    }

    /// <summary>
    /// Everything the calculation of one symbol produced, ready to be written (<see cref="WriteEvaluation"/>). The indicator and derived requests are the ones that were calculated.
    /// </summary>
    internal sealed class IndicatorEvaluation
    {
        public IndicatorEvaluation(
            string symbol,
            List<CandleData> loaded,
            bool droppedLeadingBar,
            PartialBarReport? partialBars,
            List<RequestedIndicator> requests,
            IReadOnlyList<DerivedSeriesRequest> derived,
            Dictionary<string, IIndicatorResult> results,
            HashSet<string> excludedComparisons)
        {
            Symbol = symbol;
            Loaded = loaded;
            DroppedLeadingBar = droppedLeadingBar;
            PartialBars = partialBars;
            Requests = requests;
            Derived = derived;
            Results = results;
            ExcludedComparisons = excludedComparisons;
        }

        public string Symbol { get; }

        public List<CandleData> Loaded { get; }

        public bool DroppedLeadingBar { get; }

        public PartialBarReport? PartialBars { get; }

        public List<RequestedIndicator> Requests { get; }

        public IReadOnlyList<DerivedSeriesRequest> Derived { get; }

        public Dictionary<string, IIndicatorResult> Results { get; }

        public HashSet<string> ExcludedComparisons { get; }
    }

    /// <summary>
    /// Loads the bars of one symbol and calculates the requested indicators on them exactly as sa_calculate_indicators does (partial-bar rule, comparison alignment, own time frames).
    /// <paramref name="comparisonData"/> lets a caller that evaluates many symbols hand in the data service the comparison symbols are read through for a given window end
    /// (the one the call would build itself when null); the primary symbol is read through it too.
    /// </summary>
    internal async Task<IndicatorEvaluation> EvaluateAsync(
        string symbol,
        TimeFrame timeFrame,
        int lookback,
        DateOnly? endDate,
        bool? completeBarsOnly,
        List<RequestedIndicator> requests,
        IReadOnlyList<DerivedSeriesRequest> derived,
        List<string> comparisonSymbols,
        List<string> warnings,
        Func<DateOnly?, IDataService>? comparisonData,
        CancellationToken cancellationToken)
    {
        bool reportPartial = completeBarsOnly is not null && TimeFrameAggregator.IsPeriodFrame(timeFrame);
        DateTime? referenceDate = reportPartial ? await _loader.ReferenceDateAsync(symbol, endDate, cancellationToken).ConfigureAwait(false) : null;

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

        // The running last period is left out by loading again up to the day before its start: the daily bars are cut first, exactly as an explicit endDate does,
        // so the comparison symbols and the own-timeFrame bars follow the same cut (a bar dropped after the load would leave the comparison series misaligned).
        bool droppedPartialBar = false;
        DateOnly? windowEnd = endDate;
        if (completeBarsOnly == true && CandleWindowLoader.IsLastBarPartial(loaded, timeFrame, referenceDate))
        {
            (loaded, DateOnly effectiveEnd) = await _loader.LoadWithoutRunningPeriodAsync(symbol, timeFrame, loaded, lookback, cancellationToken).ConfigureAwait(false);
            droppedLeadingBar = false;
            droppedPartialBar = true;
            windowEnd = effectiveEnd;
            if (loaded.Count == 0)
            {
                throw new ToolException(
                    ToolErrorCodes.InsufficientMarketData,
                    $"No complete {timeFrame} period is stored for {symbol}.",
                    PartialBarOutput.CompleteBarsOnlyArgument);
            }
        }

        PartialBarReport? partialBars = reportPartial
            ? new PartialBarReport(completeBarsOnly!.Value, referenceDate, TimeFrameAggregator.PeriodEnd(loaded[^1].Timestamp, timeFrame), CandleWindowLoader.IsLastBarPartial(loaded, timeFrame, referenceDate), droppedPartialBar)
            : null;

        List<CoreCandleData> candles = loaded.Select(ToCore).ToList();
        IReadOnlyDictionary<string, IReadOnlyList<CoreCandleData?>>? secondary =
            await AlignComparisonAsync(symbol, comparisonSymbols, timeFrame, candles.Count, windowEnd, warnings, comparisonData, cancellationToken).ConfigureAwait(false);

        var excluded = new HashSet<string>(
            comparisonSymbols.Where(c => secondary is null || !secondary.ContainsKey(c)),
            StringComparer.OrdinalIgnoreCase);

        cancellationToken.ThrowIfCancellationRequested();

        await LoadOwnBarsAsync(symbol, requests, DateOnly.FromDateTime(loaded[^1].Timestamp), cancellationToken).ConfigureAwait(false);

        // The token is checked before each indicator starts (gateway and pipeline), so a call that has run out of time starts no further indicator. An indicator that is
        // already running is not interrupted (except HiddenMarkovModel, which observes the token; one fed through an indicator chain does not): the call is awaited, so that calculation
        // keeps the turn until it ends (the executor has already answered EXECUTION_TIMEOUT by then).
        // The indicators of the call's own bars are one calculation exactly as before; each coarser time frame is one more calculation on its own bars.
        var results = new Dictionary<string, IIndicatorResult>();
        List<RequestedIndicator> ownBarRequests = requests.Where(r => r.OwnTimeFrame is null).ToList();
        if (ownBarRequests.Count > 0)
        {
            Merge(results, await CalculateAsync(candles, ownBarRequests.Select(r => r.Settings).ToList(), secondary, timeFrame, windowEnd, cancellationToken).ConfigureAwait(false));
        }

        foreach (IGrouping<TimeFrame, RequestedIndicator> frame in requests.Where(r => r.OwnTimeFrame is not null).GroupBy(r => r.OwnTimeFrame!.Value))
        {
            // Checkpoint between the calculations: a call cancelled meanwhile starts no further time frame.
            cancellationToken.ThrowIfCancellationRequested();
            List<CoreCandleData> frameCandles = frame.First().OwnBars!.Select(ToCore).ToList();
            Merge(results, await CalculateAsync(frameCandles, frame.Select(r => r.Settings).ToList(), null, frame.Key, windowEnd, cancellationToken).ConfigureAwait(false));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new IndicatorEvaluation(symbol, loaded, droppedLeadingBar, partialBars, requests, derived, results, excluded);
    }

    /// <summary>
    /// One calculation through the gateway. A gateway that checks the token between indicators gets it; any other gateway (a fake, a wrapper) is called as before,
    /// so the call then runs every indicator once it has started.
    /// </summary>
    private Task<Dictionary<string, IIndicatorResult>> CalculateAsync(
        IReadOnlyList<CoreCandleData> candles,
        IReadOnlyList<CoreIndicatorSettings> settings,
        IReadOnlyDictionary<string, IReadOnlyList<CoreCandleData?>>? secondary,
        TimeFrame timeFrame,
        DateOnly? endDate,
        CancellationToken cancellationToken)
        => _gateway is ITimeFrameAwareIndicatorCalculation tfAware
            ? tfAware.CalculateIndicatorsAsync(candles, settings, secondary, timeFrame, endDate, cancellationToken)
            : _gateway is ICancellableIndicatorCalculation cancellable
                ? cancellable.CalculateIndicatorsAsync(candles, settings, secondary, cancellationToken)
                : _gateway.CalculateIndicatorsAsync(candles, settings, secondary);

    /// <summary>What the call reports about the running last period of its W1 or MN1 bars when <c>completeBarsOnly</c> was given.</summary>
    internal sealed record PartialBarReport(bool CompleteBarsOnly, DateTime? ReferenceDate, DateTime LastBarPeriodEnd, bool LastBarPartial, bool DroppedPartialBar);

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
    /// Cross-ticker indicators need a comparison symbol: it must be a stored ticker and is written back in canonical form.
    /// Returns the distinct comparison symbols, the primary symbol included when an indicator compares a symbol with itself.
    /// </summary>
    internal async Task<List<string>> ResolveComparisonSymbolsAsync(
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

    /// <summary>The data service the comparison symbols (and the primary symbol) are read through for a window that ends at <paramref name="endDate"/> (the latest bars when null).</summary>
    internal IDataService CreateComparisonDataService(DateOnly? endDate)
        => endDate is null ? new WindowedDataService(_loader) : new EndDateAwareDataService(_loader, endDate);

    /// <summary>Aligns the comparison symbols to the primary timeline exactly as the chart does (carry-forward of gaps, fragmented series excluded).</summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<CoreCandleData?>>?> AlignComparisonAsync(
        string symbol,
        List<string> comparisonSymbols,
        TimeFrame timeFrame,
        int barCount,
        DateOnly? endDate,
        List<string> warnings,
        Func<DateOnly?, IDataService>? comparisonData,
        CancellationToken cancellationToken)
    {
        if (comparisonSymbols.Count == 0)
        {
            return null;
        }

        // With an end date every symbol is cut at the same day; without one the loader is the unchanged latest-bars path.
        IDataService data = comparisonData?.Invoke(endDate) ?? CreateComparisonDataService(endDate);
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
        TimeFrame timeFrame,
        DateOnly? endDate,
        int lookback,
        IndicatorEvaluation evaluation,
        int tail,
        SeriesOutputOptions output,
        List<string> warnings)
    {
        writer.WriteStartObject();
        writer.WriteString("symbol", evaluation.Symbol);
        writer.WriteString("timeFrame", timeFrame.ToString());
        if (endDate is not null)
        {
            writer.WriteString("endDate", endDate.Value.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture));
        }

        writer.WriteString("periodUnit", GetCapabilitiesTool.PeriodUnit);
        writer.WriteString("timestampFormat", ToolJson.DateFormat);
        writer.WriteNumber("lookbackBars", lookback);
        WriteEvaluation(writer, evaluation, tail, output, warnings);
        writer.WriteEndObject();
    }

    /// <summary>
    /// The members of a result that belong to one evaluated symbol, from <c>calculatedBarCount</c> to <c>results</c> (the caller writes the object around them and whatever
    /// identifies the call or the symbol before them). The one writer of sa_calculate_indicators and of every item of sa_calculate_indicators_batch.
    /// </summary>
    internal void WriteEvaluation(Utf8JsonWriter writer, IndicatorEvaluation evaluation, int tail, SeriesOutputOptions output, IEnumerable<string> warnings)
    {
        List<CandleData> loaded = evaluation.Loaded;
        List<RequestedIndicator> requests = evaluation.Requests;
        IReadOnlyList<DerivedSeriesRequest> derived = evaluation.Derived;
        Dictionary<string, IIndicatorResult> results = evaluation.Results;
        int barCount = loaded.Count;
        int returned = output.IncludeSeries ? Math.Min(tail, barCount) : 0;
        int firstReturned = barCount - returned;

        writer.WriteNumber("calculatedBarCount", barCount);
        writer.WriteNumber("returnedBarCount", returned);
        writer.WriteString("earliestCalculatedTimestamp", ToolJson.FormatDate(loaded[0].Timestamp));
        writer.WriteString("latestTimestamp", ToolJson.FormatDate(loaded[^1].Timestamp));
        writer.WriteBoolean("droppedLeadingBar", evaluation.DroppedLeadingBar);
        if (evaluation.PartialBars is { } partial)
        {
            // The same names, types and format as sa_get_candles (PartialBarOutput); written only when completeBarsOnly was given, so an answer without it is unchanged.
            writer.WriteBoolean(PartialBarOutput.CompleteBarsOnlyArgument, partial.CompleteBarsOnly);
            PartialBarOutput.Write(writer, partial.ReferenceDate, partial.LastBarPeriodEnd, partial.LastBarPartial, partial.DroppedPartialBar);
        }

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
            WriteIndicator(writer, request, requests, results, seriesById[request.Id], output, barCount, firstReturned, evaluation.ExcludedComparisons);
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
                    WriteNullable(writer, output.Round(values[i]));
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteStartObject("latestValues");
            writer.WritePropertyName(main);
            WriteNullable(writer, output.Round(values[barCount - 1]));
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
                        WriteNullable(writer, output.Round(i < values.Count ? values[i] : null));
                    }

                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }

            writer.WriteStartObject("latestValues");
            foreach ((string name, IReadOnlyList<decimal?> values) in series)
            {
                writer.WritePropertyName(name);
                WriteNullable(writer, output.Round(barCount - 1 < values.Count ? values[barCount - 1] : null));
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
                        WriteNullable(writer, output.Round(values[i]));
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
