using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.Reporting;

/// <summary>
/// <see cref="TradeStatistics"/> is the single-pass replacement for the per-metric trade scans. These tests pin it against a naive
/// multi-pass recomputation (LINQ / run-length, written independently of the production loop) and pin the per-metric overflow isolation
/// that the old one-scan-per-metric code had. Report-level equality with the independent oracle is covered by ExtendedMetricsOracleTests.
/// </summary>
public class TradeStatisticsTests
{
    private const int SeededCaseCount = 200;
    private const int MaxTradesPerCase = 60;
    private const int HoldingBarsExclusiveUpperBound = 9;

    /// <summary>A trade is a breakeven when its draw is below <see cref="BreakevenDrawCount"/> out of <see cref="DrawSpace"/>, so zeros are common enough to exercise streak resets.</summary>
    private const int DrawSpace = 10;
    private const int BreakevenDrawCount = 2;

    /// <summary>Cent-scaled net range (-500.00 .. 599.99) so sums and extremes stay exact decimals.</summary>
    private const int MinNetCents = -50_000;
    private const int MaxNetCentsExclusive = 60_000;
    private const decimal CentsPerUnit = 100m;

    private static ImmutableArray<BacktestTrade> Trades(IEnumerable<(decimal Net, int Holding)> rows)
    {
        var list = new List<BacktestTrade>();
        int bar = 0;
        foreach ((decimal net, int holding) in rows)
        {
            list.Add(ReportTestHelpers.Trade(net, entryBar: bar, exitBar: bar + holding));
            bar += holding + 1;
        }
        return list.ToImmutableArray();
    }

    private static int LongestRun(IEnumerable<decimal> nets, Func<decimal, bool> inRun)
    {
        int longest = 0;
        foreach (string run in string.Concat(nets.Select(n => inRun(n) ? 'x' : '.')).Split('.'))
        {
            longest = Math.Max(longest, run.Length);
        }
        return longest;
    }

    private static void AssertEqualsNaive(ImmutableArray<BacktestTrade> trades)
    {
        TradeStatistics actual = TradeStatistics.Compute(trades, CancellationToken.None);

        decimal[] nets = trades.Select(t => t.ClosedNet).ToArray();
        decimal[] wins = nets.Where(n => n > 0m).ToArray();
        decimal[] losses = nets.Where(n => n < 0m).Select(n => -n).ToArray();

        Assert.Equal(nets.Length, actual.Count);
        Assert.Equal(wins.Length, actual.Wins);
        Assert.Equal(losses.Length, actual.Losses);
        Assert.Equal(nets.Count(n => n == 0m), actual.Breakevens);
        Assert.Equal(wins.Sum(), actual.GrossProfit);
        Assert.Equal(losses.Sum(), actual.GrossLoss);
        Assert.Equal(wins.Length == 0 ? 0m : wins.Max(), actual.LargestWin);
        Assert.Equal(losses.Length == 0 ? 0m : losses.Max(), actual.LargestLoss);
        Assert.Equal(nets.Sum(), actual.NetSum);
        Assert.Equal(trades.Sum(t => (long)t.HoldingBars), actual.HoldingBarsSum);
        Assert.Equal(LongestRun(nets, n => n > 0m), actual.MaxWinStreak);
        Assert.Equal(LongestRun(nets, n => n < 0m), actual.MaxLossStreak);
        Assert.False(actual.GrossOverflowed);
        Assert.False(actual.NetSumOverflowed);
    }

    [Fact]
    public void NoTrades_AllAggregatesAreZero()
    {
        TradeStatistics stats = TradeStatistics.Compute(ImmutableArray<BacktestTrade>.Empty, CancellationToken.None);

        Assert.Equal(new TradeStatistics(0, 0, 0, 0, 0m, 0m, 0m, 0m, 0m, 0L, 0, 0, false, false), stats);
        AssertEqualsNaive(ImmutableArray<BacktestTrade>.Empty);
    }

    [Fact]
    public void AllBreakeven_CountsBreakevensAndNoStreaks()
    {
        ImmutableArray<BacktestTrade> trades = Trades(Enumerable.Repeat((0m, 2), 5));
        TradeStatistics stats = TradeStatistics.Compute(trades, CancellationToken.None);

        Assert.Equal(5, stats.Count);
        Assert.Equal(5, stats.Breakevens);
        Assert.Equal(0, stats.Wins);
        Assert.Equal(0, stats.Losses);
        Assert.Equal(10L, stats.HoldingBarsSum);
        Assert.Equal(0, stats.MaxWinStreak);
        Assert.Equal(0, stats.MaxLossStreak);
        AssertEqualsNaive(trades);
    }

    [Fact]
    public void Breakeven_ResetsBothStreaks_WinLossSwitchResetsTheOther()
    {
        // + + 0 + - - - 0 - + : win runs 2,1,1 -> 2; loss runs 3,1 -> 3
        ImmutableArray<BacktestTrade> trades = Trades(new[] { 1m, 2m, 0m, 3m, -1m, -2m, -3m, 0m, -4m, 5m }.Select(n => (n, 0)));
        TradeStatistics stats = TradeStatistics.Compute(trades, CancellationToken.None);

        Assert.Equal(2, stats.MaxWinStreak);
        Assert.Equal(3, stats.MaxLossStreak);
        AssertEqualsNaive(trades);
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void SeededCases_EqualNaiveRecomputation(int seed)
    {
        var random = new Random(seed);
        int k = random.Next(0, MaxTradesPerCase);
        // Cent-scaled nets with a deliberate share of zeros so that breakeven resets, long runs and ties of the extremes all occur.
        ImmutableArray<BacktestTrade> trades = Trades(Enumerable.Range(0, k).Select(_ =>
        {
            int pick = random.Next(0, DrawSpace);
            decimal net = pick < BreakevenDrawCount ? 0m : (random.Next(MinNetCents, MaxNetCentsExclusive) / CentsPerUnit);
            return (net, random.Next(0, HoldingBarsExclusiveUpperBound));
        }));

        AssertEqualsNaive(trades);
    }

    public static IEnumerable<object[]> Seeds() => Enumerable.Range(1, SeededCaseCount).Select(seed => new object[] { seed });

    [Fact]
    public void GrossOverflow_FailsExactlyTheGrossDependentMetrics()
    {
        // Two decimal.MaxValue wins overflow GrossProfit, and the old per-metric scans failed the same set and nothing else.
        ImmutableArray<BacktestTrade> trades = Trades(new[] { (decimal.MaxValue, 1), (decimal.MaxValue, 1), (-1m, 1) });
        TradeStatistics stats = TradeStatistics.Compute(trades, CancellationToken.None);

        Assert.True(stats.GrossOverflowed);
        Assert.True(stats.NetSumOverflowed); // the running net sum of two MaxValue wins overflows as well
        Assert.Equal(3, stats.Count);
        Assert.Equal(2, stats.Wins);
        Assert.Equal(1, stats.Losses);

        Assert.Throws<OverflowException>(() => BasicMetricsCalculator.ComputeGrossProfit(stats));
        Assert.Throws<OverflowException>(() => BasicMetricsCalculator.ComputeGrossLoss(stats));
        Assert.Throws<OverflowException>(() => BasicMetricsCalculator.ComputeAverageWin(stats));
        Assert.Throws<OverflowException>(() => BasicMetricsCalculator.ComputeAverageLoss(stats));
        Assert.Throws<OverflowException>(() => BasicMetricsCalculator.ComputePayoffRatio(stats));
        Assert.Throws<OverflowException>(() => BasicMetricsCalculator.ComputeLargestWin(stats));
        Assert.Throws<OverflowException>(() => BasicMetricsCalculator.ComputeLargestLoss(stats));
        Assert.Throws<OverflowException>(() => BasicMetricsCalculator.ComputeProfitFactor(stats));
        Assert.Throws<OverflowException>(() => BasicMetricsCalculator.ComputeExpectedPayoff(stats));

        Assert.Equal(2m / 3m, BasicMetricsCalculator.ComputeWinRate(stats).Value);
        Assert.Equal(1m, BasicMetricsCalculator.ComputeAverageHoldingPeriod(stats).Value);
        Assert.Equal(2m, BasicMetricsCalculator.ComputeMaxConsecutiveWins(stats).Value);
        Assert.Equal(1m, BasicMetricsCalculator.ComputeMaxConsecutiveLosses(stats).Value);
    }

    [Fact]
    public void AlternatingHugeTrades_OverflowGrossButNotTheNetSum_ExpectedPayoffStaysValid()
    {
        // Wins and losses of magnitude MaxValue cancel in the plain running sum (0, Max, 0, Max ... never overflows) but each gross side
        // overflows. The old per-metric scans gave ExpectedPayoff a value and failed only the gross-dependent metrics.
        ImmutableArray<BacktestTrade> trades = Trades(new[]
        {
            (decimal.MaxValue, 0), (-decimal.MaxValue, 0), (decimal.MaxValue, 0), (-decimal.MaxValue, 0),
        });
        TradeStatistics stats = TradeStatistics.Compute(trades, CancellationToken.None);

        Assert.True(stats.GrossOverflowed);
        Assert.False(stats.NetSumOverflowed);
        Assert.Equal(0m, stats.NetSum);
        Assert.Equal(0m, BasicMetricsCalculator.ComputeExpectedPayoff(stats).Value);
        Assert.Throws<OverflowException>(() => BasicMetricsCalculator.ComputeGrossProfit(stats));
    }

    [Fact]
    public void NetSumOverflow_FailsExpectedPayoff()
    {
        ImmutableArray<BacktestTrade> trades = Trades(new[] { (decimal.MaxValue, 0), (decimal.MaxValue, 0) });
        TradeStatistics stats = TradeStatistics.Compute(trades, CancellationToken.None);

        Assert.True(stats.NetSumOverflowed);
        Assert.Throws<OverflowException>(() => BasicMetricsCalculator.ComputeExpectedPayoff(stats));
        Assert.Equal(1m, BasicMetricsCalculator.ComputeWinRate(stats).Value);
    }

    [Fact]
    public void TradeArrayOverloads_MatchTheStatisticsOverloads()
    {
        ImmutableArray<BacktestTrade> trades = Trades(new[] { (5m, 1), (-2m, 3), (0m, 0), (7m, 2), (-1m, 4) });
        TradeStatistics stats = TradeStatistics.Compute(trades, CancellationToken.None);

        Assert.Equal(BasicMetricsCalculator.ComputeWinRate(stats), BasicMetricsCalculator.ComputeWinRate(trades));
        Assert.Equal(BasicMetricsCalculator.ComputeProfitFactor(stats), BasicMetricsCalculator.ComputeProfitFactor(trades));
        Assert.Equal(BasicMetricsCalculator.ComputeExpectedPayoff(stats), BasicMetricsCalculator.ComputeExpectedPayoff(trades));
        Assert.Equal(BasicMetricsCalculator.ComputePayoffRatio(stats), BasicMetricsCalculator.ComputePayoffRatio(trades));
        Assert.Equal(BasicMetricsCalculator.ComputeAverageHoldingPeriod(stats), BasicMetricsCalculator.ComputeAverageHoldingPeriod(trades));
        Assert.Equal(BasicMetricsCalculator.ComputeMaxConsecutiveWins(stats), BasicMetricsCalculator.ComputeMaxConsecutiveWins(trades));
    }

    [Fact]
    public void Cancellation_IsObserved()
    {
        ImmutableArray<BacktestTrade> trades = Trades(Enumerable.Repeat((1m, 0), 10));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => TradeStatistics.Compute(trades, cts.Token));
    }
}
