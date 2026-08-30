using StockAnalyzer.Core.Models;
using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StockAnalyzer.Core.Utilities;

/// <summary>
/// JSON converter for IndicatorColor to serialize/deserialize as hex strings (e.g., "#AARRGGBB").
/// </summary>
public class IndicatorColorJsonConverter : JsonConverter<IndicatorColor>
{
    public override IndicatorColor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? hex = reader.GetString();
        if (string.IsNullOrEmpty(hex)) return new IndicatorColor(255, 0, 0, 0);

        // Remove # if present
        if (hex.StartsWith('#')) hex = hex.Substring(1);

        try
        {
            if (hex.Length == 8)
            {
                byte a = Convert.ToByte(hex.Substring(0, 2), 16);
                byte r = Convert.ToByte(hex.Substring(2, 2), 16);
                byte g = Convert.ToByte(hex.Substring(4, 2), 16);
                byte b = Convert.ToByte(hex.Substring(6, 2), 16);
                return new IndicatorColor(a, r, g, b);
            }
            else if (hex.Length == 6)
            {
                byte r = Convert.ToByte(hex.Substring(0, 2), 16);
                byte g = Convert.ToByte(hex.Substring(2, 2), 16);
                byte b = Convert.ToByte(hex.Substring(4, 2), 16);
                return new IndicatorColor(255, r, g, b);
            }
        }
        catch
        {
            // Fallback
        }

        return new IndicatorColor(255, 0, 0, 0);
    }

    public override void Write(Utf8JsonWriter writer, IndicatorColor value, JsonSerializerOptions options)
    {
        writer.WriteStringValue($"#{value.A:X2}{value.R:X2}{value.G:X2}{value.B:X2}");
    }
}
