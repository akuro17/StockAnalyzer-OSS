using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Engine;
using Xunit;
using static StockAnalyzer.Core.Tests.Backtest.Verification.LedgerAssertions;

namespace StockAnalyzer.Core.Tests.Backtest.Verification;

/// <summary>
/// L3 null checks. The exhaustive symmetric finite-path test below is the exact oracle. The older
/// fixed-seed random corpus remains only as an auxiliary regression: because price and strategy use
/// matching seeds, it is neither an independence construction nor a proof of zero expected gross P&amp;L.
/// </summary>
[Trait("Category", "BacktestVerification")]
public class L3_NullStrategyTests
{
    private static readonly TimeInForce Gtc = new(true, -1);

    private sealed class FinitePathStrategy : IBacktestStrategy
    {
        public string Name => "FinitePathNull";
        public IReadOnlyList<StrategyIndicatorRequest> GetRequiredIndicators() => Array.Empty<StrategyIndicatorRequest>();
        public StrategyOrderRequest? Evaluate(StrategyContext context) => context.BarIndex switch
        {
            0 => new StrategyOrderRequest(SignalType.LongEntry, OrderType.Market, null, null, Gtc, "finite entry"),
            14 => new StrategyOrderRequest(SignalType.LongExit, OrderType.Market, null, null, Gtc, "finite exit"),
            _ => null,
        };
    }

    /// <summary>Fixed auxiliary corpus size (one total per paired-seed walk).</summary>
    private const int WalkCount = 200;

    private const int BarsPerWalk = 300;

    /// <summary>Frozen regression bound for the legacy fixed-seed corpus; not a universal statistical claim.</summary>
    private const double NullZScoreLimit = 4.0;

    private const decimal NullTestCapital = 10_000_000m;

    private sealed record WalkOutcome(BacktestResult Free, BacktestResult Costed);

    private static BacktestConfiguration FreeConfig() => VerificationHarness.MakeConfig(NullTestCapital, sizingParameter: 100m);

    private static BacktestConfiguration CostedConfig() => VerificationHarness.MakeConfig(
        NullTestCapital, commissionFlat: 1m, commissionPerUnit: 0.01m, slippageRatio: 0.001m, sizingParameter: 100m);

    private static List<WalkOutcome> RunCorpus()
    {
        var outcomes = new List<WalkOutcome>(WalkCount);
        for (int seed = 1; seed <= WalkCount; seed++)
        {
            ImmutableArray<CandleData> bars = SyntheticBars.SeededRandomWalk(seed, BarsPerWalk, VerificationParameters.FuzzStartPrice);
            BacktestInput input = VerificationHarness.MakeInput(bars);
            BacktestResult free = VerificationHarness.Run(input, FreeConfig(), new SeededRandomStrategy(seed, marketOrdersOnly: true));
            BacktestResult costed = VerificationHarness.Run(input, CostedConfig(), new SeededRandomStrategy(seed, marketOrdersOnly: true));
            outcomes.Add(new WalkOutcome(free, costed));
        }
        return outcomes;
    }

    private static decimal ClosedTradeCosts(BacktestResult r)
    {
        // Fills belonging to closed trades are the first 2 * Trades.Length (an entry fill of a still-open final position has no trade).
        IEnumerable<BacktestFill> closedFills = r.Fills.Take(2 * r.Trades.Length);
        return closedFills.Sum(f => f.Commission + f.SlippageAmount);
    }

    [Fact]
    public void FixedRandomCorpus_ZeroCostMean_RemainsWithinFrozenRegressionBound()
    {
        List<WalkOutcome> corpus = RunCorpus();

        double[] walkTotals = corpus.Select(o => (double)o.Free.Trades.Sum(t => t.ClosedNet)).ToArray();
        double mean = walkTotals.Average();
        double variance = walkTotals.Sum(v => (v - mean) * (v - mean)) / (walkTotals.Length - 1);
        double standardError = Math.Sqrt(variance / walkTotals.Length);
        double z = mean / standardError;

        Assert.True(corpus.Sum(o => o.Free.Trades.Length) > WalkCount, "the corpus must contain many trades or the test proves nothing");
        Assert.True(Math.Abs(z) < NullZScoreLimit, $"mean gross P&L per walk {mean:F2} (SE {standardError:F2}) gives z = {z:F2}");
    }

    [Fact]
    public void FixedRandomCorpus_WithCosts_SameDecisions_NetEqualsGrossMinusCommissionsAndSlippage_ExactlyPerWalk()
    {
        List<WalkOutcome> corpus = RunCorpus();

        foreach (WalkOutcome o in corpus)
        {
            Assert.Equal(o.Free.Trades.Length, o.Costed.Trades.Length);
            for (int i = 0; i < o.Free.Trades.Length; i++)
            {
                Assert.Equal(o.Free.Trades[i].EntryBar, o.Costed.Trades[i].EntryBar);
                Assert.Equal(o.Free.Trades[i].ExitBar, o.Costed.Trades[i].ExitBar);
                Assert.Equal(o.Free.Trades[i].Side, o.Costed.Trades[i].Side);
            }

            decimal gross = o.Free.Trades.Sum(t => t.ClosedNet);
            decimal net = o.Costed.Trades.Sum(t => t.ClosedNet);
            Eq(gross - ClosedTradeCosts(o.Costed), net, "Sum(net) == Sum(gross) - commissions - slippage");
        }
    }

    [Fact]
    public void FixedRandomCorpus_WithCosts_TotalNetPnLIsNegative_AndEveryTradeCostsSomething()
    {
        List<WalkOutcome> corpus = RunCorpus();

        decimal totalNet = corpus.Sum(o => o.Costed.Trades.Sum(t => t.ClosedNet));
        int tradeCount = corpus.Sum(o => o.Costed.Trades.Length);
        decimal totalCosts = corpus.Sum(o => ClosedTradeCosts(o.Costed));

        Assert.True(tradeCount > 0);
        Assert.True(totalCosts / tradeCount > 0m, "mean cost per trade must be positive");
        Assert.True(totalNet < 0m, $"the fixed auxiliary corpus should be net negative after costs, but total net was {totalNet} over {tradeCount} trades (costs {totalCosts})");
    }

    [Fact]
    public void AllFiniteSymmetricPaths_GrossCancelsAndCostsAreExact()
    {
        const int stepCount = 15;
        const int pathCount = 1 << stepCount;
        DateTime start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        BacktestConfiguration freeConfig = VerificationHarness.MakeConfig(
            initialCapital: 1_000_000m,
            sizingParameter: 1m,
            initialMarginRatio: 1m,
            maintenanceMarginRatio: 0.5m);
        BacktestConfiguration costedConfig = VerificationHarness.MakeConfig(
            initialCapital: 1_000_000m,
            commissionFlat: 1m,
            commissionPerUnit: 0m,
            slippageRatio: 0.001m,
            sizingParameter: 1m,
            initialMarginRatio: 1m,
            maintenanceMarginRatio: 0.5m);
        BacktestEngine engine = VerificationHarness.CreateEngine();
        decimal totalFree = 0m;
        decimal totalCosted = 0m;
        decimal expectedCosted = 0m;

        for (int mask = 0; mask < pathCount; mask++)
        {
            var bars = ImmutableArray.CreateBuilder<CandleData>(stepCount + 1);
            decimal price = 100m;
            bars.Add(new CandleData(start, price, price, price, price, 1_000));
            for (int step = 1; step <= stepCount; step++)
            {
                price += (mask & (1 << (step - 1))) == 0 ? -1m : 1m;
                bars.Add(new CandleData(start.AddDays(step), price, price, price, price, 1_000));
            }

            BacktestInput input = VerificationHarness.MakeInput(bars.MoveToImmutable());
            BacktestResult free = engine.Run(input, freeConfig, new FinitePathStrategy());
            BacktestResult costed = engine.Run(input, costedConfig, new FinitePathStrategy());
            BacktestTrade freeTrade = Assert.Single(free.Trades);
            BacktestTrade costedTrade = Assert.Single(costed.Trades);

            Assert.Equal(1, freeTrade.EntryBar);
            Assert.Equal(15, freeTrade.ExitBar);
            Assert.Equal(2, free.Fills.Length);
            Assert.Equal(2, costed.Fills.Length);
            Assert.DoesNotContain(free.Orders, order => order.Status == OrderStatus.Rejected);
            Assert.DoesNotContain(costed.Orders, order => order.Status == OrderStatus.Rejected);
            Assert.False(freeTrade.IsForcedLiquidation);
            Assert.False(costedTrade.IsForcedLiquidation);
            Assert.Equal(0m, free.EquityPoints[^1].MarketValue);
            Assert.Equal(0m, costed.EquityPoints[^1].MarketValue);

            decimal p1 = input.Bars[1].Close;
            decimal p15 = input.Bars[15].Close;
            decimal freeGross = p15 - p1;
            decimal expectedPathCosted = freeGross - 2m - (0.001m * (p1 + p15));
            Assert.Equal(freeGross, freeTrade.ClosedNet);
            Assert.Equal(expectedPathCosted, costedTrade.ClosedNet);

            totalFree += freeTrade.ClosedNet;
            totalCosted += costedTrade.ClosedNet;
            expectedCosted += expectedPathCosted;
        }

        Assert.Equal(0m, totalFree);
        Assert.Equal(expectedCosted, totalCosted);
        Assert.True(totalCosted < 0m);
    }
}
