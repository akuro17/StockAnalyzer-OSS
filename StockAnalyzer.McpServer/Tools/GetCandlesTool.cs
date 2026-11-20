using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>sa_get_candles: OHLCV bars of one symbol and time frame, chronological, as stored.</summary>
internal sealed class GetCandlesTool : IMcpTool
{
    private const string SymbolArgument = "symbol";
    private const string TimeFrameArgument = "timeFrame";
    private const string CountArgument = "count";
    private const string StartDateArgument = "startDate";
    private const string EndDateArgument = "endDate";
    private const string PriceUnit = "price";
    private const string VolumeUnit = "shares";
    private const string PeriodUnit = "bars";
    private const string CompactRowsArgument = "compactRows";
    private const string CandleColumnsKey = "candleColumns";
    private const string CandleRowsKey = "candleRows";

    /// <summary>The column order of every <c>candleRows</c> row (the names of the object form of a candle).</summary>
    private static readonly string[] CandleColumns = { "timestamp", "open", "high", "low", "close", "volume" };

    private readonly CandleWindowLoader _loader;
    private readonly SymbolResolver _symbols;
    private readonly McpLimits _limits;
    private readonly bool _compactDefaults;

    public GetCandlesTool(IReadOnlyStockAnalyzerGateway gateway, SymbolResolver symbols, McpLimits limits, bool compactDefaults = false)
    {
        _loader = new CandleWindowLoader(gateway);
        _symbols = symbols;
        _limits = limits;
        _compactDefaults = compactDefaults;
    }

    public string Name => "sa_get_candles";

    public string Description =>
        $"Returns stored OHLCV bars for one symbol ({SupportedNames}) in chronological order, supporting startDate/endDate, completeBarsOnly, decimalPlaces, and compactRows ({CandleColumnsKey}/{CandleRowsKey}). Read-only. " +
        ToolJson.DocLink(Name);

    public bool IsHeavy => false;

    public string? CompactUsageHint => "Pass compactRows: true, decimalPlaces: 4, or a smaller count / narrower startDate-endDate window.";

    public string? ExampleArgumentsJson => "{\"symbol\":\"7203-T\",\"timeFrame\":\"D1\",\"count\":200}";

    public string InputSchemaJson => ToolSchema.Object()
        .String(SymbolArgument, "Ticker symbol; '.' and '-' are interchangeable (7203.T and 7203-T are the same).", required: true)
        .String(TimeFrameArgument, $"Time frame of the bars. Supported: {SupportedNames}.", required: true, allowedValues: Enum.GetNames<TimeFrame>())
        .Integer(CountArgument, $"Number of bars in the requested time frame. Default {_limits.DefaultCandleCount}, at most {_limits.MaxCandlesPerRequest}.", defaultValue: _limits.DefaultCandleCount, minimum: 1, maximum: _limits.MaxCandlesPerRequest)
        .String(StartDateArgument, $"First date to include ({ToolJson.DateFormat}).")
        .String(EndDateArgument, $"Last date to include ({ToolJson.DateFormat}).")
        .Boolean(PartialBarOutput.CompleteBarsOnlyArgument, "W1 and MN1 only: drop the last bar when its period has not ended yet (see lastBarPartial). Default false.")
        .Integer(SeriesOutputOptions.DecimalPlacesArgument, $"Round open, high, low and close to this many decimal places (0 to {SeriesOutputOptions.MaxDecimalPlaces}; away from zero at the midpoint). Volume is never rounded. Default: prices as stored.", minimum: 0, maximum: SeriesOutputOptions.MaxDecimalPlaces)
        .Boolean(CompactRowsArgument, $"true returns 'candles' as an empty array and the bars as '{CandleColumnsKey}' plus '{CandleRowsKey}' (one row per bar, in the order of the columns). Default false.")
        .Build();

    public string? OutputSchemaJson => OutputSchema;

    private static readonly string OutputSchema = BuildOutputSchema();

    private static string BuildOutputSchema()
    {
        var unitsSchema = ToolSchema.Object()
            .String("price", "Price unit.", required: true)
            .String("volume", "Volume unit.", required: true);

        var candleItem = ToolSchema.Object()
            .String("timestamp", "Bar timestamp.", required: true)
            .Number("open", "Open price.", required: true)
            .Number("high", "High price.", required: true)
            .Number("low", "Low price.", required: true)
            .Number("close", "Close price.", required: true)
            .Number("volume", "Volume in shares.", required: true);

        return ToolSchema.Object()
            .String("symbol", "Ticker symbol.", required: true)
            .String("timeFrame", "Time frame of the bars.", required: true)
            .String("periodUnit", "Unit of period parameters.", required: true)
            .String("timestampFormat", "Date/time format of bar timestamps.", required: true)
            .NestedObject("units", "Units of prices and volumes.", unitsSchema, required: true)
            .Integer("barCount", "Number of returned bars.", required: true)
            .Integer("requestedCount", "Requested bar count.", required: true)
            .Integer("loadedBarCount", "Number of bars loaded from storage.", required: true)
            .Integer("matchedBarCount", "Number of bars matched by date filter.", required: true)
            .NullableString("earliestAvailableTimestamp", "Earliest available bar date in storage.")
            .Boolean("filteredByDate", "True when date range filter was applied.", required: true)
            .Boolean("truncatedByLoadCap", "True when load cap truncated history.", required: true)
            .Boolean("droppedLeadingBar", "True when oldest aggregated bar was partial.", required: true)
            .NullableString(PartialBarOutput.ReferenceDateKey, "Reference date for partial bar calculation.")
            .NullableString(PartialBarOutput.LastBarPeriodEndKey, "Period end date for the last bar.")
            .Boolean(PartialBarOutput.LastBarPartialKey, "True when the latest bar covers an incomplete period.", required: true)
            .Boolean(PartialBarOutput.DroppedPartialBarKey, "True when incomplete latest bar was dropped.", required: true)
            .ObjectArray("candles", "OHLCV bars in chronological order; an empty array when compactRows is true.", candleItem, required: true)
            .StringArray(CandleColumnsKey, "Column names of every candleRows row; present only when compactRows is true.")
            .RowArray(CandleRowsKey, "One [timestamp, open, high, low, close, volume] row per bar, oldest first; present only when compactRows is true.")
            .Build();
    }

    private static string SupportedNames => ToolArgs.SupportedTimeFrameNames;

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        string symbol = await _symbols.ResolveAsync(ToolArgs.String(arguments, SymbolArgument)!, SymbolArgument, cancellationToken).ConfigureAwait(false);
        TimeFrame timeFrame = ToolArgs.SupportedTimeFrame(arguments, TimeFrameArgument);
        int count = ToolArgs.PositiveWithin(arguments, CountArgument, _limits.DefaultCandleCount, _limits.MaxCandlesPerRequest);
        DateOnly? start = ToolArgs.Date(arguments, StartDateArgument);
        DateOnly? end = ToolArgs.Date(arguments, EndDateArgument);
        if (start > end)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"'{StartDateArgument}' is after '{EndDateArgument}'.", StartDateArgument);
        }

        int? decimalPlaces = SeriesOutputOptions.ReadDecimalPlaces(arguments, _compactDefaults ? SeriesOutputOptions.DefaultCompactDecimalPlaces : null);
        bool compactRows = ToolArgs.Boolean(arguments, CompactRowsArgument) ?? _compactDefaults;
        bool completeBarsOnly = ToolArgs.Boolean(arguments, PartialBarOutput.CompleteBarsOnlyArgument) ?? false;
        bool aggregated = TimeFrameAggregator.IsPeriodFrame(timeFrame);
        bool filterByDate = start is not null || end is not null;
        int loadCount = filterByDate ? _limits.MaxCandlesPerRequest : count;

        // Read before the bars: a Sync can replace the stored file between the two reads, and a reference date read after the bars could be later than
        // the data behind them (a week would then look complete). Read first, it is never later, so the flag can only err towards "partial".
        DateTime? referenceDate = aggregated ? await _loader.ReferenceDateAsync(symbol, end, cancellationToken).ConfigureAwait(false) : null;

        List<CandleData> loaded;
        bool droppedLeadingBar;
        if (aggregated && end is not null)
        {
            // The daily bars are cut at endDate BEFORE the period bars are built: a bar dated by its first day would otherwise carry later days.
            loaded = await _loader.LoadUpToAsync(symbol, timeFrame, end.Value, loadCount, cancellationToken).ConfigureAwait(false);
            droppedLeadingBar = false; // the whole history is cut, so the oldest bar is partial only where the stored history itself starts
        }
        else
        {
            (loaded, droppedLeadingBar) = await _loader.LoadAsync(symbol, timeFrame, loadCount, cancellationToken).ConfigureAwait(false);
        }

        List<CandleData> selected = loaded;
        if (filterByDate)
        {
            selected = loaded.Where(c => InRange(DateOnly.FromDateTime(c.Timestamp), start, end)).ToList();
        }

        bool droppedPartialBar = false;
        if (completeBarsOnly && CandleWindowLoader.IsLastBarPartial(selected, timeFrame, referenceDate))
        {
            selected = selected.Take(selected.Count - 1).ToList();
            droppedPartialBar = true;
        }

        DateTime? lastBarPeriodEnd = aggregated && selected.Count > 0 ? TimeFrameAggregator.PeriodEnd(selected[^1].Timestamp, timeFrame) : null;
        bool lastBarPartial = CandleWindowLoader.IsLastBarPartial(selected, timeFrame, referenceDate);
        int matchedBarCount = selected.Count;
        if (selected.Count > count)
        {
            selected = selected.Skip(selected.Count - count).ToList();
        }

        bool truncatedByLoadCap = start is not null
            && loaded.Count >= loadCount
            && DateOnly.FromDateTime(loaded[0].Timestamp) > start;

        return ToolJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("symbol", symbol);
            writer.WriteString("timeFrame", timeFrame.ToString());
            writer.WriteString("periodUnit", PeriodUnit);
            writer.WriteString("timestampFormat", ToolJson.DateFormat);
            writer.WriteStartObject("units");
            writer.WriteString("price", PriceUnit);
            writer.WriteString("volume", VolumeUnit);
            writer.WriteEndObject();
            writer.WriteNumber("barCount", selected.Count);
            writer.WriteNumber("requestedCount", count);
            writer.WriteNumber("loadedBarCount", loaded.Count);
            writer.WriteNumber("matchedBarCount", matchedBarCount);
            if (loaded.Count > 0)
            {
                writer.WriteString("earliestAvailableTimestamp", ToolJson.FormatDate(loaded[0].Timestamp));
            }
            else
            {
                writer.WriteNull("earliestAvailableTimestamp");
            }

            writer.WriteBoolean("filteredByDate", filterByDate);
            writer.WriteBoolean("truncatedByLoadCap", truncatedByLoadCap);
            writer.WriteBoolean("droppedLeadingBar", droppedLeadingBar);
            PartialBarOutput.Write(writer, referenceDate, lastBarPeriodEnd, lastBarPartial, droppedPartialBar);
            writer.WriteStartArray("candles");
            if (!compactRows)
            {
                foreach (CandleData bar in selected)
                {
                    writer.WriteStartObject();
                    writer.WriteString("timestamp", ToolJson.FormatDate(bar.Timestamp));
                    writer.WriteNumber("open", SeriesOutputOptions.RoundIfRequested(bar.Open, decimalPlaces));
                    writer.WriteNumber("high", SeriesOutputOptions.RoundIfRequested(bar.High, decimalPlaces));
                    writer.WriteNumber("low", SeriesOutputOptions.RoundIfRequested(bar.Low, decimalPlaces));
                    writer.WriteNumber("close", SeriesOutputOptions.RoundIfRequested(bar.Close, decimalPlaces));
                    writer.WriteNumber("volume", bar.Volume);
                    writer.WriteEndObject();
                }
            }

            writer.WriteEndArray();
            if (compactRows)
            {
                writer.WriteStartArray(CandleColumnsKey);
                foreach (string column in CandleColumns)
                {
                    writer.WriteStringValue(column);
                }

                writer.WriteEndArray();
                writer.WriteStartArray(CandleRowsKey);
                foreach (CandleData bar in selected)
                {
                    writer.WriteStartArray();
                    writer.WriteStringValue(ToolJson.FormatDate(bar.Timestamp));
                    writer.WriteNumberValue(SeriesOutputOptions.RoundIfRequested(bar.Open, decimalPlaces));
                    writer.WriteNumberValue(SeriesOutputOptions.RoundIfRequested(bar.High, decimalPlaces));
                    writer.WriteNumberValue(SeriesOutputOptions.RoundIfRequested(bar.Low, decimalPlaces));
                    writer.WriteNumberValue(SeriesOutputOptions.RoundIfRequested(bar.Close, decimalPlaces));
                    writer.WriteNumberValue(bar.Volume);
                    writer.WriteEndArray();
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        });
    }

    private static bool InRange(DateOnly date, DateOnly? start, DateOnly? end)
        => (start is null || date >= start) && (end is null || date <= end);
}
