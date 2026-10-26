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
    private const string CompleteBarsOnlyArgument = "completeBarsOnly";
    private const string PriceUnit = "price";
    private const string VolumeUnit = "shares";
    private const string PeriodUnit = "bars";

    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly CandleWindowLoader _loader;
    private readonly SymbolResolver _symbols;
    private readonly McpLimits _limits;

    public GetCandlesTool(IReadOnlyStockAnalyzerGateway gateway, SymbolResolver symbols, McpLimits limits)
    {
        _gateway = gateway;
        _loader = new CandleWindowLoader(gateway);
        _symbols = symbols;
        _limits = limits;
    }

    public string Name => "sa_get_candles";

    public string Description =>
        "Returns OHLCV bars of one symbol in chronological order (oldest first), exactly as stored. " +
        $"Supported time frames: {SupportedNames}; others return UNSUPPORTED_TIMEFRAME. " +
        $"Without dates the latest 'count' bars are returned. With startDate and/or endDate (inclusive, {ToolJson.DateFormat}) the bars are filtered inside the " +
        $"latest {_limits.MaxCandlesPerRequest} bars of the frame and then the latest 'count' of them are returned. " +
        "For W1 and MN1 with endDate the daily bars are cut at endDate before the weekly or monthly bars are built, so no day after endDate reaches a returned bar " +
        $"(the window is then the latest {_limits.MaxCandlesPerRequest} bars up to endDate). " +
        "Timestamps are bar dates as stored (not UTC-qualified); for W1 and MN1 the timestamp is the first trading day of the period and the oldest bar may cover a partial period. " +
        "The last W1 or MN1 bar is partial when its period (ISO week ending Sunday, calendar month) ends after referenceDate (endDate, or the last stored day); " +
        "lastBarPartial says so, and completeBarsOnly=true drops such a bar (droppedPartialBar). " +
        "Prices are in the instrument's price units; volume is in shares.";

    public bool IsHeavy => false;

    public string? ExampleArgumentsJson => "{\"symbol\":\"7203-T\",\"timeFrame\":\"D1\",\"count\":200}";

    public string InputSchemaJson => ToolSchema.Object()
        .String(SymbolArgument, "Ticker symbol; '.' and '-' are interchangeable (7203.T and 7203-T are the same).", required: true)
        .String(TimeFrameArgument, $"Time frame of the bars. Supported: {SupportedNames}.", required: true, allowedValues: Enum.GetNames<TimeFrame>())
        .Integer(CountArgument, $"Number of bars in the requested time frame. Default {_limits.DefaultCandleCount}, at most {_limits.MaxCandlesPerRequest}.", defaultValue: _limits.DefaultCandleCount, minimum: 1, maximum: _limits.MaxCandlesPerRequest)
        .String(StartDateArgument, $"First date to include ({ToolJson.DateFormat}).")
        .String(EndDateArgument, $"Last date to include ({ToolJson.DateFormat}).")
        .Boolean(CompleteBarsOnlyArgument, "W1 and MN1 only: drop the last bar when its period has not ended yet (see lastBarPartial). Default false.")
        .Build();

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

        bool completeBarsOnly = ToolArgs.Boolean(arguments, CompleteBarsOnlyArgument) ?? false;
        bool aggregated = timeFrame is TimeFrame.W1 or TimeFrame.MN1;
        bool filterByDate = start is not null || end is not null;
        int loadCount = filterByDate ? _limits.MaxCandlesPerRequest : count;

        // Read before the bars: a Sync can replace the stored file between the two reads, and a reference date read after the bars could be later than
        // the data behind them (a week would then look complete). Read first, it is never later, so the flag can only err towards "partial".
        DateTime? referenceDate = aggregated ? await ReferenceDateAsync(symbol, end, cancellationToken).ConfigureAwait(false) : null;

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
        if (completeBarsOnly && IsPartial(selected, timeFrame, referenceDate))
        {
            selected = selected.Take(selected.Count - 1).ToList();
            droppedPartialBar = true;
        }

        DateTime? lastBarPeriodEnd = aggregated && selected.Count > 0 ? TimeFrameAggregator.PeriodEnd(selected[^1].Timestamp, timeFrame) : null;
        bool lastBarPartial = IsPartial(selected, timeFrame, referenceDate);
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
            WriteDateOrNull(writer, "referenceDate", referenceDate);
            WriteDateOrNull(writer, "lastBarPeriodEnd", lastBarPeriodEnd);
            writer.WriteBoolean("lastBarPartial", lastBarPartial);
            writer.WriteBoolean("droppedPartialBar", droppedPartialBar);
            writer.WriteStartArray("candles");
            foreach (CandleData bar in selected)
            {
                writer.WriteStartObject();
                writer.WriteString("timestamp", ToolJson.FormatDate(bar.Timestamp));
                writer.WriteNumber("open", bar.Open);
                writer.WriteNumber("high", bar.High);
                writer.WriteNumber("low", bar.Low);
                writer.WriteNumber("close", bar.Close);
                writer.WriteNumber("volume", bar.Volume);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    /// <summary>
    /// The date the bars are known up to: endDate, but never later than the last stored daily bar; null when nothing is stored.
    /// A weekly or monthly period is complete only once this date reaches its last calendar day.
    /// </summary>
    private async Task<DateTime?> ReferenceDateAsync(string symbol, DateOnly? end, CancellationToken cancellationToken)
    {
        IReadOnlyList<CandleData> latest = await _gateway.GetCandlesAsync(symbol, TimeFrame.D1, 1, cancellationToken).ConfigureAwait(false);
        if (latest.Count == 0)
        {
            return null;
        }

        DateTime lastStored = latest[^1].Timestamp.Date;
        return end is { } endDate && endDate.ToDateTime(TimeOnly.MinValue) < lastStored ? endDate.ToDateTime(TimeOnly.MinValue) : lastStored;
    }

    private static bool IsPartial(List<CandleData> bars, TimeFrame timeFrame, DateTime? referenceDate)
        => bars.Count > 0
            && referenceDate is { } reference
            && TimeFrameAggregator.IsPartialPeriod(bars[^1].Timestamp, timeFrame, reference);

    private static void WriteDateOrNull(Utf8JsonWriter writer, string name, DateTime? value)
    {
        if (value is { } date)
        {
            writer.WriteString(name, ToolJson.FormatDate(date));
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static bool InRange(DateOnly date, DateOnly? start, DateOnly? end)
        => (start is null || date >= start) && (end is null || date <= end);
}
