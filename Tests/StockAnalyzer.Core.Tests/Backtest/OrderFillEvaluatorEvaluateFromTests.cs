#nullable enable
using System;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

/// <summary>
/// T7b (A1 (lifetime and reversal chronology)): <see cref="OrderFillEvaluator.EvaluateFrom"/> from the Open is exactly
/// <see cref="OrderFillEvaluator.Evaluate"/> (every order type, side and StopLimit activation, with slippage and fees), and from a later start it scans only the
/// residual path.
/// </summary>
public class OrderFillEvaluatorEvaluateFromTests
{
    private static readonly DateTime Day0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeInForce Gtc = new(true, -1);

    private const decimal Slippage = 0.01m;
    private const decimal FlatFee = 1m;
    private const decimal PerUnitFee = 0.5m;

    private static CandleData Bar(decimal open, decimal high, decimal low, decimal close) => new(Day0, open, high, low, close, 1000);

    private static BacktestOrder Order(OrderSide side, OrderType type, decimal? limit = null, decimal? stop = null, bool stopActivated = false)
        => new(1, side, type, 3m, limit, stop, OrderStatus.Submitted, Gtc, 0, 1, stopActivated, null, null);

    [Fact]
    public void FromTheOpen_EqualsEvaluate_ForEveryOrderTypeSideAndActivation_OverAnExhaustiveGrid()
    {
        decimal[] grid = { 80m, 85m, 90.3m, 95m, 100m, 107.7m, 115m };
        int compared = 0;

        foreach (decimal low in grid)
        foreach (decimal high in grid)
        {
            if (high < low) continue;
            foreach (decimal open in grid)
            foreach (decimal close in grid)
            {
                if (open < low || open > high || close < low || close > high) continue;
                CandleData bar = Bar(open, high, low, close);
                PathPoint origin = FillPathScanner.OpenPoint(open);

                foreach (OrderSide side in new[] { OrderSide.Buy, OrderSide.Sell })
                {
                    Compare(Order(side, OrderType.Market), bar, origin);
                    foreach (decimal price in grid)
                    {
                        Compare(Order(side, OrderType.Limit, limit: price), bar, origin);
                        Compare(Order(side, OrderType.Stop, stop: price), bar, origin);
                        foreach (decimal limit in grid)
                        foreach (bool activated in new[] { false, true })
                        {
                            Compare(Order(side, OrderType.StopLimit, limit: limit, stop: price, stopActivated: activated), bar, origin);
                            compared++;
                        }
                    }
                }
            }
        }

        Assert.True(compared > 50_000, $"grid unexpectedly small: {compared}");

        static void Compare(BacktestOrder order, CandleData bar, PathPoint origin)
        {
            OrderFillOutcome legacy = OrderFillEvaluator.Evaluate(order, bar, Slippage, FlatFee, PerUnitFee);
            ResidualFill scanned = OrderFillEvaluator.EvaluateFrom(order, bar, origin, Slippage, FlatFee, PerUnitFee);

            string context = $"{order.Side} {order.Type} L={order.LimitPrice} S={order.StopPrice} act={order.StopActivated} O{bar.Open} H{bar.High} L{bar.Low} C{bar.Close}";
            Assert.True(legacy == scanned.Outcome, context);
            Assert.Equal(legacy.Filled, scanned.FillPoint.HasValue);
        }
    }

    [Fact]
    public void MarketOrder_FromALaterStart_DoesNotFill_ItsOnlyEventWasAtOpen()
    {
        CandleData bar = Bar(100m, 110m, 95m, 102m);

        ResidualFill later = OrderFillEvaluator.EvaluateFrom(Order(OrderSide.Sell, OrderType.Market), bar, new PathPoint(0.5m, 105m), 0m, 0m, 0m);
        ResidualFill atOpen = OrderFillEvaluator.EvaluateFrom(Order(OrderSide.Sell, OrderType.Market), bar, FillPathScanner.OpenPoint(bar.Open), 0m, 0m, 0m);

        Assert.False(later.Outcome.Filled);
        Assert.Null(later.FillPoint);
        Assert.True(atOpen.Outcome.Filled);
        Assert.Equal(100m, atOpen.Outcome.FillPrice);
    }

    [Fact]
    public void SellStop_GapAtOpenIsNotEligible_AfterALaterStart_ButALaterCrossingIs()
    {
        // Sell Stop 101 with Open 100: from the Open it fills at the Open (gap). From (0.5, 105) the Open gap is earlier, so only the
        // later High->Low crossing of 101 counts: 1 + (110 - 101) / (110 - 95).
        CandleData bar = Bar(100m, 110m, 95m, 102m);
        BacktestOrder order = Order(OrderSide.Sell, OrderType.Stop, stop: 101m);

        ResidualFill fromOpen = OrderFillEvaluator.EvaluateFrom(order, bar, FillPathScanner.OpenPoint(bar.Open), 0m, 0m, 0m);
        ResidualFill fromLater = OrderFillEvaluator.EvaluateFrom(order, bar, new PathPoint(0.5m, 105m), 0m, 0m, 0m);

        Assert.Equal(100m, fromOpen.Outcome.FillPrice);
        Assert.Equal(101m, fromLater.Outcome.FillPrice);
        Assert.Equal(new PathPoint(1m + (9m / 15m), 101m), fromLater.FillPoint);
    }

    [Fact]
    public void LimitAlreadyMarketableAtTheStart_FillsAtTheStartPrice()
    {
        // Sell Limit 102 while the market is already at 105 when the Exit frees the account: filled at the prevailing 105 (better than the limit).
        CandleData bar = Bar(100m, 110m, 95m, 102m);

        ResidualFill result = OrderFillEvaluator.EvaluateFrom(Order(OrderSide.Sell, OrderType.Limit, limit: 102m), bar, new PathPoint(0.5m, 105m), 0m, 0m, 0m);

        Assert.Equal(105m, result.Outcome.FillPrice);
        Assert.Equal(new PathPoint(0.5m, 105m), result.FillPoint);
    }

    [Fact]
    public void StopLimit_FromALaterStart_TriggerAndLimitBothLieOnTheResidualPath()
    {
        // Buy StopLimit S=108 L=100 from (0.5, 105): trigger at 0.8 on Open->High, then the High->Low leg reaches 100 at 1 + (110 - 100) / 15.
        CandleData bar = Bar(100m, 110m, 95m, 102m);

        ResidualFill result = OrderFillEvaluator.EvaluateFrom(Order(OrderSide.Buy, OrderType.StopLimit, limit: 100m, stop: 108m), bar, new PathPoint(0.5m, 105m), 0m, 0m, 0m);

        Assert.True(result.Outcome.Filled);
        Assert.True(result.Outcome.StopActivated);
        Assert.Equal(new PathPoint(1m + (10m / 15m), 100m), result.FillPoint);
    }

    [Fact]
    public void StopLimit_TriggeredOnTheResidualPathButLimitNeverReached_ReportsActivationOnly()
    {
        CandleData bar = Bar(100m, 110m, 95m, 102m);

        ResidualFill result = OrderFillEvaluator.EvaluateFrom(Order(OrderSide.Buy, OrderType.StopLimit, limit: 90m, stop: 108m), bar, new PathPoint(0.5m, 105m), 0m, 0m, 0m);

        Assert.False(result.Outcome.Filled);
        Assert.True(result.Outcome.StopActivated);
        Assert.Null(result.FillPoint);
    }

    [Fact]
    public void MarketOnClose_IsRejected()
    {
        CandleData bar = Bar(100m, 110m, 95m, 102m);

        Assert.Throws<ArgumentException>(() => OrderFillEvaluator.EvaluateFrom(Order(OrderSide.Buy, OrderType.MarketOnClose), bar, FillPathScanner.OpenPoint(bar.Open), 0m, 0m, 0m));
        Assert.Null(OrderFillEvaluator.TryFindFillPosition(Order(OrderSide.Buy, OrderType.MarketOnClose), bar));
    }
}
