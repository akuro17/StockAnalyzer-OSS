using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;

namespace StockAnalyzer.Core.Tests.Backtest.Reporting;

/// <summary>
/// Reusable timing/allocation harness for report generation (Y:\Temp\sa_implementation_plan_BacktestReportVerificationOracle.md T10 and
/// Y:\Temp\sa_implementation_plan_BacktestTradeStatisticsSinglePass.md T11 use the SAME harness so before/after numbers are comparable).
/// It only measures; nothing here asserts a time. Data is seeded, so the workload is identical between runs.
/// </summary>
internal static class ReportTimingHarness
{
    private const int Seed = 20260930;
    private const decimal InitialCapital = 100m;

    /// <summary>Set to "1" to include the large sizes (up to a million trades / bars); the default run only measures the small sizes.</summary>
    public const string LargeSizesEnvironmentVariable = "SA_PERF_LARGE";

    public readonly record struct Measurement(int Trades, int Bars, double Milliseconds, long AllocatedBytes);

    /// <summary>A result with <paramref name="tradeCount"/> back-to-back trades (net in -3.0..3.0, 1..10 bars) and <paramref name="barCount"/> equity points.</summary>
    public static BacktestResult BuildResult(int tradeCount, int barCount)
    {
        var random = new Random(Seed);
        var trades = new BacktestTrade[tradeCount];
        int cursor = 0;
        for (int i = 0; i < tradeCount; i++)
        {
            int holding = random.Next(1, 11);
            trades[i] = ReportTestHelpers.Trade(random.Next(-30, 31) / 10m, entryBar: cursor, exitBar: cursor + holding);
            cursor += holding;
        }

        var equity = new decimal[barCount];
        decimal level = InitialCapital;
        for (int i = 0; i < barCount; i++)
        {
            level = Math.Max(1m, level + random.Next(-3, 4));
            equity[i] = level;
        }
        return ReportTimingHarnessBuild(equity, trades);
    }

    private static BacktestResult ReportTimingHarnessBuild(decimal[] equity, BacktestTrade[] trades) =>
        ReportTestHelpers.BuildResult(InitialCapital, equity, trades);

    /// <summary>Elapsed time and bytes allocated on this thread by one full report generation, after one untimed warm-up run of the same size.</summary>
    public static Measurement MeasureFullReport(int tradeCount, int barCount)
    {
        BacktestResult result = BuildResult(tradeCount, barCount);
        BacktestReportOptions options = ReportTestHelpers.Options();
        var generator = new BacktestReportGenerator();
        generator.Generate(result, options); // JIT warm-up

        GC.Collect();
        GC.WaitForPendingFinalizers();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        generator.Generate(result, options);
        double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        return new Measurement(tradeCount, barCount, elapsed, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
    }

    /// <summary>
    /// Only the trade-derived calculators (the seven earlier metrics that scan the trades: WinRate, ProfitFactor, ExpectedPayoff and the
    /// extended GrossProfit .. MaxConsecutiveLosses set), called the way the generator calls them. This isolates the part a single-pass
    /// refactor changes from the equity-based statistics (bootstrap etc.) that dominate a full report at large bar counts.
    /// </summary>
    public static Measurement MeasureTradeCalculators(int tradeCount)
    {
        ImmutableArray<BacktestTrade> trades = BuildResult(tradeCount, barCount: 1).Trades;
        RunTradeCalculators(trades); // JIT warm-up

        GC.Collect();
        GC.WaitForPendingFinalizers();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        RunTradeCalculators(trades);
        double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        return new Measurement(tradeCount, 0, elapsed, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
    }

    private static void RunTradeCalculators(ImmutableArray<BacktestTrade> trades)
    {
        // Same shape as BacktestReportGenerator: one TradeStatistics pass, then the status/reason rules per metric.
        // (Before the single-pass refactor each metric scanned the trades itself; the baseline in the task log was taken in that shape.)
        CancellationToken token = CancellationToken.None;
        TradeStatistics stats = TradeStatistics.Compute(trades, token);
        BasicMetricsCalculator.ComputeWinRate(stats);
        BasicMetricsCalculator.ComputeProfitFactor(stats);
        BasicMetricsCalculator.ComputeExpectedPayoff(stats);
        BasicMetricsCalculator.ComputeGrossProfit(stats);
        BasicMetricsCalculator.ComputeGrossLoss(stats);
        BasicMetricsCalculator.ComputeAverageWin(stats);
        BasicMetricsCalculator.ComputeAverageLoss(stats);
        BasicMetricsCalculator.ComputePayoffRatio(stats);
        BasicMetricsCalculator.ComputeLargestWin(stats);
        BasicMetricsCalculator.ComputeLargestLoss(stats);
        BasicMetricsCalculator.ComputeAverageHoldingPeriod(stats);
        BasicMetricsCalculator.ComputeMaxConsecutiveWins(stats);
        BasicMetricsCalculator.ComputeMaxConsecutiveLosses(stats);
    }

    public static bool LargeSizesEnabled => Environment.GetEnvironmentVariable(LargeSizesEnvironmentVariable) == "1";
}
