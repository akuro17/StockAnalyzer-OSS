using System.Globalization;
using System.Text.Json;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// sa_get_chart_drawings: the drawings the user saved for a symbol on a daily, weekly or monthly chart (owner decision U-13), with the values the chart's Data tab shows for the
/// tools that can be calculated without a chart. Read-only: the saved file is read as it is (never migrated, backed up or quarantined). The formulas are the ones of
/// <see cref="DrawingValueCatalog"/>, the code the drawing objects themselves call.
/// </summary>
internal sealed class GetChartDrawingsTool : IMcpTool
{
    private const string SymbolArgument = "symbol";
    private const string TimeFrameArgument = "timeFrame";
    private const string ContextArgument = "context";
    private const string EndDateArgument = "endDate";
    private const string LookbackArgument = "lookbackBars";
    private const string DefaultContext = DrawingContextNames.Standard;
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss";

    private static readonly TimeFrame[] StoredTimeFrames = { TimeFrame.D1, TimeFrame.W1, TimeFrame.MN1 };

    private readonly IReadOnlyDrawingDocumentSource _source;
    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly SymbolResolver _symbols;
    private readonly CandleWindowLoader _loader;
    private readonly McpLimits _limits;

    public GetChartDrawingsTool(IReadOnlyDrawingDocumentSource source, IReadOnlyStockAnalyzerGateway gateway, SymbolResolver symbols, McpLimits limits)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _symbols = symbols ?? throw new ArgumentNullException(nameof(symbols));
        _loader = new CandleWindowLoader(gateway);
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
    }

    public string Name => "sa_get_chart_drawings";

    public string Description =>
        "Returns saved chart layers, drawing objects, control points, and Data-tab calculatedValues (for supported time-based tools, with optional as-of lookAheadSuspect detection) for one symbol/timeFrame/context. Read-only. " +
        ToolJson.DocLink(Name);

    public bool IsHeavy => false;

    public string? ExampleArgumentsJson => "{\"symbol\":\"7203-T\",\"timeFrame\":\"D1\"}";

    public string InputSchemaJson => ToolSchema.Object()
        .String(SymbolArgument, "Ticker symbol; '.' and '-' are interchangeable (7203.T and 7203-T are the same).", required: true)
        .String(TimeFrameArgument, "Time frame of the chart the drawings were made on (drawings are stored per daily, weekly and monthly chart).", required: true, allowedValues: StoredTimeFrames.Select(frame => frame.ToString()))
        .String(ContextArgument, $"Drawing context of the file: {DefaultContext} (candle, bar and Heikin-Ashi charts; default), Linear, Kagi, Renko, PointAndFigure, ThreeLineBreak or ReverseWatch. Case-sensitive.")
        .String(EndDateArgument, $"Evaluate the values at the last stored bar on or before this date ({ToolJson.DateFormat}); later bars are never read. Default: the last stored bar.")
        .Integer(LookbackArgument, $"Stored bars loaded up to endDate for the values of bar-based tools (regression trend, anchored VWAP, classic pivot points). Default and maximum {_limits.MaxCandlesPerRequest}.", defaultValue: _limits.MaxCandlesPerRequest, minimum: 1, maximum: _limits.MaxCandlesPerRequest)
        .Boolean(SymbolResolver.AllowUnregisteredArgument, "Accept a symbol that is not in the ticker list but has stored bars. Default false.")
        .Build();

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        TimeFrame timeFrame = ToolArgs.SupportedTimeFrame(arguments, TimeFrameArgument);
        if (Array.IndexOf(StoredTimeFrames, timeFrame) < 0)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"{TimeFrameArgument} must be one of: {string.Join(", ", StoredTimeFrames)} (drawings are stored per daily, weekly and monthly chart).", TimeFrameArgument);
        }

        string context = ToolArgs.String(arguments, ContextArgument) ?? DefaultContext;
        DateOnly? endDate = ToolArgs.Date(arguments, EndDateArgument);
        int lookback = ToolArgs.PositiveWithin(arguments, LookbackArgument, _limits.MaxCandlesPerRequest, _limits.MaxCandlesPerRequest);
        bool allowUnregistered = ToolArgs.Boolean(arguments, SymbolResolver.AllowUnregisteredArgument) ?? false;
        ResolvedSymbol resolved = await _symbols.ResolveAsync(ToolArgs.String(arguments, SymbolArgument)!, SymbolArgument, allowUnregistered, cancellationToken).ConfigureAwait(false);
        var warnings = new List<string>();
        if (!resolved.IsRegistered)
        {
            warnings.Add(SymbolResolver.OffListWarning(new[] { resolved.Symbol }));
        }

        DrawingDocumentReadResult read;
        try
        {
            read = await _source.ReadAsync(resolved.Symbol, timeFrame, cancellationToken).ConfigureAwait(false);
        }
        catch (DataUnavailableException)
        {
            throw new ToolException(ToolErrorCodes.DataUnavailable, "The drawing file is temporarily unreadable.", hint: "Retry in a moment.");
        }

        if (read.Status == DrawingDocumentReadStatus.LegacyFormat)
        {
            warnings.Add(SavedDrawingNotices.LegacyWarning);
        }

        DrawingContextState? contextState = null;
        var availableContexts = new List<string>();
        if (read.Status == DrawingDocumentReadStatus.Read && read.Document is { } document)
        {
            foreach (DrawingContextState candidate in document.Contexts)
            {
                availableContexts.Add(candidate.ContextName);
                if (string.Equals(candidate.ContextName, context, StringComparison.Ordinal))
                {
                    contextState = candidate;
                }
            }

            if (contextState is null)
            {
                warnings.Add($"The drawing file has no context '{context}'.");
            }
        }

        // The bars are read only when an object of this context can be calculated (a file without such an object needs no data).
        IReadOnlyList<DrawingObjectRecord> storedObjects = contextState?.Objects ?? Array.Empty<DrawingObjectRecord>();
        bool needsBars = storedObjects.Any(record => IsCalculable(record, out _));
        List<CandleData> bars = new();
        if (needsBars)
        {
            bars = endDate is not null
                ? await _loader.LoadUpToAsync(resolved.Symbol, timeFrame, endDate.Value, lookback, cancellationToken).ConfigureAwait(false)
                : (await _loader.LoadAsync(resolved.Symbol, timeFrame, lookback, cancellationToken).ConfigureAwait(false)).Bars;
            if (bars.Count == 0)
            {
                warnings.Add($"No stored {timeFrame} bars exist{(endDate is null ? string.Empty : $" on or before {endDate.Value.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture)}")} for {resolved.Symbol}; values are not calculated.");
            }
        }

        DateTime? evaluationTime = bars.Count > 0 ? bars[^1].Timestamp : null;
        decimal? currentPrice = bars.Count > 0 ? bars[^1].Close : null;
        IReadOnlyList<CoreCandleData>? coreBars = bars.Count > 0
            ? bars.Select(bar => new CoreCandleData(bar.Timestamp, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume)).ToList()
            : null;

        var layerOfObject = new Dictionary<Guid, Guid>();
        if (contextState is { } layered)
        {
            foreach (DrawingLayerRecord layer in layered.Layers)
            {
                foreach (Guid id in layer.ObjectIds)
                {
                    layerOfObject.TryAdd(id, layer.LayerId);
                }
            }
        }

        // With an endDate every object is checked against it (DrawingAsOfRule, owner decision M-2 = B): the object stays in the answer and carries lookAheadSuspect.
        int suspects = 0;
        var suspectIds = new HashSet<Guid>();
        if (endDate is { } asOf)
        {
            foreach (DrawingObjectRecord record in storedObjects)
            {
                if (DrawingAsOfRule.IsAfter(record, asOf) && suspectIds.Add(record.ObjectId))
                {
                    suspects++;
                }
            }

            if (suspects > 0)
            {
                warnings.Add(DrawingAsOfRule.SuspectWarning(suspects));
            }
        }

        // Calculate first so the warnings are complete before they are written.
        var evaluated = new List<(DrawingObjectRecord Record, bool Supported, IReadOnlyList<DrawingCalculatedValue> Values)>(storedObjects.Count);
        foreach (DrawingObjectRecord record in storedObjects)
        {
            IReadOnlyList<DrawingCalculatedValue> values = Array.Empty<DrawingCalculatedValue>();
            bool supported = false;
            if (IsCalculable(record, out DrawingValueDescriptor? descriptor) && evaluationTime is { } time && (!descriptor!.RequiresCandles || coreBars is not null))
            {
                supported = true;
                values = Calculate(descriptor!, record, time, currentPrice, coreBars, warnings, ref supported);
            }

            evaluated.Add((record, supported, values));
        }

        return ToolJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("symbol", resolved.Symbol);
            writer.WriteString("timeFrame", timeFrame.ToString());
            writer.WriteBoolean("drawingFileFound", read.Status != DrawingDocumentReadStatus.NotFound);
            writer.WriteBoolean("legacyFormat", read.Status == DrawingDocumentReadStatus.LegacyFormat);
            writer.WriteString("context", context);
            writer.WriteStartArray("availableContexts");
            foreach (string name in availableContexts)
            {
                writer.WriteStringValue(name);
            }

            writer.WriteEndArray();
            if (evaluationTime is { } evaluatedAt)
            {
                writer.WriteString("evaluationDate", ToolJson.FormatDate(evaluatedAt));
            }

            writer.WriteStartArray("layers");
            if (contextState is { } layersOf)
            {
                foreach (DrawingLayerRecord layer in layersOf.Layers)
                {
                    writer.WriteStartObject();
                    writer.WriteString("layerId", layer.LayerId);
                    writer.WriteString("name", layer.Name);
                    writer.WriteBoolean("isVisible", layer.IsVisible);
                    writer.WriteBoolean("isEditLocked", layer.IsEditLocked);
                    writer.WriteBoolean("isScreenSpace", layer.IsScreenSpace);
                    WritePanel(writer, layer.Panel);
                    writer.WriteStartArray("objectIds");
                    foreach (Guid id in layer.ObjectIds)
                    {
                        writer.WriteStringValue(id);
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
            }

            writer.WriteEndArray();

            writer.WriteStartArray("objects");
            foreach (var (record, supported, values) in evaluated)
            {
                writer.WriteStartObject();
                writer.WriteString("objectId", record.ObjectId);
                writer.WriteString("type", record.TypeName);
                if (layerOfObject.TryGetValue(record.ObjectId, out Guid layerId))
                {
                    writer.WriteString("layerId", layerId);
                }

                WritePanel(writer, record.Panel);
                writer.WriteString("coordinateKind", record.CoordinateKind.ToString());
                writer.WriteStartArray("points");
                for (int i = 0; i < record.Points.Count; i++)
                {
                    WritePoint(writer, i, record.Points[i]);
                }

                writer.WriteEndArray();
                writer.WriteStartObject("parameters");
                foreach (var (key, value) in record.StyleAndParameters.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(key);
                    WriteParameter(writer, value);
                }

                writer.WriteEndObject();
                writer.WriteBoolean("valuesSupported", supported);
                if (endDate is not null)
                {
                    writer.WriteBoolean("lookAheadSuspect", suspectIds.Contains(record.ObjectId));
                }

                writer.WriteStartArray("calculatedValues");
                foreach (DrawingCalculatedValue value in values)
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

    /// <summary>
    /// True when the object is the chart object of a catalogued tool (the saved type name is the class name of the chart object; LongShortPositionObject says which of its two tools it is
    /// in its saved "type" parameter), has time-based control points, and as many of them as the tool needs.
    /// </summary>
    private static bool IsCalculable(DrawingObjectRecord record, out DrawingValueDescriptor? descriptor)
    {
        descriptor = null;
        if (record.CoordinateKind != DrawingCoordinateKind.UtcTime
            || !DrawingValueCatalog.TryGetStored(record.TypeName, DrawingStoredOptions.ReadTool(record.StyleAndParameters), out DrawingValueDescriptor found)
            || record.Points.Count != found.PointCount
            || record.Points.Any(point => point.Kind != DrawingCoordinateKind.UtcTime || point.UtcTime is null))
        {
            return false;
        }

        descriptor = found;
        return true;
    }

    private static IReadOnlyList<DrawingCalculatedValue> Calculate(
        DrawingValueDescriptor descriptor,
        DrawingObjectRecord record,
        DateTime evaluationTime,
        decimal? currentPrice,
        IReadOnlyList<CoreCandleData>? candles,
        List<string> warnings,
        ref bool supported)
    {
        // The stored time is the chart time labelled UTC; the bars carry no zone, so only the ticks matter.
        DrawingAnchor[] anchors = record.Points
            .Select(point => new DrawingAnchor(DateTime.SpecifyKind(point.UtcTime!.Value, DateTimeKind.Unspecified), point.YValue))
            .ToArray();

        bool isLong = descriptor.Name == DrawingValueCatalog.LongPositionName;
        if ((isLong || descriptor.Name == DrawingValueCatalog.ShortPositionName) && !LongShortPositionValues.IsOrdered(anchors[0].Price, anchors[1].Price, anchors[2].Price, isLong))
        {
            string rule = isLong ? "Stop < Entry < Target" : "Target < Entry < Stop";
            warnings.Add($"Object {record.ObjectId} ({record.TypeName}): Entry, Stop and Target are not in the order {rule}; the values are calculated as saved.");
        }

        DrawingValueOptions options = DrawingStoredOptions.Read(record.StyleAndParameters);
        var request = new DrawingValueRequest(anchors, evaluationTime, currentPrice, IndicatorColor.Gray, options, candles);
        try
        {
            IReadOnlyList<DrawingCalculatedValue> values = descriptor.Calculate(request);
            if (values.Count == 0)
            {
                warnings.Add($"Object {record.ObjectId} ({record.TypeName}): the tool returned no values for its saved control points.");
            }

            return values;
        }
        catch (OverflowException)
        {
            supported = false;
            warnings.Add($"Object {record.ObjectId} ({record.TypeName}): the value is outside the decimal range; no values are returned.");
            return Array.Empty<DrawingCalculatedValue>();
        }
    }

    private static void WritePanel(Utf8JsonWriter writer, PanelKey panel)
    {
        writer.WriteStartObject("panel");
        writer.WriteString("kind", panel.Kind.ToString());
        if (panel.IndicatorId is not null)
        {
            writer.WriteString("indicatorId", panel.IndicatorId);
        }

        if (panel.OverlayGroupId is not null)
        {
            writer.WriteString("overlayGroupId", panel.OverlayGroupId);
        }

        writer.WriteEndObject();
    }

    private static void WritePoint(Utf8JsonWriter writer, int index, DrawingStoredPoint point)
    {
        writer.WriteStartObject();
        writer.WriteNumber("index", index);
        writer.WriteString("kind", point.Kind.ToString());
        if (point.UtcTime is { } time)
        {
            writer.WriteString("date", ToolJson.FormatDate(time));
            writer.WriteString("time", time.ToString(TimeFormat, CultureInfo.InvariantCulture));
        }

        if (point.XValue is { } x)
        {
            writer.WriteNumber("xValue", x);
        }

        writer.WriteNumber("price", point.YValue);
        writer.WriteEndObject();
    }

    private static void WriteParameter(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonElement element:
                element.WriteTo(writer);
                break;
            default:
                JsonSerializer.Serialize(writer, value);
                break;
        }
    }
}
