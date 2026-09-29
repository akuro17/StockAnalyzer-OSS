using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Tests.Backtest.Verification;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

/// <summary>
/// Task 3 (Y:\Temp\sa_implementation_plan_BacktestPositionDirectionReversal.md sections 2.3/2.4/3.4):
/// engine-level proof that <see cref="BacktestEngine"/>'s dual pending-order slots and Exit-before-Entry
/// commit sequencing work end to end, independent of any specific <see cref="IBacktestStrategy"/>
/// implementation - a hand-scripted strategy exercises <see cref="IBacktestStrategy.Evaluate"/> and the new
/// <see cref="IBacktestStrategy.EvaluateExit"/> member directly, per bar index.
/// </summary>
public class BacktestEngineReversalTests
{
    private static readonly DateTime Bar0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeInForce Gtc = new(true, -1);

    private static CandleData FlatBar(int dayOffset, decimal price) => new(Bar0.AddDays(dayOffset), price, price, price, price, 1000);

    private static BacktestInput MakeInput(ImmutableArray<CandleData> bars) =>
        new(bars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion, Bar0, Bar0.AddDays(bars.Length + 1), 0, 0);

    private static BacktestConfiguration MakeConfig() => new()
    {
        InitialCapital = 1000m,
        SizingModel = PositionSizingModel.FixedQuantity,
        SizingParameter = 10m,
        InitialMarginRatio = 1m,
        MaintenanceMarginRatio = 0.5m,
    };

    private static StrategyOrderRequest Req(SignalType type, string reason = "r") => new(type, OrderType.Market, null, null, Gtc, reason);

    [Fact]
    public void SameBarReversalPair_ExitFillsBeforeEntry_SoEntryFundsCheckUsesPostExitCash()
    {
        // Plan section 2.4's direct regression guard: InitialMarginRatio=1 means the first Long entry
        // consumes ALL of Cash as HeldMargin (Cash becomes 0). A reversal into Short on the very next bar
        // needs that same margin back - if the engine evaluated Entry's funds check BEFORE committing the
        // accompanying Exit's fill (using the stale, pre-exit Cash=0), the Short entry would be wrongly
        // rejected as InsufficientFunds. All bars are flat (O=H=L=C) so fills are deterministic and no
        // maintenance-margin breach is ever in play.
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            FlatBar(0, 100m),  // bar0: LongEntry signalled here
            FlatBar(1, 100m),  // bar1: LongEntry fills (Open=100); same bar, reversal pair signalled
            FlatBar(2, 100m)); // bar2: both LongExit and ShortEntry fill (Open=100)

        var strategy = new ScriptedStrategy(
            primary: new Dictionary<int, StrategyOrderRequest>
            {
                [0] = Req(SignalType.LongEntry, "open-long"),
                [1] = Req(SignalType.ShortEntry, "reversal-open-short"),
            },
            accompanyingExit: new Dictionary<int, StrategyOrderRequest>
            {
                [1] = Req(SignalType.LongExit, "reversal-close-long"),
            });

        BacktestResult result = VerificationHarness.CreateEngine().Run(MakeInput(bars), MakeConfig(), strategy);

        Assert.Equal(RunStatus.Completed, result.Status);
        // Exactly 3 orders total: [0] LongEntry (bar0->fills bar1), [1] ShortEntry (bar1, from Evaluate -
        // submitted first each Step 7), [2] LongExit (bar1, from EvaluateExit - submitted second).
        Assert.Equal(3, result.Orders.Length);
        Assert.Equal(3, result.Fills.Length); // LongEntry@bar1, then (Exit-before-Entry, section 2.4) LongExit@bar2, ShortEntry@bar2
        Assert.Single(result.Trades); // the closed Long round-trip (entry bar1, exit bar2)

        BacktestOrder shortEntryOrder = result.Orders[1];
        Assert.Equal(OrderSide.Sell, shortEntryOrder.Side);
        Assert.Equal(1, shortEntryOrder.SubmittedBar);
        Assert.Equal(OrderStatus.Filled, shortEntryOrder.Status); // proves the post-exit Cash (not the stale pre-exit Cash=0) funded this entry

        BacktestTrade closedLongTrade = Assert.Single(result.Trades);
        Assert.Equal(TradeSide.Long, closedLongTrade.Side);
        Assert.Equal(100m, closedLongTrade.EntryPrice);
        Assert.Equal(100m, closedLongTrade.ExitPrice);
    }

    [Fact]
    public void EntryWithoutAccompanyingExit_NeverEvaluatedWhileStillHoldingTheOldSide_ProvingTheAboveIsNotACoincidence()
    {
        // Contrast case: the exact same reversal-entry request, but WITHOUT its accompanying exit this
        // time - nothing ever closes the original Long, so Q never returns to 0. Per section 2.4's design,
        // the pending Entry slot is only ever considered once the account is actually Flat: it must sit
        // pending indefinitely (never filled, never rejected) rather than being evaluated against the
        // still-open position's stale Cash - proving the success above genuinely depends on the account
        // having become Flat first, not some unrelated change in fund-check math. The run ends with the
        // order still pending, so the end-of-run "expire whatever never filled" cleanup (Task #8's
        // pre-existing EndOfData path) is what finally closes it out.
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            FlatBar(0, 100m),
            FlatBar(1, 100m),
            FlatBar(2, 100m));

        var strategy = new ScriptedStrategy(
            primary: new Dictionary<int, StrategyOrderRequest>
            {
                [0] = Req(SignalType.LongEntry, "open-long"),
                [1] = Req(SignalType.ShortEntry, "reversal-open-short-no-exit"),
            });

        BacktestResult result = VerificationHarness.CreateEngine().Run(MakeInput(bars), MakeConfig(), strategy);

        BacktestOrder shortEntryOrder = Assert.Single(result.Orders, o => o.SubmittedBar == 1);
        Assert.Equal(OrderStatus.Expired, shortEntryOrder.Status);
        Assert.Equal(ExpiredReason.EndOfData, shortEntryOrder.ExpiredReason);
        Assert.Empty(result.Trades); // the original Long position is still open - never closed
        Assert.DoesNotContain(result.Fills, f => f.OrderId == shortEntryOrder.OrderId); // never filled
    }

    [Fact]
    public void PerTypeOrderCountGuard_SecondSameTypeSignalTheSameBarIsSilentlyIgnored()
    {
        // Plan section 2.3's "never two of the same type" rule: a (mis-scripted/adversarial) strategy that
        // returns an Entry-type signal from BOTH Evaluate() and EvaluateExit() on the same bar must not be
        // able to create two simultaneous Entry-type pending orders - EvaluateExit's own contract says its
        // result should always be Exit-type, so the engine defensively ignores anything else it returns.
        ImmutableArray<CandleData> bars = ImmutableArray.Create(FlatBar(0, 100m), FlatBar(1, 100m));

        var strategy = new ScriptedStrategy(
            primary: new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.LongEntry, "primary") },
            accompanyingExit: new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.LongEntry, "misbehaving-duplicate") });

        BacktestResult result = VerificationHarness.CreateEngine().Run(MakeInput(bars), MakeConfig(), strategy);

        // Only ONE order was ever submitted (from Evaluate()) - EvaluateExit()'s non-Exit-type result was
        // defensively discarded by the engine before it could reach SubmitOrderFromSignal at all.
        Assert.Single(result.Orders);
    }

    [Fact]
    public void PerTypeOrderCountGuard_TwoExitTypeSignalsSameBar_SecondIsSilentlyDropped()
    {
        // Same rule, the other direction: if a strategy's Evaluate() itself already produced an Exit-type
        // signal this bar (the ordinary non-reversal exit path) and EvaluateExit() ALSO fires, the second
        // Exit-type signal must not create a second pending Exit order - it silently no-ops (no Order row
        // added), exactly like the old single-slot "still-pending order is never overwritten" rule.
        ImmutableArray<CandleData> bars = ImmutableArray.Create(
            FlatBar(0, 100m),
            FlatBar(1, 100m),  // LongEntry fills here
            FlatBar(2, 100m)); // both scripted LongExit signals target this same bar's Exit slot

        var strategy = new ScriptedStrategy(
            primary: new Dictionary<int, StrategyOrderRequest>
            {
                [0] = Req(SignalType.LongEntry, "open-long"),
                [1] = Req(SignalType.LongExit, "primary-exit"),
            },
            accompanyingExit: new Dictionary<int, StrategyOrderRequest>
            {
                [1] = Req(SignalType.LongExit, "duplicate-exit"),
            });

        BacktestResult result = VerificationHarness.CreateEngine().Run(MakeInput(bars), MakeConfig(), strategy);

        Assert.Equal(2, result.Orders.Length); // LongEntry + exactly one LongExit (the duplicate never became an Order row)
        Assert.Single(result.Trades);
    }
}
