using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.Reporting;

/// <summary>
/// Value-domain and cross-field contracts of the extended metrics enforced by <see cref="BacktestReportValidator"/>
/// (Y:\Temp\sa_implementation_plan_BacktestReportIntegrityContracts.md, T2). Each contract is violated in isolation on an otherwise valid report.
/// </summary>
public class ExtendedMetricValueContractTests
{
    private const int WinCount = 3;
    private const int LossCount = 2;
    private const int BreakevenCount = 1;

    // JSON wire numbers are authored literally so the test does not depend on the enum declarations.
    private const int StatusValid = 0;
    private const int StatusInsufficientData = 1;
    private const int UnitCurrency = 4;
    private const int UnitDimensionless = 3;
    private const int UnitBars = 5;
    private const int UnitCount = 7;
    private const int UnitExposureRatio = 8;
    private const int ReasonEmptyInput = 1;

    private static JsonObject ConsistentReportJson()
    {
        BacktestReport generated = new BacktestReportGenerator().Generate(
            ReportTestHelpers.BuildResult(100m, new[] { 120m, 90m, 108m }),
            ReportTestHelpers.Options());
        JsonObject json = JsonNode.Parse(JsonSerializer.Serialize(generated))!.AsObject();

        json[nameof(BacktestReport.TotalTrades)] = WinCount + LossCount + BreakevenCount;
        json[nameof(BacktestReport.WinTrades)] = WinCount;
        json[nameof(BacktestReport.LossTrades)] = LossCount;
        json[nameof(BacktestReport.BreakevenTrades)] = BreakevenCount;

        SetValid(json, nameof(BacktestReport.GrossProfit), 60m, UnitCurrency);
        SetValid(json, nameof(BacktestReport.GrossLoss), 20m, UnitCurrency);
        SetValid(json, nameof(BacktestReport.AverageWin), 20m, UnitCurrency);
        SetValid(json, nameof(BacktestReport.AverageLoss), 10m, UnitCurrency);
        SetValid(json, nameof(BacktestReport.PayoffRatio), 2m, UnitDimensionless);
        SetValid(json, nameof(BacktestReport.LargestWin), 30m, UnitCurrency);
        SetValid(json, nameof(BacktestReport.LargestLoss), 15m, UnitCurrency);
        SetValid(json, nameof(BacktestReport.AverageHoldingPeriod), 1.5m, UnitBars);
        SetValid(json, nameof(BacktestReport.MaxConsecutiveWins), 3m, UnitCount);
        SetValid(json, nameof(BacktestReport.MaxConsecutiveLosses), 2m, UnitCount);
        SetValid(json, nameof(BacktestReport.MaxDepthDrawdownDuration), 2m, UnitBars);
        SetValid(json, nameof(BacktestReport.LongestDrawdownDuration), 3m, UnitBars);
        SetValid(json, nameof(BacktestReport.TimeInMarket), 4m, UnitBars);
        SetValid(json, nameof(BacktestReport.Exposure), 0.5m, UnitExposureRatio);
        json[nameof(BacktestReport.MaxDepthDrawdownDurationRightCensored)] = false;
        json[nameof(BacktestReport.LongestDrawdownDurationRightCensored)] = false;
        return json;
    }

    private static void SetValid(JsonObject json, string name, decimal value, int unit) => json[name] = new JsonObject
    {
        [nameof(MetricValue.Value)] = value,
        [nameof(MetricValue.Status)] = StatusValid,
        [nameof(MetricValue.Unit)] = unit,
        [nameof(MetricValue.Reason)] = 0,
    };

    private static void SetNonValid(JsonObject json, string name, int unit) => json[name] = new JsonObject
    {
        [nameof(MetricValue.Value)] = null,
        [nameof(MetricValue.Status)] = StatusInsufficientData,
        [nameof(MetricValue.Unit)] = unit,
        [nameof(MetricValue.Reason)] = ReasonEmptyInput,
    };

    private static BacktestReport Load(JsonObject json) => JsonSerializer.Deserialize<BacktestReport>(json.ToJsonString())!;

    public static IEnumerable<object[]> Violations()
    {
        void Case(List<object[]> cases, string expectedName, string label, Action<JsonObject> mutate) =>
            cases.Add(new object[] { expectedName, label, mutate });

        var cases = new List<object[]>();
        Case(cases, "GrossProfit", "GrossProfit negative", j => SetValid(j, "GrossProfit", -1m, UnitCurrency));
        Case(cases, "GrossLoss", "GrossLoss negative", j => SetValid(j, "GrossLoss", -1m, UnitCurrency));
        Case(cases, "AverageWin", "AverageWin zero", j => SetValid(j, "AverageWin", 0m, UnitCurrency));
        Case(cases, "AverageLoss", "AverageLoss zero", j => SetValid(j, "AverageLoss", 0m, UnitCurrency));
        Case(cases, "PayoffRatio", "PayoffRatio zero", j => SetValid(j, "PayoffRatio", 0m, UnitDimensionless));
        Case(cases, "LargestWin", "LargestWin zero", j => SetValid(j, "LargestWin", 0m, UnitCurrency));
        Case(cases, "LargestLoss", "LargestLoss negative", j => SetValid(j, "LargestLoss", -1m, UnitCurrency));
        Case(cases, "AverageHoldingPeriod", "AverageHoldingPeriod negative", j => SetValid(j, "AverageHoldingPeriod", -1m, UnitBars));
        Case(cases, "MaxConsecutiveWins", "MaxConsecutiveWins above WinTrades", j => SetValid(j, "MaxConsecutiveWins", 4m, UnitCount));
        Case(cases, "MaxConsecutiveWins", "MaxConsecutiveWins fractional", j => SetValid(j, "MaxConsecutiveWins", 1.5m, UnitCount));
        Case(cases, "MaxConsecutiveWins", "MaxConsecutiveWins negative", j => SetValid(j, "MaxConsecutiveWins", -1m, UnitCount));
        Case(cases, "MaxConsecutiveLosses", "MaxConsecutiveLosses above LossTrades", j => SetValid(j, "MaxConsecutiveLosses", 3m, UnitCount));
        Case(cases, "MaxConsecutiveLosses", "MaxConsecutiveLosses fractional", j => SetValid(j, "MaxConsecutiveLosses", 0.5m, UnitCount));
        Case(cases, "MaxDepthDrawdownDuration", "MaxDepthDrawdownDuration negative", j => SetValid(j, "MaxDepthDrawdownDuration", -1m, UnitBars));
        Case(cases, "MaxDepthDrawdownDuration", "MaxDepthDrawdownDuration fractional", j => SetValid(j, "MaxDepthDrawdownDuration", 2.5m, UnitBars));
        Case(cases, "LongestDrawdownDuration", "LongestDrawdownDuration negative", j => SetValid(j, "LongestDrawdownDuration", -1m, UnitBars));
        Case(cases, "LongestDrawdownDuration", "LongestDrawdownDuration fractional", j => SetValid(j, "LongestDrawdownDuration", 3.5m, UnitBars));
        Case(cases, "LongestDrawdownDuration", "LongestDrawdownDuration shorter than MaxDepthDrawdownDuration", j => SetValid(j, "LongestDrawdownDuration", 1m, UnitBars));
        Case(cases, "TimeInMarket", "TimeInMarket negative", j => SetValid(j, "TimeInMarket", -1m, UnitBars));
        Case(cases, "TimeInMarket", "TimeInMarket fractional", j => SetValid(j, "TimeInMarket", 1.5m, UnitBars));
        Case(cases, "Exposure", "Exposure above one", j => SetValid(j, "Exposure", 1.5m, UnitExposureRatio));
        Case(cases, "Exposure", "Exposure negative", j => SetValid(j, "Exposure", -0.1m, UnitExposureRatio));
        Case(cases, "Exposure", "Exposure zero while TimeInMarket positive", j => SetValid(j, "Exposure", 0m, UnitExposureRatio));
        Case(cases, "Exposure", "Exposure positive while TimeInMarket zero", j => SetValid(j, "TimeInMarket", 0m, UnitBars));
        Case(cases, "AverageWin", "AverageWin above LargestWin", j => SetValid(j, "AverageWin", 31m, UnitCurrency));
        Case(cases, "LargestWin", "LargestWin above GrossProfit", j => SetValid(j, "LargestWin", 61m, UnitCurrency));
        Case(cases, "AverageLoss", "AverageLoss above LargestLoss", j => SetValid(j, "AverageLoss", 16m, UnitCurrency));
        Case(cases, "LargestLoss", "LargestLoss above GrossLoss", j => SetValid(j, "LargestLoss", 21m, UnitCurrency));
        return cases;
    }

    [Fact]
    public void ConsistentReport_IsAccepted() => BacktestReportValidator.Validate(Load(ConsistentReportJson()));

    [Theory]
    [MemberData(nameof(Violations))]
    public void ViolatedContract_IsRejected_NamingTheMetric(string expectedName, string label, Action<JsonObject> mutate)
    {
        JsonObject json = ConsistentReportJson();
        mutate(json);

        ArgumentException error = Assert.Throws<ArgumentException>(() => BacktestReportValidator.Validate(Load(json)));
        Assert.True(error.Message.Contains(expectedName, StringComparison.Ordinal), $"{label}: message '{error.Message}' does not name {expectedName}.");
    }

    [Fact]
    public void BoundaryValues_AreAccepted()
    {
        JsonObject json = ConsistentReportJson();
        SetValid(json, "AverageWin", 30m, UnitCurrency);       // == LargestWin
        SetValid(json, "LargestWin", 60m, UnitCurrency);       // == GrossProfit
        SetValid(json, "AverageLoss", 15m, UnitCurrency);      // == LargestLoss
        SetValid(json, "LargestLoss", 20m, UnitCurrency);      // == GrossLoss
        SetValid(json, "MaxConsecutiveWins", 3m, UnitCount);   // == WinTrades
        SetValid(json, "MaxConsecutiveLosses", 0m, UnitCount);
        SetValid(json, "MaxDepthDrawdownDuration", 3m, UnitBars);      // == LongestDrawdownDuration
        SetValid(json, "Exposure", 1m, UnitExposureRatio);
        SetValid(json, "AverageHoldingPeriod", 0m, UnitBars);

        BacktestReportValidator.Validate(Load(json));
    }

    [Fact]
    public void NonValidMetrics_AreNotConstrained()
    {
        JsonObject json = ConsistentReportJson();
        SetNonValid(json, "GrossProfit", UnitCurrency);
        SetNonValid(json, "LargestWin", UnitCurrency);
        SetNonValid(json, "TimeInMarket", UnitBars);
        SetNonValid(json, "MaxConsecutiveLosses", UnitCount);
        SetValid(json, "AverageWin", 500m, UnitCurrency); // would exceed both bounds, but neither bound is Valid

        BacktestReportValidator.Validate(Load(json));
    }

    [Fact]
    public void LegacyReportWithoutExtendedMetrics_IsAccepted()
    {
        BacktestReport generated = new BacktestReportGenerator().Generate(
            ReportTestHelpers.BuildResult(100m, new[] { 120m, 90m, 108m }),
            ReportTestHelpers.Options());
        BacktestReport legacy = ReportTestHelpers.ToLegacyReport(generated);

        Assert.Empty(legacy.EnumerateExtendedMetrics());
        BacktestReportValidator.Validate(legacy);
    }

    [Fact]
    public void GeneratedReportsWithTrades_SatisfyEveryContract()
    {
        decimal[][] tradeNets =
        {
            Array.Empty<decimal>(),
            new[] { 5m },
            new[] { -5m },
            new[] { 0m, 0m },
            new[] { 10m, -4m, 0m, 7m, 7m, -1m },
            new[] { 0.3333333333333333333333333333m, 0.3333333333333333333333333333m, 0.3333333333333333333333333333m },
        };

        foreach (decimal[] nets in tradeNets)
        {
            var trades = Array.ConvertAll(nets, net => ReportTestHelpers.Trade(net));
            BacktestReport report = new BacktestReportGenerator().Generate(
                ReportTestHelpers.BuildResult(100m, new[] { 120m, 90m, 108m }, trades),
                ReportTestHelpers.Options());

            BacktestReportValidator.Validate(report);
        }
    }
}
