using System;
using System.Collections.Generic;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.Core.Tests.Backtest.Reporting;

/// <summary>
/// Timing / allocation LOG for report generation (T10). It asserts nothing about time: it prints numbers so a later refactor
/// (Y:\Temp\sa_implementation_plan_BacktestTradeStatisticsSinglePass.md) has a measured baseline instead of an estimate.
/// The default run measures small sizes only; set <c>SA_PERF_LARGE=1</c> for the large ones (up to a million trades / bars) and read the output with
/// <c>dotnet test --filter "Category=Perf" --logger "console;verbosity=detailed"</c>.
/// </summary>
[Trait("Category", "Perf")]
public class ExtendedMetricsTimingLogTests(ITestOutputHelper output)
{
    private static readonly int[] SmallTradeCounts = { 10_000 };
    private static readonly int[] LargeTradeCounts = { 10_000, 100_000, 1_000_000 };
    private static readonly int[] SmallBarCounts = { 10_000 };
    private static readonly int[] LargeBarCounts = { 100_000, 1_000_000 };

    /// <summary>Trades are scanned by every trade metric; bars by the equity statistics. A fixed small bar count keeps the bootstrap from hiding the trade cost.</summary>
    private const int BarsWhileVaryingTrades = 1_000;

    /// <summary>No trades while varying bars, so the equity-side cost is measured on its own.</summary>
    private const int TradesWhileVaryingBars = 0;

    private void Log(string kind, ReportTimingHarness.Measurement measurement) => output.WriteLine(
        $"[perf] {kind}: trades={measurement.Trades,9:N0} bars={measurement.Bars,9:N0} elapsed={measurement.Milliseconds,10:F1} ms allocated={measurement.AllocatedBytes / 1024.0 / 1024.0,9:F2} MiB");

    [Fact]
    public void LogTradeCalculatorTiming()
    {
        foreach (int trades in ReportTimingHarness.LargeSizesEnabled ? LargeTradeCounts : SmallTradeCounts)
        {
            Log("trade-calculators", ReportTimingHarness.MeasureTradeCalculators(trades));
        }
    }

    [Fact]
    public void LogFullReportTiming_VaryingTheTradeCount()
    {
        foreach (int trades in ReportTimingHarness.LargeSizesEnabled ? LargeTradeCounts : SmallTradeCounts)
        {
            Log("full-report", ReportTimingHarness.MeasureFullReport(trades, BarsWhileVaryingTrades));
        }
    }

    [Fact]
    public void LogFullReportTiming_VaryingTheBarCount()
    {
        foreach (int bars in ReportTimingHarness.LargeSizesEnabled ? LargeBarCounts : SmallBarCounts)
        {
            Log("full-report", ReportTimingHarness.MeasureFullReport(TradesWhileVaryingBars, bars));
        }
    }
}
