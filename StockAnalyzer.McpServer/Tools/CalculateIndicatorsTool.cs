using System.Globalization;
using System.Text.Json;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
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
    private const string LookbackArgument = "lookbackBars";
    private const string TailArgument = "outputTailCount";
    private const string EndDateArgument = "endDate";
    private const string AsOfDateArgument = "asOfDate";
    private const string ComparisonSymbolName = "ComparisonSymbol";

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
        "Omitted parameters use the application defaults. All periods are in bars of the requested time frame (periodUnit is always bars). " +
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
        "allowUnregistered true also accepts a symbol (and comparison symbols) that is not in the ticker list but has stored daily bars; it is named in warnings, and a symbol without stored bars is SYMBOL_NOT_FOUND. " +
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
                .String("priceSource", "Price the indicator reads (for example Close). Affects indicators that work on one price series. Default: the indicator's default.", allowedValues: Enum.GetNames<PriceType>())
                .String("sourceIndicatorId", "Id of another indicator in this call whose output series is this indicator's input instead of the price.")
                .String("sourceOutputSeriesName", "Output series of the source indicator (default: its main series).")
                .String("dynamicPeriodIndicatorId", "Id of another indicator in this call whose output series drives this indicator's adaptive period.")
                .String("dynamicPeriodOutputSeriesName", "Output series of the dynamic-period indicator (default: its main series)."),
            required: true,
            minItems: 1)
        .Integer(LookbackArgument, $"Stored bars the calculation runs over, newest last. Default and maximum {_limits.MaxCandlesPerRequest}.", defaultValue: _limits.MaxCandlesPerRequest, minimum: 1, maximum: _limits.MaxCandlesPerRequest)
        .Integer(TailArgument, $"Number of trailing bars returned per series. Default {_limits.DefaultOutputTailBars}, at most {_limits.MaxOutputTailBars}.", defaultValue: _limits.DefaultOutputTailBars, minimum: 1, maximum: _limits.MaxOutputTailBars)
        .String(EndDateArgument, $"Last date to include ({ToolJson.DateFormat}). If specified, the latest bar is the last bar dated on or before it. Default: the latest stored bar.")
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

        List<RequestedIndicator> requests = Bind(indicators);
        _chain.Apply(requests);
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

        // The pipeline cannot be cancelled: the call is awaited without the token so that a calculation that outlives its time limit
        // keeps the turn until it ends (the executor has already answered EXECUTION_TIMEOUT by then).
        Dictionary<string, IIndicatorResult> results = await _gateway
            .CalculateIndicatorsAsync(candles, requests.Select(r => r.Settings).ToList(), secondary)
            .ConfigureAwait(false);

        return ToolJson.Build(writer => WriteResult(writer, symbol, timeFrame, endDate, lookback, loaded, droppedLeadingBar, tail, requests, results, warnings, excluded));
    }

    private List<RequestedIndicator> Bind(JsonElement indicators)
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

            requests.Add(new RequestedIndicator(
                index,
                id,
                entry,
                settings,
                Optional(item, "sourceIndicatorId"),
                Optional(item, "sourceOutputSeriesName"),
                Optional(item, "dynamicPeriodIndicatorId"),
                Optional(item, "dynamicPeriodOutputSeriesName")));
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
        Dictionary<string, IIndicatorResult> results,
        List<string> warnings,
        HashSet<string> excludedComparisons)
    {
        int barCount = loaded.Count;
        int returned = Math.Min(tail, barCount);
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
        writer.WriteStartArray("timestamps");
        for (int i = firstReturned; i < barCount; i++)
        {
            writer.WriteStringValue(ToolJson.FormatDate(loaded[i].Timestamp));
        }

        writer.WriteEndArray();
        WriteStrings(writer, "warnings", warnings);
        writer.WriteStartObject("results");
        foreach (RequestedIndicator request in requests)
        {
            writer.WritePropertyName(request.Id);
            WriteIndicator(writer, request, requests, results, barCount, firstReturned, excludedComparisons);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private void WriteIndicator(
        Utf8JsonWriter writer,
        RequestedIndicator request,
        List<RequestedIndicator> requests,
        Dictionary<string, IIndicatorResult> results,
        int barCount,
        int firstReturned,
        HashSet<string> excludedComparisons)
    {
        CoreIndicatorSettings settings = request.Settings;
        IndicatorType type = Enum.Parse<IndicatorType>(request.Entry.Type);
        results.TryGetValue(settings.Id, out IIndicatorResult? result);

        var series = new List<(string Name, IReadOnlyList<decimal?> Values)>();
        if (result is { IsSuccessful: true })
        {
            foreach (string name in request.Entry.OutputSeriesNames)
            {
                if (IndicatorOutputSeriesResolver.TryResolveSeries(result, type, name, settings.PriceSource, out IReadOnlyList<decimal?> values))
                {
                    series.Add((name, values));
                }
            }
        }

        bool anyValue = series.Any(s => s.Values.Any(v => v is not null));
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

            var projected = series.Where(s => s.Values.Count > barCount).ToList();
            if (projected.Count > 0)
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
