using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StockAnalyzer.Avalonia.Drawing.Serialization;

/// <summary>
/// Serializes any IChartObject implementer generically: a "$type" discriminator (the CLR type
/// name, resolved via ChartObjectTypeRegistry) plus every public readable/writable property,
/// with "Points" and "Id" handled specially. This lets ~40 heterogeneous drawing-tool classes be
/// persisted without modifying any of them (see ChartObjectInstanceFactory for why a naive
/// polymorphic-constructor-matching approach does not work here).
/// </summary>
internal sealed class ChartObjectJsonConverter : JsonConverter<IChartObject>
{
    private static readonly HashSet<string> SkippedPropertyNames = new() { "Points", "Id" };

    public override IChartObject? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (!root.TryGetProperty("$type", out var typeEl))
            throw new JsonException("Chart object JSON is missing the \"$type\" discriminator.");

        var typeName = typeEl.GetString() ?? throw new JsonException("Chart object \"$type\" was null.");
        var concreteType = ChartObjectTypeRegistry.Resolve(typeName)
            ?? throw new JsonException($"Unknown chart object type: {typeName}");

        var instance = ChartObjectInstanceFactory.Create(concreteType);

        // Restore points explicitly: guaranteed correct regardless of which constructor overload
        // consumed which (placeholder) point arguments.
        if (root.TryGetProperty("points", out var pointsEl))
        {
            var points = JsonSerializer.Deserialize<List<ChartPoint>>(pointsEl.GetRawText(), options) ?? new List<ChartPoint>();
            instance.Points.Clear();
            instance.Points.AddRange(points);
        }

        foreach (var prop in concreteType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (SkippedPropertyNames.Contains(prop.Name)) continue;
            if (prop.GetIndexParameters().Length > 0) continue;

            var setter = prop.GetSetMethod(nonPublic: true);
            if (setter == null) continue; // computed / read-only property (e.g. SkiaColor, IsLong)

            var jsonName = JsonNamingPolicy.CamelCase.ConvertName(prop.Name);
            if (!root.TryGetProperty(jsonName, out var valueEl)) continue;
            if (valueEl.ValueKind == JsonValueKind.Null) continue;

            try
            {
                var value = JsonSerializer.Deserialize(valueEl.GetRawText(), prop.PropertyType, options);
                setter.Invoke(instance, new[] { value });
            }
            catch
            {
                // Best-effort: skip members that fail to convert rather than aborting the whole object.
            }
        }

        return instance;
    }

    public override void Write(Utf8JsonWriter writer, IChartObject value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("$type", value.GetType().Name);

        writer.WritePropertyName("points");
        JsonSerializer.Serialize(writer, value.Points, options);

        foreach (var prop in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (SkippedPropertyNames.Contains(prop.Name)) continue;
            if (prop.GetIndexParameters().Length > 0) continue;
            if (!prop.CanRead) continue;
            // Only persist properties a future Read() could actually restore.
            if (prop.GetSetMethod(nonPublic: true) == null) continue;

            object? propValue;
            try
            {
                propValue = prop.GetValue(value);
            }
            catch
            {
                continue;
            }

            writer.WritePropertyName(JsonNamingPolicy.CamelCase.ConvertName(prop.Name));
            JsonSerializer.Serialize(writer, propValue, prop.PropertyType, options);
        }

        writer.WriteEndObject();
    }
}
