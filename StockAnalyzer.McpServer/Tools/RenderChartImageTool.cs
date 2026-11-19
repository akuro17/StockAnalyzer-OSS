using System.Globalization;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Trend;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Rendering;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// sa_render_chart_image: a PNG chart of the stored bars of one symbol (candles, volume, overlay lines, oscillator panels, buy/sell markers) in the fixed style
/// "AiRecognitionV1", returned as an image block together with the mapping that turns pixels back into values. The tool only maps the request to the Core renderer
/// and the result to JSON; the drawing is <see cref="AiChartImageRenderer"/> and the indicators are those of sa_calculate_indicators. It reads stored data only and writes no file.
/// Opt-in extras (owner decision U-14; a request without them gives the same picture and the same answer as before): Parabolic SAR dots, the causal lines and the cloud of Ichimoku,
/// the histogram of an oscillator (includeHistogram) and the saved drawings of the user in the fixed high-contrast colour (includeSavedDrawings).
/// </summary>
internal sealed class RenderChartImageTool : IMcpImageTool
{
    private const string SymbolArgument = "symbol";
    private const string TimeFrameArgument = "timeFrame";
    private const string EndDateArgument = "endDate";
    private const string BarCountArgument = "barCount";
    private const string WidthArgument = "width";
    private const string HeightArgument = "height";
    private const string BackgroundArgument = "background";
    private const string IncludeVolumeArgument = "includeVolume";
    private const string LookbackArgument = "lookbackBars";
    private const string OverlaysArgument = "overlays";
    private const string OscillatorsArgument = "oscillators";
    private const string MarkersArgument = "markers";
    private const string IncludeHistogramArgument = "includeHistogram";
    private const string IncludeSavedDrawingsArgument = "includeSavedDrawings";
    private const string IncludeBarCoordinatesArgument = "includeBarCoordinates";
    private const string BarsListUnit = "index counts bars from 0 (the oldest drawn bar); ";
    private const string CountsUnit = "barCountDrawn, barsOmittedCount and legendEntriesInImage are numbers of bars or entries.";
    private const string IchimokuOmissionNote = "Ichimoku ChikouSpan is omitted because it is non-causal; TenkanSen, KijunSen, SenkouSpanA, SenkouSpanB and the Kumo cloud are drawn.";
    private const string IchimokuFutureNote = "The Senkou spans are plotted ahead of the bar they are calculated from, so the part of the cloud beyond the last drawn bar is not drawn.";
    private const string BackgroundWhite = "White";
    private const string BackgroundLightGray = "LightGray";
    private const string SideBuy = "Buy";
    private const string SideSell = "Sell";
    private const string CalculatorHint = "Use sa_calculate_indicators for this indicator.";

    private static readonly string[] IchimokuDrawnSeries =
    {
        CoreIchimokuIndicator.TenkanSenSeriesName,
        CoreIchimokuIndicator.KijunSenSeriesName,
        CoreIchimokuIndicator.SenkouSpanASeriesName,
        CoreIchimokuIndicator.SenkouSpanBSeriesName,
    };

    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly IReadOnlyDrawingDocumentSource? _drawings;
    private readonly SymbolResolver _symbols;
    private readonly CandleWindowLoader _loader;
    private readonly IndicatorRequestBinder _binder;
    private readonly IndicatorChainPlan _chain;
    private readonly McpLimits _limits;

    public RenderChartImageTool(
        IReadOnlyStockAnalyzerGateway gateway,
        SymbolResolver symbols,
        IndicatorCatalog catalog,
        IndicatorSettingsBinder binder,
        IndicatorChainPlan chain,
        McpLimits limits,
        IReadOnlyDrawingDocumentSource? drawings = null)
    {
        _gateway = gateway;
        _drawings = drawings;
        _symbols = symbols;
        _loader = new CandleWindowLoader(gateway);
        _binder = new IndicatorRequestBinder(catalog, binder);
        _chain = chain;
        _limits = limits;
    }

    public string Name => "sa_render_chart_image";

    public string Description =>
        $"Renders a symbol's stored bars as a PNG chart (fixed {AiChartImageStyle.Name} style) with volume, overlays, up to {AiChartImageStyle.MaxOscillatorPanels} oscillator panels, buy/sell markers, optional saved drawings, and exact pixel-to-value mapping. " +
        "Read-only; never reads user chart colours or mutates Data. " +
        ToolJson.DocLink(Name);

    public bool IsHeavy => false;

    public IReadOnlyDictionary<string, string>? MisnamedArgumentHints { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["indicators"] = $"This tool takes overlays (price-scale indicators) and oscillators (one panel each), not 'indicators'.",
        ["derived"] = "Derived series are not drawn. " + CalculatorHint,
        ["completeBarsOnly"] = "This tool draws the stored bars as they are. " + CalculatorHint,
        ["outputTailCount"] = $"This tool draws the last {BarCountArgument} bars.",
        ["asOfDate"] = $"This tool names the last-bar date '{EndDateArgument}'.",
    };

    public string? ExampleArgumentsJson =>
        "{\"symbol\":\"7203-T\",\"timeFrame\":\"D1\",\"barCount\":120,\"overlays\":[{\"id\":\"sma25\",\"type\":\"SMA\",\"parameters\":{\"Period\":25}}],\"oscillators\":[{\"id\":\"rsi14\",\"type\":\"RSI\",\"parameters\":{\"Period\":14}}]}";

    public string InputSchemaJson
    {
        get
        {
            ToolSchema indicator = ToolSchema.Object()
                .String("id", "Unique id chosen by the client; the legend and the panels are named by it.", required: true)
                .String("type", "Indicator type name from sa_get_capabilities (case-insensitive), for example SMA.", required: true)
                .FreeObject("parameters", "Parameter values by name (case-insensitive), for example {\"Period\": 25}. Names, types and ranges: sa_get_capabilities.")
                .String("priceSource", "Price the indicator reads (for example Close). Default: the indicator's default.", allowedValues: Enum.GetNames<PriceType>())
                .String("sourceIndicatorId", "Id of another indicator in this call whose output series is this indicator's input instead of the price.")
                .String("sourceOutputSeriesName", "Output series of the source indicator (default: its main series).")
                .String("dynamicPeriodIndicatorId", "Id of another indicator in this call whose output series drives this indicator's adaptive period.")
                .String("dynamicPeriodOutputSeriesName", "Output series of the dynamic-period indicator (default: its main series).");
            ToolSchema marker = ToolSchema.Object()
                .String("date", $"Date ({ToolJson.DateFormat}) of a drawn bar, exactly as bars lists it.", required: true)
                .String("side", "Buy draws an upward triangle under the bar, Sell a downward triangle over it.", required: true, allowedValues: new[] { SideBuy, SideSell });

            return ToolSchema.Object()
                .String(SymbolArgument, "Ticker symbol; '.' and '-' are interchangeable (7203.T and 7203-T are the same).", required: true)
                .String(TimeFrameArgument, $"Time frame of the bars. Supported: {ToolArgs.SupportedTimeFrameNames}.", required: true, allowedValues: Enum.GetNames<TimeFrame>())
                .String(EndDateArgument, $"Date ({ToolJson.DateFormat}) of the last bar to draw (inclusive). Default: the latest stored bar.")
                .Integer(
                    BarCountArgument,
                    $"Bars to draw (the latest ones up to endDate). At most {McpLimitsContract.EffectiveMaxChartImageBars(_limits)}; fewer when the width is small.",
                    defaultValue: McpLimitsContract.EffectiveDefaultChartImageBars(_limits),
                    minimum: 1,
                    maximum: McpLimitsContract.EffectiveMaxChartImageBars(_limits))
                .Integer(WidthArgument, "Image width in pixels.", defaultValue: _limits.DefaultChartImageWidth, minimum: AiChartImageStyle.MinImageWidthPx, maximum: _limits.MaxChartImageWidth)
                .Integer(HeightArgument, "Image height in pixels.", defaultValue: _limits.DefaultChartImageHeight, minimum: AiChartImageStyle.MinImageHeightPx, maximum: _limits.MaxChartImageHeight)
                .String(BackgroundArgument, $"Background colour: {BackgroundWhite} ({AiChartImageStyle.BackgroundWhiteHex}, default) or {BackgroundLightGray} ({AiChartImageStyle.BackgroundLightGrayHex}).", allowedValues: new[] { BackgroundWhite, BackgroundLightGray })
                .Boolean(IncludeVolumeArgument, "Draw the volume panel under the price panel. Default true.")
                .Boolean(IncludeHistogramArgument, "Also draw the histogram series of each oscillator (for example the MACD histogram) as bars from zero behind its lines. Default false.")
                .Boolean(IncludeSavedDrawingsArgument, $"Also draw the user's saved horizontal lines, vertical lines, trend lines, rays and parallel channels on the price panel in the fixed colour {AiChartImageStyle.SavedDrawingHex} (the drawings of the same daily, weekly or monthly chart). Default false.")
                .Boolean(IncludeBarCoordinatesArgument, "Include per-bar pixel coordinate entries in bars array; false saves tokens. Default true.")
                .Integer(
                    LookbackArgument,
                    $"Stored bars loaded up to endDate for indicator/drawing calculations. Default and max {_limits.MaxCandlesPerRequest}.",
                    defaultValue: _limits.MaxCandlesPerRequest,
                    minimum: 1,
                    maximum: _limits.MaxCandlesPerRequest)
                .ObjectArray(OverlaysArgument, $"Indicators drawn on the price panel (price-scale types, for example SMA, EMA, BB). At most {_limits.MaxImageOverlays}.", indicator)
                .ObjectArray(OscillatorsArgument, $"Indicators that get a panel each (for example RSI, MACD). At most {AiChartImageStyle.MaxOscillatorPanels}.", indicator)
                .ObjectArray(MarkersArgument, $"Buy/sell markers on drawn bars. At most {_limits.MaxImageMarkers}.", marker)
                .Boolean(SymbolResolver.AllowUnregisteredArgument, "Also accept a symbol that is not in the ticker list but has stored daily bars (named in warnings). Default false.")
                .Build();
        }
    }

    public async Task<ToolImageResult> InvokeImageAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        // ---- cheap checks first: scalar arguments, then the shape and count of the lists, then the size, then stored data ----
        bool allowUnregistered = ToolArgs.Boolean(arguments, SymbolResolver.AllowUnregisteredArgument) ?? false;
        TimeFrame timeFrame = ToolArgs.SupportedTimeFrame(arguments, TimeFrameArgument);
        DateOnly? endDate = ToolArgs.Date(arguments, EndDateArgument);
        bool includeVolume = ToolArgs.Boolean(arguments, IncludeVolumeArgument) ?? true;
        bool includeHistogram = ToolArgs.Boolean(arguments, IncludeHistogramArgument) ?? false;
        bool includeSavedDrawings = ToolArgs.Boolean(arguments, IncludeSavedDrawingsArgument) ?? false;
        bool includeBarCoordinates = ToolArgs.Boolean(arguments, IncludeBarCoordinatesArgument) ?? true;
        bool lightGray = string.Equals(ToolArgs.String(arguments, BackgroundArgument), BackgroundLightGray, StringComparison.Ordinal);
        int barCount = ToolArgs.PositiveWithin(arguments, BarCountArgument, McpLimitsContract.EffectiveDefaultChartImageBars(_limits), McpLimitsContract.EffectiveMaxChartImageBars(_limits));
        int width = ReadSize(arguments, WidthArgument, _limits.DefaultChartImageWidth, AiChartImageStyle.MinImageWidthPx, _limits.MaxChartImageWidth);
        int height = ReadSize(arguments, HeightArgument, _limits.DefaultChartImageHeight, AiChartImageStyle.MinImageHeightPx, _limits.MaxChartImageHeight);
        int lookback = ToolArgs.PositiveWithin(arguments, LookbackArgument, _limits.MaxCandlesPerRequest, _limits.MaxCandlesPerRequest);
        if (ToolArgs.Integer(arguments, LookbackArgument) is not null && lookback < barCount)
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"'{LookbackArgument}' ({lookback}) is smaller than '{BarCountArgument}' ({barCount}).",
                LookbackArgument,
                $"Give at least {barCount}, or omit {LookbackArgument}.");
        }

        List<JsonElement> overlayItems = Items(arguments, OverlaysArgument, _limits.MaxImageOverlays);
        List<JsonElement> oscillatorItems = Items(arguments, OscillatorsArgument, AiChartImageStyle.MaxOscillatorPanels);
        List<JsonElement> markerItems = Items(arguments, MarkersArgument, _limits.MaxImageMarkers);
        CheckSize(width, height, barCount, includeVolume, oscillatorItems.Count);

        // ---- the indicators: bound exactly as sa_calculate_indicators binds them, then checked against what an image can draw ----
        // This needs the catalogue only, so it runs before the symbol is looked up and before any bar is read: a request that cannot be drawn is refused without touching stored data.
        var bindItems = overlayItems.Select((item, i) => (item, $"{OverlaysArgument}[{i}]"))
            .Concat(oscillatorItems.Select((item, i) => (item, $"{OscillatorsArgument}[{i}]")))
            .ToList();
        List<RequestedIndicator> requests = _binder.Bind(bindItems, timeFrame);
        _chain.Apply(requests);
        for (int i = 0; i < requests.Count; i++)
        {
            CheckDrawable(requests[i], requests, isOverlay: i < overlayItems.Count);
        }

        var warnings = new List<string>();
        ResolvedSymbol resolved = await _symbols.ResolveAsync(ToolArgs.String(arguments, SymbolArgument)!, SymbolArgument, allowUnregistered, cancellationToken).ConfigureAwait(false);
        string symbol = resolved.Symbol;
        if (!resolved.IsRegistered)
        {
            warnings.Add(SymbolResolver.OffListWarning(new[] { symbol }));
        }

        // ---- the bars: the whole calculation window when indicators or saved drawings are drawn (the chart places a drawing by every loaded bar), else only the drawn bars ----
        int count = requests.Count > 0 || includeSavedDrawings ? lookback : barCount;
        List<CandleData> loaded;
        if (endDate is { } asOf)
        {
            loaded = await _loader.LoadUpToAsync(symbol, timeFrame, asOf, count, cancellationToken).ConfigureAwait(false);
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
            (loaded, _) = await _loader.LoadAsync(symbol, timeFrame, count, cancellationToken).ConfigureAwait(false);
            if (loaded.Count == 0)
            {
                throw new ToolException(ToolErrorCodes.InsufficientMarketData, $"No stored {timeFrame} bars exist for {symbol}.", SymbolArgument);
            }
        }

        var notes = new List<string>
        {
            "Do not read prices from the picture: take numbers from bars and from sa_calculate_indicators.",
        };
        int drawn = Math.Min(barCount, loaded.Count);
        int windowStart = loaded.Count - drawn;
        if (drawn < barCount)
        {
            notes.Add($"Only {loaded.Count} bars are stored in this window, so {drawn} bars are drawn instead of the {barCount} asked for.");
        }

        List<CandleData> drawnCandles = loaded.GetRange(windowStart, drawn);

        // ---- calculate on the whole window, cut the drawn window afterwards ----
        var overlays = new List<AiChartOverlay>();
        var oscillators = new List<AiChartOscillator>();
        if (requests.Count > 0)
        {
            List<CoreCandleData> core = loaded.Select(c => new CoreCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume)).ToList();
            Dictionary<string, IIndicatorResult> results = await CalculateAsync(core, requests.Select(r => r.Settings).ToList(), timeFrame, endDate, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            for (int i = 0; i < requests.Count; i++)
            {
                RequestedIndicator request = requests[i];
                var type = Enum.Parse<IndicatorType>(request.Entry.Type);
                if (!results.TryGetValue(request.Settings.Id, out IIndicatorResult? result) || !result.IsSuccessful)
                {
                    throw new ToolException(
                        ToolErrorCodes.InvalidIndicatorParameter,
                        $"{request.Entry.Type} could not be calculated: {result?.ErrorMessage ?? "no result"}.",
                        request.Path,
                        CalculatorHint);
                }

                bool ichimoku = type == IndicatorType.Ichimoku;
                IReadOnlyList<AiChartSelectedSeries> selected = ichimoku
                    ? AiChartSeriesSelector.SelectNamed(result, IchimokuDrawnSeries, windowStart, drawn)
                    : AiChartSeriesSelector.Select(type, result, windowStart, drawn, request.Settings.ParameterObject);
                if (selected.Count == 0)
                {
                    notes.Add($"'{request.Id}' ({request.Entry.Type}) has no value to draw in the drawn window (the stored history may be shorter than its warm-up), so it is not drawn.");
                    continue;
                }

                AiChartLineSeries[] series = selected.Select(s => new AiChartLineSeries(s.Name, s.Values)).ToArray();
                if (i < overlayItems.Count)
                {
                    AiChartCloud? cloud = ichimoku ? CloudOf(selected) : null;
                    AiChartLineShape shape = type == IndicatorType.ParabolicSAR ? AiChartLineShape.Dots : AiChartLineShape.Line;
                    overlays.Add(new AiChartOverlay(request.Id, type, AiChartMovingAverageSet.RepresentativePeriod(type, request.Settings.ParameterObject), series, shape, cloud));
                    if (ichimoku)
                    {
                        AddOnce(notes, IchimokuOmissionNote);
                        AddOnce(notes, IchimokuFutureNote);
                    }
                }
                else
                {
                    IReadOnlyList<decimal?>? histogram = includeHistogram
                        ? AiChartSeriesSelector.SelectHistogram(type, result, windowStart, drawn, request.Settings.ParameterObject)
                        : null;
                    if (includeHistogram && histogram is null)
                    {
                        notes.Add($"'{request.Id}' ({request.Entry.Type}) has no histogram series with a value in the drawn window, so no histogram is drawn for it.");
                    }

                    oscillators.Add(new AiChartOscillator(request.Id, series, request.Settings.MinValue, request.Settings.MaxValue, request.Settings.IncludeZeroBaseline, histogram));
                }
            }
        }

        // ---- markers: the date must be the date of a drawn bar ----
        var markers = new List<AiChartMarker>();
        var markerEcho = new List<(string Date, string Side, int Index)>();
        for (int i = 0; i < markerItems.Count; i++)
        {
            string text = markerItems[i].GetProperty("date").GetString()!;
            string side = markerItems[i].GetProperty("side").GetString()!;
            string path = $"{MarkersArgument}[{i}].date";
            if (!DateOnly.TryParseExact(text, ToolJson.DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"'{path}' must be a date in the form {ToolJson.DateFormat}.", path);
            }

            int index = drawnCandles.FindIndex(c => DateOnly.FromDateTime(c.Timestamp) == date);
            if (index < 0)
            {
                throw new ToolException(
                    ToolErrorCodes.InvalidArgument,
                    $"No drawn bar is dated {text}.",
                    path,
                    $"Use the date of a drawn bar, from {ToolJson.FormatDate(drawnCandles[0].Timestamp)} to {ToolJson.FormatDate(drawnCandles[^1].Timestamp)} (the dates in bars; a non-trading day has no bar).");
            }

            markers.Add(new AiChartMarker(index, side == SideBuy));
            markerEcho.Add((text, side, index));
        }

        // ---- saved drawings: read last, because they are placed on the drawn bars ----
        ImageSavedDrawingSelection savedSelection = ImageSavedDrawingSelection.Empty;
        if (includeSavedDrawings)
        {
            savedSelection = await ReadSavedDrawingsAsync(symbol, timeFrame, loaded, windowStart, endDate, notes, warnings, cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<ImageSavedDrawing> savedDrawings = savedSelection.Drawings;

        cancellationToken.ThrowIfCancellationRequested();
        var request2 = new AiChartImageRequest(
            drawnCandles,
            width,
            height,
            lightGray ? AiChartBackground.LightGray : AiChartBackground.White,
            includeVolume,
            overlays,
            oscillators,
            markers,
            includeSavedDrawings ? savedDrawings.Select(d => d.Drawing).ToList() : null);
        AiChartImageResult image = AiChartImageRenderer.Render(request2, cancellationToken);
        if (includeSavedDrawings && savedDrawings.Count > 0)
        {
            int outside = image.Drawings!.Count(placement => !placement.IsVisible);
            if (outside > 0)
            {
                notes.Add($"{outside.ToString(CultureInfo.InvariantCulture)} saved drawing(s) lie outside the visible part of the price panel and leave no pixels.");
            }
        }

        var extras = new ImageExtras(
            includeHistogram,
            includeSavedDrawings,
            oscillators.Where(o => o.HistogramValues is not null).Select(o => o.Id).ToList(),
            overlays.Any(o => o.Cloud is not null),
            savedDrawings,
            overlays.Any(o => o.Shape == AiChartLineShape.Dots),
            includeSavedDrawings && endDate is not null ? savedSelection.AfterEndDate : null,
            includeBarCoordinates);
        string json = ToolJson.Build(writer => WriteResult(writer, symbol, timeFrame, endDate, lightGray, includeVolume, image, markerEcho, notes, warnings, extras));
        return new ToolImageResult(json, image.Png);
    }

    /// <summary>The cloud between the two Senkou spans, when both were drawn (a series without any value in the window is not selected, so a short history gives no cloud).</summary>
    private static AiChartCloud? CloudOf(IReadOnlyList<AiChartSelectedSeries> selected)
    {
        AiChartSelectedSeries? spanA = selected.FirstOrDefault(s => s.Name == CoreIchimokuIndicator.SenkouSpanASeriesName);
        AiChartSelectedSeries? spanB = selected.FirstOrDefault(s => s.Name == CoreIchimokuIndicator.SenkouSpanBSeriesName);
        return spanA is not null && spanB is not null ? new AiChartCloud(spanA.Values, spanB.Values) : null;
    }

    private static void AddOnce(List<string> notes, string note)
    {
        if (!notes.Contains(note))
        {
            notes.Add(note);
        }
    }

    private async Task<ImageSavedDrawingSelection> ReadSavedDrawingsAsync(
        string symbol,
        TimeFrame timeFrame,
        IReadOnlyList<CandleData> loaded,
        int firstDrawnIndex,
        DateOnly? endDate,
        List<string> notes,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (_drawings is null)
        {
            notes.Add("Saved drawings are not available in this server, so none is drawn.");
            return ImageSavedDrawingSelection.Empty;
        }

        DrawingDocumentReadResult read;
        try
        {
            read = await _drawings.ReadAsync(symbol, timeFrame, cancellationToken).ConfigureAwait(false);
        }
        catch (DataUnavailableException)
        {
            throw new ToolException(ToolErrorCodes.DataUnavailable, "The drawing file is temporarily unreadable.", hint: "Retry in a moment, or omit includeSavedDrawings.");
        }

        return ImageSavedDrawings.Select(read, loaded.Select(c => c.Timestamp).ToList(), firstDrawnIndex, _limits.MaxImageSavedDrawings, endDate, notes, warnings);
    }

    /// <summary>What the opt-in extras added to the picture, for the answer.</summary>
    private sealed record ImageExtras(
        bool IncludeHistogram,
        bool IncludeSavedDrawings,
        IReadOnlyList<string> HistogramOscillatorIds,
        bool HasCloud,
        IReadOnlyList<ImageSavedDrawing> SavedDrawings,
        bool HasDots,
        int? SavedDrawingsAfterEndDate,
        bool IncludeBarCoordinates);

    private static int ReadSize(JsonElement arguments, string name, int defaultValue, int minimum, int maximum)
    {
        long? value = ToolArgs.Integer(arguments, name);
        if (value is null)
        {
            return defaultValue;
        }

        if (value < minimum)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"'{name}' must be at least {minimum} pixels.", name, $"Use {minimum} or more.");
        }

        if (value > maximum)
        {
            throw new ToolException(ToolErrorCodes.LimitExceeded, $"'{name}' is larger than the limit {maximum}.", name, $"Use {maximum} or less.");
        }

        return (int)value;
    }

    private static List<JsonElement> Items(JsonElement arguments, string name, int maximum)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out JsonElement list))
        {
            return new List<JsonElement>();
        }

        if (list.GetArrayLength() > maximum)
        {
            throw new ToolException(ToolErrorCodes.LimitExceeded, $"At most {maximum} entries in '{name}'.", name, $"Send {maximum} or fewer.");
        }

        return list.EnumerateArray().ToList();
    }

    /// <summary>Refuses a size that cannot hold the bars or the panels, and says what would fit (rules R-1 and R-4 of plan 15).</summary>
    private void CheckSize(int width, int height, int barCount, bool includeVolume, int oscillatorCount)
    {
        AiChartSizeCheck check = AiChartLayout.CheckSize(width, height, barCount, includeVolume, oscillatorCount);
        if (check.IsValid)
        {
            return;
        }

        string spacing = AiChartImageStyle.MinBarSpacingPx.ToString("0.#", CultureInfo.InvariantCulture);
        if (check.WidthTooNarrow)
        {
            int plot = width - AiChartImageStyle.MarginLeftPx - AiChartImageStyle.MarginRightPx;
            string hint = check.MinWidthPx <= _limits.MaxChartImageWidth
                ? $"At width {width} at most {check.MaxBarsForWidth} bars fit; {barCount} bars need width {check.MinWidthPx} or more. Reduce {BarCountArgument} or increase {WidthArgument}."
                : $"At width {width} at most {check.MaxBarsForWidth} bars fit; {barCount} bars would need width {check.MinWidthPx}, above the limit {_limits.MaxChartImageWidth}. Reduce {BarCountArgument}.";
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"Width {width} cannot draw {barCount} bars: the plot area is {plot} pixels and each bar needs at least {spacing} pixels, so that candle bodies stay apart from their wicks.",
                BarCountArgument,
                hint);
        }

        string heightHint = check.MinHeightPx <= _limits.MaxChartImageHeight
            ? $"This panel layout needs height {check.MinHeightPx} or more. Increase {HeightArgument}, or use fewer oscillators, or set {IncludeVolumeArgument} false."
            : $"This panel layout needs height {check.MinHeightPx}, above the limit {_limits.MaxChartImageHeight}. Use fewer oscillators or set {IncludeVolumeArgument} false.";
        throw new ToolException(
            ToolErrorCodes.InvalidArgument,
            $"Height {height} is too low for this panel layout (price panel{(includeVolume ? ", volume panel" : string.Empty)}{(oscillatorCount > 0 ? $", {oscillatorCount} oscillator panel(s)" : string.Empty)}): each panel has a minimum height.",
            HeightArgument,
            heightHint);
    }

    /// <summary>What an image can draw: no second symbol, no types with a special chart rendering, and the right list for the indicator (decided by the chart's own overlay flag of its settings).</summary>
    private void CheckDrawable(RequestedIndicator request, IReadOnlyList<RequestedIndicator> requests, bool isOverlay)
    {
        var type = Enum.Parse<IndicatorType>(request.Entry.Type);
        string typePath = $"{request.Path}.type";
        if (request.Settings.ParameterObject is ICrossTickerParameter)
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"{request.Entry.Type} compares two symbols and is not drawn in an image.",
                typePath,
                CalculatorHint);
        }

        if (AiChartUndrawableTypes.Contains(type))
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"{request.Entry.Type} is drawn in a special way by the chart (dots, a cloud, bars or a profile) and cannot be drawn as a line in an image.",
                typePath,
                $"{CalculatorHint} Volume has its own panel: set {IncludeVolumeArgument}.");
        }

        if (_chain.NonCausalCauseOf(request, requests) is { } cause && !IsOmittedIchimokuChikou(type, request, cause))
        {
            throw NonCausalRefusal(request, cause);
        }

        if (isOverlay && !request.Settings.IsOverlay)
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"{request.Entry.Type} is not on the price scale; drawn on the price panel it would flatten the candles.",
                typePath,
                $"Put it in {OscillatorsArgument}.");
        }

        if (!isOverlay && request.Settings.IsOverlay)
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"{request.Entry.Type} is on the price scale and belongs on the price panel.",
                typePath,
                $"Put it in {OverlaysArgument}.");
        }
    }

    /// <summary>
    /// True for the one look-ahead the image draws around: the ChikouSpan of an Ichimoku that takes no other indicator as input. The image leaves that series out (and says so), and the four
    /// causal lines are drawn; an Ichimoku that is fed by another indicator keeps the refusal of that chain.
    /// </summary>
    private static bool IsOmittedIchimokuChikou(IndicatorType type, RequestedIndicator request, IndicatorChainPlan.NonCausalCause cause)
        => type == IndicatorType.Ichimoku
           && ReferenceEquals(cause.Origin, request)
           && cause.Argument == RequestedIndicator.TypeKey
           && request.SourceId is null
           && request.DynamicId is null;

    /// <summary>
    /// The refusal of an indicator whose values at a bar depend on later bars (owner decision M-1 = A, M-1b = the one list of the backtest eligibility rule and the chain rule of
    /// sa_calculate_indicators). The param is the key that carries the cause: <c>type</c> of the indicator that looks ahead, or the <c>sourceIndicatorId</c> /
    /// <c>dynamicPeriodIndicatorId</c> of the indicator that takes such an indicator as input; the message names the indicator that looks ahead.
    /// </summary>
    private static ToolException NonCausalRefusal(RequestedIndicator request, IndicatorChainPlan.NonCausalCause cause)
    {
        string origin = $"'{cause.Origin.Id}' ({cause.Origin.Entry.Type})";
        string message = ReferenceEquals(cause.Origin, request)
            ? $"Indicator {origin} looks ahead (its value at a bar depends on later bars), so it cannot be drawn in an image."
            : $"Indicator '{request.Id}' ({request.Entry.Type}) takes the indicator {origin} as input, and that indicator looks ahead (its value at a bar depends on later bars), so it cannot be drawn in an image.";
        return new ToolException(
            ToolErrorCodes.InvalidArgument,
            message,
            $"{request.Path}.{cause.Argument}",
            $"{CalculatorHint} It returns the values and lists the series that look ahead in nonCausalSeries. For swing pivots with the date each was confirmed use sa_analyze_market_structure.");
    }

    /// <summary>One calculation through the gateway; a gateway that checks the token between indicators gets it (as sa_calculate_indicators does).</summary>
    private Task<Dictionary<string, IIndicatorResult>> CalculateAsync(
        IReadOnlyList<CoreCandleData> candles,
        IReadOnlyList<CoreIndicatorSettings> settings,
        TimeFrame timeFrame,
        DateOnly? endDate,
        CancellationToken cancellationToken)
        => _gateway is ITimeFrameAwareIndicatorCalculation tfAware
            ? tfAware.CalculateIndicatorsAsync(candles, settings, null, timeFrame, endDate, cancellationToken)
            : _gateway is ICancellableIndicatorCalculation cancellable
                ? cancellable.CalculateIndicatorsAsync(candles, settings, null, cancellationToken)
                : _gateway.CalculateIndicatorsAsync(candles, settings, null);

    /// <summary>The saved drawings that left pixels, in the saved order: the object, its kind, its control points as saved (date, price) with their image pixels.</summary>
    private static void WriteSavedDrawings(Utf8JsonWriter writer, AiChartImageResult image, IReadOnlyList<ImageSavedDrawing> drawings)
    {
        writer.WriteStartArray("drawingsDrawn");
        IReadOnlyList<AiChartDrawingPlacement> placements = image.Drawings ?? Array.Empty<AiChartDrawingPlacement>();
        foreach (AiChartDrawingPlacement placement in placements.Where(p => p.IsVisible))
        {
            ImageSavedDrawing drawing = drawings[placement.Index];
            writer.WriteStartObject();
            writer.WriteString("objectId", drawing.ObjectId);
            writer.WriteString("tool", drawing.Tool);
            writer.WriteStartArray("points");
            for (int i = 0; i < drawing.Anchors.Count; i++)
            {
                writer.WriteStartObject();
                writer.WriteString("date", ToolJson.FormatDate(drawing.Anchors[i].Time));
                writer.WriteNumber("price", drawing.Anchors[i].Price);
                writer.WriteNumber("xPx", placement.Points[i].XPx);
                writer.WriteNumber("yPx", placement.Points[i].YPx);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private void WriteResult(
        System.Text.Json.Utf8JsonWriter writer,
        string symbol,
        TimeFrame timeFrame,
        DateOnly? endDate,
        bool lightGray,
        bool includeVolume,
        AiChartImageResult image,
        IReadOnlyList<(string Date, string Side, int Index)> markers,
        IReadOnlyList<string> notes,
        IReadOnlyList<string> warnings,
        ImageExtras extras)
    {
        writer.WriteStartObject();
        writer.WriteString("style", AiChartImageStyle.Name);
        writer.WriteString("symbol", symbol);
        writer.WriteString("timeFrame", timeFrame.ToString());
        if (endDate is { } end)
        {
            writer.WriteString("endDate", end.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture));
        }

        writer.WriteString("mimeType", IMcpImageTool.PngMimeType);
        writer.WriteNumber("widthPx", image.WidthPx);
        writer.WriteNumber("heightPx", image.HeightPx);
        writer.WriteString("background", lightGray ? BackgroundLightGray : BackgroundWhite);
        writer.WriteBoolean("includeVolume", includeVolume);
        if (extras.IncludeHistogram)
        {
            writer.WriteBoolean("includeHistogram", true);
        }

        if (extras.IncludeSavedDrawings)
        {
            writer.WriteBoolean("includeSavedDrawings", true);
        }

        writer.WriteNumber("barCountDrawn", image.Bars.Count);
        writer.WriteString("firstBarDate", ToolJson.FormatDate(image.Bars[0].Date));
        writer.WriteString("lastBarDate", ToolJson.FormatDate(image.Bars[^1].Date));

        writer.WriteStartObject("colors");
        writer.WriteString("upHex", AiChartImageStyle.UpHex);
        writer.WriteString("downHex", AiChartImageStyle.DownHex);
        writer.WriteString("dojiHex", AiChartImageStyle.DojiHex);
        if (extras.HistogramOscillatorIds.Count > 0)
        {
            writer.WriteString("histogramNonNegativeHex", AiChartImageStyle.HistogramNonNegativeHex);
            writer.WriteString("histogramNegativeHex", AiChartImageStyle.HistogramNegativeHex);
            writer.WriteNumber("histogramAlpha", AiChartImageStyle.HistogramAlpha);
        }

        if (extras.HasCloud)
        {
            writer.WriteString("cloudBullishHex", AiChartImageStyle.CloudBullishHex);
            writer.WriteString("cloudBearishHex", AiChartImageStyle.CloudBearishHex);
            writer.WriteNumber("cloudAlpha", AiChartImageStyle.CloudAlpha);
        }

        if (extras.IncludeSavedDrawings)
        {
            writer.WriteString("savedDrawingHex", AiChartImageStyle.SavedDrawingHex);
            writer.WriteNumber("savedDrawingWidthPx", AiChartImageStyle.SavedDrawingWidthPx);
        }

        writer.WriteEndObject();

        writer.WriteStartObject("axes");
        writer.WriteNumber("priceMin", image.Axes.PriceMin);
        writer.WriteNumber("priceMax", image.Axes.PriceMax);
        writer.WriteNumber("pricePlotLeftPx", image.Axes.PricePlotLeftPx);
        writer.WriteNumber("pricePlotRightPx", image.Axes.PricePlotRightPx);
        writer.WriteNumber("pricePlotTopPx", image.Axes.PricePlotTopPx);
        writer.WriteNumber("pricePlotBottomPx", image.Axes.PricePlotBottomPx);
        writer.WriteNumber("barSpacingPx", image.Axes.BarSpacingPx);
        writer.WriteNumber("firstBarCenterXPx", image.Axes.FirstBarCenterXPx);
        writer.WriteEndObject();

        writer.WriteStartArray("panels");
        foreach (AiChartPanelInfo panel in image.Panels)
        {
            writer.WriteStartObject();
            writer.WriteString("id", panel.Id);
            writer.WriteString("kind", panel.Kind.ToString());
            writer.WriteNumber("topPx", panel.TopPx);
            writer.WriteNumber("bottomPx", panel.BottomPx);
            writer.WriteNumber("valueMin", panel.ValueMin);
            writer.WriteNumber("valueMax", panel.ValueMax);
            writer.WriteString("unit", panel.Kind switch
            {
                AiChartPanelKind.Price => "price (the unit of the stored prices)",
                AiChartPanelKind.Volume => "volume (the unit of the stored volume)",
                _ => "the indicator's own unit (the application stores no output unit per indicator)",
            });
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteStartArray("legend");
        foreach (AiChartLegendEntry entry in image.Legend)
        {
            writer.WriteStartObject();
            writer.WriteString("id", entry.Label);
            writer.WriteString("colorHex", entry.ColorHex);
            writer.WriteString("lineStyle", entry.LineStyle.ToString());
            writer.WriteNumber("lineWidthPx", entry.LineWidthPx);
            if (entry.Shape != AiChartLineShape.Line)
            {
                writer.WriteString("shape", entry.Shape.ToString());
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteNumber("legendEntriesInImage", image.LegendEntriesDrawn);

        writer.WriteStartArray("markers");
        foreach ((string date, string side, int index) in markers)
        {
            writer.WriteStartObject();
            writer.WriteString("date", date);
            writer.WriteString("side", side);
            writer.WriteNumber("barIndex", index);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        if (extras.HistogramOscillatorIds.Count > 0)
        {
            writer.WriteStartArray("histograms");
            foreach (string id in extras.HistogramOscillatorIds)
            {
                writer.WriteStartObject();
                writer.WriteString("id", id);
                writer.WriteString("panelId", id);
                writer.WriteNumber("baselineValue", 0);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        if (extras.IncludeSavedDrawings)
        {
            WriteSavedDrawings(writer, image, extras.SavedDrawings);

            if (extras.SavedDrawingsAfterEndDate is { } afterEndDate)
            {
                writer.WriteNumber("savedDrawingsAfterEndDate", afterEndDate);
            }
        }

        int listed = Math.Min(image.Bars.Count, _limits.MaxImageBarsListed);
        int first = image.Bars.Count - listed;
        if (extras.IncludeBarCoordinates)
        {
            writer.WriteStartArray("bars");
            for (int i = first; i < image.Bars.Count; i++)
            {
                AiChartBarPosition bar = image.Bars[i];
                writer.WriteStartObject();
                writer.WriteNumber("index", bar.Index);
                writer.WriteString("date", ToolJson.FormatDate(bar.Date));
                writer.WriteNumber("xPx", bar.XPx);
                writer.WriteNumber("openYPx", bar.OpenYPx);
                writer.WriteNumber("highYPx", bar.HighYPx);
                writer.WriteNumber("lowYPx", bar.LowYPx);
                writer.WriteNumber("closeYPx", bar.CloseYPx);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        writer.WriteNumber("barsOmittedCount", first);

        writer.WriteStartObject("units");
        writer.WriteString("pixels", "Image pixels; origin at the top left; pixel-edge coordinates, so the pixel that holds (x, y) is (floor x, floor y); rounded to 2 decimals (names ending in Px).");
        writer.WriteString("prices", "The unit of the stored prices (priceMin, priceMax and the price panel values).");
        // Without the bars list the sentence on the list is left out and the sentence on the counts (which are always written) stays.
        writer.WriteString("bars", extras.IncludeBarCoordinates ? BarsListUnit + CountsUnit : CountsUnit);

        writer.WriteString("dates", $"Calendar dates ({ToolJson.DateFormat}); a weekly or monthly bar is dated by its first day.");
        writer.WriteEndObject();

        writer.WriteStartArray("notes");
        foreach (string note in notes)
        {
            writer.WriteStringValue(note);
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
    }
}
