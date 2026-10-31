using System.Reflection;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using StockAnalyzer.McpServer.Tools;

namespace StockAnalyzer.McpServer.Catalog;

/// <summary>A report metric an optimisation can rank by: its JSON name (the key sa_run_backtest writes) and how to read it from a report.</summary>
internal sealed record ObjectiveMetric(string Name, string PropertyName, Func<BacktestReport, MetricValue?> Read);

/// <summary>
/// The objectives of sa_optimize_backtest and sa_run_walk_forward_analysis: the metrics of the existing backtest report, read as they are (no formula is
/// defined here). Every <see cref="MetricValue"/> property of <see cref="BacktestReport"/> is an objective, found by reflection so a metric the
/// application adds is offered by itself, except the ones in <see cref="Excluded"/>: metrics the production run path never reports as Valid (measured).
/// A metric that is Valid only for some runs (for example ProfitFactor needs a losing trade) stays in; a run where it is not Valid is left out of
/// the ranking and counted as invalid.
/// </summary>
internal static class OptimizationObjectives
{
    /// <summary>
    /// Metrics measured as never Valid through the production path (NoSamplingEvidenceTrustPolicy), keyed by report property name, with the reason.
    /// A test re-measures them, so the entry has to be removed when the application makes the metric available.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Excluded { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [nameof(BacktestReport.SQN)] = "NotApplicable (RiskDataMissing) in every production run, also with riskManagement set (measured 2026-10-30): the report is built without the risk data SQN needs.",
    };

    /// <summary>Every <see cref="MetricValue"/> property of the report, in declaration order (excluded ones too: they can still be shown, just not ranked by).</summary>
    private static readonly PropertyInfo[] MetricProperties = typeof(BacktestReport)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(property => property.CanRead && (property.PropertyType == typeof(MetricValue) || property.PropertyType == typeof(MetricValue?)))
        .ToArray();

    public static IReadOnlyList<ObjectiveMetric> All { get; } = MetricProperties
        .Where(property => !Excluded.ContainsKey(property.Name))
        .Select(property => new ObjectiveMetric(
            BacktestReportJson.JsonName(property.Name),
            property.Name,
            report => (MetricValue?)property.GetValue(report)))
        .ToArray();

    public static IReadOnlyList<string> Names { get; } = All.Select(metric => metric.Name).ToArray();

    /// <summary>The metric of <paramref name="report"/> named by its <see cref="BacktestReport"/> property, whether or not it is offered as an objective.</summary>
    /// <exception cref="ArgumentException">The report has no such metric property.</exception>
    public static MetricValue? ReadMetric(BacktestReport report, string propertyName)
    {
        PropertyInfo? property = MetricProperties.FirstOrDefault(candidate => candidate.Name == propertyName);
        return property is null
            ? throw new ArgumentException($"BacktestReport has no metric '{propertyName}'.", nameof(propertyName))
            : (MetricValue?)property.GetValue(report);
    }

    /// <summary>The objective named <paramref name="name"/> (the report's JSON key, case-insensitive).</summary>
    /// <exception cref="ToolException">The name is not an objective; the message says why when the metric is excluded on purpose.</exception>
    public static ObjectiveMetric Resolve(string name, string path)
    {
        string trimmed = name.Trim();
        ObjectiveMetric? found = All.FirstOrDefault(metric => string.Equals(metric.Name, trimmed, StringComparison.OrdinalIgnoreCase));
        if (found is not null)
        {
            return found;
        }

        KeyValuePair<string, string> excluded = Excluded.FirstOrDefault(entry =>
            string.Equals(entry.Key, trimmed, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(BacktestReportJson.JsonName(entry.Key), trimmed, StringComparison.OrdinalIgnoreCase));
        string reason = excluded.Key is null ? string.Empty : $" {excluded.Key} is not offered: {excluded.Value}";
        throw new ToolException(
            ToolErrorCodes.InvalidArgument,
            $"'{name}' is not an objective.{reason}",
            path,
            $"Objectives: {string.Join(", ", Names)}.");
    }
}
