using System.Reflection;
using System.Text.Json;
using StockAnalyzer.Core.Models.Portfolio;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// Writes <see cref="TickerMetadata"/> as JSON from the record's public properties (camelCase names, fields without a
/// stored value omitted). <see cref="ExcludedFields"/> is the single place that hides a field from the client: the owner
/// decided to expose everything (including user notes, reminder, strategy prices and tag) and may hide fields later.
/// </summary>
internal static class TickerMetadataJson
{
    /// <summary>Property names that are not sent to the client. Empty by owner decision (2026-10-22).</summary>
    public static IReadOnlySet<string> ExcludedFields { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The identity fields that make up the short summary shown in ticker lists.</summary>
    public static IReadOnlyList<string> SummaryFields { get; } = new[]
    {
        nameof(TickerMetadata.ShortName), nameof(TickerMetadata.LongName), nameof(TickerMetadata.Region),
        nameof(TickerMetadata.Sector), nameof(TickerMetadata.Industry), nameof(TickerMetadata.Currency),
    };

    private static readonly PropertyInfo[] Properties = typeof(TickerMetadata)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.GetIndexParameters().Length == 0)
        .OrderBy(p => p.MetadataToken)
        .ToArray();

    /// <summary>Every property that can be written (all public instance properties).</summary>
    public static IReadOnlyList<string> AllFields { get; } = Properties.Select(p => p.Name).ToArray();

    public static string JsonName(string propertyName) => char.ToLowerInvariant(propertyName[0]) + propertyName[1..];

    public static void WriteAll(Utf8JsonWriter writer, TickerMetadata metadata)
    {
        writer.WriteStartObject();
        foreach (PropertyInfo property in Properties)
        {
            if (!ExcludedFields.Contains(property.Name))
            {
                WriteProperty(writer, property, metadata);
            }
        }

        writer.WriteEndObject();
    }

    /// <summary>Writes the <see cref="SummaryFields"/> into the object that is currently open.</summary>
    public static void WriteSummaryFields(Utf8JsonWriter writer, TickerMetadata metadata)
    {
        foreach (PropertyInfo property in Properties.Where(p => SummaryFields.Contains(p.Name) && !ExcludedFields.Contains(p.Name)))
        {
            WriteProperty(writer, property, metadata);
        }
    }

    private static void WriteProperty(Utf8JsonWriter writer, PropertyInfo property, TickerMetadata metadata)
    {
        object? value = property.GetValue(metadata);
        if (value is null)
        {
            return;
        }

        string name = JsonName(property.Name);
        switch (value)
        {
            case string text:
                writer.WriteString(name, text);
                break;
            case decimal number:
                writer.WriteNumber(name, number);
                break;
            case long number:
                writer.WriteNumber(name, number);
                break;
            case int number:
                writer.WriteNumber(name, number);
                break;
            case bool flag:
                writer.WriteBoolean(name, flag);
                break;
            case double number when double.IsFinite(number):
                writer.WriteNumber(name, number);
                break;
            case double:
                break; // NaN and Infinity have no JSON form; the field is left out.
            case DateTime moment:
                writer.WriteString(name, ToolJson.FormatDateTime(moment));
                break;
            default:
                throw new NotSupportedException($"TickerMetadata.{property.Name} has the unsupported type {value.GetType().Name}.");
        }
    }
}
