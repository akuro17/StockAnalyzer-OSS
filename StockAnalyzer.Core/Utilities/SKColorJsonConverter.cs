using SkiaSharp;
using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StockAnalyzer.Core.Utilities;

/// <summary>
/// JSON converter for SKColor to serialize/deserialize as hex strings (e.g., "#RRGGBBAA").
/// </summary>
public class SKColorJsonConverter : JsonConverter<SKColor>
{
    public override SKColor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? hex = reader.GetString();
        if (string.IsNullOrEmpty(hex)) return SKColors.Transparent;

        if (SKColor.TryParse(hex, out var color))
        {
            return color;
        }

        return SKColors.Transparent;
    }

    public override void Write(Utf8JsonWriter writer, SKColor value, JsonSerializerOptions options)
    {
        writer.WriteStringValue($"#{value.Alpha:X2}{value.Red:X2}{value.Green:X2}{value.Blue:X2}");
    }
}
