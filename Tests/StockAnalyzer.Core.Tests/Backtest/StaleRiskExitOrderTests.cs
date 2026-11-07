#nullable enable
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Tests.Backtest.Verification;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

/// <summary>
/// The stale risk-exit order (docs/sa_ai_context_McpPart2WebAiReview, T1/T2, Q01, owner decision D-1 = A, BacktestExecutionSemantics.Version 4).
/// A Stop / Limit breach order is submitted at the Close of bar t for bar t+1 and is GTC. When bar t+1 does not reach its price the strategy cancels it
/// at the Close of bar t+1 (the engine applies the cancellation at Step 1 of bar t+2), so it no longer occupies the single Exit slot for good.
/// A breach found at the Close of bar t+1 cannot be submitted (the slot is still occupied), so the replacement order is submitted at the Close of
/// bar t+2 and fills from bar t+3: that one-bar delay is part of the rule. Before the fix the position below was never closed (T1 probe, 2026-11-04).
/// </summary>
public class StaleRiskExitOrderTests
{
    private static BacktestConditionEntry LongEntryCloseAbove(decimal threshold) => new()
    {
        Left = new BacktestConditionSide { IndicatorType = IndicatorType.Price, PriceSource = PriceType.Close },
        Operator = ComparisonOperator.GreaterThan,
        TargetMode = RightHandTargetMode.NumericValue,
        RightNumericValue = threshold,
        Role = BacktestConditionRole.EntryOnly,
        Position = TradeSide.Long,
    };

    private static BacktestConditionEntry ShortEntryCloseBelow(decimal threshold) => new()
    {
        Left = new BacktestConditionSide { IndicatorType = IndicatorType.Price, PriceSource = PriceType.Close },
        Operator = ComparisonOperator.LessThan,
        TargetMode = RightHandTargetMode.NumericValue,
        RightNumericValue = threshold,
        Role = BacktestConditionRole.EntryOnly,
        Position = TradeSide.Short,
    };

    private static readonly BacktestRiskManagementSettings StopAndTarget = new() { StopLossPercent = 0.05m, TakeProfitPercent = 0.10m };

    private static BacktestResult Run(ImmutableArray<CandleData> bars, BacktestRiskManagementSettings risk, BacktestConditionEntry? entry = null)
    {
        var input = VerificationHarness.MakeInput(bars);
        var strategy = new ConditionBasedBacktestStrategy(new[] { entry ?? LongEntryCloseAbove(102m) }, risk);
        return VerificationHarness.Run(input, VerificationHarness.MakeConfig(), strategy);
    }

    private static ImmutableArray<CandleData> Table(params (decimal Open, decimal High, decimal Low, decimal Close)[] rows)
        => SyntheticBars.FromTable(rows);

    // Long head shared by the scenarios: bar 1 closes above 102 (entry signal), bar 2 opens at 100 (entry fill at 100: SL 5 % = 95, TP 10 % = 110).
    // Bar 2 touches neither level. Bar 3 is the breach bar of each scenario.

    [Fact]
    public void Control_TakeProfitBreachFollowedByAGapThrough_ExitsOnTheNextBar_WithoutAnyCancellation()
    {
        // Bar 3: High 112 reaches TP 110 -> Limit sell 110 pending for bar 4. Bar 4 opens at 111 (through the limit) -> filled on bar 4.
        BacktestResult result = Run(
            Table(
                (100m, 101m, 99m, 100m),   // 0
                (100m, 106m, 99m, 105m),   // 1 entry signal
                (100m, 102m, 99m, 101m),   // 2 entry fill at 100
                (101m, 112m, 100m, 108m),  // 3 TP level touched
                (111m, 112m, 109m, 110m),  // 4 opens through the limit
                (110m, 111m, 108m, 109m)), // 5
            StopAndTarget);

        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.Equal(2, trade.EntryBar);
        Assert.Equal(4, trade.ExitBar);
        Assert.Equal("Take-Profit breach", trade.ExitReason);
        Assert.DoesNotContain(result.Orders, order => order.Status == OrderStatus.Cancelled); // V6: a run without a bounce is untouched
        Assert.Equal(
            new[] { (OrderType.Market, OrderStatus.Filled), (OrderType.Limit, OrderStatus.Filled) },
            result.Orders.Take(2).Select(order => (order.Type, order.Status)).ToArray()); // (a later re-entry order, if any, is not part of this check)
    }

    [Fact]
    public void V1_TakeProfitTouchedThenGapDownThroughStopLoss_StopLossIsIssuedAtTheCloseOfBarTPlus2_AndFillsFromBarTPlus3()
    {
        // t = 3. Bar 3 touches TP (Limit 110 pending). Bar 4 gaps down through the stop level 95 and does not reach 110: the Limit is cancelled at the
        // Close of bar 4 (applied at Step 1 of bar 5); the stop breach of bar 4 cannot be submitted (slot occupied). Bar 5 is still below the stop:
        // the Stop is submitted at the Close of bar 5 and bar 6 opens at 85 (through 95) -> exit on bar 6 at the Open.
        BacktestResult result = Run(
            Table(
                (100m, 101m, 99m, 100m),   // 0
                (100m, 106m, 99m, 105m),   // 1 entry signal
                (100m, 102m, 99m, 101m),   // 2 entry fill at 100
                (101m, 112m, 100m, 108m),  // 3 TP level touched -> Limit 110 pending
                (90m, 92m, 85m, 88m),      // 4 gaps through the stop level, does not reach 110 -> Limit cancelled at this Close
                (88m, 89m, 84m, 85m),      // 5 cancellation applied; stop breach -> Stop 95 submitted at this Close
                (85m, 86m, 80m, 81m)),     // 6 opens through the stop -> filled at the Open
            StopAndTarget);

        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.Equal(6, trade.ExitBar);
        Assert.Equal(85m, trade.ExitPrice);
        Assert.Equal("Stop-Loss breach", trade.ExitReason);
        Assert.Equal(95.00m, trade.StopLevelAtExit);
        Assert.Equal(
            new[] { (OrderType.Market, OrderStatus.Filled), (OrderType.Limit, OrderStatus.Cancelled), (OrderType.Stop, OrderStatus.Filled) },
            result.Orders.Select(order => (order.Type, order.Status)).ToArray());
    }

    [Fact]
    public void V2_AfterTheCancellation_ABarThatReboundsAboveTheStop_LeavesNoExitAndNoPendingOrder()
    {
        // Same as V1 up to bar 4; bar 5 rebounds above the stop level (Low 96 > 95) and stays below 110: nothing breaches, the first order stays Cancelled
        // and no new order is created. (Without the fix the Limit would still be pending and keep blocking.)
        BacktestResult result = Run(
            Table(
                (100m, 101m, 99m, 100m),   // 0
                (100m, 106m, 99m, 105m),   // 1 entry signal
                (100m, 102m, 99m, 101m),   // 2 entry fill at 100
                (101m, 112m, 100m, 108m),  // 3 TP level touched -> Limit 110 pending
                (90m, 92m, 85m, 88m),      // 4 gaps through the stop level; the stop breach cannot be submitted, the Limit is cancelled
                (97m, 104m, 96m, 100m),    // 5 rebounds above the stop level
                (100m, 103m, 98m, 101m)),  // 6
            StopAndTarget);

        Assert.Empty(result.Trades);
        Assert.Equal(
            new[] { (OrderType.Market, OrderStatus.Filled), (OrderType.Limit, OrderStatus.Cancelled) },
            result.Orders.Select(order => (order.Type, order.Status)).ToArray());
    }

    [Fact]
    public void V3_TimeStopDueWhileTheTakeProfitLimitIsStale_IsIssuedAtTheCloseOfBarTPlus2()
    {
        // maxHoldingBars = 2 (entry bar 2: due from the Close of bar 4). Bar 3 touches TP; bar 4 does not reach it: the Limit is cancelled at the Close
        // of bar 4 and the time stop of bar 4 is dropped (slot occupied). At the Close of bar 5 the slot is free: Market exit, filled at the Open of bar 6.
        BacktestResult result = Run(
            Table(
                (100m, 101m, 99m, 100m),   // 0
                (100m, 106m, 99m, 105m),   // 1 entry signal
                (100m, 102m, 99m, 101m),   // 2 entry fill at 100
                (101m, 112m, 100m, 108m),  // 3 TP level touched -> Limit 110 pending
                (105m, 106m, 101m, 103m),  // 4 does not reach 110; heldBars = 2 -> Time Stop due but dropped; Limit cancelled
                (103m, 104m, 101m, 102m),  // 5 Time Stop issued at this Close
                (102m, 103m, 100m, 101m)), // 6 filled at the Open
            new BacktestRiskManagementSettings
            {
                TakeProfitPercent = 0.10m,
                DynamicExits = new DynamicExitConfiguration { MaxHoldingBars = 2 },
            });

        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.Equal(6, trade.ExitBar);
        Assert.Equal(102m, trade.ExitPrice);
        Assert.Equal("Time Stop", trade.ExitReason);
        Assert.Contains(result.Orders, order => order.Type == OrderType.Limit && order.Status == OrderStatus.Cancelled);
    }

    [Fact]
    public void V4_ATakeProfitBreachAfterTheBounce_IsAcceptedAgain()
    {
        // Bar 3 touches TP, bar 4 does not reach it (Limit cancelled), bar 5 touches TP again: a new Limit is submitted at its Close and bar 6 opens
        // through it -> exit on bar 6 at the Open.
        BacktestResult result = Run(
            Table(
                (100m, 101m, 99m, 100m),   // 0
                (100m, 106m, 99m, 105m),   // 1 entry signal
                (100m, 102m, 99m, 101m),   // 2 entry fill at 100
                (101m, 112m, 100m, 108m),  // 3 TP level touched -> Limit 110 pending
                (105m, 106m, 101m, 103m),  // 4 does not reach 110 -> Limit cancelled at this Close
                (104m, 115m, 103m, 112m),  // 5 touches TP again -> new Limit 110 submitted at this Close
                (112m, 114m, 111m, 113m)), // 6 opens through the limit
            StopAndTarget);

        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.Equal(6, trade.ExitBar);
        Assert.Equal("Take-Profit breach", trade.ExitReason);
        Assert.Equal(
            new[] { (OrderType.Market, OrderStatus.Filled), (OrderType.Limit, OrderStatus.Cancelled), (OrderType.Limit, OrderStatus.Filled) },
            result.Orders.Take(3).Select(order => (order.Type, order.Status)).ToArray());
    }

    [Fact]
    public void ABarThatTouchesTheOrderPriceExactly_FillsItAndIsNotCancelled()
    {
        // Bar 3 breaches the stop (Low 94 <= 95; Close 100): Stop sell 95 pending. Bar 4 touches 95 exactly (Low 95): the engine fills on an inclusive
        // touch, so the strategy must not cancel it.
        BacktestResult result = Run(
            Table(
                (100m, 101m, 99m, 100m),   // 0
                (100m, 106m, 99m, 105m),   // 1 entry signal
                (100m, 102m, 99m, 101m),   // 2 entry fill at 100
                (101m, 104m, 94m, 100m),   // 3 stop level breached -> Stop 95 pending
                (98m, 99m, 95m, 97m),      // 4 Low == 95: touched
                (97m, 98m, 96m, 97m)),     // 5
            StopAndTarget);

        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.Equal(4, trade.ExitBar);
        Assert.Equal(95m, trade.ExitPrice);
        Assert.DoesNotContain(result.Orders, order => order.Status == OrderStatus.Cancelled);
    }

    [Fact]
    public void ShortPosition_BuyLimitThatIsNotReached_IsCancelled_AndALaterStopBreachExits()
    {
        // Short entry at 100: SL 5 % = 105 (Buy Stop), TP 10 % = 90 (Buy Limit). Bar 3 touches 90 (Limit pending); bar 4 stays above 90 -> cancelled;
        // bar 5 breaches 105 -> Stop submitted; bar 6 opens at 109 (through 105) -> exit at the Open.
        BacktestResult result = Run(
            Table(
                (100m, 101m, 99m, 100m),   // 0
                (100m, 101m, 94m, 95m),    // 1 entry signal (Close < 98)
                (100m, 101m, 99m, 100m),   // 2 short entry fill at 100
                (99m, 100m, 88m, 92m),     // 3 touches 90 -> Buy Limit 90 pending
                (95m, 98m, 93m, 96m),      // 4 does not reach 90 -> cancelled at this Close
                (96m, 110m, 95m, 108m),    // 5 breaches 105 -> Buy Stop 105 submitted
                (109m, 112m, 107m, 110m)), // 6 opens through the stop
            StopAndTarget,
            ShortEntryCloseBelow(98m));

        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.Equal(TradeSide.Short, trade.Side);
        Assert.Equal(6, trade.ExitBar);
        Assert.Equal(109m, trade.ExitPrice);
        Assert.Equal("Stop-Loss breach", trade.ExitReason);
        Assert.Contains(result.Orders, order => order.Type == OrderType.Limit && order.Status == OrderStatus.Cancelled);
    }
}
