using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.Reporting;

/// <summary>Extended trade metrics (Y:\Temp\sa_implementation_plan_BacktestReportExtendedMetrics.md): calculators, report wiring, validation and persistence.</summary>
public class ExtendedTradeMetricsTests
{
    private static ImmutableArray<BacktestTrade> Trades(params decimal[] closedNet) =>
        closedNet.Select(net => ReportTestHelpers.Trade(net)).ToImmutableArray();

    // ---- Task 1: trade PnL metrics --------------------------------------------------------------

    [Fact]
    public void PnlMetrics_MixedTrades_MatchHandComputedValues()
    {
        // wins 10, 20 ; losses 5, 15 ; breakeven 0
        var trades = Trades(10m, -5m, 20m, 0m, -15m);

        Assert.Equal(30m, BasicMetricsCalculator.ComputeGrossProfit(trades).Value);
        Assert.Equal(20m, BasicMetricsCalculator.ComputeGrossLoss(trades).Value);
        Assert.Equal(15m, BasicMetricsCalculator.ComputeAverageWin(trades).Value);
        Assert.Equal(10m, BasicMetricsCalculator.ComputeAverageLoss(trades).Value);
        Assert.Equal(1.5m, BasicMetricsCalculator.ComputePayoffRatio(trades).Value);
        Assert.Equal(20m, BasicMetricsCalculator.ComputeLargestWin(trades).Value);
        Assert.Equal(15m, BasicMetricsCalculator.ComputeLargestLoss(trades).Value);
    }

    [Fact]
    public void PnlMetrics_UnitsAreCurrencyExceptPayoffRatio()
    {
        var trades = Trades(10m, -5m);

        Assert.Equal(MetricUnit.Currency, BasicMetricsCalculator.ComputeGrossProfit(trades).Unit);
        Assert.Equal(MetricUnit.Currency, BasicMetricsCalculator.ComputeGrossLoss(trades).Unit);
        Assert.Equal(MetricUnit.Currency, BasicMetricsCalculator.ComputeAverageWin(trades).Unit);
        Assert.Equal(MetricUnit.Currency, BasicMetricsCalculator.ComputeAverageLoss(trades).Unit);
        Assert.Equal(MetricUnit.Dimensionless, BasicMetricsCalculator.ComputePayoffRatio(trades).Unit);
        Assert.Equal(MetricUnit.Currency, BasicMetricsCalculator.ComputeLargestWin(trades).Unit);
        Assert.Equal(MetricUnit.Currency, BasicMetricsCalculator.ComputeLargestLoss(trades).Unit);
    }

    [Fact]
    public void PnlMetrics_GrossRatioEqualsProfitFactor_AndAverageTimesCountEqualsGross()
    {
        var trades = Trades(7m, -3m, 11m, -2m, 5m);

        decimal grossProfit = BasicMetricsCalculator.ComputeGrossProfit(trades).Value!.Value;
        decimal grossLoss = BasicMetricsCalculator.ComputeGrossLoss(trades).Value!.Value;

        Assert.Equal(BasicMetricsCalculator.ComputeProfitFactor(trades).Value, grossProfit / grossLoss);
        Assert.Equal(grossProfit, BasicMetricsCalculator.ComputeAverageWin(trades).Value!.Value * 3m);
        Assert.Equal(grossLoss, BasicMetricsCalculator.ComputeAverageLoss(trades).Value!.Value * 2m);
    }

    [Fact]
    public void PnlMetrics_NoClosedTrades_AllInsufficientData()
    {
        var trades = ImmutableArray<BacktestTrade>.Empty;

        foreach (MetricValue metric in new[]
        {
            BasicMetricsCalculator.ComputeGrossProfit(trades),
            BasicMetricsCalculator.ComputeGrossLoss(trades),
            BasicMetricsCalculator.ComputeAverageWin(trades),
            BasicMetricsCalculator.ComputeAverageLoss(trades),
            BasicMetricsCalculator.ComputePayoffRatio(trades),
            BasicMetricsCalculator.ComputeLargestWin(trades),
            BasicMetricsCalculator.ComputeLargestLoss(trades),
            BasicMetricsCalculator.ComputeAverageHoldingPeriod(trades),
            BasicMetricsCalculator.ComputeMaxConsecutiveWins(trades),
            BasicMetricsCalculator.ComputeMaxConsecutiveLosses(trades),
        })
        {
            Assert.Equal(MetricStatus.InsufficientData, metric.Status);
            Assert.Equal(MetricReason.NoClosedTrades, metric.Reason);
            Assert.Null(metric.Value);
        }
    }

    [Fact]
    public void PnlMetrics_OnlyLosses_WinSideUndefined_SumsAreZero()
    {
        var trades = Trades(-4m, -6m);

        Assert.Equal(0m, BasicMetricsCalculator.ComputeGrossProfit(trades).Value);
        Assert.Equal(10m, BasicMetricsCalculator.ComputeGrossLoss(trades).Value);
        AssertNonValid(BasicMetricsCalculator.ComputeAverageWin(trades), MetricStatus.Undefined, MetricReason.NoWinningTrades);
        AssertNonValid(BasicMetricsCalculator.ComputeLargestWin(trades), MetricStatus.Undefined, MetricReason.NoWinningTrades);
        Assert.Equal(5m, BasicMetricsCalculator.ComputeAverageLoss(trades).Value);
        Assert.Equal(6m, BasicMetricsCalculator.ComputeLargestLoss(trades).Value);
        AssertNonValid(BasicMetricsCalculator.ComputePayoffRatio(trades), MetricStatus.Undefined, MetricReason.NoWinningTrades);
    }

    [Fact]
    public void PnlMetrics_OnlyWins_LossSideUndefined_PayoffRatioPositiveInfinity()
    {
        var trades = Trades(4m, 6m);

        Assert.Equal(10m, BasicMetricsCalculator.ComputeGrossProfit(trades).Value);
        Assert.Equal(0m, BasicMetricsCalculator.ComputeGrossLoss(trades).Value);
        AssertNonValid(BasicMetricsCalculator.ComputeAverageLoss(trades), MetricStatus.Undefined, MetricReason.NoLosingTrades);
        AssertNonValid(BasicMetricsCalculator.ComputeLargestLoss(trades), MetricStatus.Undefined, MetricReason.NoLosingTrades);
        Assert.Equal(5m, BasicMetricsCalculator.ComputeAverageWin(trades).Value);
        Assert.Equal(6m, BasicMetricsCalculator.ComputeLargestWin(trades).Value);
        AssertNonValid(BasicMetricsCalculator.ComputePayoffRatio(trades), MetricStatus.PositiveInfinity, MetricReason.ZeroDivisor);
    }

    [Fact]
    public void PnlMetrics_AllBreakeven_PayoffRatioUndefinedAllBreakeven_SumsZero()
    {
        var trades = Trades(0m, 0m);

        Assert.Equal(0m, BasicMetricsCalculator.ComputeGrossProfit(trades).Value);
        Assert.Equal(0m, BasicMetricsCalculator.ComputeGrossLoss(trades).Value);
        AssertNonValid(BasicMetricsCalculator.ComputePayoffRatio(trades), MetricStatus.Undefined, MetricReason.AllBreakeven);
        AssertNonValid(BasicMetricsCalculator.ComputeAverageWin(trades), MetricStatus.Undefined, MetricReason.NoWinningTrades);
        AssertNonValid(BasicMetricsCalculator.ComputeAverageLoss(trades), MetricStatus.Undefined, MetricReason.NoLosingTrades);
    }

    // ---- Task 2: AverageHoldingPeriod -----------------------------------------------------------

    [Fact]
    public void AverageHoldingPeriod_MeanOverAllTradesIncludingBreakeven_InBars()
    {
        var trades = ImmutableArray.Create(
            ReportTestHelpers.Trade(10m, entryBar: 0, exitBar: 2),   // 2 bars, win
            ReportTestHelpers.Trade(-5m, entryBar: 3, exitBar: 8),   // 5 bars, loss
            ReportTestHelpers.Trade(0m, entryBar: 9, exitBar: 10));  // 1 bar, breakeven

        MetricValue metric = BasicMetricsCalculator.ComputeAverageHoldingPeriod(trades);

        Assert.Equal(MetricStatus.Valid, metric.Status);
        Assert.Equal(MetricUnit.Bars, metric.Unit);
        Assert.Equal(8m / 3m, metric.Value);
    }

    [Fact]
    public void AverageHoldingPeriod_SingleTrade_EqualsItsHoldingBars()
    {
        var trades = ImmutableArray.Create(ReportTestHelpers.Trade(1m, entryBar: 4, exitBar: 11));

        Assert.Equal(7m, BasicMetricsCalculator.ComputeAverageHoldingPeriod(trades).Value);
    }

    // ---- Task 3: MaxConsecutiveWins / MaxConsecutiveLosses --------------------------------------

    [Fact]
    public void MaxConsecutive_MixedSequence_ReturnsLongestRuns()
    {
        // W W L W W W L L L L W  -> wins 3, losses 4
        var trades = Trades(1m, 2m, -1m, 3m, 4m, 5m, -1m, -2m, -3m, -4m, 6m);

        MetricValue wins = BasicMetricsCalculator.ComputeMaxConsecutiveWins(trades);
        MetricValue losses = BasicMetricsCalculator.ComputeMaxConsecutiveLosses(trades);

        Assert.Equal(3m, wins.Value);
        Assert.Equal(4m, losses.Value);
        Assert.Equal(MetricUnit.Count, wins.Unit);
        Assert.Equal(MetricUnit.Count, losses.Unit);
    }

    [Fact]
    public void MaxConsecutive_BreakevenBreaksBothStreaks()
    {
        // W W 0 W  -> the breakeven resets the win run: longest win run is 2, not 3.
        Assert.Equal(2m, BasicMetricsCalculator.ComputeMaxConsecutiveWins(Trades(1m, 2m, 0m, 3m)).Value);
        // L 0 L L  -> longest loss run is 2, not 3.
        Assert.Equal(2m, BasicMetricsCalculator.ComputeMaxConsecutiveLosses(Trades(-1m, 0m, -2m, -3m)).Value);
    }

    [Fact]
    public void MaxConsecutive_NoTradesOfThatKind_ValidZero()
    {
        Assert.Equal(0m, BasicMetricsCalculator.ComputeMaxConsecutiveWins(Trades(-1m, -2m)).Value);
        Assert.Equal(0m, BasicMetricsCalculator.ComputeMaxConsecutiveLosses(Trades(1m, 2m)).Value);
    }

    [Fact]
    public void MaxConsecutive_AllWins_EqualsTradeCount()
    {
        Assert.Equal(4m, BasicMetricsCalculator.ComputeMaxConsecutiveWins(Trades(1m, 1m, 1m, 1m)).Value);
    }

    [Fact]
    public void MaxConsecutive_UsesGivenOrder_NeverSorts()
    {
        // The same multiset in a different order gives a different streak: the calculator consumes the chronological order as supplied.
        Assert.Equal(2m, BasicMetricsCalculator.ComputeMaxConsecutiveWins(Trades(1m, 1m, -1m, 1m)).Value);
        Assert.Equal(3m, BasicMetricsCalculator.ComputeMaxConsecutiveWins(Trades(1m, 1m, 1m, -1m)).Value);
    }

    // ---- Report wiring, validation, persistence -------------------------------------------------

    private static BacktestReport GenerateReport(params decimal[] closedNet) =>
        new BacktestReportGenerator().Generate(
            ReportTestHelpers.BuildResult(100m, new[] { 110m, 100m, 115m }, closedNet.Select(n => ReportTestHelpers.Trade(n)).ToArray()),
            ReportTestHelpers.Options());

    [Fact]
    public void Generator_PopulatesEveryExtendedMetric_WithCanonicalUnits()
    {
        BacktestReport report = GenerateReport(10m, -5m, 20m);

        var present = report.EnumerateExtendedMetrics().ToList();

        Assert.Equal(ReportTestHelpers.ExtendedMetricJsonNames, present.Select(p => p.Name).ToArray());
        Assert.All(present, p => Assert.Equal(p.ExpectedUnit, p.Metric.Unit));
        Assert.Equal(30m, report.GrossProfit!.Value.Value);
        Assert.Equal(5m, report.GrossLoss!.Value.Value);
        Assert.Equal(15m, report.AverageWin!.Value.Value);
        Assert.Equal(5m, report.AverageLoss!.Value.Value);
        Assert.Equal(3m, report.PayoffRatio!.Value.Value);
        Assert.Equal(20m, report.LargestWin!.Value.Value);
        Assert.Equal(5m, report.LargestLoss!.Value.Value);
        Assert.Equal(1m, report.AverageHoldingPeriod!.Value.Value);
        Assert.Equal(1m, report.MaxConsecutiveWins!.Value.Value);
        Assert.Equal(1m, report.MaxConsecutiveLosses!.Value.Value);
        Assert.Equal(1, report.FormulaVersion);
    }

    [Fact]
    public void Generator_NoTrades_ExtendedMetricsAreInsufficientData_AndReportStillValidates()
    {
        BacktestReport report = new BacktestReportGenerator().Generate(
            ReportTestHelpers.BuildResult(100m, new[] { 100m, 101m }), ReportTestHelpers.Options());

        // Only the ten trade-derived metrics depend on Trades; the equity-curve metrics (drawdown durations, exposure) are still Valid.
        string[] tradeDerived = ReportTestHelpers.ExtendedMetricJsonNames.Take(10).ToArray();
        var tradeMetrics = report.EnumerateExtendedMetrics().Where(p => tradeDerived.Contains(p.Name)).ToList();
        Assert.Equal(10, tradeMetrics.Count);
        Assert.All(tradeMetrics, p => Assert.Equal(MetricStatus.InsufficientData, p.Metric.Status));
        BacktestReportValidator.Validate(report);
    }

    [Fact]
    public void Validator_LegacyReportWithoutExtendedMetrics_IsAccepted()
    {
        BacktestReport legacy = ReportTestHelpers.ToLegacyReport(GenerateReport(10m, -5m));

        Assert.Empty(legacy.EnumerateExtendedMetrics());
        BacktestReportValidator.Validate(legacy);
    }

    [Fact]
    public void Validator_ExtendedMetricWithWrongUnit_IsRejected()
    {
        BacktestReport valid = GenerateReport(10m, -5m);
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(valid))!.AsObject();
        node["MaxConsecutiveWins"] = System.Text.Json.Nodes.JsonNode.Parse(
            JsonSerializer.Serialize(MetricValue.Valid(1m, MetricUnit.Currency)));
        BacktestReport tampered = JsonSerializer.Deserialize<BacktestReport>(node.ToJsonString())!;

        var ex = Assert.Throws<ArgumentException>(() => BacktestReportValidator.Validate(tampered));
        Assert.Contains("MaxConsecutiveWins", ex.Message);
    }

    [Fact]
    public async Task Exporter_RoundTripsExtendedMetrics_AndLegacyJsonOmitsThem()
    {
        BacktestReport report = GenerateReport(10m, -5m, 20m);
        string fileName = "test_extended_" + Guid.NewGuid().ToString("N") + ".json";
        string path = PathDiscovery.ResolveBacktestReportExportPath(fileName);
        var exporter = new BacktestReportExporter();
        try
        {
            await exporter.ExportAsync(report, fileName);
            BacktestReport? loaded = await AtomicJsonFile.LoadAsync<BacktestReport?>(path);

            Assert.NotNull(loaded);
            Assert.Equal(report.EnumerateExtendedMetrics().ToList(), loaded!.EnumerateExtendedMetrics().ToList());

            BacktestReport legacy = ReportTestHelpers.ToLegacyReport(report);
            string legacyJson = JsonSerializer.Serialize(legacy);
            foreach (string name in ReportTestHelpers.ExtendedMetricJsonNames)
            {
                Assert.DoesNotContain("\"" + name + "\"", legacyJson);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void AssertNonValid(MetricValue metric, MetricStatus status, MetricReason reason)
    {
        Assert.Equal(status, metric.Status);
        Assert.Equal(reason, metric.Reason);
        Assert.Null(metric.Value);
    }
}
