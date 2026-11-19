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

    /// <summary>The optional boolean argument that selects the compact metric form (see <see cref="CompactMetrics"/>); default false keeps the verbose form.</summary>
    public const string CompactReportArgument = "compactReport";

    /// <summary>The schema text of <see cref="CompactReportArgument"/>, shared by every tool that offers it.</summary>
    public const string CompactReportDescription =
        "Write each metric as a bare number (null unless Valid), plus unavailableMetrics and metricUnits. Default false.";

    public static void Write(Utf8JsonWriter writer, BacktestReport report, bool compact = false)
    {
        CompactMetrics? compactMetrics = compact ? new CompactMetrics() : null;
        writer.WriteStartObject();
        foreach (PropertyInfo property in Properties)
        {
            object? value = property.GetValue(report);
            if (value is null)
            {
                continue;
            }

            string name = ToCamelCase(property.Name);
            if (compactMetrics is not null && value is MetricValue metric)
            {
                compactMetrics.Write(writer, name, metric);
                continue;
            }

            writer.WritePropertyName(name);
            WriteValue(writer, value, property);
        }

        compactMetrics?.WriteSummary(writer);
        writer.WriteEndObject();
    }

    /// <summary>
    /// The compact metric form: a Valid metric is written as its number, any other as null, and the writer remembers what it left out. <see cref="WriteSummary"/> then
    /// writes <c>unavailableMetrics</c> (the non-Valid metrics, in the order written; omitted when there are none) and <c>metricUnits</c> (the unit of each Valid metric, in the order written).
    /// Nothing is calculated here: the number is the metric's own value, unrounded.
    /// </summary>
    internal sealed class CompactMetrics
    {
        private readonly List<(string Name, MetricValue Metric)> _unavailable = new();
        private readonly List<(string Name, MetricUnit Unit)> _units = new();

        /// <summary>
        /// Writes <paramref name="name"/> with the bare number, or null. A missing metric is treated as NotApplicable, the way the verbose form writes it. A metric that says
        /// Valid but holds no value (only a default(MetricValue) can: the constructor enforces the pair) is no valid figure either, so it is listed as NotApplicable as well,
        /// never as "Valid" in the list of unavailable metrics.
        /// </summary>
        public void Write(Utf8JsonWriter writer, string name, MetricValue? metric)
        {
            if (metric is { Status: MetricStatus.Valid, Value: { } number } valid)
            {
                writer.WriteNumber(name, number);
                _units.Add((name, valid.Unit));
                return;
            }

            writer.WriteNull(name);
            MetricValue listed = metric is { Status: not MetricStatus.Valid } unavailable
                ? unavailable
                : MetricValue.NonValid(MetricStatus.NotApplicable, metric?.Unit ?? MetricUnit.Dimensionless, MetricReason.None);
            _unavailable.Add((name, listed));
        }

        public void WriteSummary(Utf8JsonWriter writer)
        {
            if (_unavailable.Count > 0)
            {
                writer.WriteStartArray("unavailableMetrics");
                foreach ((string name, MetricValue metric) in _unavailable)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", name);
                    writer.WriteString("status", metric.Status.ToString());
                    writer.WriteString("reason", metric.Reason.ToString());
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteStartObject("metricUnits");
            foreach ((string name, MetricUnit unit) in _units)
            {
                writer.WriteString(name, unit.ToString());
            }

            writer.WriteEndObject();
        }
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

    public static void WriteRunFingerprint(Utf8JsonWriter writer, BacktestReportRunFingerprint? fingerprint)
    {
        writer.WriteStartObject("runFingerprint");
        if (fingerprint is { } fp)
        {
            writer.WriteBoolean("isAvailable", fp.IsAvailable);
            if (fp.Sha256 is { } sha)
            {
                writer.WriteString("sha256", sha);
            }

            writer.WriteNumber("schemaVersion", fp.SchemaVersion);
            writer.WriteNumber("executionSemanticsVersion", fp.ExecutionSemanticsVersion);
            if (fp.UnavailableReason is { } reason)
            {
                writer.WriteString("unavailableReason", reason);
            }
        }
        else
        {
            writer.WriteBoolean("isAvailable", false);
            writer.WriteNumber("schemaVersion", 0);
            writer.WriteNumber("executionSemanticsVersion", 0);
        }

        writer.WriteEndObject();
    }

    public static void WriteMetric(Utf8JsonWriter writer, string propertyName, MetricValue? metric)
    {
        writer.WriteStartObject(propertyName);
        if (metric is { } m)
        {
            writer.WriteString("status", m.Status.ToString());
            if (m.Value is { } number)
            {
                writer.WriteNumber("value", number);
            }
            else
            {
                writer.WriteNull("value");
            }

            writer.WriteString("unit", m.Unit.ToString());
            writer.WriteString("reason", m.Reason.ToString());
        }
        else
        {
            writer.WriteString("status", MetricStatus.NotApplicable.ToString());
            writer.WriteNull("value");
            writer.WriteString("unit", MetricUnit.Dimensionless.ToString());
            writer.WriteString("reason", MetricReason.None.ToString());
        }

        writer.WriteEndObject();
    }
}
