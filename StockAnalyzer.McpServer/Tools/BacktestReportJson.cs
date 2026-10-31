using System.Reflection;
using System.Text.Json;
using StockAnalyzer.Core.Services.Backtest.Reporting;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// Writes a <see cref="BacktestReport"/> as JSON by reflection over its public properties, so no field list is kept here and a metric the
/// application adds appears without a change. A property of a type this writer does not know fails loudly (see <see cref="CanWrite"/>, pinned
/// by a test) instead of being dropped. Metrics keep the report's own shape: status, value (null unless Valid), unit, reason.
/// </summary>
internal static class BacktestReportJson
{
    /// <summary>The run fingerprint is written once, at the top level of the tool result, not inside the report.</summary>
    public static IReadOnlySet<string> ExcludedProperties { get; } = new HashSet<string>(StringComparer.Ordinal) { nameof(BacktestReport.RunFingerprint) };

    private static readonly PropertyInfo[] Properties = typeof(BacktestReport)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(property => property.CanRead && !ExcludedProperties.Contains(property.Name))
        .ToArray();

    /// <summary>The names of the properties the writer emits when they have a value.</summary>
    public static IReadOnlyList<string> WrittenProperties { get; } = Properties.Select(property => property.Name).ToArray();

    /// <summary>True when <paramref name="property"/> is excluded on purpose or has a type the writer knows.</summary>
    public static bool CanWrite(PropertyInfo property)
        => ExcludedProperties.Contains(property.Name) || IsKnownType(Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType);

    public static void Write(Utf8JsonWriter writer, BacktestReport report)
    {
        writer.WriteStartObject();
        foreach (PropertyInfo property in Properties)
        {
            object? value = property.GetValue(report);
            if (value is null)
            {
                continue;
            }

            writer.WritePropertyName(ToCamelCase(property.Name));
            WriteValue(writer, value, property);
        }

        writer.WriteEndObject();
    }

    private static bool IsKnownType(Type type)
        => type == typeof(MetricValue) || type == typeof(ConfidenceInterval) || type == typeof(int) || type == typeof(bool)
           || type == typeof(decimal) || type == typeof(string) || type.IsEnum;

    private static void WriteValue(Utf8JsonWriter writer, object value, PropertyInfo property)
    {
        switch (value)
        {
            case MetricValue metric:
                writer.WriteStartObject();
                writer.WriteString("status", metric.Status.ToString());
                if (metric.Value is { } number)
                {
                    writer.WriteNumber("value", number);
                }
                else
                {
                    writer.WriteNull("value");
                }

                writer.WriteString("unit", metric.Unit.ToString());
                writer.WriteString("reason", metric.Reason.ToString());
                writer.WriteEndObject();
                break;
            case ConfidenceInterval interval:
                writer.WriteStartObject();
                writer.WriteNumber("lower", interval.Lower);
                writer.WriteNumber("upper", interval.Upper);
                writer.WriteEndObject();
                break;
            case int integer:
                writer.WriteNumberValue(integer);
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case decimal amount:
                writer.WriteNumberValue(amount);
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case Enum named:
                writer.WriteStringValue(named.ToString());
                break;
            default:
                throw new NotSupportedException($"BacktestReport.{property.Name} has a type the report writer does not know ({value.GetType().Name}).");
        }
    }

    /// <summary>The JSON key the report writer uses for the property named <paramref name="propertyName"/> ("CAGR" is "cagr", "AnnualizedSharpe" is "annualizedSharpe").</summary>
    public static string JsonName(string propertyName) => ToCamelCase(propertyName);

    private static string ToCamelCase(string name)
    {
        // Acronym-led names (CAGR, SQN) lower-case the whole leading run, so the JSON reads "cagr", "sqn", "annualizedSharpe".
        int upper = 0;
        while (upper < name.Length && char.IsUpper(name[upper]))
        {
            upper++;
        }

        if (upper <= 1 || upper == name.Length)
        {
            return upper == name.Length ? name.ToLowerInvariant() : char.ToLowerInvariant(name[0]) + name[1..];
        }

        return name[..(upper - 1)].ToLowerInvariant() + name[(upper - 1)..];
    }
}
