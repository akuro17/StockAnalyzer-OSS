using System.Globalization;
using System.Text.Json;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// sa_calculate_drawing_values: builds virtual control points of a drawing tool from stored bars (or from explicit dates and prices)
/// and returns the values the chart's Data tab would show for that tool, without a chart. The formulas live in StockAnalyzer.Core/Drawing
/// (the same code the drawing objects call); this class only chooses the control points and writes the JSON.
/// </summary>
internal sealed class CalculateDrawingValuesTool : IMcpTool
{
    private const string SymbolArgument = "symbol";
    private const string TimeFrameArgument = "timeFrame";
    private const string EndDateArgument = "endDate";
    private const string LookbackArgument = "lookbackBars";
    private const string ToolArgument = "tool";
    private const string AnchorModeArgument = "anchorMode";
    private const string AnchorBarsArgument = "anchorBars";
    private const string PriceFieldArgument = "priceField";
    private const string PointsArgument = "points";
    private const string PointDateKey = "date";
    private const string PointPriceKey = "price";
    private const string EvaluationDatesArgument = "evaluationDates";
    private const string ShowExtensionsArgument = "showExtensions";

    private const string ExplicitMode = "Explicit";
    private const string RangeExtremesMode = "RangeExtremes";
    private const string RangeEndpointsMode = "RangeEndpoints";
    private const string ExplicitSource = "Explicit";
    private const string FibonacciRetracementTool = "FibonacciRetracement";
    private const int TwoPointToolPointCount = 2;
    private const int MinRangeExtremesBars = 2;

    private static readonly string[] AnchorModes = { ExplicitMode, RangeExtremesMode, RangeEndpointsMode };
    private static readonly string[] PriceFields = { nameof(CandleData.Open), nameof(CandleData.High), nameof(CandleData.Low), nameof(CandleData.Close) };

    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly SymbolResolver _symbols;
    private readonly CandleWindowLoader _loader;
    private readonly McpLimits _limits;

    public CalculateDrawingValuesTool(IReadOnlyStockAnalyzerGateway gateway, SymbolResolver symbols, McpLimits limits)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _symbols = symbols ?? throw new ArgumentNullException(nameof(symbols));
        _loader = new CandleWindowLoader(gateway);
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
    }

    public string Name => "sa_calculate_drawing_values";

    public string Description =>
        "Calculates the values the chart's Data tab shows for a drawing tool (for example Fibonacci levels, trend line price, channel lines) " +
        "from virtual control points, without drawing anything or saving anything. Read-only. The values are produced by the same code as the " +
        "chart's drawing objects. Control points are chosen by anchorMode: RangeExtremes (two-point tools: the lowest low and the highest high " +
        "inside the last anchorBars bars, the earlier bar is the first point), RangeEndpoints (the first and the last bar of that window, price taken " +
        "from priceField; one- and two-point tools), or Explicit (points: one {date, price or priceField} per control point; the only mode for " +
        "three-point tools). anchorBars counts bars of the chosen timeFrame, not calendar days; left out, it is every loaded bar. Values are evaluated at evaluationDates " +
        "(default: the last loaded bar); a date without a stored bar is allowed and only extends the geometry (hasBar false, no current price). " +
        "Time math is calendar time (price change per 24 hours), not bar count. Causal as of endDate (bars after it are never read). " +
        "FibonacciRetracement: level 0 is the second point and level 1 the first point. " +
        "GannFan: AxB means A time units to B price units (slope factor B/A); 1x1 runs through both points. " +
        "SineLine: the first point is the centre line and the start of the wave; the second price sets the amplitude and the second date the period; " +
        "the curve reaches the second price a quarter period after the first date, not at the second date. " +
        "TimeCycles: a date on a cycle boundary starts a new cycle (phase 0%); NextCycle is the first boundary strictly after the date. " +
        "Weekly and monthly bars are dated by the first day of their period; an evaluation date is matched to a bar only by exact date. " +
        "For W1 and MN1 droppedLeadingBar says whether the oldest, partial period was left out; completeBarsOnly adds the partial-bar keys of sa_get_candles. " +
        "controlPoints in the answer lists the points that were used.";

    public bool IsHeavy => false;

    public string? ExampleArgumentsJson =>
        "{\"symbol\":\"7203-T\",\"timeFrame\":\"D1\",\"tool\":\"FibonacciRetracement\",\"anchorMode\":\"RangeExtremes\",\"anchorBars\":120}";

    public string InputSchemaJson => ToolSchema.Object()
        .String(SymbolArgument, "Ticker symbol; '.' and '-' are interchangeable (7203.T and 7203-T are the same).", required: true)
        .String(TimeFrameArgument, $"Time frame of the bars ({ToolArgs.SupportedTimeFrameNames}).", required: true, allowedValues: Enum.GetNames<TimeFrame>())
        .String(EndDateArgument, $"Last date to include ({ToolJson.DateFormat}). If specified, bars up to this date are loaded.")
        .Integer(LookbackArgument, $"Stored bars loaded up to endDate, newest last. Default and maximum {_limits.MaxCandlesPerRequest}.", defaultValue: _limits.MaxCandlesPerRequest, minimum: 1, maximum: _limits.MaxCandlesPerRequest)
        .String(ToolArgument, $"Drawing tool: {string.Join(", ", DrawingValueCatalog.All.Select(d => $"{d.Name} ({d.PointCount} point{(d.PointCount == 1 ? string.Empty : "s")})"))}.", required: true, allowedValues: DrawingValueCatalog.All.Select(d => d.Name))
        .String(AnchorModeArgument, "How the control points are chosen: RangeExtremes (two-point tools), RangeEndpoints (one- and two-point tools) or Explicit (every tool, required for three-point tools).", required: true, allowedValues: AnchorModes)
        .Integer(AnchorBarsArgument, $"RangeExtremes and RangeEndpoints only (refused with Explicit): size of the anchor window in bars, counted back from the last loaded bar. Default uses all loaded bars (at most {_limits.MaxCandlesPerRequest}). RangeExtremes needs at least {MinRangeExtremesBars}; 1..{_limits.MaxCandlesPerRequest}.", defaultValue: _limits.MaxCandlesPerRequest, minimum: 1, maximum: _limits.MaxCandlesPerRequest)
        .String(PriceFieldArgument, "Bar price used by RangeEndpoints and by an Explicit point that has no price. Default Close. Refused with RangeExtremes.", allowedValues: PriceFields)
        .ObjectArray(PointsArgument, "Explicit only (required there, refused otherwise): exactly one item per control point of the tool, in order. Give price, or priceField, or neither (then the top-level priceField, default Close, of the bar on that date is used).",
            ToolSchema.Object()
                .String(PointDateKey, $"Date of the point ({ToolJson.DateFormat}); the latest stored bar on or before it is used (a warning names the bar date).", required: true)
                .Number(PointPriceKey, "Price of the point. Not allowed together with priceField.")
                .String(PriceFieldArgument, "Bar price to use for this point. Not allowed together with price.", allowedValues: PriceFields))
        .StringArray(EvaluationDatesArgument, $"Dates ({ToolJson.DateFormat}) to evaluate the tool at, 1..{_limits.MaxDrawingEvaluationDates}, without duplicates. Default: the date of the last loaded bar.")
        .Boolean(ShowExtensionsArgument, $"{FibonacciRetracementTool} only: true uses the extension level table. false is accepted for every tool and changes nothing; true is refused for other tools.")
        .Boolean(PartialBarOutput.CompleteBarsOnlyArgument, "W1 and MN1 only (ignored for D1). true leaves out a running last week or month before the control points and the default evaluation date are chosen; false keeps it and says so. Not given: the running period is used as before and the partial-bar keys are not written.")
        .Boolean(SymbolResolver.AllowUnregisteredArgument, "Accept a symbol that is not in the ticker list but has stored bars. Default false.")
        .Build();

    /// <param name="RequestedDate">The date the caller gave for an Explicit point (null for the range modes, whose date is the bar's own).</param>
    private sealed record ControlPoint(DateTime Time, decimal Price, string Source, DateOnly? RequestedDate = null);

    private sealed record ExplicitItem(DateOnly Date, decimal? Price, string? PriceField);

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        // ---- Arguments that need no data ----
        string toolName = ToolArgs.String(arguments, ToolArgument)!;
        if (!DrawingValueCatalog.TryGet(toolName, out DrawingValueDescriptor descriptor))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"{ToolArgument} must be one of: {string.Join(", ", DrawingValueCatalog.All.Select(d => d.Name))}.", ToolArgument);
        }

        string anchorMode = ToolArgs.String(arguments, AnchorModeArgument)!;
        if (!AnchorModes.Contains(anchorMode, StringComparer.Ordinal))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"{AnchorModeArgument} must be one of: {string.Join(", ", AnchorModes)}.", AnchorModeArgument);
        }

        bool hasAnchorBars = arguments.TryGetProperty(AnchorBarsArgument, out _);
        bool hasPoints = arguments.TryGetProperty(PointsArgument, out _);
        bool hasPriceField = arguments.TryGetProperty(PriceFieldArgument, out _);
        string? topPriceField = ReadPriceField(arguments, PriceFieldArgument);

        int? anchorBarsArgument = null;
        int minAnchorBars = 0;
        List<ExplicitItem> explicitItems = new();
        switch (anchorMode)
        {
            case ExplicitMode:
                if (hasAnchorBars)
                {
                    throw new ToolException(ToolErrorCodes.InvalidArgument, $"{AnchorBarsArgument} is not used with {ExplicitMode}.", AnchorBarsArgument);
                }

                explicitItems = ReadExplicitItems(arguments, descriptor);
                break;

            case RangeExtremesMode:
                if (descriptor.PointCount != TwoPointToolPointCount)
                {
                    throw new ToolException(ToolErrorCodes.InvalidArgument, $"{RangeExtremesMode} needs a two-point tool; {descriptor.Name} has {descriptor.PointCount} point(s).", AnchorModeArgument, $"Use {RangeEndpointsMode} (one- and two-point tools) or {ExplicitMode}.");
                }

                RejectPoints(hasPoints);
                if (hasPriceField)
                {
                    throw new ToolException(ToolErrorCodes.InvalidArgument, $"{PriceFieldArgument} is not used with {RangeExtremesMode} (it takes the lows and highs).", PriceFieldArgument);
                }

                minAnchorBars = MinRangeExtremesBars;
                anchorBarsArgument = ReadAnchorBars(arguments, hasAnchorBars, minAnchorBars);
                break;

            default:
                if (descriptor.PointCount > TwoPointToolPointCount)
                {
                    throw new ToolException(ToolErrorCodes.InvalidArgument, $"{RangeEndpointsMode} needs a one- or two-point tool; {descriptor.Name} has {descriptor.PointCount} points.", AnchorModeArgument, $"Use {ExplicitMode}.");
                }

                RejectPoints(hasPoints);
                minAnchorBars = descriptor.PointCount;
                anchorBarsArgument = ReadAnchorBars(arguments, hasAnchorBars, minAnchorBars);
                break;
        }

        bool? showExtensionsArgument = ToolArgs.Boolean(arguments, ShowExtensionsArgument);
        if (showExtensionsArgument is true && !string.Equals(descriptor.Name, FibonacciRetracementTool, StringComparison.Ordinal))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"{ShowExtensionsArgument} is only used with {FibonacciRetracementTool}.", ShowExtensionsArgument);
        }

        bool showExtensions = showExtensionsArgument ?? false;
        List<DateOnly>? requestedDates = ReadEvaluationDates(arguments);

        bool allowUnregistered = ToolArgs.Boolean(arguments, SymbolResolver.AllowUnregisteredArgument) ?? false;
        ResolvedSymbol resolved = await _symbols.ResolveAsync(ToolArgs.String(arguments, SymbolArgument)!, SymbolArgument, allowUnregistered, cancellationToken).ConfigureAwait(false);
        string symbol = resolved.Symbol;
        var warnings = new List<string>();
        if (!resolved.IsRegistered)
        {
            warnings.Add(SymbolResolver.OffListWarning(new[] { resolved.Symbol }));
        }

        TimeFrame timeFrame = ToolArgs.SupportedTimeFrame(arguments, TimeFrameArgument);
        DateOnly? endDate = ToolArgs.Date(arguments, EndDateArgument);
        int lookback = ToolArgs.PositiveWithin(arguments, LookbackArgument, _limits.MaxCandlesPerRequest, _limits.MaxCandlesPerRequest);

        // completeBarsOnly concerns W1 and MN1 only; given (true or false) it adds the partial-bar keys, not given nothing about the partial period is written.
        bool periodFrame = TimeFrameAggregator.IsPeriodFrame(timeFrame);
        bool? completeBarsOnly = ToolArgs.Boolean(arguments, PartialBarOutput.CompleteBarsOnlyArgument);
        bool reportPartial = completeBarsOnly is not null && periodFrame;
        // Read before the bars (see CandleWindowLoader.ReferenceDateAsync): a reference date read after them could be later than the data behind them.
        DateTime? referenceDate = reportPartial ? await _loader.ReferenceDateAsync(symbol, endDate, cancellationToken).ConfigureAwait(false) : null;

        // ---- Bars (same branches and errors as sa_find_support_resistance) ----
        List<CandleData> bars;
        bool droppedLeadingBar = false;
        if (endDate is not null)
        {
            bars = await _loader.LoadUpToAsync(symbol, timeFrame, endDate.Value, lookback, cancellationToken).ConfigureAwait(false);
            if (bars.Count == 0)
            {
                var check = await _gateway.GetCandlesAsync(symbol, timeFrame, 1, cancellationToken).ConfigureAwait(false);
                if (check.Count == 0)
                {
                    throw new ToolException(ToolErrorCodes.SymbolNotFound, $"No stored market data exists for {symbol}.", SymbolArgument);
                }

                throw new ToolException(ToolErrorCodes.InsufficientMarketData, $"No stored {timeFrame} bars exist on or before {endDate.Value.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture)} for {symbol}.", EndDateArgument);
            }
        }
        else
        {
            (bars, droppedLeadingBar) = await _loader.LoadAsync(symbol, timeFrame, lookback, cancellationToken).ConfigureAwait(false);
            if (bars.Count == 0)
            {
                throw new ToolException(ToolErrorCodes.InsufficientMarketData, $"No stored {timeFrame} bars exist for {symbol}.", SymbolArgument);
            }
        }

        // The running last period is left out by loading again up to the day before its start, exactly as sa_calculate_indicators does.
        bool droppedPartialBar = false;
        if (completeBarsOnly == true && CandleWindowLoader.IsLastBarPartial(bars, timeFrame, referenceDate))
        {
            (bars, _) = await _loader.LoadWithoutRunningPeriodAsync(symbol, timeFrame, bars, lookback, cancellationToken).ConfigureAwait(false);
            droppedLeadingBar = false;
            droppedPartialBar = true;
            if (bars.Count == 0)
            {
                throw new ToolException(ToolErrorCodes.InsufficientMarketData, $"No complete {timeFrame} period is stored for {symbol}.", PartialBarOutput.CompleteBarsOnlyArgument);
            }
        }

        // ---- Control points ----
        // An omitted anchorBars means the whole loaded window.
        int anchorBars = anchorBarsArgument ?? bars.Count;
        if (anchorMode != ExplicitMode && anchorBars < minAnchorBars)
        {
            throw new ToolException(ToolErrorCodes.InsufficientMarketData, $"Only {bars.Count} bar(s) were loaded; {descriptor.Name} with {anchorMode} needs at least {minAnchorBars}.", AnchorBarsArgument);
        }

        List<ControlPoint> controlPoints = anchorMode switch
        {
            ExplicitMode => BuildExplicitPoints(explicitItems, bars, topPriceField, warnings),
            RangeExtremesMode => BuildRangeExtremes(bars, anchorBars),
            _ => BuildRangeEndpoints(bars, anchorBars, descriptor.PointCount, topPriceField ?? nameof(CandleData.Close)),
        };
        DrawingAnchor[] anchors = controlPoints.Select(p => new DrawingAnchor(p.Time, p.Price)).ToArray();

        // ---- Evaluation ----
        List<DateOnly> dates = requestedDates ?? new List<DateOnly> { DateOnly.FromDateTime(bars[^1].Timestamp) };
        var lastBarOfDate = new Dictionary<DateOnly, CandleData>();
        foreach (CandleData bar in bars)
        {
            lastBarOfDate[DateOnly.FromDateTime(bar.Timestamp)] = bar;
        }

        // A W1 or MN1 bar is dated by the first day of its period, so a date inside a loaded period matches no bar; the warning names the bar of that period.
        Dictionary<DateTime, DateOnly>? barDateOfPeriod = null;
        if (TimeFrameAggregator.IsPeriodFrame(timeFrame))
        {
            barDateOfPeriod = new Dictionary<DateTime, DateOnly>();
            foreach (CandleData bar in bars)
            {
                barDateOfPeriod[TimeFrameAggregator.PeriodStart(bar.Timestamp, timeFrame).Date] = DateOnly.FromDateTime(bar.Timestamp);
            }
        }

        var evaluations = new List<(DateOnly Date, bool HasBar, decimal? CurrentPrice, IReadOnlyList<DrawingCalculatedValue> Values)>(dates.Count);
        for (int i = 0; i < dates.Count; i++)
        {
            DateOnly date = dates[i];
            bool hasBar = lastBarOfDate.TryGetValue(date, out CandleData evaluationBar);
            if (!hasBar
                && barDateOfPeriod is not null
                && barDateOfPeriod.TryGetValue(TimeFrameAggregator.PeriodStart(date.ToDateTime(TimeOnly.MinValue), timeFrame).Date, out DateOnly periodBarDate))
            {
                warnings.Add($"{EvaluationDatesArgument}[{i}]: no {timeFrame} bar is dated {date.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture)}; the bar of that period is dated {periodBarDate.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture)}. The value uses the date only to extend the geometry.");
            }

            DateTime timestamp = hasBar
                ? evaluationBar.Timestamp
                : new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, bars[^1].Timestamp.Kind);
            decimal? currentPrice = hasBar ? evaluationBar.Close : null;
            var request = new DrawingValueRequest(anchors, timestamp, currentPrice, IndicatorColor.Gray, new DrawingValueOptions(showExtensions));

            IReadOnlyList<DrawingCalculatedValue> values = CalculateOrReportOverflow(descriptor, request, i);
            if (values.Count == 0)
            {
                warnings.Add($"{EvaluationDatesArgument}[{i}]: the tool returned no values for these control points.");
            }

            evaluations.Add((date, hasBar, currentPrice, values));
        }

        return ToolJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("symbol", symbol);
            writer.WriteString("timeFrame", timeFrame.ToString());
            if (endDate is not null)
            {
                writer.WriteString("endDate", endDate.Value.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture));
            }

            writer.WriteNumber("lookbackBars", lookback);
            writer.WriteNumber("barCount", bars.Count);
            if (periodFrame)
            {
                writer.WriteBoolean("droppedLeadingBar", droppedLeadingBar);
            }

            if (reportPartial)
            {
                writer.WriteBoolean(PartialBarOutput.CompleteBarsOnlyArgument, completeBarsOnly!.Value);
                PartialBarOutput.Write(writer, referenceDate, TimeFrameAggregator.PeriodEnd(bars[^1].Timestamp, timeFrame), CandleWindowLoader.IsLastBarPartial(bars, timeFrame, referenceDate), droppedPartialBar);
            }

            writer.WriteString("tool", descriptor.Name);
            writer.WriteString("anchorMode", anchorMode);
            if (anchorMode != ExplicitMode)
            {
                writer.WriteNumber("anchorBars", anchorBars);
            }

            writer.WriteStartArray("controlPoints");
            for (int i = 0; i < controlPoints.Count; i++)
            {
                writer.WriteStartObject();
                writer.WriteNumber("index", i);
                writer.WriteString("date", ToolJson.FormatDate(controlPoints[i].Time));
                if (controlPoints[i].RequestedDate is { } requestedDate)
                {
                    writer.WriteString("requestedDate", requestedDate.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture));
                }

                writer.WriteNumber("price", controlPoints[i].Price);
                writer.WriteString("source", controlPoints[i].Source);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartArray("evaluations");
            foreach (var evaluation in evaluations)
            {
                writer.WriteStartObject();
                writer.WriteString("date", evaluation.Date.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture));
                writer.WriteBoolean("hasBar", evaluation.HasBar);
                ToolJson.WriteNullableNumber(writer, "currentPrice", evaluation.CurrentPrice);
                writer.WriteStartArray("values");
                foreach (DrawingCalculatedValue value in evaluation.Values)
                {
                    writer.WriteStartObject();
                    writer.WriteString("key", value.Key);
                    writer.WriteString("label", value.Label);
                    ToolJson.WriteNullableNumber(writer, "value", value.NumericValue);
                    writer.WriteString("text", value.FormattedText);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            if (warnings.Count > 0)
            {
                writer.WriteStartArray("warnings");
                foreach (string warning in warnings)
                {
                    writer.WriteStringValue(warning);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        });
    }

    // ---- Argument reading ----

    private static void RejectPoints(bool hasPoints)
    {
        if (hasPoints)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"{PointsArgument} is only used with {ExplicitMode}.", PointsArgument);
        }
    }

    /// <summary>The given anchorBars, or null when it is left out (the whole loaded window is used then).</summary>
    private int? ReadAnchorBars(JsonElement arguments, bool present, int minimum)
    {
        if (!present)
        {
            return null;
        }

        int bars = ToolArgs.PositiveWithin(arguments, AnchorBarsArgument, _limits.MaxCandlesPerRequest, _limits.MaxCandlesPerRequest);
        if (bars < minimum)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"{AnchorBarsArgument} must be at least {minimum} for this tool and mode.", AnchorBarsArgument);
        }

        return bars;
    }

    private static string? ReadPriceField(JsonElement element, string name)
    {
        string? field = ToolArgs.String(element, name);
        if (field is not null && !PriceFields.Contains(field, StringComparer.Ordinal))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"{name} must be one of: {string.Join(", ", PriceFields)}.", name);
        }

        return field;
    }

    private static List<ExplicitItem> ReadExplicitItems(JsonElement arguments, DrawingValueDescriptor descriptor)
    {
        if (!arguments.TryGetProperty(PointsArgument, out JsonElement points) || points.ValueKind != JsonValueKind.Array)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"{PointsArgument} is required with {ExplicitMode}.", PointsArgument);
        }

        if (points.GetArrayLength() != descriptor.PointCount)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"{descriptor.Name} needs exactly {descriptor.PointCount} point(s) in {PointsArgument}; {points.GetArrayLength()} given.", PointsArgument);
        }

        var items = new List<ExplicitItem>(descriptor.PointCount);
        int k = 0;
        foreach (JsonElement item in points.EnumerateArray())
        {
            string path = $"{PointsArgument}[{k}]";
            string dateText = ToolArgs.String(item, PointDateKey)!;
            if (!DateOnly.TryParseExact(dateText, ToolJson.DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"{path}.{PointDateKey} must be a date in the form {ToolJson.DateFormat}.", $"{path}.{PointDateKey}");
            }

            decimal? price = null;
            if (item.TryGetProperty(PointPriceKey, out JsonElement priceElement))
            {
                if (priceElement.ValueKind != JsonValueKind.Number || !priceElement.TryGetDecimal(out decimal parsed))
                {
                    throw new ToolException(ToolErrorCodes.InvalidArgument, $"{path}.{PointPriceKey} must be a finite number inside the decimal range.", $"{path}.{PointPriceKey}");
                }

                price = parsed;
            }

            string? itemField = ReadPriceField(item, PriceFieldArgument);
            if (price is not null && itemField is not null)
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"{path} must not have both {PointPriceKey} and {PriceFieldArgument}.", path);
            }

            items.Add(new ExplicitItem(date, price, itemField));
            k++;
        }

        return items;
    }

    private List<DateOnly>? ReadEvaluationDates(JsonElement arguments)
    {
        if (!arguments.TryGetProperty(EvaluationDatesArgument, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        int count = array.GetArrayLength();
        if (count == 0)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"{EvaluationDatesArgument} must not be empty.", EvaluationDatesArgument, "Leave it out to evaluate at the last loaded bar.");
        }

        if (count > _limits.MaxDrawingEvaluationDates)
        {
            throw new ToolException(ToolErrorCodes.LimitExceeded, $"{EvaluationDatesArgument} has {count} entries; the limit is {_limits.MaxDrawingEvaluationDates}.", EvaluationDatesArgument, $"Use {_limits.MaxDrawingEvaluationDates} or fewer.");
        }

        var dates = new List<DateOnly>(count);
        var seen = new HashSet<DateOnly>();
        int i = 0;
        foreach (JsonElement element in array.EnumerateArray())
        {
            string path = $"{EvaluationDatesArgument}[{i}]";
            if (!DateOnly.TryParseExact(element.GetString(), ToolJson.DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"{path} must be a date in the form {ToolJson.DateFormat}.", path);
            }

            if (!seen.Add(date))
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"{path} repeats the date {date.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture)}.", path);
            }

            dates.Add(date);
            i++;
        }

        return dates;
    }

    // ---- Control points ----

    private static List<ControlPoint> BuildRangeExtremes(List<CandleData> bars, int anchorBars)
    {
        int start = RequireAnchorWindow(bars, anchorBars);
        int iLow = start;
        int iHigh = start;
        for (int i = start + 1; i < bars.Count; i++)
        {
            if (bars[i].Low < bars[iLow].Low)
            {
                iLow = i;
            }

            if (bars[i].High > bars[iHigh].High)
            {
                iHigh = i;
            }
        }

        if (iLow == iHigh)
        {
            string highText = bars[iHigh].High.ToString(CultureInfo.InvariantCulture);
            string lowText = bars[iLow].Low.ToString(CultureInfo.InvariantCulture);
            throw new ToolException(
                ToolErrorCodes.InsufficientMarketData,
                $"The highest high ({highText} on {ToolJson.FormatDate(bars[iHigh].Timestamp)}) and the lowest low ({lowText} on {ToolJson.FormatDate(bars[iLow].Timestamp)}) of the anchor window are on the same bar; use a larger anchorBars or anchorMode Explicit.",
                AnchorBarsArgument);
        }

        var low = new ControlPoint(bars[iLow].Timestamp, bars[iLow].Low, nameof(CandleData.Low));
        var high = new ControlPoint(bars[iHigh].Timestamp, bars[iHigh].High, nameof(CandleData.High));
        return iLow < iHigh ? new List<ControlPoint> { low, high } : new List<ControlPoint> { high, low };
    }

    private static List<ControlPoint> BuildRangeEndpoints(List<CandleData> bars, int anchorBars, int pointCount, string priceField)
    {
        int start = RequireAnchorWindow(bars, anchorBars);
        var points = new List<ControlPoint> { new(bars[start].Timestamp, PriceOf(bars[start], priceField), priceField) };
        if (pointCount == TwoPointToolPointCount)
        {
            points.Add(new ControlPoint(bars[^1].Timestamp, PriceOf(bars[^1], priceField), priceField));
        }

        return points;
    }

    private static int RequireAnchorWindow(List<CandleData> bars, int anchorBars)
    {
        if (anchorBars > bars.Count)
        {
            throw new ToolException(ToolErrorCodes.InsufficientMarketData, $"{AnchorBarsArgument} is {anchorBars} but only {bars.Count} bars were loaded.", AnchorBarsArgument, "Use a smaller anchorBars or a larger lookbackBars.");
        }

        return bars.Count - anchorBars;
    }

    private static List<ControlPoint> BuildExplicitPoints(List<ExplicitItem> items, List<CandleData> bars, string? topPriceField, List<string> warnings)
    {
        DateOnly first = DateOnly.FromDateTime(bars[0].Timestamp);
        DateOnly last = DateOnly.FromDateTime(bars[^1].Timestamp);
        var points = new List<ControlPoint>(items.Count);
        for (int k = 0; k < items.Count; k++)
        {
            ExplicitItem item = items[k];
            string datePath = $"{PointsArgument}[{k}].{PointDateKey}";
            if (item.Date < first || item.Date > last)
            {
                throw new ToolException(
                    ToolErrorCodes.InvalidArgument,
                    $"{datePath} {item.Date.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture)} is outside the loaded bars ({first.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture)} to {last.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture)}).",
                    datePath);
            }

            CandleData bar = bars[0];
            for (int i = bars.Count - 1; i >= 0; i--)
            {
                if (DateOnly.FromDateTime(bars[i].Timestamp) <= item.Date)
                {
                    bar = bars[i];
                    break;
                }
            }

            DateOnly barDate = DateOnly.FromDateTime(bar.Timestamp);
            if (barDate != item.Date)
            {
                warnings.Add($"{PointsArgument}[{k}].{PointDateKey} {item.Date.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture)} has no stored bar; resolved to {barDate.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture)}.");
            }

            if (item.Price is { } explicitPrice)
            {
                points.Add(new ControlPoint(bar.Timestamp, explicitPrice, ExplicitSource, item.Date));
            }
            else
            {
                string field = item.PriceField ?? topPriceField ?? nameof(CandleData.Close);
                points.Add(new ControlPoint(bar.Timestamp, PriceOf(bar, field), field, item.Date));
            }
        }

        return points;
    }

    private static decimal PriceOf(CandleData bar, string field) => field switch
    {
        nameof(CandleData.Open) => bar.Open,
        nameof(CandleData.High) => bar.High,
        nameof(CandleData.Low) => bar.Low,
        _ => bar.Close,
    };

    // ---- Calculation ----

    /// <summary>
    /// Runs the calculator. The value texts are formatted with <see cref="DrawingValueFormat.Culture"/> inside the calculators, so this method does not touch the
    /// culture of the thread. A decimal overflow is reported at the evaluation date of the call.
    /// </summary>
    private static IReadOnlyList<DrawingCalculatedValue> CalculateOrReportOverflow(DrawingValueDescriptor descriptor, DrawingValueRequest request, int evaluationIndex)
    {
        try
        {
            return descriptor.Calculate(request);
        }
        catch (OverflowException)
        {
            string path = $"{EvaluationDatesArgument}[{evaluationIndex}]";
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"The value is outside the decimal range; check the control point prices and {path}.", path);
        }
    }
}
