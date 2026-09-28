using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using StockAnalyzer.Core.Tests.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.Verification;

/// <summary>
/// L3 metric correctness against golden values computed by an INDEPENDENT implementation (Assets\MetricsGolden\compute_metrics_golden.py,
/// Python standard library only, formulas taken from the P2 spec document). The public <see cref="BacktestReportGenerator"/> is fed a real
/// <see cref="BacktestResult"/> built from the JSON input; nothing in the engine or calculators is shared with the expected values.
/// The calculators narrow through double (spec Gate G3), so the comparison uses a relative tolerance instead of bit equality.
/// </summary>
[Trait("Category", "BacktestVerification")]
public class L3_MetricsGoldenTests
{
    /// <summary>Relative tolerance: double arithmetic differs between platforms/implementations at ~1e-15; 1e-9 leaves margin without hiding a formula error.</summary>
    private const double GoldenRelativeTolerance = 1e-9;

    private static readonly string GoldenPath = Path.Combine(AppContext.BaseDirectory, "Assets", "MetricsGolden", "metrics_golden_cases.json");

    public static IEnumerable<object[]> CaseNames()
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(GoldenPath));
        return doc.RootElement.GetProperty("cases").EnumerateArray().Select(c => new object[] { c.GetProperty("name").GetString()! }).ToList();
    }

    private static decimal Dec(JsonElement e) => decimal.Parse(e.GetString()!, CultureInfo.InvariantCulture);

    private static DateTime Utc(string isoDate) => DateTime.SpecifyKind(DateTime.Parse(isoDate, CultureInfo.InvariantCulture), DateTimeKind.Utc);

    private static void AssertClose(string metric, double expected, MetricValue actual)
    {
        Assert.True(actual.Status == MetricStatus.Valid, $"{metric}: expected a Valid metric but was {actual.Status}/{actual.Reason}");
        double value = (double)actual.Value!.Value;
        double allowed = GoldenRelativeTolerance * Math.Max(1.0, Math.Abs(expected));
        Assert.True(Math.Abs(value - expected) <= allowed, $"{metric}: golden {expected:R} vs actual {value:R} (allowed {allowed:E2})");
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void ReportMetrics_MatchTheIndependentPythonGolden(string caseName)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(GoldenPath));
        JsonElement c = doc.RootElement.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("name").GetString() == caseName);

        decimal initial = Dec(c.GetProperty("initialCapital"));
        decimal[] points = c.GetProperty("pointEquity").EnumerateArray().Select(Dec).ToArray();
        BacktestTrade[] trades = c.GetProperty("tradeClosedNet").EnumerateArray().Select(e => ReportTestHelpers.Trade(Dec(e))).ToArray();
        var options = new BacktestReportOptions(TimeFrame.D1, c.GetProperty("historyStartIndex").GetInt32(),
            Utc(c.GetProperty("evaluationStart").GetString()!), Utc(c.GetProperty("evaluationEnd").GetString()!))
        {
            AnnualPeriods = c.GetProperty("annualPeriods").GetInt32(),
            AnnualRiskFreeRate = Dec(c.GetProperty("annualRiskFreeRate")),
            AnnualMAR = Dec(c.GetProperty("annualMar")),
        };

        BacktestReport report = new BacktestReportGenerator().Generate(ReportTestHelpers.BuildResult(initial, points, trades), options);

        JsonElement expected = c.GetProperty("expected");
        double E(string name) => expected.GetProperty(name).GetDouble();
        AssertClose("TotalPnL", E("TotalPnL"), report.TotalPnL);
        AssertClose("TotalReturn", E("TotalReturn"), report.TotalReturn);
        AssertClose("CAGR", E("CAGR"), report.CAGR);
        AssertClose("WinRate", E("WinRate"), report.WinRate);
        AssertClose("ProfitFactor", E("ProfitFactor"), report.ProfitFactor);
        AssertClose("ExpectedPayoff", E("ExpectedPayoff"), report.ExpectedPayoff);
        AssertClose("MaxDrawdown", E("MaxDrawdown"), report.MaxDrawdown);
        AssertClose("MaxDrawdownAmount", E("MaxDrawdownAmount"), report.MaxDrawdownAmount);
        AssertClose("UlcerIndex", E("UlcerIndex"), report.UlcerIndex);
        AssertClose("BarSharpe", E("BarSharpe"), report.BarSharpe);
        AssertClose("AnnualizedSharpe", E("AnnualizedSharpe"), report.AnnualizedSharpe);
        AssertClose("BarSortino", E("BarSortino"), report.BarSortino);
        AssertClose("AnnualizedSortino", E("AnnualizedSortino"), report.AnnualizedSortino);
        AssertClose("CalmarFullPeriod", E("CalmarFullPeriod"), report.CalmarFullPeriod);
        AssertClose("RecoveryFactor", E("RecoveryFactor"), report.RecoveryFactor);

        // Current schema has no InitialRiskAmount: SQN can never be computed (documented in RiskAdjustedMetricsCalculator.ComputeSqn).
        Assert.Equal(MetricStatus.NotApplicable, report.SQN.Status);
        Assert.Equal(MetricReason.RiskDataMissing, report.SQN.Reason);
        Assert.Equal(trades.Length, report.TotalTrades);
    }

    [Fact]
    public void GoldenFile_ContainsTheSpecTextbookCase()
    {
        // Guards the golden input itself: E = [100,120,90,108] must give MDD 0.25, DD amount 30, TotalReturn 0.08 (spec section 6).
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(GoldenPath));
        JsonElement textbook = doc.RootElement.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("name").GetString() == "textbook").GetProperty("expected");
        Assert.Equal(0.25, textbook.GetProperty("MaxDrawdown").GetDouble(), 12);
        Assert.Equal(30.0, textbook.GetProperty("MaxDrawdownAmount").GetDouble(), 12);
        Assert.Equal(0.08, textbook.GetProperty("TotalReturn").GetDouble(), 12);
        Assert.Equal(Math.Sqrt((625.0 + 100.0) / 3.0), textbook.GetProperty("UlcerIndex").GetDouble(), 9);
    }
}
