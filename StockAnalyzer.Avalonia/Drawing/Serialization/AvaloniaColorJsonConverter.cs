using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Media;

namespace StockAnalyzer.Avalonia.Drawing.Serialization;

/// <summary>
/// Avalonia.Media.Color is an immutable struct with get-only A/R/G/B properties, so the default
/// System.Text.Json struct handling cannot round-trip it. Serialize it as its "#AARRGGBB" string
/// form instead, using Color's own ToString()/Parse() implementations.
/// </summary>
internal sealed class AvaloniaColorJsonConverter : JsonConverter<Color>
{
    public override Color Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => Color.Parse(reader.GetString() ?? "#FF000000");

    public override void Write(Utf8JsonWriter writer, Color value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}
