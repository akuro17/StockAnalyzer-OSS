using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// Hand-computed fixtures of the portfolio accountant. Calendar facts: block 0..191, window 64, so the first decision is after the close of
/// index 63 and fills at the open of index 64; 128 decisions end with the mark at the close of index 191. Every expected number below is
/// written out in the comment of its test, independent of the code under test.
/// </summary>
public sealed class RlPortfolioSessionTests
{
    private static BacktestConfiguration Costs(decimal capital, decimal slippage = 0m, decimal flat = 0m, decimal perUnit = 0m)
        => RlContract.CreateConfiguration(new BacktestConfiguration { SizingModel = PositionSizingModel.FixedQuantity, SizingParameter = 1m, InitialCapital = capital, SlippageRatio = slippage, CommissionFlat = flat, CommissionPerUnit = perUnit });

    private static ImmutableArray<decimal> W(params decimal[] weights) => weights.ToImmutableArray();

    private static RlPortfolioSession Session(RlPanelBarsFixture bars, BacktestConfiguration costs, params int[] tradable)
        => new(bars, RlPanelBarsFixture.Block(Enumerable.Range(0, bars.SymbolCount).ToArray()), tradable.ToImmutableArray(), costs);

    private static Dictionary<(int, int), (decimal, decimal)> Prices(params (int Symbol, int Index, decimal Open, decimal Close)[] rows)
        => rows.ToDictionary(r => (r.Symbol, r.Index), r => (r.Open, r.Close));

    [Fact]
    public void PureCashHoldsTheInitialCapitalForTheWholeEpisode()
    {
        using var session = Session(RlPanelBarsFixture.Flat(new[] { 100m, 50m }), Costs(10_000m), 0, 1);
        var start = session.Reset();
        Assert.Equal((10_000m, 10_000m, 0, 128, 63), (start.Equity, start.Cash, start.CompletedDecisions, start.RemainingDecisions, start.AnchorIndex));
        RlPortfolioStepResult last = null!;
        for (int k = 0; k < 128; k++)
        {
            last = session.Step(W(0m, 0m));
            Assert.Equal(0m, last.Reward);
            Assert.Equal(10_000m, last.Snapshot.Equity);
            Assert.All(last.Trace, t => Assert.True(t.ExecutedQuantity == 0m && t.FillPrice is null && t.Commission == 0m && t.Rejection is null));
        }
        Assert.True(last.Terminated);
        Assert.Equal(RlPortfolioSessionState.Terminated, session.State);
        Assert.Equal((191, 0, 0), (last.Snapshot.AnchorIndex, last.Snapshot.RemainingDecisions, last.Snapshot.OrderCount));
        Assert.Throws<InvalidOperationException>(() => session.Step(W(0m, 0m)));
    }

    [Fact]
    public void SingleAllocationFillsAtTheNextOpenAndIsMarkedAtTheNextClose()
    {
        // E = 10000, w = 0.5, Close(63) = 100: target 50 shares; Open(64) = 101: cost 5050, cash 4950; Close(64) = 110: E = 4950 + 5500 = 10450.
        var bars = RlPanelBarsFixture.Flat(new[] { 100m, 50m }, Prices((0, 64, 101m, 110m)));
        using var session = Session(bars, Costs(10_000m), 0, 1);
        session.Reset();
        var result = session.Step(W(0.5m, 0m));
        var s = result.Snapshot;
        Assert.Equal(0.045m, result.Reward); // (10450 - 10000) / 10000
        Assert.Equal((4950m, 10_450m, 64), (s.Cash, s.Equity, s.AnchorIndex));
        Assert.Equal(new[] { 50m, 0m }, s.Quantities.ToArray());
        Assert.Equal(5500m / 10450m, s.Weights[0]);
        Assert.Equal(4950m / 10450m, s.CashWeight);
        Assert.Equal(0.505m, s.PreviousTurnover); // 5050 / 10000
        Assert.Equal((10_450m, 0m, 1, 0), (s.PeakEquity, s.Drawdown, s.OrderCount, s.RejectedBuyCount));
        var trace = result.Trace[0];
        Assert.Equal((0, 0m, 50m, 50m, 101m, 0m), (trace.SymbolIndex, trace.PreviousQuantity, trace.TargetQuantity, trace.ExecutedQuantity, trace.FillPrice!.Value, trace.Commission));
        Assert.Null(trace.Rejection);
        Assert.Equal(0m, result.Trace[1].ExecutedQuantity);
        Assert.Null(result.Trace[1].FillPrice);
    }

    [Fact]
    public void TwoAssetRebalanceWithSlippageAndCommissionsMatchesTheHandLedger()
    {
        // Costs: slippage 1%, commission 1 + 0.1 per share. Initial 10000. Prices (open, close): A 64 (100, 104), 65 (110, 111); B 64 (50, 49), 65 (52, 53); Close(63): A 100, B 50.
        var bars = RlPanelBarsFixture.Flat(new[] { 100m, 50m }, Prices((0, 64, 100m, 104m), (0, 65, 110m, 111m), (1, 64, 50m, 49m), (1, 65, 52m, 53m)));
        using var session = Session(bars, Costs(10_000m, 0.01m, 1m, 0.1m), 0, 1);
        session.Reset();

        // Decision 0, w = (0.5, 0.3): targets 50 and 60. Buy A at 101: 5050 + (1 + 5) = 5056. Buy B at 50.5: 3030 + (1 + 6) = 3037. Cash 10000 - 8093 = 1907.
        // Close(64): E = 1907 + 50 * 104 + 60 * 49 = 10047.
        var first = session.Step(W(0.5m, 0.3m));
        Assert.Equal((1907m, 10_047m), (first.Snapshot.Cash, first.Snapshot.Equity));
        Assert.Equal(0.0047m, first.Reward);
        Assert.Equal(new[] { 50m, 60m }, first.Snapshot.Quantities.ToArray());
        Assert.Equal((101m, 6m), (first.Trace[0].FillPrice!.Value, first.Trace[0].Commission));
        Assert.Equal((50.5m, 7m), (first.Trace[1].FillPrice!.Value, first.Trace[1].Commission));
        Assert.Equal(13m, first.Snapshot.CommissionPaid);
        Assert.Equal(80m, first.Snapshot.SlippageCost); // 50 * |101 - 100| + 60 * |50.5 - 50|

        // Decision 1 (E = 10047), w = (0, 0.5): A target 0 (sell 50), B target floor(0.5 * 10047 / 49) = floor(102.52) = 102 (buy 42).
        // Sell A at 110 * 0.99 = 108.9: 5445 - (1 + 5) = 5439, cash 7346. Buy B at 52 * 1.01 = 52.52: 2205.84 + (1 + 4.2) = 2211.04, cash 5134.96.
        // Close(65): E = 5134.96 + 102 * 53 = 10540.96.
        var second = session.Step(W(0m, 0.5m));
        Assert.Equal((5134.96m, 10_540.96m), (second.Snapshot.Cash, second.Snapshot.Equity));
        Assert.Equal(0.049396m, second.Reward); // (10540.96 - 10047) / 10000
        Assert.Equal(new[] { 0m, 102m }, second.Snapshot.Quantities.ToArray());
        Assert.Equal((-50m, 108.9m, 6m), (second.Trace[0].ExecutedQuantity, second.Trace[0].FillPrice!.Value, second.Trace[0].Commission));
        Assert.Equal((42m, 52.52m, 5.2m), (second.Trace[1].ExecutedQuantity, second.Trace[1].FillPrice!.Value, second.Trace[1].Commission));
        Assert.Equal(24.2m, second.Snapshot.CommissionPaid);
        Assert.Equal(156.84m, second.Snapshot.SlippageCost); // 80 + 50 * |108.9 - 110| + 42 * |52.52 - 52|
        Assert.Equal((5445m + 2205.84m) / 10_047m, second.Snapshot.PreviousTurnover);
        Assert.Equal(4, second.Snapshot.OrderCount);
    }

    [Fact]
    public void ABuyThatTheCashCannotPayIsRejectedWholeAndCounted()
    {
        // Initial 1000, both at 100: targets 5 and 5. A costs 500 (cash 500). B opens at 300: 5 * 300 = 1500 > 500, rejected, no partial fill.
        var bars = RlPanelBarsFixture.Flat(new[] { 100m, 100m }, Prices((1, 64, 300m, 300m)));
        using var session = Session(bars, Costs(1_000m), 0, 1);
        session.Reset();
        var result = session.Step(W(0.5m, 0.5m));
        Assert.Equal(RlPortfolioRejection.InsufficientFunds, result.Trace[1].Rejection);
        Assert.Equal((0m, 5m), (result.Trace[1].ExecutedQuantity, result.Trace[1].TargetQuantity));
        Assert.Null(result.Trace[1].FillPrice);
        Assert.Equal(new[] { 5m, 0m }, result.Snapshot.Quantities.ToArray());
        Assert.Equal((500m, 1_000m, 2, 1), (result.Snapshot.Cash, result.Snapshot.Equity, result.Snapshot.OrderCount, result.Snapshot.RejectedBuyCount));
        Assert.Equal(0m, result.Reward);
    }

    [Fact]
    public void SellsAreProcessedBeforeBuysSoProceedsFundTheSameDecision()
    {
        // Flat 100. Decision 0 buys 10 of B (cost 1000, cash 0). Decision 1 swaps to A: the buy of A (index 0) would come first in index order
        // and fail on zero cash; selling B (index 1) first gives cash 1000, so the buy of A is accepted.
        using var session = Session(RlPanelBarsFixture.Flat(new[] { 100m, 100m }), Costs(1_000m), 0, 1);
        session.Reset();
        session.Step(W(0m, 1m));
        var swap = session.Step(W(1m, 0m));
        Assert.Equal(new[] { 10m, 0m }, swap.Snapshot.Quantities.ToArray());
        Assert.Equal((0, 3, 0m), (swap.Snapshot.RejectedBuyCount, swap.Snapshot.OrderCount, swap.Snapshot.Cash));
        Assert.Null(swap.Trace[0].Rejection);
    }

    [Fact]
    public void ANonTradableAssetCanNeverReceiveWeight()
    {
        // Symbol 2 is not tradable in this block (ineligible or in the other symbol group): the accountant rejects its weight, never coerces it.
        var bars = RlPanelBarsFixture.Flat(new[] { 100m, 50m, 20m });
        using var session = new RlPortfolioSession(bars, RlPanelBarsFixture.Block(0, 1), ImmutableArray.Create(0, 1), Costs(10_000m));
        var before = session.Reset();
        Assert.Throws<ArgumentException>(() => session.Step(W(0.2m, 0.2m, 0.1m)));
        var ok = session.Step(W(0.2m, 0.2m, 0m));
        Assert.Equal(1, ok.Snapshot.CompletedDecisions);
        Assert.Equal(0, before.CompletedDecisions);
    }

    [Fact]
    public void TheEpisodeEndsMarkedToMarketWithoutLiquidation()
    {
        // Flat 100 / 50, w = (0.4, 0.4): A 40 shares, B 80 shares, cash 2000, bought at decision 0 and held (targets stay equal while E = 10000).
        // Close(191) of A is 120: E = 2000 + 40 * 120 + 80 * 50 = 10800; the last reward is (10800 - 10000) / 10000 = 0.08; nothing is sold.
        var bars = RlPanelBarsFixture.Flat(new[] { 100m, 50m }, Prices((0, 191, 100m, 120m)));
        using var session = Session(bars, Costs(10_000m), 0, 1);
        session.Reset();
        RlPortfolioStepResult last = null!;
        var rewards = new List<decimal>();
        for (int k = 0; k < 128; k++) { last = session.Step(W(0.4m, 0.4m)); rewards.Add(last.Reward); }
        Assert.Equal(0.08m, rewards[^1]);
        Assert.All(rewards.Take(127), r => Assert.Equal(0m, r));
        Assert.Equal((10_800m, 2_000m, 191, 2), (last.Snapshot.Equity, last.Snapshot.Cash, last.Snapshot.AnchorIndex, last.Snapshot.OrderCount));
        Assert.Equal(new[] { 40m, 80m }, last.Snapshot.Quantities.ToArray());
        Assert.True(last.Terminated && last.Snapshot.Terminated);
    }

    private static Func<int, int, (decimal, decimal)?> Wave => (s, t) => s == 0
        ? (100m + (t + 3) % 5, 100m + t % 7)
        : (50m + (t + 1) % 4 * 0.5m, 50m + t % 11 * 0.5m);

    private static decimal Close(int symbol, int index) => Wave(symbol, index)!.Value.Item2;

    [Fact]
    public void EquityReconcilesAndRewardsTelescopeAtEveryStepOfAFullEpisode()
    {
        var costs = Costs(50_000m, 0.001m, 0.5m, 0.01m);
        using var session = Session(new RlPanelBarsFixture(2, Wave), costs, 0, 1);
        var previous = session.Reset();
        decimal rewardSum = 0m;
        for (int k = 0; k < 128; k++)
        {
            var result = session.Step(W((k % 4) * 0.1m, ((k + 1) % 3) * 0.1m));
            var s = result.Snapshot;
            int anchor = 63 + k + 1;
            Assert.Equal(anchor, s.AnchorIndex);
            decimal equity = s.Cash + s.Quantities[0] * Close(0, anchor) + s.Quantities[1] * Close(1, anchor);
            Assert.Equal(equity, s.Equity);
            Assert.Equal((s.Equity - previous.Equity) / 50_000m, result.Reward);
            Assert.True(s.Cash >= 0m && s.Quantities.All(q => q >= 0m && q == decimal.Truncate(q)));
            Assert.Equal(1m, s.CashWeight + s.Weights.Sum(), 12);
            rewardSum += result.Reward;
            previous = s;
        }
        Assert.Equal(previous.Equity - 50_000m, rewardSum * 50_000m, 10);
    }

    private static List<(decimal Equity, decimal Cash, decimal[] Quantities, decimal Reward)> Run(RlPortfolioSession session)
    {
        var log = new List<(decimal, decimal, decimal[], decimal)>();
        session.Reset();
        for (int k = 0; k < 128; k++)
        {
            var r = session.Step(W((k % 4) * 0.1m, ((k + 1) % 3) * 0.1m));
            log.Add((r.Snapshot.Equity, r.Snapshot.Cash, r.Snapshot.Quantities.ToArray(), r.Reward));
        }
        return log;
    }

    [Fact]
    public void RunsAreDeterministicAndResetRestartsTheEpisode()
    {
        var costs = Costs(50_000m, 0.001m, 0.5m, 0.01m);
        using var a = Session(new RlPanelBarsFixture(2, Wave), costs, 0, 1);
        using var b = Session(new RlPanelBarsFixture(2, Wave), costs, 0, 1);
        var first = Run(a);
        var second = Run(b);
        var again = Run(a);
        Assert.Equal(RlPortfolioSessionState.Terminated, a.State);
        for (int k = 0; k < 128; k++)
        {
            Assert.Equal((first[k].Equity, first[k].Cash, first[k].Reward), (second[k].Equity, second[k].Cash, second[k].Reward));
            Assert.Equal((first[k].Equity, first[k].Cash, first[k].Reward), (again[k].Equity, again[k].Cash, again[k].Reward));
            Assert.Equal(first[k].Quantities, again[k].Quantities);
        }
    }

    [Fact]
    public void StateMachineFollowsTheSpecification()
    {
        var session = Session(RlPanelBarsFixture.Flat(new[] { 100m, 50m }), Costs(10_000m), 0, 1);
        Assert.Equal(RlPortfolioSessionState.Created, session.State);
        Assert.Throws<InvalidOperationException>(() => session.Step(W(0m, 0m)));
        session.Reset();
        Assert.Equal(RlPortfolioSessionState.Ready, session.State);
        session.Step(W(0.1m, 0.1m));
        Assert.Equal(0, session.Reset().CompletedDecisions);
        for (int k = 0; k < 128; k++) session.Step(W(0m, 0m));
        Assert.Equal(RlPortfolioSessionState.Terminated, session.State);
        Assert.Equal(0, session.Reset().CompletedDecisions);
        Assert.Equal(RlPortfolioSessionState.Ready, session.State);
        session.Dispose();
        session.Dispose();
        Assert.Equal(RlPortfolioSessionState.Disposed, session.State);
        Assert.Throws<ObjectDisposedException>(() => session.Reset());
        Assert.Throws<ObjectDisposedException>(() => session.Step(W(0m, 0m)));
    }

    [Fact]
    public void CancellationIsHonoredOnlyBeforeAStepStarts()
    {
        using var session = Session(RlPanelBarsFixture.Flat(new[] { 100m, 50m }), Costs(10_000m), 0, 1);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => session.Reset(cancelled.Token));
        Assert.Equal(RlPortfolioSessionState.Created, session.State);
        session.Reset();
        Assert.Throws<OperationCanceledException>(() => session.Step(W(0.1m, 0.1m), cancelled.Token));
        Assert.Equal(1, session.Step(W(0.1m, 0.1m)).Snapshot.CompletedDecisions);
    }

    [Fact]
    public void InvalidWeightsChangeNothing()
    {
        using var session = Session(RlPanelBarsFixture.Flat(new[] { 100m, 50m }), Costs(10_000m), 0, 1);
        session.Reset();
        Assert.Throws<ArgumentException>(() => session.Step(W(-0.1m, 0.1m)));
        Assert.Throws<ArgumentException>(() => session.Step(W(0.6m, 0.5m)));
        Assert.Throws<ArgumentException>(() => session.Step(W(0.1m)));
        Assert.Throws<ArgumentException>(() => session.Step(default));
        var step = session.Step(W(0.6m, 0.4m));
        Assert.Equal((1, 1), (step.Snapshot.CompletedDecisions, step.Snapshot.OrderCount - 1));
    }

    [Fact]
    public void AnAccountInvariantViolationMakesTheSessionUnusable()
    {
        // Initial 1000, flat commission 300. Decision 0 buys 5 of A (500 + 300), cash 200. Open(65) = 10: selling 5 yields 50 - 300 = -250, cash -50.
        var bars = RlPanelBarsFixture.Flat(new[] { 100m, 50m }, Prices((0, 65, 10m, 10m)));
        using var session = Session(bars, Costs(1_000m, flat: 300m), 0, 1);
        session.Reset();
        Assert.Equal(200m, session.Step(W(0.5m, 0m)).Snapshot.Cash);
        Assert.Throws<InvalidOperationException>(() => session.Step(W(0m, 0m)));
        Assert.Throws<InvalidOperationException>(() => session.Step(W(0m, 0m)));
        Assert.Throws<InvalidOperationException>(() => session.Reset());
    }

    [Fact]
    public void ConcurrentCallsAreSerialized()
    {
        using var session = Session(RlPanelBarsFixture.Flat(new[] { 100m, 50m }), Costs(10_000m), 0, 1);
        session.Reset();
        var tasks = Enumerable.Range(0, 2).Select(_ => Task.Run(() => { for (int k = 0; k < 64; k++) session.Step(W(0m, 0m)); })).ToArray();
        Task.WaitAll(tasks);
        Assert.Equal(RlPortfolioSessionState.Terminated, session.State);
        Assert.Throws<InvalidOperationException>(() => session.Step(W(0m, 0m)));
    }

    [Fact]
    public void OnlyBarsOfTheDecisionAndTheNextDayAreReadWhileStepping()
    {
        var bars = RlPanelBarsFixture.Flat(new[] { 100m, 50m });
        using var session = Session(bars, Costs(10_000m), 0, 1);
        session.Reset();
        bars.Reads.Clear();
        session.Step(W(0.2m, 0.2m)); // decision at index 63 reads 63 (close), 64 (open, close) and the 64 snapshot only
        Assert.All(bars.Reads, r => Assert.InRange(r.Index, 63, 64));
    }

    [Fact]
    public void ConstructionRejectsInconsistentInputs()
    {
        var bars = RlPanelBarsFixture.Flat(new[] { 100m, 50m, 20m });
        var costs = Costs(10_000m);
        var block = RlPanelBarsFixture.Block(0, 1, 2);
        Assert.Throws<ArgumentNullException>(() => new RlPortfolioSession(null!, block, ImmutableArray.Create(0, 1), costs));
        Assert.Throws<ArgumentNullException>(() => new RlPortfolioSession(bars, null!, ImmutableArray.Create(0, 1), costs));
        Assert.Throws<ArgumentNullException>(() => new RlPortfolioSession(bars, block, ImmutableArray.Create(0, 1), null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RlPortfolioSession(bars, block, ImmutableArray.Create(0, 1), new BacktestConfiguration()));
        Assert.Throws<ArgumentException>(() => new RlPortfolioSession(bars, block with { EndExclusive = 191 }, ImmutableArray.Create(0, 1), costs));
        Assert.Throws<ArgumentException>(() => new RlPortfolioSession(bars, block, default, costs));
        Assert.Throws<ArgumentException>(() => new RlPortfolioSession(bars, block, ImmutableArray.Create(0), costs));
        Assert.Throws<ArgumentException>(() => new RlPortfolioSession(bars, block, ImmutableArray.Create(1, 0), costs));
        Assert.Throws<ArgumentException>(() => new RlPortfolioSession(bars, block, ImmutableArray.Create(0, 3), costs));
        Assert.Throws<ArgumentException>(() => new RlPortfolioSession(bars, RlPanelBarsFixture.Block(0, 1), ImmutableArray.Create(0, 2), costs));
    }

    [Fact]
    public void ABlockEligibleSymbolWithAMissingBarIsRejectedAtConstruction()
    {
        var bars = new RlPanelBarsFixture(2, (s, t) => s == 1 && t == 100 ? null : (100m, 100m));
        Assert.Throws<ArgumentException>(() => new RlPortfolioSession(bars, RlPanelBarsFixture.Block(0, 1), ImmutableArray.Create(0, 1), Costs(10_000m)));
    }
}
