using System;
using System.Linq;
using System.Reflection;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// The Results-tab metrics table only shows what <see cref="BacktestReport"/> carries, and the MCP server writes every public property of the report
/// (see <c>BacktestReportJson</c>). So every row of the window's table is in the MCP result too: "strategy metrics" are the report's metrics and
/// nothing needs adding. This guard keeps that true when a row is added to the table.
/// </summary>
public class BacktestMetricCatalogReportCoverageTests
{
    // The one row whose value is built from a count and not read from a metric property of the report.
    private const string ClosedTradesRow = "ClosedTrades";

    [Fact]
    public void EveryRowOfTheMetricsTableSelectsAValueThatTheReportCarries()
    {
        BacktestReport report = ReportWithDistinctMetricValues();
        PropertyInfo[] metricProperties = MetricProperties();
        Assert.NotEmpty(metricProperties);
        var problems = new System.Collections.Generic.List<string>();

        foreach (BacktestMetricDefinition row in BacktestMetricCatalog.Definitions)
        {
            MetricValue? selected = row.Select(report);
            if (selected is null)
            {
                problems.Add($"{row.Name}: Select returned null for a report that carries every metric");
                continue;
            }

            if (row.Name == ClosedTradesRow)
            {
                if (selected != MetricValue.Valid(report.TotalTrades, MetricUnit.Count))
                {
                    problems.Add($"{row.Name}: not the report's trade count");
                }

                continue;
            }

            PropertyInfo? property = metricProperties.SingleOrDefault(p => p.Name == row.Name);
            if (property is null)
            {
                problems.Add($"{row.Name}: the report has no metric property of this name");
            }
            else if (!Equals(selected, property.GetValue(report)))
            {
                problems.Add($"{row.Name}: Select does not return the report's {property.Name}");
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void TheRowNamesAreUniqueAndTheTableIsNotEmpty()
    {
        var names = BacktestMetricCatalog.Definitions.Select(row => row.Name).ToArray();

        Assert.NotEmpty(names);
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }

    private static PropertyInfo[] MetricProperties()
        => typeof(BacktestReport).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(MetricValue) || p.PropertyType == typeof(MetricValue?)).ToArray();

    // Every metric property gets its own value, so a row that reads the wrong property is caught.
    private static BacktestReport ReportWithDistinctMetricValues()
    {
        var report = new BacktestReport { TotalTrades = 7 };
        decimal next = 1m;
        foreach (PropertyInfo property in MetricProperties())
        {
            property.SetValue(report, MetricValue.Valid(next++, MetricUnit.Count));
        }

        return report;
    }
}
