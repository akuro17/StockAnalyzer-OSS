using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// sa_extract_signal_events: extracts bars where a user-defined condition evaluates to true,
/// calculating per-event bar gaps, leaf-level comparisons, optional forward return diagnostics, and full boolean series.
/// </summary>
internal sealed class ExtractSignalEventsTool : IMcpTool
{
    private const string SymbolArgument = "symbol";
    private const string TimeFrameArgument = "timeFrame";
    private const string ConditionArgument = "condition";
    private const string StartArgument = "startDate";
    private const string EndArgument = "endDate";
    private const string EventModeArgument = "eventMode";
    private const string ForwardBarsArgument = "forwardBars";
    private const string MaxEventsArgument = "maxEvents";
    private const string OrderArgument = "order";
    private const string IncludeBooleanSeriesArgument = "includeBooleanSeries";
    private const string IncludeEventStudyArgument = "includeEventStudy";
    private const string CompactRowsArgument = "compactRows";
    private const string EventColumnsKey = "eventColumns";
    private const string EventRowsKey = "eventRows";

    private static readonly string[] EventColumns =
    {
        "barIndex", "timestamp", "close", "barsSincePreviousEvent", "comparisons", "forwardReturns", "forwardReturnsFromNextOpen", "lookAheadDiagnostic",
    };

    private const string EveryTrueBarMode = "EveryTrueBar";
    private const string FirstTrueBarOfRunMode = "FirstTrueBarOfRun";
    private static readonly string[] EventModes = { EveryTrueBarMode, FirstTrueBarOfRunMode };

    private const string OldestFirstOrder = "OldestFirst";
    private const string NewestFirstOrder = "NewestFirst";
    private static readonly string[] OrderValues = { OldestFirstOrder, NewestFirstOrder };

    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly SymbolResolver _symbols;
    private readonly CandleWindowLoader _loader;
    private readonly BacktestConditionBinder _conditions;
    private readonly IIndicatorFactory _indicatorFactory;
    private readonly McpLimits _limits;
    private readonly bool _compactDefaults;

    public ExtractSignalEventsTool(
        IReadOnlyStockAnalyzerGateway gateway,
        SymbolResolver symbols,
        BacktestConditionBinder conditions,
        IIndicatorFactory indicatorFactory,
        McpLimits limits,
        bool compactDefaults = false)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _symbols = symbols ?? throw new ArgumentNullException(nameof(symbols));
        _loader = new CandleWindowLoader(gateway);
        _conditions = conditions ?? throw new ArgumentNullException(nameof(conditions));
        _indicatorFactory = indicatorFactory ?? throw new ArgumentNullException(nameof(indicatorFactory));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _compactDefaults = compactDefaults;
    }

    public string Name => "sa_extract_signal_events";

    public string Description =>
        $"Extracts bars matching a backtest condition tree (supports {string.Join(", ", ConditionOperatorExpander.ExpandedComparisonNames)}), with leaf comparisons, forward return diagnostics, optional {IncludeEventStudyArgument}, booleanSeries, and {CompactRowsArgument}. " +
        ToolJson.DocLink(Name);

    public bool IsHeavy => false;

    public string? CompactUsageHint => "Pass compactRows: true, includeBooleanSeries: false, or a smaller maxEvents.";

    public string? ExampleArgumentsJson =>
        "{\"symbol\":\"7203-T\",\"timeFrame\":\"D1\",\"condition\":{\"left\":{\"indicator\":\"RSI\",\"parameters\":{\"Period\":14}},\"comparison\":\"LessThan\",\"right\":30},\"eventMode\":\"FirstTrueBarOfRun\",\"forwardBars\":[5,10],\"maxEvents\":50}";

    public string InputSchemaJson => ToolSchema.Object()
        .String(SymbolArgument, "The symbol to test (a ticker from sa_list_tickers).", required: true)
        .String(TimeFrameArgument, $"Time frame of the bars ({string.Join(", ", ReadOnlyGatewayContract.SupportedBacktestTimeFrames)}).", required: true, allowedValues: Enum.GetNames<TimeFrame>())
        .FreeObject(ConditionArgument, "A condition group {operator: And|Or, children: [...]} or a single comparison {left, comparison, right}.", required: true)
        .String(StartArgument, "First evaluated day, yyyy-MM-dd. Default: start of stored history.")
        .String(EndArgument, "Last evaluated day, yyyy-MM-dd. Default: latest stored bar.")
        .String(EventModeArgument, $"Event filter mode: {EveryTrueBarMode} (default, every bar matching condition) or {FirstTrueBarOfRunMode} (only the initial true bar of consecutive matches).", allowedValues: EventModes)
        .IntegerArray(ForwardBarsArgument, $"Forward bar horizons (each 1..{_limits.MaxForwardBars}, at most {_limits.MaxForwardBarsList} horizons).")
        .Boolean(IncludeEventStudyArgument, $"true adds eventStudy: count, missingCount, mean, median, stdDev, min, max and positiveRatio of the forward returns per horizon and basis over all events (before {MaxEventsArgument}). Needs {ForwardBarsArgument}. Default false.")
        .Integer(MaxEventsArgument, $"Most events to return. Default and maximum {_limits.MaxReturnedEvents}.", defaultValue: _limits.MaxReturnedEvents, minimum: 1, maximum: _limits.MaxReturnedEvents)
        .String(OrderArgument, $"Output ordering: {OldestFirstOrder} (default) or {NewestFirstOrder}.", allowedValues: OrderValues)
        .Boolean(IncludeBooleanSeriesArgument, "Include boolean series for every bar in the evaluation window. Default false.")
        .Boolean(CompactRowsArgument, $"true returns 'events' as [] and writes '{EventColumnsKey}' plus '{EventRowsKey}' (one row per event, comparisons as [path, left, right, result]). Default {(_compactDefaults ? "true" : "false")}.")
        .Boolean(SymbolResolver.AllowUnregisteredArgument, "Accept a symbol that is not in the ticker list but has stored bars. Default false.")
        .Build();

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        TimeFrame timeFrame = ResolveTimeFrame(arguments);
        DateOnly? requestedStart = ToolArgs.Date(arguments, StartArgument);
        DateOnly? requestedEnd = ToolArgs.Date(arguments, EndArgument);
        if (requestedStart.HasValue && requestedEnd.HasValue && requestedStart.Value > requestedEnd.Value)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"{StartArgument} must not be after {EndArgument}.", StartArgument);
        }

        string eventMode = ToolArgs.String(arguments, EventModeArgument) ?? EveryTrueBarMode;
        if (!EventModes.Contains(eventMode, StringComparer.Ordinal))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Unknown eventMode '{eventMode}'.", EventModeArgument, $"Allowed: {string.Join(", ", EventModes)}");
        }

        string order = ToolArgs.String(arguments, OrderArgument) ?? OldestFirstOrder;
        if (!OrderValues.Contains(order, StringComparer.Ordinal))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Unknown order '{order}'.", OrderArgument, $"Allowed: {string.Join(", ", OrderValues)}");
        }

        int maxEvents = ToolArgs.PositiveWithin(arguments, MaxEventsArgument, _limits.MaxReturnedEvents, _limits.MaxReturnedEvents);
        bool includeBooleanSeries = ToolArgs.Boolean(arguments, IncludeBooleanSeriesArgument) ?? false;
        bool compactRows = ToolArgs.Boolean(arguments, CompactRowsArgument) ?? _compactDefaults;

        IReadOnlyList<int> forwardBars = ParseForwardBars(arguments);
        bool includeEventStudy = ToolArgs.Boolean(arguments, IncludeEventStudyArgument) ?? false;
        if (includeEventStudy && forwardBars.Count == 0)
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"'{IncludeEventStudyArgument}' requires '{ForwardBarsArgument}' to be specified.",
                IncludeEventStudyArgument,
                $"Name at least one horizon in {ForwardBarsArgument}, or omit {IncludeEventStudyArgument}.");
        }

        if (!arguments.TryGetProperty(ConditionArgument, out JsonElement conditionElement) || conditionElement.ValueKind != JsonValueKind.Object)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, "condition is required and must be an object.", ConditionArgument);
        }
        IBacktestConditionNode node = _conditions.BindSingleCondition(conditionElement, ConditionArgument, timeFrame);

        bool allowUnregistered = ToolArgs.Boolean(arguments, SymbolResolver.AllowUnregisteredArgument) ?? false;
        ResolvedSymbol resolved = await _symbols.ResolveAsync(ToolArgs.String(arguments, SymbolArgument)!, SymbolArgument, allowUnregistered, cancellationToken).ConfigureAwait(false);
        string symbol = resolved.Symbol;
        IReadOnlyList<string> symbolWarnings = !resolved.IsRegistered
            ? new[] { SymbolResolver.OffListWarning(new[] { resolved.Symbol }) }
            : Array.Empty<string>();

        List<CandleData> stored;
        if (requestedEnd is { } end)
        {
            stored = await _loader.LoadUpToAsync(symbol, timeFrame, end, _limits.MaxCandlesPerRequest, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var (bars, _) = await _loader.LoadAsync(symbol, timeFrame, _limits.MaxCandlesPerRequest, cancellationToken).ConfigureAwait(false);
            stored = bars;
        }

        if (stored.Count == 0)
        {
            throw new ToolException(ToolErrorCodes.InsufficientMarketData, $"No stored {timeFrame} bars exist for {symbol}.", SymbolArgument);
        }

        DateOnly storedStart = DateOnly.FromDateTime(stored[0].Timestamp);
        DateOnly storedEnd = DateOnly.FromDateTime(stored[^1].Timestamp);

        DateOnly effectiveStart = requestedStart ?? storedStart;
        DateOnly effectiveEnd = requestedEnd ?? storedEnd;
        if (effectiveStart > effectiveEnd)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"{StartArgument} must not be after {EndArgument}.", StartArgument);
        }

        int evaluationEndIndex = stored.Count - 1;
        int evaluationStartIndex = -1;
        for (int i = 0; i <= evaluationEndIndex; i++)
        {
            if (DateOnly.FromDateTime(stored[i].Timestamp) >= effectiveStart)
            {
                evaluationStartIndex = i;
                break;
            }
        }

        if (evaluationStartIndex == -1 || evaluationStartIndex > evaluationEndIndex)
        {
            throw new ToolException(ToolErrorCodes.InsufficientMarketData, $"No bars found between {effectiveStart:yyyy-MM-dd} and {effectiveEnd:yyyy-MM-dd}.", StartArgument);
        }

        int loadedBarCount = evaluationEndIndex + 1;
        int evaluatedBarCount = evaluationEndIndex - evaluationStartIndex + 1;

        if (includeBooleanSeries && evaluatedBarCount > _limits.MaxOutputTailBars)
        {
            throw new ToolException(
                ToolErrorCodes.LimitExceeded,
                $"The boolean series for {evaluatedBarCount} bars exceeds the limit of {_limits.MaxOutputTailBars} bars.",
                IncludeBooleanSeriesArgument,
                "Narrow the date range or set includeBooleanSeries to false.");
        }

        IReadOnlyList<CandleData> loadedBars = stored;

        // Indicator requests & computation
        (IReadOnlyList<StrategyIndicatorRequest> requests, IReadOnlyDictionary<BacktestConditionSide, string> mapping) =
            ConditionSeriesEvaluator.BuildIndicatorRequests(node);

        Dictionary<TimeFrame, IReadOnlyList<CandleData>>? additionalBars = null;
        var foreignFrames = requests.Where(r => r.Frame is { } f && f != timeFrame).Select(r => r.Frame!.Value).ToHashSet();
        if (foreignFrames.Count > 0)
        {
            additionalBars = new Dictionary<TimeFrame, IReadOnlyList<CandleData>>(foreignFrames.Count);
            foreach (TimeFrame frame in foreignFrames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<CandleData> foreign;
                if (requestedEnd is { } foreignEnd)
                {
                    foreign = await _loader.LoadUpToAsync(symbol, frame, foreignEnd, _limits.MaxCandlesPerRequest, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var (foreignLoaded, _) = await _loader.LoadAsync(symbol, frame, _limits.MaxCandlesPerRequest, cancellationToken).ConfigureAwait(false);
                    foreign = foreignLoaded;
                }
                additionalBars[frame] = foreign;
            }
        }

        IndicatorSeriesSet indicators = ConditionSeriesEvaluator.PrepareIndicators(
            _indicatorFactory,
            loadedBars,
            timeFrame,
            requests,
            additionalBars,
            cancellationToken);

        bool[] series = ConditionSeriesEvaluator.EvaluateSeries(node, mapping, indicators, 0, loadedBarCount);

        // Event scanning
        var allEvents = new List<ExtractedSignalEvent>();
        int? previousEventBarIndex = null;
        int? lastEventBarIndex = null;

        for (int i = evaluationStartIndex; i <= evaluationEndIndex; i++)
        {
            bool isTrue = series[i];
            if (!isTrue) continue;

            // A run starts where the condition turns true in the whole series: the bar before the evaluation window (warm-up bars are evaluated too)
            // decides, so the result does not depend on startDate (owner decision D-8 = A).
            if (eventMode == FirstTrueBarOfRunMode && i > 0 && series[i - 1])
            {
                // Consecutive match, skip in FirstTrueBarOfRunMode
                continue;
            }

            int? barsSince = previousEventBarIndex is { } prev ? i - prev : null;
            previousEventBarIndex = i;
            lastEventBarIndex = i;

            IReadOnlyList<LeafComparisonDetail> comparisons = ConditionSeriesEvaluator.EvaluateLeaves(node, mapping, indicators, i);

            // Forward returns
            CandleData bar = loadedBars[i];
            var fwdReturns = new Dictionary<string, decimal?>(forwardBars.Count);
            var fwdReturnsNextOpen = new Dictionary<string, decimal?>(forwardBars.Count);

            foreach (int h in forwardBars)
            {
                string key = h.ToString(CultureInfo.InvariantCulture);
                int targetIndex = i + h;
                if (targetIndex <= evaluationEndIndex && bar.Close != 0m)
                {
                    fwdReturns[key] = (loadedBars[targetIndex].Close / bar.Close) - 1m;
                }
                else
                {
                    fwdReturns[key] = null;
                }

                int nextIndex = i + 1;
                if (nextIndex <= evaluationEndIndex && targetIndex <= evaluationEndIndex && loadedBars[nextIndex].Open != 0m)
                {
                    fwdReturnsNextOpen[key] = (loadedBars[targetIndex].Close / loadedBars[nextIndex].Open) - 1m;
                }
                else
                {
                    fwdReturnsNextOpen[key] = null;
                }
            }

            allEvents.Add(new ExtractedSignalEvent(
                i,
                ToolJson.FormatDateTime(bar.Timestamp),
                bar.Close,
                barsSince,
                comparisons,
                fwdReturns,
                fwdReturnsNextOpen));
        }

        int totalEventCount = allEvents.Count;
        bool truncated = totalEventCount > maxEvents;

        IReadOnlyList<ExtractedSignalEvent> returnedEvents;
        if (order == NewestFirstOrder)
        {
            returnedEvents = allEvents.AsEnumerable().Reverse().Take(maxEvents).ToList();
        }
        else
        {
            returnedEvents = allEvents.Take(maxEvents).ToList();
        }

        int? lastEventBarsAgo = lastEventBarIndex is { } lastBar ? evaluationEndIndex - lastBar : null;

        return ToolJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("symbol", symbol);
            writer.WriteString("timeFrame", timeFrame.ToString());
            writer.WriteNumber("loadedBarCount", loadedBarCount);
            writer.WriteNumber("evaluatedBarCount", evaluatedBarCount);
            writer.WriteNumber("eventCount", totalEventCount);
            writer.WriteNumber("returnedEventCount", returnedEvents.Count);
            writer.WriteBoolean("truncated", truncated);
            writer.WriteString("order", order);

            if (lastEventBarsAgo.HasValue)
            {
                writer.WriteNumber("lastEventBarsAgo", lastEventBarsAgo.Value);
            }
            else
            {
                writer.WriteNull("lastEventBarsAgo");
            }

            writer.WriteStartArray("events");
            if (!compactRows)
            {
                foreach (ExtractedSignalEvent ev in returnedEvents)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("barIndex", ev.BarIndex);
                    writer.WriteString("timestamp", ev.Timestamp);
                    writer.WriteNumber("close", ev.Close);

                    if (ev.BarsSincePreviousEvent.HasValue)
                    {
                        writer.WriteNumber("barsSincePreviousEvent", ev.BarsSincePreviousEvent.Value);
                    }
                    else
                    {
                        writer.WriteNull("barsSincePreviousEvent");
                    }

                    writer.WriteStartArray("comparisons");
                    foreach (LeafComparisonDetail comp in ev.Comparisons)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("path", comp.Path);
                        if (comp.Left.HasValue) writer.WriteNumber("left", comp.Left.Value); else writer.WriteNull("left");
                        if (comp.Right.HasValue) writer.WriteNumber("right", comp.Right.Value); else writer.WriteNull("right");
                        writer.WriteBoolean("result", comp.Result);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();

                    WriteForwardReturnObject(writer, "forwardReturns", ev.ForwardReturns);
                    WriteForwardReturnObject(writer, "forwardReturnsFromNextOpen", ev.ForwardReturnsFromNextOpen);

                    writer.WriteBoolean("lookAheadDiagnostic", true);
                    writer.WriteEndObject();
                }
            }
            writer.WriteEndArray();

            if (compactRows)
            {
                ToolJson.WriteStringArray(writer, EventColumnsKey, EventColumns);
                writer.WriteStartArray(EventRowsKey);
                foreach (ExtractedSignalEvent ev in returnedEvents)
                {
                    writer.WriteStartArray();
                    writer.WriteNumberValue(ev.BarIndex);
                    writer.WriteStringValue(ev.Timestamp);
                    writer.WriteNumberValue(ev.Close);
                    ToolJson.WriteNullableValue(writer, ev.BarsSincePreviousEvent);
                    writer.WriteStartArray();
                    foreach (LeafComparisonDetail comp in ev.Comparisons)
                    {
                        writer.WriteStartArray();
                        writer.WriteStringValue(comp.Path);
                        ToolJson.WriteNullableValue(writer, comp.Left);
                        ToolJson.WriteNullableValue(writer, comp.Right);
                        writer.WriteBooleanValue(comp.Result);
                        writer.WriteEndArray();
                    }
                    writer.WriteEndArray();
                    WriteForwardReturnObjectValue(writer, ev.ForwardReturns);
                    WriteForwardReturnObjectValue(writer, ev.ForwardReturnsFromNextOpen);
                    writer.WriteBooleanValue(true);
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
            }

            if (includeEventStudy)
            {
                EventStudySummary.Write(
                    writer,
                    forwardBars.Distinct().ToList(),
                    allEvents.Select(e => e.ForwardReturns).ToList(),
                    allEvents.Select(e => e.ForwardReturnsFromNextOpen).ToList());
            }

            if (includeBooleanSeries)
            {
                writer.WriteStartObject("booleanSeries");
                writer.WriteStartArray("timestamps");
                for (int i = evaluationStartIndex; i <= evaluationEndIndex; i++)
                {
                    writer.WriteStringValue(ToolJson.FormatDateTime(loadedBars[i].Timestamp));
                }
                writer.WriteEndArray();

                writer.WriteStartArray("values");
                for (int i = evaluationStartIndex; i <= evaluationEndIndex; i++)
                {
                    writer.WriteBooleanValue(series[i]);
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull("booleanSeries");
            }

            if (symbolWarnings.Count > 0)
            {
                writer.WriteStartArray("warnings");
                foreach (string warning in symbolWarnings)
                {
                    writer.WriteStringValue(warning);
                }
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        });
    }

    private static TimeFrame ResolveTimeFrame(JsonElement arguments)
    {
        string? name = ToolArgs.String(arguments, TimeFrameArgument);
        if (name is null)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, "timeFrame is required.", TimeFrameArgument);
        }

        if (!Enum.TryParse<TimeFrame>(name, ignoreCase: true, out TimeFrame frame) ||
            !ReadOnlyGatewayContract.SupportedBacktestTimeFrames.Contains(frame))
        {
            throw new ToolException(
                ToolErrorCodes.UnsupportedTimeFrame,
                $"Time frame '{name}' is not supported.",
                TimeFrameArgument,
                $"Supported: {string.Join(", ", ReadOnlyGatewayContract.SupportedBacktestTimeFrames)}.");
        }

        return frame;
    }

    private IReadOnlyList<int> ParseForwardBars(JsonElement arguments)
    {
        if (!arguments.TryGetProperty(ForwardBarsArgument, out JsonElement element) || element.ValueKind == JsonValueKind.Null)
        {
            return Array.Empty<int>();
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, "forwardBars must be an array of integers.", ForwardBarsArgument);
        }

        int count = element.GetArrayLength();
        if (count > _limits.MaxForwardBarsList)
        {
            throw new ToolException(
                ToolErrorCodes.LimitExceeded,
                $"forwardBars has {count} entries; maximum is {_limits.MaxForwardBarsList}.",
                ForwardBarsArgument);
        }

        var result = new List<int>(count);
        int index = 0;
        foreach (JsonElement item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out int h) || h < 1 || h > _limits.MaxForwardBars)
            {
                throw new ToolException(
                    ToolErrorCodes.InvalidArgument,
                    $"forwardBars[{index}] must be an integer between 1 and {_limits.MaxForwardBars}.",
                    $"{ForwardBarsArgument}[{index}]");
            }

            result.Add(h);
            index++;
        }

        return result;
    }

    private static void WriteForwardReturnObject(Utf8JsonWriter writer, string propertyName, IReadOnlyDictionary<string, decimal?> returns)
    {
        writer.WriteStartObject(propertyName);
        WriteForwardReturnEntries(writer, returns);
        writer.WriteEndObject();
    }

    private static void WriteForwardReturnObjectValue(Utf8JsonWriter writer, IReadOnlyDictionary<string, decimal?> returns)
    {
        writer.WriteStartObject();
        WriteForwardReturnEntries(writer, returns);
        writer.WriteEndObject();
    }

    private static void WriteForwardReturnEntries(Utf8JsonWriter writer, IReadOnlyDictionary<string, decimal?> returns)
    {
        foreach ((string k, decimal? val) in returns)
        {
            if (val.HasValue) writer.WriteNumber(k, val.Value); else writer.WriteNull(k);
        }
    }

    private sealed record ExtractedSignalEvent(
        int BarIndex,
        string Timestamp,
        decimal Close,
        int? BarsSincePreviousEvent,
        IReadOnlyList<LeafComparisonDetail> Comparisons,
        IReadOnlyDictionary<string, decimal?> ForwardReturns,
        IReadOnlyDictionary<string, decimal?> ForwardReturnsFromNextOpen);
}
