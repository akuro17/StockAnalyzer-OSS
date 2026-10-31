using System.Globalization;
using System.Text;
using System.Text.Json;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>JSON output conventions of the tools: compact text, invariant culture, decimals written as numbers (never through double).</summary>
internal static class ToolJson
{
    /// <summary>The date format of every date a tool accepts or returns.</summary>
    public const string DateFormat = "yyyy-MM-dd";

    /// <summary>The date-time format of stored times: as stored, no offset and no Z (stored bar times are not UTC-qualified).</summary>
    public const string DateTimeFormat = "yyyy-MM-ddTHH:mm:ss";

    public static string Build(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Writes <paramref name="value"/> as a number, or a JSON null when it has no value (a decimal is written as a number, never through double).</summary>
    public static void WriteNullableNumber(Utf8JsonWriter writer, string name, decimal? value)
    {
        if (value is { } present)
        {
            writer.WriteNumber(name, present);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    /// <summary>Writes <paramref name="value"/> as a whole number, or a JSON null when it has no value.</summary>
    public static void WriteNullableNumber(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is { } present)
        {
            writer.WriteNumber(name, present);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    public static string FormatDate(DateTime value) => value.ToString(DateFormat, CultureInfo.InvariantCulture);

    public static string FormatDateTime(DateTime value) => value.ToString(DateTimeFormat, CultureInfo.InvariantCulture);
}
