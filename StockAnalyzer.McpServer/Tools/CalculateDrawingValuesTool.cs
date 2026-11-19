using System.Globalization;
using System.Text.Json;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Drawing.Values;
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
    private const string PitchforkKindArgument = "pitchforkKind";
    private const string PivotLeftArgument = "pivotLeftStrength";
    private const string PivotRightArgument = "pivotRightStrength";
    private const string PivotPreferenceArgument = "pivotPreference";

    private const string ExplicitMode = "Explicit";
    private const string RangeExtremesMode = "RangeExtremes";
    private const string RangeEndpointsMode = "RangeEndpoints";
    private const string AutoPivotsMode = "AutoPivots";
    private const string LatestPivot = "Latest";
    private const string HighPivot = "High";
    private const string LowPivot = "Low";
    private const string LongPositionTool = DrawingValueCatalog.LongPositionName;
    private const string ShortPositionTool = DrawingValueCatalog.ShortPositionName;
    private const int DefaultPivotStrength = 5;
    private const string ExplicitSource = "Explicit";
    private const string FibonacciRetracementTool = "FibonacciRetracement";
    private const string PitchforkTool = "Pitchfork";
    private const int TwoPointToolPointCount = 2;
    private const int MinRangeExtremesBars = 2;

    private static readonly string[] AnchorModes = { ExplicitMode, RangeExtremesMode, RangeEndpointsMode, AutoPivotsMode };
    private static readonly string[] PivotPreferences = { LatestPivot, HighPivot, LowPivot };
    private static readonly string[] PriceFields = { nameof(CandleData.Open), nameof(CandleData.High), nameof(CandleData.Low), nameof(CandleData.Close) };
    private static readonly string[] PitchforkKindNames = Enum.GetNames<PitchforkKind>();

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
        "Calculates chart Data-tab values for a drawing tool (e.g. Fibonacci, TrendLine, CrossLine, DatePriceRange, AnchoredVWAP) from virtual control points chosen by anchorMode (RangeExtremes, RangeEndpoints, AutoPivots, Explicit). " +
        "Read-only; respects causal endDate and completeBarsOnly. " +
        ToolJson.DocLink(Name);

    public bool IsHeavy => false;

    public string? ExampleArgumentsJson =>
        "{\"symbol\":\"7203-T\",\"timeFrame\":\"D1\",\"tool\":\"FibonacciRetracement\",\"anchorMode\":\"RangeExtremes\",\"anchorBars\":120}";

    public string InputSchemaJson => ToolSchema.Object()
        .String(SymbolArgument, "Ticker symbol; '.' and '-' are interchangeable (7203.T and 7203-T are the same).", required: true)
        .String(TimeFrameArgument, $"Time frame of the bars ({ToolArgs.SupportedTimeFrameNames}).", required: true, allowedValues: Enum.GetNames<TimeFrame>())
        .String(EndDateArgument, $"Last date to include ({ToolJson.DateFormat}). If specified, bars up to this date are loaded.")
        .Integer(LookbackArgument, $"Stored bars loaded up to endDate, newest last. Default and maximum {_limits.MaxCandlesPerRequest}.", defaultValue: _limits.MaxCandlesPerRequest, minimum: 1, maximum: _limits.MaxCandlesPerRequest)
        .String(ToolArgument, "Drawing tool name.", required: true, allowedValues: DrawingValueCatalog.All.Select(d => d.Name))
        .String(AnchorModeArgument, $"Anchor selection mode: RangeExtremes, RangeEndpoints, {AutoPivotsMode}, or Explicit.", required: true, allowedValues: AnchorModes)
        .Integer(AnchorBarsArgument, $"Lookback window in bars for non-Explicit modes (1..{_limits.MaxCandlesPerRequest}). Default: all loaded bars.", defaultValue: _limits.MaxCandlesPerRequest, minimum: 1, maximum: _limits.MaxCandlesPerRequest)
        .Integer(PivotLeftArgument, $"{AutoPivotsMode} only: bars to the left of a swing (1..{_limits.MaxPivotStrength}). Default {DefaultPivotStrength}.", defaultValue: DefaultPivotStrength, minimum: 1, maximum: _limits.MaxPivotStrength)
        .Integer(PivotRightArgument, $"{AutoPivotsMode} only: bars to the right of a swing (1..{_limits.MaxPivotStrength}). Default {DefaultPivotStrength}.", defaultValue: DefaultPivotStrength, minimum: 1, maximum: _limits.MaxPivotStrength)
        .String(PivotPreferenceArgument, $"{AutoPivotsMode} 1-point tools only: {LatestPivot} (default), {HighPivot}, or {LowPivot}.", allowedValues: PivotPreferences)
        .String(PriceFieldArgument, "Bar price for RangeEndpoints and Explicit points without price. Default Close.", allowedValues: PriceFields)
        .ObjectArray(PointsArgument, "Explicit only: one {date, price?, priceField?} item per control point of the tool.",
            ToolSchema.Object()
                .String(PointDateKey, $"Date of the point ({ToolJson.DateFormat}).", required: true)
                .Number(PointPriceKey, "Price of the point (excludes priceField).")
                .String(PriceFieldArgument, "Bar price for this point (excludes price).", allowedValues: PriceFields))
        .StringArray(EvaluationDatesArgument, $"Dates ({ToolJson.DateFormat}) to evaluate at (1..{_limits.MaxDrawingEvaluationDates}). Default: last loaded bar.")
        .Boolean(ShowExtensionsArgument, $"{FibonacciRetracementTool} only: true uses extension levels.")
        .String(PitchforkKindArgument, $"{PitchforkTool} only: {string.Join(", ", PitchforkKindNames)}. Default {nameof(PitchforkKind.Andrews)}.", allowedValues: PitchforkKindNames)
        .Boolean(PartialBarOutput.CompleteBarsOnlyArgument, "W1/MN1 only: true drops an incomplete last period, false keeps it; either adds partial-bar keys.")
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

        bool hasPivotArguments = arguments.TryGetProperty(PivotLeftArgument, out _)
            || arguments.TryGetProperty(PivotRightArgument, out _)
            || arguments.TryGetProperty(PivotPreferenceArgument, out _);
        if (hasPivotArguments && anchorMode != AutoPivotsMode)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"{PivotLeftArgument}, {PivotRightArgument} and {PivotPreferenceArgument} are only used with {AutoPivotsMode}.", AnchorModeArgument);
        }

        int pivotLeft = DefaultPivotStrength;
        int pivotRight = DefaultPivotStrength;
        string pivotPreference = LatestPivot;
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

            case AutoPivotsMode:
                if (descriptor.Name is LongPositionTool or ShortPositionTool)
                {
                    throw new ToolException(ToolErrorCodes.InvalidArgument, $"{AutoPivotsMode} cannot place {descriptor.Name} (Entry, Stop and Target are decisions, not swings).", AnchorModeArgument, $"Use {ExplicitMode}.");
                }

                RejectPoints(hasPoints);
                if (hasPriceField)
                {
                    throw new ToolException(ToolErrorCodes.InvalidArgument, $"{PriceFieldArgument} is not used with {AutoPivotsMode} (a pivot is the high or the low of its bar).", PriceFieldArgument);
                }

                pivotLeft = ToolArgs.PositiveWithin(arguments, PivotLeftArgument, DefaultPivotStrength, _limits.MaxPivotStrength);
                pivotRight = ToolArgs.PositiveWithin(arguments, PivotRightArgument, DefaultPivotStrength, _limits.MaxPivotStrength);
                pivotPreference = ToolArgs.String(arguments, PivotPreferenceArgument) ?? LatestPivot;
                if (!PivotPreferences.Contains(pivotPreference, StringComparer.Ordinal))
                {
                    throw new ToolException(ToolErrorCodes.InvalidArgument, $"{PivotPreferenceArgument} must be one of: {string.Join(", ", PivotPreferences)}.", PivotPreferenceArgument);
                }

                if (pivotPreference != LatestPivot && descriptor.PointCount > 1)
                {
                    throw new ToolException(ToolErrorCodes.InvalidArgument, $"{PivotPreferenceArgument} {pivotPreference} is only used with a one-point tool; {descriptor.Name} has {descriptor.PointCount} points and takes the latest alternating pivots.", PivotPreferenceArgument, $"Leave {PivotPreferenceArgument} out or use {LatestPivot}.");
                }

                minAnchorBars = pivotLeft + pivotRight + 1;
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

        string? pitchforkKindArgument = ToolArgs.String(arguments, PitchforkKindArgument);
        if (pitchforkKindArgument is not null && !PitchforkKindNames.Contains(pitchforkKindArgument, StringComparer.Ordinal))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"{PitchforkKindArgument} must be one of: {string.Join(", ", PitchforkKindNames)}.", PitchforkKindArgument);
        }

        PitchforkKind pitchforkKind = pitchforkKindArgument is null ? PitchforkKind.Andrews : Enum.Parse<PitchforkKind>(pitchforkKindArgument);
        if (pitchforkKind != PitchforkKind.Andrews && !string.Equals(descriptor.Name, PitchforkTool, StringComparison.Ordinal))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"{PitchforkKindArgument} {pitchforkKind} is only used with {PitchforkTool}.", PitchforkKindArgument);
        }

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
            AutoPivotsMode => BuildAutoPivots(bars, anchorBars, descriptor, pivotLeft, pivotRight, pivotPreference),
            _ => BuildRangeEndpoints(bars, anchorBars, descriptor.PointCount, topPriceField ?? nameof(CandleData.Close)),
        };
        DrawingAnchor[] anchors = controlPoints.Select(p => new DrawingAnchor(p.Time, p.Price)).ToArray();
        RequireOrderedPosition(descriptor.Name, anchors);
        IReadOnlyList<CoreCandleData>? candles = descriptor.RequiresCandles
            ? bars.Select(b => new CoreCandleData(b.Timestamp, b.Open, b.High, b.Low, b.Close, b.Volume)).ToList()
            : null;

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
            var request = new DrawingValueRequest(anchors, timestamp, currentPrice, IndicatorColor.Gray, new DrawingValueOptions(showExtensions, pitchforkKind), candles);

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

            if (anchorMode == AutoPivotsMode)
            {
                writer.WriteNumber(PivotLeftArgument, pivotLeft);
                writer.WriteNumber(PivotRightArgument, pivotRight);
                writer.WriteString(PivotPreferenceArgument, pivotPreference);
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

    /// <summary>
    /// Confirmed swing pivots of the anchor window (PivotDetectionEngine), consecutive pivots of one kind folded to the more extreme one so they alternate.
    /// A one-point tool takes the latest pivot (of the preferred kind); a two- or three-point tool takes the latest alternating pivots in time order.
    /// </summary>
    private static List<ControlPoint> BuildAutoPivots(List<CandleData> bars, int anchorBars, DrawingValueDescriptor descriptor, int left, int right, string preference)
    {
        int start = RequireAnchorWindow(bars, anchorBars);
        var raw = new List<FractalPivot>();
        PivotDetectionEngine.ExtractPivots(bars.GetRange(start, bars.Count - start), left, right, raw);

        var folded = new List<FractalPivot>(raw.Count);
        foreach (FractalPivot pivot in raw)
        {
            if (folded.Count > 0 && folded[^1].Type == pivot.Type)
            {
                bool moreExtreme = pivot.Type == FractalPivotType.High ? pivot.Price > folded[^1].Price : pivot.Price < folded[^1].Price;
                if (moreExtreme)
                {
                    folded[^1] = pivot;
                }
            }
            else
            {
                folded.Add(pivot);
            }
        }

        List<FractalPivot> chosen;
        if (descriptor.PointCount == 1)
        {
            FractalPivotType? wanted = preference switch
            {
                HighPivot => FractalPivotType.High,
                LowPivot => FractalPivotType.Low,
                _ => null,
            };
            int index = folded.FindLastIndex(p => wanted is null || p.Type == wanted);
            chosen = index < 0 ? new List<FractalPivot>() : new List<FractalPivot> { folded[index] };
        }
        else
        {
            chosen = folded.Count >= descriptor.PointCount ? folded.GetRange(folded.Count - descriptor.PointCount, descriptor.PointCount) : new List<FractalPivot>();
        }

        if (chosen.Count < descriptor.PointCount)
        {
            throw new ToolException(
                ToolErrorCodes.InsufficientMarketData,
                $"The anchor window of {anchorBars} bar(s) holds {folded.Count} confirmed swing pivot(s) with {PivotLeftArgument} {left} and {PivotRightArgument} {right}; {descriptor.Name} needs {descriptor.PointCount}{(descriptor.PointCount == 1 && preference != LatestPivot ? $" of kind {preference}" : string.Empty)}.",
                AnchorBarsArgument,
                $"Use a larger {AnchorBarsArgument}, smaller {PivotLeftArgument} and {PivotRightArgument}, or {ExplicitMode}.");
        }

        return chosen
            .Select(p => new ControlPoint(p.Timestamp, p.Price, p.Type == FractalPivotType.High ? "PivotHigh" : "PivotLow"))
            .ToList();
    }

    /// <summary>
    /// LongPosition: Stop &lt; Entry &lt; Target; ShortPosition: Target &lt; Entry &lt; Stop (the control points are Entry, Stop, Target). The calculator itself never refuses an order
    /// (the chart and sa_get_chart_drawings must not fail on a drag or a saved drawing); this tool does, as owner decision U-11 says.
    /// </summary>
    private static void RequireOrderedPosition(string toolName, DrawingAnchor[] anchors)
    {
        bool isLong = toolName == LongPositionTool;
        if (!isLong && toolName != ShortPositionTool)
        {
            return;
        }

        if (!LongShortPositionValues.IsOrdered(anchors[0].Price, anchors[1].Price, anchors[2].Price, isLong))
        {
            string rule = isLong ? "Stop < Entry < Target" : "Target < Entry < Stop";
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"{toolName} needs {rule} (points are Entry, Stop, Target); got Entry {anchors[0].Price.ToString(CultureInfo.InvariantCulture)}, Stop {anchors[1].Price.ToString(CultureInfo.InvariantCulture)}, Target {anchors[2].Price.ToString(CultureInfo.InvariantCulture)}.",
                PointsArgument);
        }
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
