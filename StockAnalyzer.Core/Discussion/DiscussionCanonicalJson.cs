using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace StockAnalyzer.Core.Discussion;

/// <summary>
/// The canonical form of the arguments of an analysis snapshot (spec 5.10.1): the one definition, used for the reuse key and for the stored arguments. Two argument objects that
/// differ only in key order or white space have the same canonical text; two that differ in the spelling of a number do not (a missed reuse is harmless, a wrong reuse is not).
/// </summary>
public static class DiscussionCanonicalJson
{
    /// <summary>Nesting depth the arguments may have (the root object is level 1).</summary>
    public const int MaxDepth = 32;

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Writes <paramref name="arguments"/> without insignificant white space, with the properties of every object in ascending <see cref="string.CompareOrdinal(string, string)"/> order of
    /// their unescaped names, array order kept, strings through one fixed encoder, and numbers as the text they had (never parsed, never reformatted).
    /// </summary>
    /// <exception cref="DiscussionException">Not an object, nested deeper than <see cref="MaxDepth"/>, a repeated property name (the meaning would be ambiguous), or larger than <see cref="DiscussionLimits.AnalysisArgumentsMaxBytes"/> bytes.</exception>
    public static string Canonicalize(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new DiscussionException(DiscussionErrorCodes.InvalidArgument, "arguments must be a JSON object.", "arguments");
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            Write(writer, arguments, 1);
        }

        if (stream.Length > DiscussionLimits.AnalysisArgumentsMaxBytes)
        {
            throw new DiscussionException(
                DiscussionErrorCodes.LimitExceeded,
                $"The arguments are larger than {DiscussionLimits.AnalysisArgumentsMaxBytes} bytes.",
                "arguments");
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    private static void Write(Utf8JsonWriter writer, JsonElement value, int depth)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                CheckDepth(depth);
                writer.WriteStartObject();
                List<JsonProperty> properties = value.EnumerateObject().ToList();
                properties.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
                for (int i = 1; i < properties.Count; i++)
                {
                    if (string.Equals(properties[i - 1].Name, properties[i].Name, StringComparison.Ordinal))
                    {
                        throw new DiscussionException(DiscussionErrorCodes.InvalidArgument, "arguments contain the same key twice.", "arguments", "duplicate key");
                    }
                }

                foreach (JsonProperty property in properties)
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value, depth + 1);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                CheckDepth(depth);
                writer.WriteStartArray();
                foreach (JsonElement item in value.EnumerateArray())
                {
                    Write(writer, item, depth + 1);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }

    private static void CheckDepth(int depth)
    {
        if (depth > MaxDepth)
        {
            throw new DiscussionException(DiscussionErrorCodes.InvalidArgument, $"arguments are nested deeper than {MaxDepth} levels.", "arguments");
        }
    }
}
