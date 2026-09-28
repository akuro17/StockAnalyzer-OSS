using System;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

public class OrderFillEvaluatorTests
{
    private static readonly DateTime Bar0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static CandleData Bar(decimal open, decimal high, decimal low, decimal close)
        => new(Bar0, open, high, low, close, 1000);

    private static BacktestOrder MakeOrder(
        OrderSide side, OrderType type, decimal quantity = 10m,
        decimal? limitPrice = null, decimal? stopPrice = null, bool stopActivated = false)
        => new(
            OrderId: 1, Side: side, Type: type,
            Quantity: quantity, LimitPrice: limitPrice, StopPrice: stopPrice,
            Status: OrderStatus.Submitted, TimeInForce: new TimeInForce(true, -1),
            SubmittedBar: 0, EarliestFillBar: 1,
            StopActivated: stopActivated,
            ExpiredReason: null, RejectedReason: null);

    // ---- Market ----

    [Fact]
    public void Market_Buy_FillsAtOpen_WithSlippage()
    {
        var order = MakeOrder(OrderSide.Buy, OrderType.Market, quantity: 10m);
        var bar = Bar(100m, 110m, 95m, 105m);
        var outcome = OrderFillEvaluator.Evaluate(order, bar, slippageRatio: 0.01m, commissionFlat: 1m, commissionPerUnit: 0.1m);

        Assert.True(outcome.Filled);
        Assert.Equal(101m, outcome.FillPrice); // 100 * 1.01
        Assert.Equal(2m, outcome.Commission); // 1 + 0.1*10
        Assert.Equal(10m, outcome.SlippageAmount); // |101-100|*10
    }

    [Fact]
    public void Market_Sell_FillsAtOpen_WithSlippage()
    {
        var order = MakeOrder(OrderSide.Sell, OrderType.Market, quantity: 10m);
        var bar = Bar(100m, 110m, 95m, 105m);
        var outcome = OrderFillEvaluator.Evaluate(order, bar, slippageRatio: 0.01m, commissionFlat: 0m, commissionPerUnit: 0m);

        Assert.True(outcome.Filled);
        Assert.Equal(99m, outcome.FillPrice); // 100 * 0.99
    }

    // ---- MarketOnClose ----

    [Fact]
    public void MarketOnClose_FillsAtClose()
    {
        var order = MakeOrder(OrderSide.Buy, OrderType.MarketOnClose, quantity: 5m);
        var bar = Bar(100m, 110m, 95m, 108m);
        var outcome = OrderFillEvaluator.EvaluateMarketOnClose(order, bar, slippageRatio: 0m, commissionFlat: 0m, commissionPerUnit: 0m);

        Assert.True(outcome.Filled);
        Assert.Equal(108m, outcome.FillPrice);
    }

    [Fact]
    public void MarketOnClose_NonNullLimitPrice_Throws()
    {
        var order = MakeOrder(OrderSide.Buy, OrderType.MarketOnClose, limitPrice: 100m);
        var bar = Bar(100m, 110m, 95m, 108m);
        Assert.Throws<ArgumentException>(() => OrderFillEvaluator.EvaluateMarketOnClose(order, bar, 0m, 0m, 0m));
    }

    [Fact]
    public void Evaluate_MarketOnCloseType_Throws()
    {
        var order = MakeOrder(OrderSide.Buy, OrderType.MarketOnClose);
        var bar = Bar(100m, 110m, 95m, 108m);
        Assert.Throws<ArgumentException>(() => OrderFillEvaluator.Evaluate(order, bar, 0m, 0m, 0m));
    }

    // ---- Limit ----

    [Fact]
    public void LimitBuy_OpenBelowLimit_FillsAtOpen()
    {
        var order = MakeOrder(OrderSide.Buy, OrderType.Limit, limitPrice: 102m);
        var bar = Bar(open: 100m, high: 105m, low: 98m, close: 101m);
        var outcome = OrderFillEvaluator.Evaluate(order, bar, 0m, 0m, 0m);

        Assert.True(outcome.Filled);
        Assert.Equal(100m, outcome.FillPrice);
    }

    [Fact]
    public void LimitBuy_BarLowBelowLimit_FillsAtLimit()
    {
        var order = MakeOrder(OrderSide.Buy, OrderType.Limit, limitPrice: 97m);
        var bar = Bar(open: 100m, high: 105m, low: 95m, close: 99m);
        var outcome = OrderFillEvaluator.Evaluate(order, bar, 0m, 0m, 0m);

        Assert.True(outcome.Filled);
        Assert.Equal(97m, outcome.FillPrice);
    }

    [Fact]
    public void LimitBuy_BarLowAboveLimit_NoFill()
    {
        var order = MakeOrder(OrderSide.Buy, OrderType.Limit, limitPrice: 90m);
        var bar = Bar(open: 100m, high: 105m, low: 95m, close: 99m);
        var outcome = OrderFillEvaluator.Evaluate(order, bar, 0m, 0m, 0m);

        Assert.False(outcome.Filled);
    }

    [Fact]
    public void LimitBuy_SlippagePushesAboveLimit_ClampedToLimit()
    {
        // Open=100 <= Limit=102 -> P0=Open=100. Slippage 5% -> 105, clamped back down to 102.
        var order = MakeOrder(OrderSide.Buy, OrderType.Limit, limitPrice: 102m);
        var bar = Bar(open: 100m, high: 106m, low: 98m, close: 101m);
        var outcome = OrderFillEvaluator.Evaluate(order, bar, slippageRatio: 0.05m, 0m, 0m);

        Assert.True(outcome.Filled);
        Assert.Equal(102m, outcome.FillPrice);
    }

    [Fact]
    public void LimitSell_BarHighAboveLimit_FillsAtLimit()
    {
        var order = MakeOrder(OrderSide.Sell, OrderType.Limit, limitPrice: 108m);
        var bar = Bar(open: 100m, high: 110m, low: 98m, close: 102m);
        var outcome = OrderFillEvaluator.Evaluate(order, bar, 0m, 0m, 0m);

        Assert.True(outcome.Filled);
        Assert.Equal(108m, outcome.FillPrice);
    }

    // ---- Stop ----

    [Fact]
    public void StopBuy_GapOpen_FillsAtOpen()
    {
        var order = MakeOrder(OrderSide.Buy, OrderType.Stop, stopPrice: 100m);
        var bar = Bar(open: 103m, high: 106m, low: 101m, close: 104m);
        var outcome = OrderFillEvaluator.Evaluate(order, bar, 0m, 0m, 0m);

        Assert.True(outcome.Filled);
        Assert.Equal(103m, outcome.FillPrice); // Open already >= Stop
    }

    [Fact]
    public void StopBuy_TriggeredIntrabar_FillsAtStop_BecomesMarket_NoClamp()
    {
        var order = MakeOrder(OrderSide.Buy, OrderType.Stop, stopPrice: 105m);
        var bar = Bar(open: 100m, high: 110m, low: 98m, close: 106m);
        var outcome = OrderFillEvaluator.Evaluate(order, bar, slippageRatio: 0.02m, 0m, 0m);

        Assert.True(outcome.Filled);
        Assert.Equal(105m * 1.02m, outcome.FillPrice); // no clamp: allowed to execute worse than Stop
    }

    [Fact]
    public void StopBuy_NeverTouched_NoFill()
    {
        var order = MakeOrder(OrderSide.Buy, OrderType.Stop, stopPrice: 120m);
        var bar = Bar(open: 100m, high: 110m, low: 98m, close: 106m);
        var outcome = OrderFillEvaluator.Evaluate(order, bar, 0m, 0m, 0m);

        Assert.False(outcome.Filled);
    }

    // ---- StopLimit: Buy ----

    [Fact]
    public void BuyStopLimit_TriggerPriceAtOrBelowLimit_FillsImmediatelyAtTrigger()
    {
        // Stop=105 triggers intrabar; Limit=106 >= trigger price(105) -> immediate fill at 105.
        var order = MakeOrder(OrderSide.Buy, OrderType.StopLimit, stopPrice: 105m, limitPrice: 106m);
        var bar = Bar(open: 100m, high: 110m, low: 98m, close: 107m);
        var outcome = OrderFillEvaluator.Evaluate(order, bar, 0m, 0m, 0m);

        Assert.True(outcome.Filled);
        Assert.Equal(105m, outcome.FillPrice);
    }

    [Fact]
    public void BuyStopLimit_TriggerAboveLimit_LowReachesLimit_Fills()
    {
        var order = MakeOrder(OrderSide.Buy, OrderType.StopLimit, stopPrice: 105m, limitPrice: 102m);
        var bar = Bar(open: 100m, high: 110m, low: 99m, close: 103m);
        var outcome = OrderFillEvaluator.Evaluate(order, bar, 0m, 0m, 0m);

        Assert.True(outcome.Filled);
        Assert.Equal(102m, outcome.FillPrice);
    }

    [Fact]
    public void BuyStopLimit_TriggerAboveLimit_LowNeverReachesLimit_ActivatesWithoutFilling()
    {
        var order = MakeOrder(OrderSide.Buy, OrderType.StopLimit, stopPrice: 105m, limitPrice: 100m);
        var bar = Bar(open: 100m, high: 110m, low: 101m, close: 103m);
        var outcome = OrderFillEvaluator.Evaluate(order, bar, 0m, 0m, 0m);

        Assert.False(outcome.Filled);
        Assert.True(outcome.StopActivated);
    }

    [Fact]
    public void BuyStopLimit_NotTriggered_NoFill_NotActivated()
    {
        var order = MakeOrder(OrderSide.Buy, OrderType.StopLimit, stopPrice: 120m, limitPrice: 118m);
        var bar = Bar(open: 100m, high: 110m, low: 98m, close: 106m);
        var outcome = OrderFillEvaluator.Evaluate(order, bar, 0m, 0m, 0m);

        Assert.False(outcome.Filled);
        Assert.False(outcome.StopActivated);
    }

    [Fact]
    public void BuyStopLimit_AlreadyActivated_ScansFreshBarForLimit()
    {
        var order = MakeOrder(OrderSide.Buy, OrderType.StopLimit, stopPrice: 105m, limitPrice: 100m, stopActivated: true);
        var bar = Bar(open: 103m, high: 106m, low: 99m, close: 101m);
        var outcome = OrderFillEvaluator.Evaluate(order, bar, 0m, 0m, 0m);

        Assert.True(outcome.Filled);
        Assert.Equal(100m, outcome.FillPrice);
    }

    // ---- StopLimit: Sell (covers the OHLC-vs-OLHC-order-sensitive "recovery via Close" case) ----

    [Fact]
    public void SellStopLimit_TriggerAtOpen_ScansWholeBarIncludingHigh()
    {
        // Stop=95 triggers immediately at Open=94 (Open<=Stop). Limit=105 is above the trigger price,
        // so the whole bar (including the Open->High leg) is available: High=108 >= 105 -> fill at 105.
        var order = MakeOrder(OrderSide.Sell, OrderType.StopLimit, stopPrice: 95m, limitPrice: 105m);
        var bar = Bar(open: 94m, high: 108m, low: 90m, close: 100m);
        var outcome = OrderFillEvaluator.Evaluate(order, bar, 0m, 0m, 0m);

        Assert.True(outcome.Filled);
        Assert.Equal(105m, outcome.FillPrice);
    }

    [Fact]
    public void SellStopLimit_TriggeredPartwayDownHighLowLeg_HighAlreadyConsumed_NoFillEvenIfHighWasAboveLimit()
    {
        // Open=100 > Stop=95, so the Stop only triggers partway down the High->Low leg. Even though
        // this bar's High=110 is above Limit=105, that High occurred BEFORE the trigger and per spec
        // (bar extremes seen before the trigger point must never be reused for the fill) must not be
        // used. Close=100 < Limit=105, so no recovery back up to the limit either -> no fill this bar,
        // but the Stop leg is now activated.
        var order = MakeOrder(OrderSide.Sell, OrderType.StopLimit, stopPrice: 95m, limitPrice: 105m);
        var bar = Bar(open: 100m, high: 110m, low: 92m, close: 100m);
        var outcome = OrderFillEvaluator.Evaluate(order, bar, 0m, 0m, 0m);

        Assert.False(outcome.Filled);
        Assert.True(outcome.StopActivated);
    }

    [Fact]
    public void SellStopLimit_TriggeredPartwayDownHighLowLeg_RecoversToLimitViaClose_Fills()
    {
        // Same setup as above, but the bar recovers enough that Close >= Limit -> fills using the
        // Low->Close recovery leg (the only leg still available after the trigger consumed High).
        var order = MakeOrder(OrderSide.Sell, OrderType.StopLimit, stopPrice: 95m, limitPrice: 96m);
        var bar = Bar(open: 100m, high: 110m, low: 92m, close: 97m);
        var outcome = OrderFillEvaluator.Evaluate(order, bar, 0m, 0m, 0m);

        Assert.True(outcome.Filled);
        Assert.Equal(96m, outcome.FillPrice);
    }

    [Fact]
    public void SellStopLimit_TriggerPriceAtOrAboveLimit_FillsImmediatelyAtTrigger()
    {
        var order = MakeOrder(OrderSide.Sell, OrderType.StopLimit, stopPrice: 95m, limitPrice: 94m);
        var bar = Bar(open: 100m, high: 110m, low: 92m, close: 97m);
        var outcome = OrderFillEvaluator.Evaluate(order, bar, 0m, 0m, 0m);

        Assert.True(outcome.Filled);
        Assert.Equal(95m, outcome.FillPrice);
    }

    [Fact]
    public void SellStopLimit_AlreadyActivated_ScansFreshBarForLimit()
    {
        var order = MakeOrder(OrderSide.Sell, OrderType.StopLimit, stopPrice: 95m, limitPrice: 105m, stopActivated: true);
        var bar = Bar(open: 100m, high: 108m, low: 98m, close: 106m);
        var outcome = OrderFillEvaluator.Evaluate(order, bar, 0m, 0m, 0m);

        Assert.True(outcome.Filled);
        Assert.Equal(105m, outcome.FillPrice);
    }
}
