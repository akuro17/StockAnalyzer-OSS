#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Tests.Backtest.Verification;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

/// <summary>
/// T8 (A1 (Order cancellation), owner decision G4): an optional strategy capability queues a cancellation at Close i,
/// the engine applies it at Step 1 of bar i+1 (before GTD expiry and fills), the slot is NOT freed during Close i's own submissions, a replacement can be
/// submitted at Close i+1 (fills from bar i+2), and a cancellation queued at the final Close is never applied.
/// </summary>
public class BacktestEngineOrderCancellationTests
{
    private static readonly DateTime Bar0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeInForce Gtc = new(true, -1);

    private static CandleData Flat(int dayOffset, decimal price) => new(Bar0.AddDays(dayOffset), price, price, price, price, 1000);

    private static CandleData Bar(int dayOffset, decimal open, decimal high, decimal low, decimal close) => new(Bar0.AddDays(dayOffset), open, high, low, close, 1000);

    /// <summary>Full-cash model (no leverage, no fees): a Long/Short of 1 unit needs its whole notional.</summary>
    private static BacktestConfiguration CashConfig() => new()
    {
        InitialCapital = 1000m,
        SizingModel = PositionSizingModel.FixedQuantity,
        SizingParameter = 1m,
        InitialMarginRatio = 1m,
        MaintenanceMarginRatio = 0.5m,
    };

    private static StrategyOrderRequest Req(SignalType type, OrderType orderType = OrderType.Market, decimal? limitPrice = null, decimal? stopPrice = null, TimeInForce? tif = null)
        => new(type, orderType, limitPrice, stopPrice, tif ?? Gtc, "r");

    private static BacktestInput InputOf(ImmutableArray<CandleData> bars) => new(bars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion, Bar0, Bar0.AddDays(bars.Length + 1), 0, 0);

    private static BacktestResult Run(IBacktestStrategy strategy, ImmutableArray<CandleData> bars, CancellationToken token = default)
        => VerificationHarness.CreateEngine().Run(InputOf(bars), CashConfig(), strategy, token);

    private static OrderCancellationIntent CancelEntryAtBar(StrategyContext context, int barIndex)
        => context.BarIndex == barIndex && context.PendingEntryOrderId is { } id ? new OrderCancellationIntent(id, null) : OrderCancellationIntent.None;

    [Fact]
    public void PendingLimitEntry_CancelledBeforeItsTouch_NeverFills_EvenWhenTheNextBarWouldTouchIt()
    {
        // Buy Limit 90 submitted at Close 0 (order 1). Cancellation queued at Close 1 is applied at Step 1 of bar 2 - before bar 2's Low of 85 could fill it.
        var strategy = new CancellingStrategy(
            new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.LongEntry, OrderType.Limit, limitPrice: 90m) },
            context => CancelEntryAtBar(context, 1));

        BacktestResult result = Run(strategy, ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m), Bar(2, 100m, 100m, 85m, 100m), Flat(3, 100m)));

        Assert.Empty(result.Fills);
        BacktestOrder order = Assert.Single(result.Orders);
        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Null(order.ExpiredReason);
    }

    [Fact]
    public void AnIdNotYetPendingAtTheClose_IsIgnored_EvenIfTheOrderIsSubmittedAtThatSameClose()
    {
        // At Close 0 the strategy names id 1 - a guess: the order is only submitted AFTER the capability call, so it was not pending at that Close and
        // the intent must not reach it. The Limit stays live and fills on bar 1.
        var strategy = new CancellingStrategy(
            new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.LongEntry, OrderType.Limit, limitPrice: 90m) },
            context => context.BarIndex == 0 ? new OrderCancellationIntent(1L, null) : OrderCancellationIntent.None);

        BacktestResult result = Run(strategy, ImmutableArray.Create(Flat(0, 100m), Bar(1, 100m, 100m, 85m, 100m), Flat(2, 100m)));

        Assert.Single(result.Fills); // id 1 was not pending at Close 0, so nothing was cancelled and the order filled on bar 1
        Assert.Equal(OrderStatus.Filled, result.Orders[0].Status);
    }

    [Fact]
    public void CancellationBeatsGtdExpiry_TheOrderEndsCancelledNotExpired()
    {
        // Limit 90 with ExpiryBar 2 is pending at Close 1; the cancellation is applied at Step 1 of bar 2 (the expiry check only expires when i > ExpiryBar,
        // which would be bar 3). Queue the cancellation at Close 2 instead: applied at Step 1 of bar 3, BEFORE the expiry check that would expire it there.
        var strategy = new CancellingStrategy(
            new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.LongEntry, OrderType.Limit, limitPrice: 90m, tif: new TimeInForce(false, 2)) },
            context => CancelEntryAtBar(context, 2));

        BacktestResult result = Run(strategy, ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m), Flat(2, 100m), Flat(3, 100m)));

        BacktestOrder order = Assert.Single(result.Orders);
        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Null(order.ExpiredReason);
    }

    [Fact]
    public void UnknownDuplicateAndTerminalIds_ChangeNothing()
    {
        // Named: an unknown id (999), the pending order's id twice, and (on a later bar) the id of the already-filled order.
        long? firstOrderId = null;
        var strategy = new CancellingStrategy(
            new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.LongEntry) },
            context =>
            {
                if (context.BarIndex == 0) return new OrderCancellationIntent(999L, 999L);
                if (context.BarIndex == 1 && context.PendingOrderId is { } id) firstOrderId = id;
                return new OrderCancellationIntent(1L, 1L); // order 1 is Filled by bar 1's Open: terminal, no slot names it any more
            });

        BacktestResult result = Run(strategy, ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m), Flat(2, 100m)));

        Assert.Null(firstOrderId);
        BacktestOrder order = Assert.Single(result.Orders);
        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Single(result.Fills);
    }

    [Fact]
    public void BothSlots_CanBeCancelledInOneIntent()
    {
        // Long held from bar 1's Open. At Close 1: LongExit Limit 200 (Exit slot) and ShortEntry Limit 300 (Entry slot) are submitted; at Close 2 the strategy
        // names both pending ids; both end Cancelled and neither fills on bar 3 even though bar 3's High of 400 would touch both.
        var strategy = new CancellingStrategy(
            new Dictionary<int, StrategyOrderRequest>
            {
                [0] = Req(SignalType.LongEntry),
                [1] = Req(SignalType.ShortEntry, OrderType.Limit, limitPrice: 300m),
            },
            context => context.BarIndex == 2 && context.PendingEntryOrderId is { } entryId && context.PendingExitOrderId is { } exitId
                ? new OrderCancellationIntent(entryId, exitId)
                : OrderCancellationIntent.None,
            new Dictionary<int, StrategyOrderRequest> { [1] = Req(SignalType.LongExit, OrderType.Limit, limitPrice: 200m) });

        BacktestResult result = Run(strategy, ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m), Flat(2, 100m), Bar(3, 100m, 400m, 100m, 100m)));

        Assert.Equal(3, result.Orders.Length);
        Assert.Equal(OrderStatus.Cancelled, result.Orders[1].Status);
        Assert.Equal(OrderStatus.Cancelled, result.Orders[2].Status);
        Assert.Single(result.Fills); // only the initial long entry
        Assert.Empty(result.Trades);
    }

    [Fact]
    public void CancelAndReplace_TheSlotStaysOccupiedAtCloseI_TheReplacementIsSubmittedAtCloseIPlusOneAndFillsFromBarIPlusTwo()
    {
        // Buy Limit 90 (order 1) submitted at Close 0. At Close 1 the strategy cancels it AND, in the same call, requests a replacement Buy Limit 95.
        // The slot is still occupied at Close 1, so the replacement is ignored (no order row). The cancellation is applied at Step 1 of bar 2; the strategy
        // resubmits at Close 2 (order 2, EarliestFillBar 3) and it fills on bar 3.
        var strategy = new CancellingStrategy(
            new Dictionary<int, StrategyOrderRequest>
            {
                [0] = Req(SignalType.LongEntry, OrderType.Limit, limitPrice: 90m),
                [1] = Req(SignalType.LongEntry, OrderType.Limit, limitPrice: 95m),
                [2] = Req(SignalType.LongEntry, OrderType.Limit, limitPrice: 95m),
            },
            context => CancelEntryAtBar(context, 1));

        BacktestResult result = Run(strategy, ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m), Bar(2, 100m, 100m, 92m, 100m), Bar(3, 100m, 100m, 94m, 100m)));

        Assert.Equal(2, result.Orders.Length); // the same-Close replacement produced no order row
        Assert.Equal(OrderStatus.Cancelled, result.Orders[0].Status);
        BacktestOrder replacement = result.Orders[1];
        Assert.Equal(2, replacement.SubmittedBar);
        Assert.Equal(3, replacement.EarliestFillBar);
        Assert.Equal(OrderStatus.Filled, replacement.Status);
        BacktestFill fill = Assert.Single(result.Fills);
        Assert.Equal(3, fill.BarIndex); // bar 2's Low of 92 (which would touch 95) cannot fill it: it does not exist yet
        Assert.Equal(95m, fill.Price);
    }

    [Fact]
    public void CancellationQueuedAtTheFinalClose_IsNeverApplied_ButEndOfDataStillExpiresTheOrder()
    {
        var strategy = new CancellingStrategy(
            new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.LongEntry, OrderType.Limit, limitPrice: 90m) },
            context => CancelEntryAtBar(context, 2));

        BacktestResult result = Run(strategy, ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m), Flat(2, 100m)));

        BacktestOrder order = Assert.Single(result.Orders);
        Assert.Equal(OrderStatus.Expired, order.Status);
        Assert.Equal(ExpiredReason.EndOfData, order.ExpiredReason);
    }

    [Fact]
    public void InvocationCounts_OfEvaluateAndEvaluateExit_AreUnchanged_AndTheCapabilityIsCalledOncePerActiveBar()
    {
        var strategy = new CancellingStrategy(new Dictionary<int, StrategyOrderRequest>(), _ => OrderCancellationIntent.None);

        Run(strategy, ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m), Flat(2, 100m), Flat(3, 100m)));

        Assert.Equal(4, strategy.EvaluateCalls);
        Assert.Equal(4, strategy.EvaluateExitCalls);
        Assert.Equal(4, strategy.CancelCalls);
    }

    [Fact]
    public void StrategyWithoutTheCapability_ProducesTheSameResultAsBefore()
    {
        var script = new Dictionary<int, StrategyOrderRequest>
        {
            [0] = Req(SignalType.LongEntry),
            [2] = Req(SignalType.LongExit),
        };
        ImmutableArray<CandleData> bars = ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m), Flat(2, 110m), Flat(3, 110m));

        BacktestResult plain = Run(new ScriptedStrategy(script), bars);
        BacktestResult capable = Run(new CancellingStrategy(script, _ => OrderCancellationIntent.None), bars);

        Assert.Equal(plain.ReproducibilityHash, capable.ReproducibilityHash);
        Assert.True(plain.Orders.SequenceEqual(capable.Orders));
        Assert.True(plain.Fills.SequenceEqual(capable.Fills));
    }

    [Fact]
    public void RunCancellationToken_IsIndependent_StillThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var strategy = new CancellingStrategy(new Dictionary<int, StrategyOrderRequest>(), _ => OrderCancellationIntent.None);

        Assert.Throws<OperationCanceledException>(() => Run(strategy, ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m)), cts.Token));
        Assert.Equal(0, strategy.CancelCalls);
    }
}
