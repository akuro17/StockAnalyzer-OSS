using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Media;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models.Drawing;

namespace StockAnalyzer.Avalonia.Drawing.Serialization;

/// <summary>
/// Coordinate transform metadata used by DrawingDocumentCodec to accurately determine
/// coordinate classifications (e.g. index charts with TimeMap vs raw index charts).
/// </summary>
public readonly record struct DrawingCoordinateMetadata(
    ChartAxisMode AxisMode,
    bool HasTimeMap
);

/// <summary>
/// Container for materialized chart objects and runtime-persistent ID mappings.
/// Disposing this document safely disposes any materialized chart objects.
/// </summary>
public sealed class MaterializedDrawingDocument : IDisposable
{
    private readonly Dictionary<Guid, Guid> _persistentToRuntime = new();
    private readonly Dictionary<Guid, Guid> _runtimeToPersistent = new();
    private readonly List<IChartObject> _allTrackedObjects = new();

    public Dictionary<ChartDrawingContextType, List<IChartObject>> Objects { get; } = new();
    public Dictionary<ChartDrawingContextType, List<DrawingLayerRecord>> Layers { get; } = new();

    /// <summary>Link groups per context; ids inside are persistent ids (map with <see cref="PersistentToRuntimeIds"/>).</summary>
    public Dictionary<ChartDrawingContextType, IReadOnlyList<DrawingLinkGroupRecord>> LinkGroups { get; } = new();

    public IReadOnlyDictionary<Guid, Guid> PersistentToRuntimeIds => _persistentToRuntime;
    public IReadOnlyDictionary<Guid, Guid> RuntimeToPersistentIds => _runtimeToPersistent;

    internal void TrackObject(IChartObject obj)
    {
        _allTrackedObjects.Add(obj);
    }

    internal void RegisterMapping(Guid persistentId, Guid runtimeId)
    {
        _persistentToRuntime[persistentId] = runtimeId;
        _runtimeToPersistent[runtimeId] = persistentId;
    }

    /// <summary>
    /// Detaches tracked objects so they are not disposed when this document is disposed.
    /// Used when transferring ownership to ChartObjectManager upon successful materialization.
    /// </summary>
    public void DetachObjects()
    {
        _allTrackedObjects.Clear();
    }

    public void Dispose()
    {
        foreach (var obj in _allTrackedObjects)
        {
            if (obj is IDisposable disposable)
            {
                try
                {
                    disposable.Dispose();
                }
                catch
                {
                    // Best-effort disposal of individual objects
                }
            }
        }
        _allTrackedObjects.Clear();

        foreach (var list in Objects.Values)
        {
            list.Clear();
        }
        Objects.Clear();
        foreach (var list in Layers.Values)
        {
            list.Clear();
        }
        Layers.Clear();
        LinkGroups.Clear();
        _persistentToRuntime.Clear();
        _runtimeToPersistent.Clear();
    }
}

/// <summary>
/// Codec for V2 drawing document serialization, deep capture, and materialization.
/// Strictly enforces schema validation (rejection of unknown types/properties),
/// respects JsonPropertyOrder, and preserves explicit UTC / decimal coordinate representations without loss.
/// </summary>
public static class DrawingDocumentCodec
{
    private static readonly HashSet<string> ReservedExcludedProperties;

    static DrawingDocumentCodec()
    {
        ReservedExcludedProperties = new HashSet<string>(DrawingParameterTags.IgnoredPropertyNames, StringComparer.OrdinalIgnoreCase)
        {
            "ZIndex",
            "PanelIndex"
        };
    }

    public static readonly Guid FallbackDefaultLayerId = new("00000000-0000-0000-0000-000000000001");

    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters =
        {
            new AvaloniaColorJsonConverter(),
            new JsonStringEnumConverter()
        }
    };

    /// <summary>
    /// Captures the complete state of a ChartObjectManager into an immutable DrawingDocumentState DTO.
    /// </summary>
    public static DrawingDocumentState Capture(
        ChartObjectManager manager,
        DrawingDocumentKey key,
        long revision,
        IReadOnlyDictionary<Guid, Guid>? runtimeToPersistentIds = null,
        IReadOnlyDictionary<ChartDrawingContextType, DrawingCoordinateMetadata>? coordinateMetadata = null,
        IReadOnlyList<DrawingLayerRecord>? layers = null,
        IReadOnlyDictionary<ChartDrawingContextType, IReadOnlyList<DrawingLayerRecord>>? layersByContext = null)
    {
        if (manager == null) throw new ArgumentNullException(nameof(manager));

        var snapshot = manager.GetSnapshot();
        var contexts = new List<DrawingContextState>(snapshot.Count);

        foreach (var (contextType, objects) in snapshot)
        {
            DrawingCoordinateMetadata? meta = null;
            if (coordinateMetadata != null && coordinateMetadata.TryGetValue(contextType, out var m))
            {
                meta = m;
            }

            var coordKind = GetCoordinateKind(contextType, meta);
            var objectRecords = new List<DrawingObjectRecord>(objects.Count);
            var objectIds = new List<Guid>(objects.Count);
            var localRuntimeToPersistent = new Dictionary<Guid, Guid>();

            foreach (var obj in objects)
            {
                var runtimeId = obj.Id;
                var persistentId = (runtimeToPersistentIds != null && runtimeToPersistentIds.TryGetValue(runtimeId, out var pid))
                    ? pid
                    : Guid.NewGuid();

                localRuntimeToPersistent[runtimeId] = persistentId;
                objectIds.Add(persistentId);

                var points = new List<DrawingStoredPoint>(obj.Points.Count);
                foreach (var pt in obj.Points)
                {
                    points.Add(ConvertChartPointToStored(pt, coordKind));
                }

                var panel = obj.PanelIndex == -1 ? PanelKey.Main : PanelKey.OverlayGroup(obj.PanelIndex);
                var parameters = ExtractParameters(obj);

                var record = new DrawingObjectRecord(
                    persistentId,
                    obj.GetType().Name,
                    panel,
                    coordKind,
                    points,
                    parameters
                );

                objectRecords.Add(record);
            }

            IReadOnlyList<DrawingLayerRecord>? targetSourceLayers = null;
            if (layersByContext != null && layersByContext.TryGetValue(contextType, out var ctxLayers))
            {
                targetSourceLayers = ctxLayers;
            }
            else if (contextType == manager.CurrentContext && layers != null)
            {
                targetSourceLayers = layers;
            }

            IReadOnlyList<DrawingLayerRecord> contextLayers;
            if (targetSourceLayers != null && targetSourceLayers.Count > 0)
            {
                var mappedLayers = new List<DrawingLayerRecord>(targetSourceLayers.Count);
                foreach (var l in targetSourceLayers)
                {
                    var mappedIds = new List<Guid>(l.ObjectIds.Count);
                    foreach (var id in l.ObjectIds)
                    {
                        if (localRuntimeToPersistent.TryGetValue(id, out var pid))
                        {
                            mappedIds.Add(pid);
                        }
                        else if (runtimeToPersistentIds != null && runtimeToPersistentIds.TryGetValue(id, out var pid2))
                        {
                            mappedIds.Add(pid2);
                        }
                        else
                        {
                            mappedIds.Add(id);
                        }
                    }
                    mappedLayers.Add(new DrawingLayerRecord(l.LayerId, l.Name, l.Panel, l.IsVisible, l.IsEditLocked, mappedIds));
                }
                contextLayers = mappedLayers;
            }
            else
            {
                var defaultLayerId = FallbackDefaultLayerId;
                contextLayers = new[]
                {
                    new DrawingLayerRecord(
                        defaultLayerId,
                        "Default Layer",
                        PanelKey.Main,
                        isVisible: true,
                        isEditLocked: false,
                        objectIds: objectIds
                    )
                };
            }

            contexts.Add(new DrawingContextState(
                contextType.ToString(),
                contextLayers,
                objectRecords,
                manager.GetLinkGroupRecords(contextType, localRuntimeToPersistent)
            ));
        }

        return new DrawingDocumentState(
            DrawingDocumentState.CurrentSchemaVersion,
            key,
            revision,
            contexts
        );
    }

    /// <summary>
    /// Materializes an immutable DrawingDocumentState into runtime IChartObject instances.
    /// Rejects unknown types, unknown properties, or invalid values strictly.
    /// </summary>
    public static MaterializedDrawingDocument Materialize(DrawingDocumentState state)
    {
        if (state.Version != DrawingDocumentState.CurrentSchemaVersion)
        {
            throw new NotSupportedException($"Unsupported drawing document schema version: {state.Version}. Expected: {DrawingDocumentState.CurrentSchemaVersion}");
        }

        var document = new MaterializedDrawingDocument();

        try
        {
            var seenObjectIds = new HashSet<Guid>();

            foreach (var ctx in state.Contexts)
            {
                if (!Enum.TryParse<ChartDrawingContextType>(ctx.ContextName, out var contextType))
                {
                    throw new NotSupportedException($"Unknown drawing context type: '{ctx.ContextName}'.");
                }

                var list = new List<IChartObject>(ctx.Objects.Count);
                int zIndex = 0;

                foreach (var record in ctx.Objects)
                {
                    if (!seenObjectIds.Add(record.ObjectId))
                    {
                        throw new InvalidDataException($"Duplicate ObjectId detected in drawing document: {record.ObjectId}");
                    }

                    var concreteType = ChartObjectTypeRegistry.Resolve(record.TypeName);
                    if (concreteType == null)
                    {
                        throw new NotSupportedException($"Unknown chart object type '{record.TypeName}'. Registration in ChartObjectTypeRegistry required.");
                    }

                    var instance = ChartObjectInstanceFactory.Create(concreteType);
                    document.TrackObject(instance);

                    // Restore points explicitly
                    instance.Points.Clear();
                    foreach (var storedPt in record.Points)
                    {
                        instance.Points.Add(ConvertStoredToChartPoint(storedPt, record.CoordinateKind));
                    }

                    // Restore properties respecting JsonPropertyOrder
                    ApplyParameters(instance, concreteType, record.StyleAndParameters);

                    // Boundary check for relative bar patterns (R12)
                    ValidateRelativeBarBounds(instance);

                    // Restore projection values (ZIndex and PanelIndex)
                    instance.PanelIndex = record.Panel.Kind == PanelKind.Main ? -1 : (record.Panel.OverlayPanelId ?? -1);
                    instance.ZIndex = zIndex++;

                    document.RegisterMapping(record.ObjectId, instance.Id);
                    list.Add(instance);
                }

                document.Objects[contextType] = list;

                var mappedLayers = new List<DrawingLayerRecord>(ctx.Layers.Count);
                foreach (var l in ctx.Layers)
                {
                    var mappedIds = new List<Guid>(l.ObjectIds.Count);
                    foreach (var pid in l.ObjectIds)
                    {
                        mappedIds.Add(document.PersistentToRuntimeIds.TryGetValue(pid, out var rid) ? rid : pid);
                    }
                    mappedLayers.Add(new DrawingLayerRecord(l.LayerId, l.Name, l.Panel, l.IsVisible, l.IsEditLocked, mappedIds));
                }
                document.Layers[contextType] = mappedLayers;
                document.LinkGroups[contextType] = ctx.LinkGroups;
            }

            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Determines coordinate kind using context type and optional transform metadata (SSoT resolution for G2/R02).
    /// </summary>
    public static DrawingCoordinateKind GetCoordinateKind(
        ChartDrawingContextType contextType,
        DrawingCoordinateMetadata? metadata = null)
    {
        if (metadata.HasValue)
        {
            var mode = metadata.Value.AxisMode;
            if (mode == ChartAxisMode.Time || mode == ChartAxisMode.GaplessTime)
            {
                return DrawingCoordinateKind.UtcTime;
            }
            if (mode == ChartAxisMode.Index)
            {
                // If TimeMap is present, the chart tracks real timestamps via fractional index bridge
                return metadata.Value.HasTimeMap ? DrawingCoordinateKind.UtcTime : DrawingCoordinateKind.LogicalIndex;
            }
            if (mode == ChartAxisMode.Volume)
            {
                return DrawingCoordinateKind.VolumePosition;
            }
        }

        return contextType switch
        {
            ChartDrawingContextType.Standard => DrawingCoordinateKind.UtcTime,
            ChartDrawingContextType.Linear => DrawingCoordinateKind.UtcTime,
            ChartDrawingContextType.Kagi => DrawingCoordinateKind.LogicalIndex,
            ChartDrawingContextType.Renko => DrawingCoordinateKind.LogicalIndex,
            ChartDrawingContextType.PointAndFigure => DrawingCoordinateKind.LogicalIndex,
            ChartDrawingContextType.ThreeLineBreak => DrawingCoordinateKind.LogicalIndex,
            ChartDrawingContextType.ReverseWatch => DrawingCoordinateKind.VolumePosition,
            _ => throw new ArgumentOutOfRangeException(nameof(contextType), contextType, "Unmapped chart drawing context type.")
        };
    }

    public static DrawingStoredPoint ConvertChartPointToStored(ChartPoint pt, DrawingCoordinateKind kind)
    {
        return kind switch
        {
            DrawingCoordinateKind.UtcTime => DrawingStoredPoint.CreateUtc(
                pt.Time.Kind == DateTimeKind.Utc ? pt.Time : DateTime.SpecifyKind(pt.Time, DateTimeKind.Utc),
                pt.Price
            ),
            DrawingCoordinateKind.LogicalIndex => DrawingStoredPoint.CreateIndex(
                (decimal)pt.Time.Ticks,
                pt.Price
            ),
            DrawingCoordinateKind.VolumePosition => DrawingStoredPoint.CreateVolume(
                (decimal)pt.Time.Ticks,
                pt.Price
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Invalid coordinate kind.")
        };
    }

    public static ChartPoint ConvertStoredToChartPoint(DrawingStoredPoint storedPt, DrawingCoordinateKind kind)
    {
        return kind switch
        {
            DrawingCoordinateKind.UtcTime => new ChartPoint(
                storedPt.UtcTime ?? throw new InvalidDataException("UtcTime was null on UtcTime coordinate point."),
                storedPt.YValue
            ),
            DrawingCoordinateKind.LogicalIndex => new ChartPoint(
                SafeTicksToDateTime(storedPt.XValue, nameof(DrawingCoordinateKind.LogicalIndex)),
                storedPt.YValue
            ),
            DrawingCoordinateKind.VolumePosition => new ChartPoint(
                SafeTicksToDateTime(storedPt.XValue, nameof(DrawingCoordinateKind.VolumePosition)),
                storedPt.YValue
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Invalid coordinate kind.")
        };
    }

    private static DateTime SafeTicksToDateTime(decimal? xValue, string coordinateName)
    {
        if (!xValue.HasValue)
        {
            throw new InvalidDataException($"XValue was null on {coordinateName} coordinate point.");
        }

        try
        {
            long ticks = checked((long)Math.Round(xValue.Value, MidpointRounding.AwayFromZero));
            if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
            {
                throw new InvalidDataException($"Ticks value {ticks} for {coordinateName} is outside valid DateTime range.");
            }
            return new DateTime(ticks);
        }
        catch (OverflowException)
        {
            throw new InvalidDataException($"Ticks value {xValue.Value} for {coordinateName} caused integer overflow.");
        }
    }

    public static string SerializeToJson(DrawingDocumentState state)
    {
        return JsonSerializer.Serialize(state, SerializerOptions);
    }

    public static DrawingDocumentState DeserializeFromJson(string json)
    {
        var state = JsonSerializer.Deserialize<DrawingDocumentState>(json, SerializerOptions);
        if (state.Version != DrawingDocumentState.CurrentSchemaVersion)
        {
            throw new NotSupportedException($"Unsupported drawing document schema version: {state.Version}. Expected: {DrawingDocumentState.CurrentSchemaVersion}");
        }
        return state;
    }

    private static Dictionary<string, object?> ExtractParameters(IChartObject obj)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var type = obj.GetType();

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (ReservedExcludedProperties.Contains(prop.Name)) continue;
            if (prop.GetIndexParameters().Length > 0) continue;
            if (!prop.CanRead) continue;
            if (prop.GetSetMethod(nonPublic: true) == null) continue;
            if (prop.GetCustomAttribute<JsonIgnoreAttribute>() != null) continue;

            object? value;
            try
            {
                value = prop.GetValue(obj);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to extract property '{prop.Name}' from '{type.Name}'.", ex);
            }

            // Deep clone GhostCandle collections to prevent shared mutable state (R01)
            if (value is IEnumerable<GhostCandle> candles)
            {
                var clonedCandles = new List<GhostCandle>();
                foreach (var c in candles)
                {
                    clonedCandles.Add(new GhostCandle
                    {
                        TicksOffset = c.TicksOffset,
                        Open = c.Open,
                        High = c.High,
                        Low = c.Low,
                        Close = c.Close
                    });
                }
                value = clonedCandles;
            }

            var camelName = JsonNamingPolicy.CamelCase.ConvertName(prop.Name);
            result[camelName] = value;
        }

        return result;
    }

    private static void ApplyParameters(IChartObject instance, Type concreteType, IReadOnlyDictionary<string, object?> parameters)
    {
        var properties = concreteType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 &&
                        p.GetSetMethod(nonPublic: true) != null &&
                        !ReservedExcludedProperties.Contains(p.Name) &&
                        p.GetCustomAttribute<JsonIgnoreAttribute>() == null)
            .OrderBy(p => p.GetCustomAttribute<JsonPropertyOrderAttribute>()?.Order ?? 0)
            .ToList();

        var propMap = properties.ToDictionary(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name), p => p, StringComparer.OrdinalIgnoreCase);

        // Strict validation: reject any unknown parameters (R06)
        var allPublicNames = new HashSet<string>(
            concreteType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetIndexParameters().Length == 0)
                .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var key in parameters.Keys)
        {
            if (!propMap.ContainsKey(key) && !allPublicNames.Contains(key))
            {
                throw new NotSupportedException($"Unknown property '{key}' on chart object type '{concreteType.Name}'.");
            }
        }

        // Apply parameters in strict JsonPropertyOrder sequence (R10)
        foreach (var prop in properties)
        {
            var camelName = JsonNamingPolicy.CamelCase.ConvertName(prop.Name);
            if (!parameters.TryGetValue(camelName, out var rawValue))
            {
                continue;
            }

            var setter = prop.GetSetMethod(nonPublic: true)!;
            object? converted = ConvertPropertyValue(rawValue, prop.PropertyType);

            // If property is List<GhostCandle>, ensure fresh instance with deep copied elements
            if (converted is IEnumerable<GhostCandle> ghostCandles)
            {
                var freshList = new List<GhostCandle>();
                foreach (var gc in ghostCandles)
                {
                    freshList.Add(new GhostCandle
                    {
                        TicksOffset = gc.TicksOffset,
                        Open = gc.Open,
                        High = gc.High,
                        Low = gc.Low,
                        Close = gc.Close
                    });
                }
                converted = freshList;
            }

            try
            {
                setter.Invoke(instance, new[] { converted });
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to restore property '{prop.Name}' on '{concreteType.Name}'.", ex);
            }
        }
    }

    private static void ValidateRelativeBarBounds(IChartObject instance)
    {
        if (instance is BarPatternObject barPattern && barPattern.Points.Count > 0)
        {
            long anchorTicks = barPattern.Points[0].Time.Ticks;
            foreach (var c in barPattern.Candles)
            {
                try
                {
                    checked
                    {
                        long targetTicks = anchorTicks + c.TicksOffset;
                        if (targetTicks < DateTime.MinValue.Ticks || targetTicks > DateTime.MaxValue.Ticks)
                        {
                            throw new InvalidDataException($"Ghost candle offset {c.TicksOffset} overflows DateTime range at anchor {anchorTicks}.");
                        }
                    }
                }
                catch (OverflowException)
                {
                    throw new InvalidDataException($"Ghost candle offset {c.TicksOffset} caused overflow when adding to anchor ticks.");
                }
            }
        }
        else if (instance is GhostFeedObject ghostFeed && ghostFeed.Points.Count > 0)
        {
            long anchorTicks = ghostFeed.Points[0].Time.Ticks;
            foreach (var c in ghostFeed.Candles)
            {
                try
                {
                    checked
                    {
                        long targetTicks = anchorTicks + c.TicksOffset;
                        if (targetTicks < DateTime.MinValue.Ticks || targetTicks > DateTime.MaxValue.Ticks)
                        {
                            throw new InvalidDataException($"Ghost candle offset {c.TicksOffset} overflows DateTime range at anchor {anchorTicks}.");
                        }
                    }
                }
                catch (OverflowException)
                {
                    throw new InvalidDataException($"Ghost candle offset {c.TicksOffset} caused overflow when adding to anchor ticks.");
                }
            }
        }
    }

    private static object? ConvertPropertyValue(object? rawValue, Type targetType)
    {
        if (rawValue == null)
        {
            return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
        }

        if (rawValue is JsonElement jsonEl)
        {
            return JsonSerializer.Deserialize(jsonEl.GetRawText(), targetType, SerializerOptions);
        }

        if (targetType.IsAssignableFrom(rawValue.GetType()))
        {
            return rawValue;
        }

        // Handle List<GhostCandle> deep conversion
        if (targetType == typeof(List<GhostCandle>))
        {
            if (rawValue is System.Collections.IEnumerable enumerable)
            {
                var list = new List<GhostCandle>();
                foreach (var item in enumerable)
                {
                    if (item is GhostCandle gc)
                    {
                        list.Add(new GhostCandle
                        {
                            TicksOffset = gc.TicksOffset,
                            Open = gc.Open,
                            High = gc.High,
                            Low = gc.Low,
                            Close = gc.Close
                        });
                    }
                    else if (item is JsonElement el)
                    {
                        list.Add(JsonSerializer.Deserialize<GhostCandle>(el.GetRawText(), SerializerOptions)!);
                    }
                }
                return list;
            }
        }

        if (targetType == typeof(Color) && rawValue is string strColor)
        {
            return Color.Parse(strColor);
        }

        // Handle generic List<T>, IReadOnlyList<T>, IReadOnlyCollection<T>, IEnumerable<T>, IList<T>, ICollection<T> conversion
        // Guard: string implements IEnumerable but must not be treated as a character sequence here.
        if (targetType.IsGenericType && rawValue is not string)
        {
            var genericDef = targetType.GetGenericTypeDefinition();
            if (genericDef == typeof(List<>) ||
                genericDef == typeof(IReadOnlyList<>) ||
                genericDef == typeof(IReadOnlyCollection<>) ||
                genericDef == typeof(IEnumerable<>) ||
                genericDef == typeof(IList<>) ||
                genericDef == typeof(ICollection<>))
            {
                var itemType = targetType.GetGenericArguments()[0];
                if (rawValue is System.Collections.IEnumerable enumerable)
                {
                    var listType = typeof(List<>).MakeGenericType(itemType);
                    var listInstance = (System.Collections.IList)Activator.CreateInstance(listType)!;
                    foreach (var item in enumerable)
                    {
                        listInstance.Add(ConvertPropertyValue(item, itemType));
                    }
                    return listInstance;
                }
            }
        }

        // Handle single-dimensional array conversion (e.g. T[])
        // Guard: string implements IEnumerable but must not be treated as a character sequence here.
        if (targetType.IsArray && targetType.GetArrayRank() == 1 && rawValue is not string)
        {
            var itemType = targetType.GetElementType()!;
            if (rawValue is System.Collections.IEnumerable enumerable)
            {
                var items = new System.Collections.ArrayList();
                foreach (var item in enumerable)
                {
                    items.Add(ConvertPropertyValue(item, itemType));
                }
                var array = Array.CreateInstance(itemType, items.Count);
                items.CopyTo(array, 0);
                return array;
            }
        }

        var underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (underlyingType.IsEnum)
        {
            return rawValue is string strEnum ? Enum.Parse(underlyingType, strEnum) : Enum.ToObject(underlyingType, rawValue);
        }

        try
        {
            return Convert.ChangeType(rawValue, underlyingType, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
        }
    }
}
