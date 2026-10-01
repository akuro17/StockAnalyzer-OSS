using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Nodes;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;
using static StockAnalyzer.Tests.Backtest.BacktestReportTestFactory;

namespace StockAnalyzer.Tests.Backtest;

/// <summary>Color rules of the Results-tab metrics (Y:\Temp\sa_implementation_plan_BacktestMetricSemanticColors.md, option A): colored by meaning, not by the sign of every value.</summary>
public class BacktestMetricSemanticTests
{
    private enum Rule { Sign, Gain, Loss, Neutral }

    private enum Color { Plus, Minus, Neutral }

    /// <summary>The approved classification (plan section 2), written out independently of the catalog.</summary>
    private static readonly IReadOnlyDictionary<string, Rule> ExpectedRules = new Dictionary<string, Rule>
    {
        ["TotalPnL"] = Rule.Sign, ["TotalReturn"] = Rule.Sign, ["CAGR"] = Rule.Sign, ["ExpectedPayoff"] = Rule.Sign,
        ["ExposureAdjustedCAGR"] = Rule.Sign, ["BarSharpe"] = Rule.Sign, ["AnnualizedSharpe"] = Rule.Sign,
        ["AnnualizedSharpeAutocorrelationAdjusted"] = Rule.Sign, ["BarSortino"] = Rule.Sign, ["AnnualizedSortino"] = Rule.Sign,
        ["AnnualizedSortinoAutocorrelationAdjusted"] = Rule.Sign, ["CalmarFullPeriod"] = Rule.Sign, ["RecoveryFactor"] = Rule.Sign,
        ["GrossProfit"] = Rule.Gain, ["AverageWin"] = Rule.Gain, ["LargestWin"] = Rule.Gain,
        ["GrossLoss"] = Rule.Loss, ["AverageLoss"] = Rule.Loss, ["LargestLoss"] = Rule.Loss,
        ["MaxDrawdown"] = Rule.Loss, ["MaxDrawdownAmount"] = Rule.Loss,
        ["UlcerIndex"] = Rule.Neutral, ["WinRate"] = Rule.Neutral, ["ProfitFactor"] = Rule.Neutral, ["PayoffRatio"] = Rule.Neutral,
        ["SQN"] = Rule.Neutral, ["ClosedTrades"] = Rule.Neutral, ["AverageHoldingPeriod"] = Rule.Neutral,
        ["MaxConsecutiveWins"] = Rule.Neutral, ["MaxConsecutiveLosses"] = Rule.Neutral, ["MaxDepthDrawdownDuration"] = Rule.Neutral,
        ["LongestDrawdownDuration"] = Rule.Neutral, ["TimeInMarket"] = Rule.Neutral, ["Exposure"] = Rule.Neutral,
    };

    private static string NameOf(BacktestMetricDisplayRow row) => row.LabelKey["Backtest_Metric_".Length..];

    private static ImmutableArray<BacktestMetricDisplayRow> Rows(BacktestReport report) =>
        BacktestMetricGroupBuilder.Build(report, NullLocalizationService.Instance).SelectMany(g => g.Rows).ToImmutableArray();

    private static Color Expect(Rule rule, decimal value) => rule switch
    {
        Rule.Sign => value > 0m ? Color.Plus : value < 0m ? Color.Minus : Color.Neutral,
        Rule.Gain => value > 0m ? Color.Plus : Color.Neutral,
        Rule.Loss => value > 0m ? Color.Minus : Color.Neutral,
        _ => Color.Neutral,
    };

    private static Color Actual(BacktestMetricSemantic semantic) => semantic switch
    {
        BacktestMetricSemantic.Plus => Color.Plus,
        BacktestMetricSemantic.Minus => Color.Minus,
        _ => Color.Neutral,
    };

    /// <summary>The numeric value a row shows, read from the report itself (not from the row); null when the metric is not Valid.</summary>
    private static decimal? ValueOf(BacktestReport report, string name)
    {
        if (name == "ClosedTrades") return report.TotalTrades;
        object? property = typeof(BacktestReport).GetProperty(name)!.GetValue(report);
        MetricValue? metric = property as MetricValue?;
        return metric is { Status: MetricStatus.Valid } valid ? valid.Value : null;
    }

    // ---- C1 -------------------------------------------------------------------------------------

    [Fact]
    public void EveryRowIsColoredByTheApprovedRule()
    {
        BacktestReport report = GenerateReport(10m, -4m, 6m, -2m);
        ImmutableArray<BacktestMetricDisplayRow> rows = Rows(report);

        Assert.Equal(ExpectedRules.Keys.OrderBy(n => n, StringComparer.Ordinal), rows.Select(NameOf).OrderBy(n => n, StringComparer.Ordinal));
        foreach (BacktestMetricDisplayRow row in rows)
        {
            string name = NameOf(row);
            decimal? value = ValueOf(report, name);
            Color expected = value is { } v ? Expect(ExpectedRules[name], v) : Color.Neutral;
            Assert.True(expected == Actual(row.Semantic), $"{name}: expected {expected}, was {row.Semantic} (value {value})");
        }
    }

    [Fact]
    public void Drawdown_WinRate_ProfitFactor_AndUlcerIndexNoLongerShowTheGainColor()
    {
        BacktestReport report = GenerateReport(10m, -4m, 6m, -2m);
        ImmutableArray<BacktestMetricDisplayRow> rows = Rows(report);
        BacktestMetricDisplayRow Row(string name) => rows.Single(r => NameOf(r) == name);

        Assert.True(report.MaxDrawdown.Value > 0m);
        Assert.Equal(BacktestMetricSemantic.Minus, Row("MaxDrawdown").Semantic);
        Assert.Equal(BacktestMetricSemantic.Minus, Row("MaxDrawdownAmount").Semantic);
        Assert.Equal(BacktestMetricSemantic.Neutral, Row("UlcerIndex").Semantic);
        Assert.Equal(BacktestMetricSemantic.Neutral, Row("WinRate").Semantic);
        Assert.Equal(BacktestMetricSemantic.Neutral, Row("ProfitFactor").Semantic);
        Assert.Equal(BacktestMetricSemantic.Plus, Row("TotalPnL").Semantic);
    }

    // ---- C2 -------------------------------------------------------------------------------------

    [Fact]
    public void NoDrawdown_IsNeutralNotMinus()
    {
        BacktestReport report = GenerateReport(10m, 6m);
        Assert.Equal(0m, report.MaxDrawdown.Value);
        Assert.Equal(0m, report.MaxDrawdownAmount.Value);

        ImmutableArray<BacktestMetricDisplayRow> rows = Rows(report);

        Assert.Equal(BacktestMetricSemantic.Neutral, rows.Single(r => NameOf(r) == "MaxDrawdown").Semantic);
        Assert.Equal(BacktestMetricSemantic.Neutral, rows.Single(r => NameOf(r) == "MaxDrawdownAmount").Semantic);
    }

    // ---- C3 -------------------------------------------------------------------------------------

    [Fact]
    public void NonValidRows_AreNeutralWhateverTheRule()
    {
        BacktestReport report = GenerateReport();

        ImmutableArray<BacktestMetricDisplayRow> rows = Rows(report);

        Assert.Contains(rows, r => r.Status != MetricStatus.Valid);
        Assert.All(rows.Where(r => r.Status != MetricStatus.Valid), r => Assert.Equal(BacktestMetricSemantic.Neutral, r.Semantic));
    }

    // ---- C4 -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("AnnualizedSharpe", "1.25", false, BacktestMetricSemantic.Plus)]
    [InlineData("AnnualizedSharpe", "-0.5", false, BacktestMetricSemantic.Minus)]
    [InlineData("ExposureAdjustedCAGR", "0.05", true, BacktestMetricSemantic.Plus)]
    [InlineData("ExposureAdjustedCAGR", "-0.05", true, BacktestMetricSemantic.Minus)]
    [InlineData("ExposureAdjustedCAGR", "0", true, BacktestMetricSemantic.Neutral)]
    public void SignColoredMetrics_FollowTheSignOfTheirValue(string name, string valueText, bool isReturnRatio, BacktestMetricSemantic expected)
    {
        // The value is parsed as decimal from text: decimal is not a legal attribute argument and a double would be a lossy detour.
        decimal value = decimal.Parse(valueText, System.Globalization.CultureInfo.InvariantCulture);
        MetricUnit unit = isReturnRatio ? MetricUnit.ReturnRatio : MetricUnit.Dimensionless;
        BacktestReport report = WithMembers(GenerateReport(10m, -4m), new Dictionary<string, JsonNode?>
        {
            [name] = Metric(value, unit),
        });

        Assert.Equal(expected, Rows(report).Single(r => NameOf(r) == name).Semantic);
    }
}
