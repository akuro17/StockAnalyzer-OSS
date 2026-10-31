using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.DivergenceCross;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// sa_detect_divergences: structures and exposes divergence and moving-average crossover signals
/// detected by the Core DivergenceCrossDetector over stored candles and indicator series.
/// </summary>
internal sealed class DetectDivergencesTool : IMcpTool
{
    private const string SymbolArgument = "symbol";
    private const string TimeFrameArgument = "timeFrame";
    private const string StartArgument = "startDate";
    private const string EndArgument = "endDate";
    private const string IndicatorArgument = "indicator";
    private const string IndicatorParametersArgument = "indicatorParameters";
    private const string PivotOrderArgument = "pivotOrder";
    private const string DetectCrossesArgument = "detectCrosses";
    private const string ShortMaPeriodArgument = "shortMaPeriod";
    private const string LongMaPeriodArgument = "longMaPeriod";
    private const string MaxEventsArgument = "maxEvents";

    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly SymbolResolver _symbols;
    private readonly CandleWindowLoader _loader;
    private readonly IndicatorCatalog _catalog;
    private readonly IndicatorSettingsBinder _binder;
    private readonly IIndicatorFactory _indicatorFactory;
    private readonly McpLimits _limits;

    public DetectDivergencesTool(
        IReadOnlyStockAnalyzerGateway gateway,
        SymbolResolver symbols,
        IndicatorCatalog catalog,
        IndicatorSettingsBinder binder,
        IIndicatorFactory indicatorFactory,
        McpLimits limits)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _symbols = symbols ?? throw new ArgumentNullException(nameof(symbols));
        _loader = new CandleWindowLoader(gateway);
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _binder = binder ?? throw new ArgumentNullException(nameof(binder));
        _indicatorFactory = indicatorFactory ?? throw new ArgumentNullException(nameof(indicatorFactory));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
    }

    public string Name => "sa_detect_divergences";

    public string Description =>
        "Detects technical divergences between price swings and an indicator (e.g. RSI, MACD), " +
        "and moving-average crossovers (Golden Cross / Dead Cross) using the application's DivergenceCrossDetector. " +
        "Outputs exact pivot timestamps, indices, price values, and indicator values without inventing ad-hoc strength scores.";

    public bool IsHeavy => false;

    public string? ExampleArgumentsJson =>
        "{\"symbol\":\"7203-T\",\"timeFrame\":\"D1\",\"indicator\":\"RSI\",\"pivotOrder\":5,\"detectCrosses\":true,\"shortMaPeriod\":25,\"longMaPeriod\":75}";

    public string InputSchemaJson => ToolSchema.Object()
        .String(SymbolArgument, "The symbol to test (a ticker from sa_list_tickers).", required: true)
        .String(TimeFrameArgument, $"Time frame of the bars ({string.Join(", ", ReadOnlyGatewayContract.SupportedBacktestTimeFrames)}). Default D1.", allowedValues: Enum.GetNames<TimeFrame>())
        .String(StartArgument, "First evaluated day, yyyy-MM-dd. Default: start of stored history.")
        .String(EndArgument, "Last evaluated day, yyyy-MM-dd. Default: latest stored bar.")
        .String(IndicatorArgument, "Indicator type to compare price against for divergence (e.g. RSI, MACD, Stochastics). Default RSI.")
        .FreeObject(IndicatorParametersArgument, "Optional parameters for the divergence indicator (e.g. {\"Period\": 14}).")
        .Integer(PivotOrderArgument, "Number of bars on each side to confirm a swing high/low (2..50). Default 5.", defaultValue: IndicatorDefaultConstants.DivergencePivotLookback, minimum: 2, maximum: 50)
        .Boolean(DetectCrossesArgument, "Also detect moving average crossovers (Golden Cross / Dead Cross). Default false.")
        .Integer(ShortMaPeriodArgument, "Short-term moving average period for GC/DC (1..200). Default 25.", defaultValue: IndicatorDefaultConstants.CrossShortPeriod, minimum: 1, maximum: 200)
        .Integer(LongMaPeriodArgument, "Long-term moving average period for GC/DC (2..500). Default 75.", defaultValue: IndicatorDefaultConstants.CrossLongPeriod, minimum: 2, maximum: 500)
        .Integer(MaxEventsArgument, $"Most events to return. Default and maximum {_limits.MaxReturnedEvents}.", defaultValue: _limits.MaxReturnedEvents, minimum: 1, maximum: _limits.MaxReturnedEvents)
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

        int pivotOrder = (int)(ToolArgs.Integer(arguments, PivotOrderArgument) ?? IndicatorDefaultConstants.DivergencePivotLookback);
        if (pivotOrder < 2 || pivotOrder > 50)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, "pivotOrder must be between 2 and 50.", PivotOrderArgument);
        }

        bool detectCrosses = ToolArgs.Boolean(arguments, DetectCrossesArgument) ?? false;
        int shortMaPeriod = (int)(ToolArgs.Integer(arguments, ShortMaPeriodArgument) ?? IndicatorDefaultConstants.CrossShortPeriod);
        int longMaPeriod = (int)(ToolArgs.Integer(arguments, LongMaPeriodArgument) ?? IndicatorDefaultConstants.CrossLongPeriod);

        if (shortMaPeriod < 1 || shortMaPeriod > 200)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, "shortMaPeriod must be between 1 and 200.", ShortMaPeriodArgument);
        }
        if (longMaPeriod < 2 || longMaPeriod > 500)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, "longMaPeriod must be between 2 and 500.", LongMaPeriodArgument);
        }
        if (shortMaPeriod >= longMaPeriod)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, "shortMaPeriod must be strictly less than longMaPeriod.", ShortMaPeriodArgument);
        }

        int maxEvents = ToolArgs.PositiveWithin(arguments, MaxEventsArgument, _limits.MaxReturnedEvents, _limits.MaxReturnedEvents);

        string indicatorName = ToolArgs.String(arguments, IndicatorArgument) ?? "RSI";
        IndicatorCatalogEntry entry = _catalog.Find(indicatorName)
            ?? throw new ToolException(ToolErrorCodes.UnknownIndicator, $"Unknown indicator type '{indicatorName}'.", IndicatorArgument, "Call sa_get_capabilities to list the type names.");
        if (!entry.IsRegistered)
        {
            throw new ToolException(
                ToolErrorCodes.UnknownIndicator,
                $"Indicator type '{entry.Type}' is defined but cannot be calculated.",
                IndicatorArgument,
                "Types with isRegistered false in sa_get_capabilities are not available.");
        }

        BacktestIndicatorViolationReason violation = BacktestIndicatorEligibility.Check(Enum.Parse<IndicatorType>(entry.Type), null);
        if (violation != BacktestIndicatorViolationReason.None)
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"Indicator '{entry.Type}' cannot be used for divergence detection ({violation}).",
                IndicatorArgument,
                "Non-causal indicators (e.g. ZigZag) and unsupported types cannot be used.");
        }

        if (entry.IsCrossTicker)
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"Cross-ticker indicator '{entry.Type}' cannot be used for divergence detection.",
                IndicatorArgument);
        }

        arguments.TryGetProperty(IndicatorParametersArgument, out JsonElement paramElem);
        if (paramElem.ValueKind != JsonValueKind.Undefined && paramElem.ValueKind != JsonValueKind.Null && paramElem.ValueKind != JsonValueKind.Object)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, "indicatorParameters must be an object.", IndicatorParametersArgument);
        }

        CoreIndicatorSettings settings = _binder.Bind(entry, paramElem, null, IndicatorParametersArgument);

        bool allowUnregistered = ToolArgs.Boolean(arguments, SymbolResolver.AllowUnregisteredArgument) ?? false;
        ResolvedSymbol resolved = await _symbols.ResolveAsync(ToolArgs.String(arguments, SymbolArgument)!, SymbolArgument, allowUnregistered, cancellationToken).ConfigureAwait(false);
        string symbol = resolved.Symbol;
        IReadOnlyList<string> symbolWarnings = !resolved.IsRegistered
            ? new[] { SymbolResolver.OffListWarning(new[] { resolved.Symbol }) }
            : Array.Empty<string>();

        List<CandleData> stored;
        if (requestedEnd is { } reqEnd)
        {
            stored = await _loader.LoadUpToAsync(symbol, timeFrame, reqEnd, _limits.MaxCandlesPerRequest, cancellationToken).ConfigureAwait(false);
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

        IReadOnlyList<CandleData> loadedBars = stored;

        // Calculate indicator
        IndicatorType indicatorType = Enum.Parse<IndicatorType>(entry.Type);
        ICoreIndicator indicator = _indicatorFactory.Create(indicatorType, settings.ParameterObject)
            ?? throw new ToolException(ToolErrorCodes.InvalidArgument, $"Failed to create indicator '{entry.Type}'.", IndicatorArgument);

        var coreCandles = new CoreCandleData[loadedBars.Count];
        for (int i = 0; i < loadedBars.Count; i++)
        {
            CandleData bar = loadedBars[i];
            coreCandles[i] = new CoreCandleData(bar.Timestamp, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume);
        }

        IIndicatorResult indicatorResult = indicator.Calculate(coreCandles);
        if (!indicatorResult.IsSuccessful)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Indicator calculation failed: {indicatorResult.ErrorMessage}", IndicatorArgument);
        }

        var priceHighs = loadedBars.Select(b => b.High).ToList();
        var priceLows = loadedBars.Select(b => b.Low).ToList();
        var indicatorValues = indicatorResult.MainValues;

        // Run Divergence detection
        IReadOnlyList<DivergenceSignal> divergences = DivergenceCrossDetector.DetectDivergences(
            priceHighs, priceLows, indicatorValues, pivotOrder);

        var events = new List<DetectedSignalEvent>();

        foreach (DivergenceSignal d in divergences)
        {
            // Event occurs when the second pivot is confirmed; filter by evaluation window
            if (d.PriceEndIndex >= evaluationStartIndex && d.PriceEndIndex <= evaluationEndIndex)
            {
                events.Add(new DetectedSignalEvent(
                    Kind: "Divergence",
                    SignalType: d.Type.ToString(),
                    Direction: ResolveDirection(d.Type),
                    TriggerIndex: d.PriceEndIndex,
                    Timestamp: ToolJson.FormatDateTime(loadedBars[d.PriceEndIndex].Timestamp),
                    PriceStartIndex: d.PriceStartIndex,
                    PriceEndIndex: d.PriceEndIndex,
                    PriceStartTime: ToolJson.FormatDateTime(loadedBars[d.PriceStartIndex].Timestamp),
                    PriceEndTime: ToolJson.FormatDateTime(loadedBars[d.PriceEndIndex].Timestamp),
                    PriceStartValue: d.PriceStartValue,
                    PriceEndValue: d.PriceEndValue,
                    IndicatorStartIndex: d.IndicatorStartIndex,
                    IndicatorEndIndex: d.IndicatorEndIndex,
                    IndicatorStartTime: ToolJson.FormatDateTime(loadedBars[d.IndicatorStartIndex].Timestamp),
                    IndicatorEndTime: ToolJson.FormatDateTime(loadedBars[d.IndicatorEndIndex].Timestamp),
                    IndicatorStartValue: d.IndicatorStartValue,
                    IndicatorEndValue: d.IndicatorEndValue,
                    CrossIndex: null,
                    ShortValue: null,
                    LongValue: null));
            }
        }

        // Run Cross detection if requested
        if (detectCrosses)
        {
            ICoreIndicator shortMa = _indicatorFactory.Create(IndicatorType.SMA, new CoreSmaParameter { Period = shortMaPeriod })
                ?? throw new InvalidOperationException("Failed to create short SMA indicator.");
            ICoreIndicator longMa = _indicatorFactory.Create(IndicatorType.SMA, new CoreSmaParameter { Period = longMaPeriod })
                ?? throw new InvalidOperationException("Failed to create long SMA indicator.");

            IIndicatorResult shortResult = shortMa.Calculate(coreCandles);
            IIndicatorResult longResult = longMa.Calculate(coreCandles);

            if (shortResult.IsSuccessful && longResult.IsSuccessful)
            {
                IReadOnlyList<CrossSignal> crosses = DivergenceCrossDetector.DetectCrosses(shortResult.MainValues, longResult.MainValues);
                foreach (CrossSignal c in crosses)
                {
                    if (c.CrossIndex >= evaluationStartIndex && c.CrossIndex <= evaluationEndIndex)
                    {
                        events.Add(new DetectedSignalEvent(
                            Kind: "Cross",
                            SignalType: c.Type.ToString(),
                            Direction: ResolveDirection(c.Type),
                            TriggerIndex: c.CrossIndex,
                            Timestamp: ToolJson.FormatDateTime(loadedBars[c.CrossIndex].Timestamp),
                            PriceStartIndex: null,
                            PriceEndIndex: null,
                            PriceStartTime: null,
                            PriceEndTime: null,
                            PriceStartValue: null,
                            PriceEndValue: null,
                            IndicatorStartIndex: null,
                            IndicatorEndIndex: null,
                            IndicatorStartTime: null,
                            IndicatorEndTime: null,
                            IndicatorStartValue: null,
                            IndicatorEndValue: null,
                            CrossIndex: c.CrossIndex,
                            ShortValue: c.ShortValue,
                            LongValue: c.LongValue));
                    }
                }
            }
        }

        // Sort events chronologically by trigger index
        events.Sort((a, b) => a.TriggerIndex.CompareTo(b.TriggerIndex));

        int totalEventCount = events.Count;
        bool truncated = totalEventCount > maxEvents;
        IReadOnlyList<DetectedSignalEvent> returnedEvents = events.Take(maxEvents).ToList();

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

            writer.WriteStartArray("events");
            foreach (DetectedSignalEvent ev in returnedEvents)
            {
                writer.WriteStartObject();
                writer.WriteString("kind", ev.Kind);
                writer.WriteString("signalType", ev.SignalType);
                writer.WriteString("direction", ev.Direction);
                writer.WriteNumber("triggerIndex", ev.TriggerIndex);
                writer.WriteString("timestamp", ev.Timestamp);

                if (ev.Kind == "Divergence")
                {
                    writer.WriteNumber("priceStartIndex", ev.PriceStartIndex!.Value);
                    writer.WriteNumber("priceEndIndex", ev.PriceEndIndex!.Value);
                    writer.WriteString("priceStartTime", ev.PriceStartTime);
                    writer.WriteString("priceEndTime", ev.PriceEndTime);
                    writer.WriteNumber("priceStartValue", ev.PriceStartValue!.Value);
                    writer.WriteNumber("priceEndValue", ev.PriceEndValue!.Value);

                    writer.WriteNumber("indicatorStartIndex", ev.IndicatorStartIndex!.Value);
                    writer.WriteNumber("indicatorEndIndex", ev.IndicatorEndIndex!.Value);
                    writer.WriteString("indicatorStartTime", ev.IndicatorStartTime);
                    writer.WriteString("indicatorEndTime", ev.IndicatorEndTime);
                    writer.WriteNumber("indicatorStartValue", ev.IndicatorStartValue!.Value);
                    writer.WriteNumber("indicatorEndValue", ev.IndicatorEndValue!.Value);
                }
                else if (ev.Kind == "Cross")
                {
                    writer.WriteNumber("crossIndex", ev.CrossIndex!.Value);
                    writer.WriteNumber("shortValue", ev.ShortValue!.Value);
                    writer.WriteNumber("longValue", ev.LongValue!.Value);
                }

                writer.WriteEndObject();
            }
            writer.WriteEndArray();

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

    private static string ResolveDirection(SignalType type) => type switch
    {
        SignalType.RegularBullishDivergence or SignalType.HiddenBullishDivergence or SignalType.GoldenCross or SignalType.GenericBullish => "Bullish",
        SignalType.RegularBearishDivergence or SignalType.HiddenBearishDivergence or SignalType.DeadCross or SignalType.GenericBearish => "Bearish",
        _ => "Neutral"
    };

    private static TimeFrame ResolveTimeFrame(JsonElement arguments)
    {
        string? name = ToolArgs.String(arguments, TimeFrameArgument);
        if (name is null)
        {
            return TimeFrame.D1;
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

    private sealed record DetectedSignalEvent(
        string Kind,
        string SignalType,
        string Direction,
        int TriggerIndex,
        string Timestamp,
        int? PriceStartIndex,
        int? PriceEndIndex,
        string? PriceStartTime,
        string? PriceEndTime,
        decimal? PriceStartValue,
        decimal? PriceEndValue,
        int? IndicatorStartIndex,
        int? IndicatorEndIndex,
        string? IndicatorStartTime,
        string? IndicatorEndTime,
        decimal? IndicatorStartValue,
        decimal? IndicatorEndValue,
        int? CrossIndex,
        decimal? ShortValue,
        decimal? LongValue);
}
