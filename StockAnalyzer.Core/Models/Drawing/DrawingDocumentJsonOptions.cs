using System.Text.Json;
using System.Text.Json.Serialization;

namespace StockAnalyzer.Core.Models.Drawing;

/// <summary>
/// The one definition of how a V2 drawing document is written to and read from JSON, apart from the chart's colour type. The chart's codec adds its colour converter;
/// the read-only reader uses these options as they are (a colour is then one more JSON value of <see cref="DrawingObjectRecord.StyleAndParameters"/>).
/// </summary>
public static class DrawingDocumentJsonOptions
{
    /// <summary>A new set of options (callers may add converters to their own copy).</summary>
    public static JsonSerializerOptions Create() => new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters =
        {
            new JsonStringEnumConverter(),
        },
    };
}
