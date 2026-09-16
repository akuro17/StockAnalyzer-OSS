using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Serialization;
using StockAnalyzer.Core.Helpers;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Models.Drawing;

/// <summary>
/// Uniquely identifies a drawing document by normalized ticker and timeframe.
/// Enforces ticker normalization (via TickerHelper.NormalizeTicker) at construction.
/// </summary>
public readonly record struct DrawingDocumentKey
{
    [JsonPropertyName("ticker")]
    public string Ticker { get; }

    [JsonPropertyName("timeframe")]
    public TimeframeType Timeframe { get; }

    [JsonConstructor]
    public DrawingDocumentKey(string ticker, TimeframeType timeframe)
    {
        Ticker = TickerHelper.NormalizeTicker(ticker ?? string.Empty);
        Timeframe = timeframe;
    }

    public override string ToString() => $"{Ticker}.{Timeframe}";
}

/// <summary>
/// Specifies the nature of the X-axis coordinate stored in a drawing point.
/// </summary>
public enum DrawingCoordinateKind
{
    UtcTime,
    LogicalIndex,
    VolumePosition
}

/// <summary>
/// A type-safe, immutable point representation for drawing persistence and frozen state.
/// Ensures that real timestamps are stored strictly as UTC and non-time coordinates as decimals.
/// </summary>
public readonly record struct DrawingStoredPoint
{
    [JsonPropertyName("kind")]
    public DrawingCoordinateKind Kind { get; }

    [JsonPropertyName("utcTime")]
    public DateTime? UtcTime { get; }

    [JsonPropertyName("xValue")]
    public decimal? XValue { get; }

    [JsonPropertyName("yValue")]
    public decimal YValue { get; }

    [JsonConstructor]
    public DrawingStoredPoint(DrawingCoordinateKind kind, DateTime? utcTime, decimal? xValue, decimal yValue)
    {
        Kind = kind;
        YValue = yValue;

        switch (kind)
        {
            case DrawingCoordinateKind.UtcTime:
                if (!utcTime.HasValue)
                {
                    throw new ArgumentException("UtcTime must not be null when Kind is UtcTime.", nameof(utcTime));
                }
                if (xValue.HasValue)
                {
                    throw new ArgumentException("XValue must be null when Kind is UtcTime.", nameof(xValue));
                }
                if (utcTime.Value.Kind != DateTimeKind.Utc)
                {
                    UtcTime = DateTime.SpecifyKind(utcTime.Value, DateTimeKind.Utc);
                }
                else
                {
                    UtcTime = utcTime;
                }
                XValue = null;
                break;

            case DrawingCoordinateKind.LogicalIndex:
            case DrawingCoordinateKind.VolumePosition:
                if (!xValue.HasValue)
                {
                    throw new ArgumentException($"XValue must not be null when Kind is {kind}.", nameof(xValue));
                }
                if (utcTime.HasValue)
                {
                    throw new ArgumentException($"UtcTime must be null when Kind is {kind}.", nameof(utcTime));
                }
                UtcTime = null;
                XValue = xValue;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown coordinate kind.");
        }
    }

    public static DrawingStoredPoint CreateUtc(DateTime utcTime, decimal yValue)
    {
        var validatedUtc = utcTime.Kind == DateTimeKind.Utc ? utcTime : DateTime.SpecifyKind(utcTime, DateTimeKind.Utc);
        return new DrawingStoredPoint(DrawingCoordinateKind.UtcTime, validatedUtc, null, yValue);
    }

    public static DrawingStoredPoint CreateIndex(decimal index, decimal yValue)
    {
        return new DrawingStoredPoint(DrawingCoordinateKind.LogicalIndex, null, index, yValue);
    }

    public static DrawingStoredPoint CreateVolume(decimal volume, decimal yValue)
    {
        return new DrawingStoredPoint(DrawingCoordinateKind.VolumePosition, null, volume, yValue);
    }
}

/// <summary>
/// Distinguishes the panel target type for a drawing layer or object.
/// </summary>
public enum PanelKind
{
    Main,
    Indicator,
    OverlayGroup
}

/// <summary>
/// Stable identity for a chart panel, independent of transient screen display index.
/// </summary>
public readonly record struct PanelKey
{
    [JsonPropertyName("kind")]
    public PanelKind Kind { get; }

    [JsonPropertyName("indicatorId")]
    public string? IndicatorId { get; }

    [JsonPropertyName("overlayPanelId")]
    public int? OverlayPanelId { get; }

    [JsonPropertyName("overlayGroupId")]
    public string? OverlayGroupId { get; }

    [JsonConstructor]
    public PanelKey(PanelKind kind, string? indicatorId = null, int? overlayPanelId = null, string? overlayGroupId = null)
    {
        Kind = kind;
        IndicatorId = indicatorId;
        OverlayPanelId = overlayPanelId;
        OverlayGroupId = overlayGroupId ?? (overlayPanelId.HasValue ? overlayPanelId.Value.ToString() : null);
    }

    public static PanelKey Main => new(PanelKind.Main);

    public static PanelKey Indicator(string indicatorId)
    {
        if (string.IsNullOrWhiteSpace(indicatorId))
        {
            throw new ArgumentException("Indicator ID must not be empty.", nameof(indicatorId));
        }
        return new PanelKey(PanelKind.Indicator, indicatorId: indicatorId);
    }

    public static PanelKey OverlayGroup(int overlayPanelId)
    {
        return new PanelKey(PanelKind.OverlayGroup, overlayPanelId: overlayPanelId, overlayGroupId: overlayPanelId.ToString());
    }

    public static PanelKey OverlayGroup(string overlayGroupId)
    {
        if (string.IsNullOrWhiteSpace(overlayGroupId))
        {
            throw new ArgumentException("Overlay group ID must not be empty.", nameof(overlayGroupId));
        }
        int? numeric = int.TryParse(overlayGroupId, out var n) ? n : null;
        return new PanelKey(PanelKind.OverlayGroup, overlayPanelId: numeric, overlayGroupId: overlayGroupId);
    }
}

/// <summary>
/// Represents the persistent metadata and child object IDs of an independent drawing layer.
/// </summary>
public readonly record struct DrawingLayerRecord
{
    [JsonPropertyName("layerId")]
    public Guid LayerId { get; }

    [JsonPropertyName("name")]
    public string Name { get; }

    [JsonPropertyName("panel")]
    public PanelKey Panel { get; }

    [JsonPropertyName("isVisible")]
    public bool IsVisible { get; }

    [JsonPropertyName("isEditLocked")]
    public bool IsEditLocked { get; }

    [JsonPropertyName("objectIds")]
    public IReadOnlyList<Guid> ObjectIds { get; }

    [JsonConstructor]
    public DrawingLayerRecord(
        Guid layerId,
        string name,
        PanelKey panel,
        bool isVisible,
        bool isEditLocked,
        IReadOnlyList<Guid>? objectIds)
    {
        LayerId = layerId;
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Panel = panel;
        IsVisible = isVisible;
        IsEditLocked = isEditLocked;
        ObjectIds = objectIds != null ? Array.AsReadOnly(objectIds.ToArray()) : Array.Empty<Guid>();
    }
}

/// <summary>
/// Immutable value representation of a relative ghost candle for BarPatternObject and GhostFeedObject.
/// </summary>
public readonly record struct GhostCandleRecord(
    [property: JsonPropertyName("ticksOffset")] long TicksOffset,
    [property: JsonPropertyName("open")] decimal Open,
    [property: JsonPropertyName("high")] decimal High,
    [property: JsonPropertyName("low")] decimal Low,
    [property: JsonPropertyName("close")] decimal Close
);

/// <summary>
/// Complete persistent record of an individual drawing object.
/// StyleAndParameters encapsulates all properties outside Points and Identity, guaranteeing deep value copies.
/// </summary>
public readonly record struct DrawingObjectRecord
{
    [JsonPropertyName("objectId")]
    public Guid ObjectId { get; }

    [JsonPropertyName("typeName")]
    public string TypeName { get; }

    [JsonPropertyName("panel")]
    public PanelKey Panel { get; }

    [JsonPropertyName("coordinateKind")]
    public DrawingCoordinateKind CoordinateKind { get; }

    [JsonPropertyName("points")]
    public IReadOnlyList<DrawingStoredPoint> Points { get; }

    [JsonPropertyName("styleAndParameters")]
    public IReadOnlyDictionary<string, object?> StyleAndParameters { get; }

    [JsonConstructor]
    public DrawingObjectRecord(
        Guid objectId,
        string typeName,
        PanelKey panel,
        DrawingCoordinateKind coordinateKind,
        IReadOnlyList<DrawingStoredPoint>? points,
        IReadOnlyDictionary<string, object?>? styleAndParameters)
    {
        ObjectId = objectId;
        TypeName = typeName ?? throw new ArgumentNullException(nameof(typeName));
        Panel = panel;
        CoordinateKind = coordinateKind;
        Points = points != null ? Array.AsReadOnly(points.ToArray()) : Array.Empty<DrawingStoredPoint>();
        StyleAndParameters = styleAndParameters != null 
            ? DeepCloneParameters(styleAndParameters)
            : new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>());
    }

    private static IReadOnlyDictionary<string, object?> DeepCloneParameters(IReadOnlyDictionary<string, object?> source)
    {
        var dict = new Dictionary<string, object?>(source.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in source)
        {
            dict[k] = DeepCloneValue(v);
        }
        return new ReadOnlyDictionary<string, object?>(dict);
    }

    private static object? DeepCloneValue(object? value)
    {
        if (value == null) return null;
        if (value is string || value.GetType().IsValueType) return value;

        if (value is System.Text.Json.JsonElement je)
        {
            return je.Clone();
        }

        if (value is System.Collections.IEnumerable enumerable)
        {
            var list = new List<object?>();
            foreach (var item in enumerable)
            {
                list.Add(DeepCloneValue(item));
            }
            return list.AsReadOnly();
        }

        if (value is ICloneable cloneable)
        {
            return cloneable.Clone();
        }

        try
        {
            return System.Text.Json.JsonSerializer.SerializeToElement(value);
        }
        catch
        {
            return value.ToString();
        }
    }
}

/// <summary>
/// State of a single chart drawing context (e.g. Standard, Renko, ReverseWatch) within a document.
/// </summary>
public readonly record struct DrawingContextState
{
    [JsonPropertyName("contextName")]
    public string ContextName { get; }

    [JsonPropertyName("layers")]
    public IReadOnlyList<DrawingLayerRecord> Layers { get; }

    [JsonPropertyName("objects")]
    public IReadOnlyList<DrawingObjectRecord> Objects { get; }

    [JsonConstructor]
    public DrawingContextState(
        string contextName,
        IReadOnlyList<DrawingLayerRecord>? layers,
        IReadOnlyList<DrawingObjectRecord>? objects)
    {
        ContextName = contextName ?? throw new ArgumentNullException(nameof(contextName));
        Layers = layers != null ? Array.AsReadOnly(layers.ToArray()) : Array.Empty<DrawingLayerRecord>();
        Objects = objects != null ? Array.AsReadOnly(objects.ToArray()) : Array.Empty<DrawingObjectRecord>();
    }
}

/// <summary>
/// Root document schema (V2) for chart drawing persistence.
/// </summary>
public readonly record struct DrawingDocumentState
{
    public const int CurrentSchemaVersion = 2;

    [JsonPropertyName("version")]
    public int Version { get; }

    [JsonPropertyName("key")]
    public DrawingDocumentKey Key { get; }

    [JsonPropertyName("revision")]
    public long Revision { get; }

    [JsonPropertyName("contexts")]
    public IReadOnlyList<DrawingContextState> Contexts { get; }

    [JsonConstructor]
    public DrawingDocumentState(
        int version,
        DrawingDocumentKey key,
        long revision,
        IReadOnlyList<DrawingContextState>? contexts)
    {
        Version = version;
        Key = key;
        Revision = revision;
        Contexts = contexts != null ? Array.AsReadOnly(contexts.ToArray()) : Array.Empty<DrawingContextState>();
    }
}

/// <summary>
/// An immutable, deeply frozen copy of a drawing context state for Undo/Redo history.
/// Contains zero references to live UI or drawing instances.
/// </summary>
public readonly record struct FrozenContextState
{
    [JsonPropertyName("contextName")]
    public string ContextName { get; }

    [JsonPropertyName("layers")]
    public IReadOnlyList<DrawingLayerRecord> Layers { get; }

    [JsonPropertyName("objects")]
    public IReadOnlyList<DrawingObjectRecord> Objects { get; }

    [JsonConstructor]
    public FrozenContextState(
        string contextName,
        IReadOnlyList<DrawingLayerRecord>? layers,
        IReadOnlyList<DrawingObjectRecord>? objects)
    {
        ContextName = contextName ?? throw new ArgumentNullException(nameof(contextName));
        Layers = layers != null ? Array.AsReadOnly(layers.ToArray()) : Array.Empty<DrawingLayerRecord>();
        Objects = objects != null ? Array.AsReadOnly(objects.ToArray()) : Array.Empty<DrawingObjectRecord>();
    }

    public static FrozenContextState FromContextState(DrawingContextState state)
    {
        return new FrozenContextState(state.ContextName, state.Layers, state.Objects);
    }

    public DrawingContextState ToContextState()
    {
        return new DrawingContextState(ContextName, Layers, Objects);
    }
}
