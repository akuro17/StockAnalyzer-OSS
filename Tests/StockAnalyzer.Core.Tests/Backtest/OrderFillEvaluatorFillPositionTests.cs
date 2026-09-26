#nullable enable
using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

/// <summary>
/// T7a (A1 (StopLimit fill priority)): <see cref="OrderFillEvaluator.TryFindFillPosition"/> reports where an
/// order would ACTUALLY FILL, on the path scale [0,3]; <see cref="OrderFillEvaluator.TryFindCandidatePosition"/> keeps reporting the Stop trigger.
/// </summary>
public class OrderFillEvaluatorFillPositionTests
{
    private static readonly DateTime Day0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeInForce Gtc = new(true, -1);

    private static CandleData Bar(decimal open, decimal high, decimal low, decimal close) => new(Day0, open, high, low, close, 1000);

    private static BacktestOrder Order(OrderSide side, OrderType type, decimal? limit = null, decimal? stop = null, bool stopActivated = false)
        => new(1, side, type, 1m, limit, stop, OrderStatus.Submitted, Gtc, 0, 1, stopActivated, null, null);

    [Fact]
    public void MarketLimitStop_FillPositionEqualsCandidatePosition()
    {
        CandleData bar = Bar(95m, 105m, 85m, 92m);

        Assert.Equal(0m, OrderFillEvaluator.TryFindFillPosition(Order(OrderSide.Buy, OrderType.Market), bar));
        foreach (BacktestOrder order in new[]
        {
            Order(OrderSide.Buy, OrderType.Limit, limit: 90m),
            Order(OrderSide.Sell, OrderType.Limit, limit: 100m),
            Order(OrderSide.Buy, OrderType.Stop, stop: 100m),
            Order(OrderSide.Sell, OrderType.Stop, stop: 90m),
            Order(OrderSide.Sell, OrderType.Limit, limit: 200m),
        })
        {
            Assert.Equal(OrderFillEvaluator.TryFindCandidatePosition(order, bar), OrderFillEvaluator.TryFindFillPosition(order, bar));
        }
    }

    [Fact]
    public void ActivatedStopLimit_FillPositionEqualsLimitTouchPosition()
    {
        CandleData bar = Bar(93m, 101m, 92m, 99m);
        BacktestOrder activatedSell = Order(OrderSide.Sell, OrderType.StopLimit, limit: 100m, stop: 90m, stopActivated: true);

        Assert.Equal(0.875m, OrderFillEvaluator.TryFindFillPosition(activatedSell, bar)); // (100-93)/(101-93)
        Assert.Equal(OrderFillEvaluator.TryFindCandidatePosition(activatedSell, bar), OrderFillEvaluator.TryFindFillPosition(activatedSell, bar));
    }

    [Fact]
    public void BuyStopLimit_TriggeredOnRisingLeg_FillsWhereTheHighToLowLegReachesTheLimit()
    {
        // O95 H105 L85 C92, Stop=100 (trigger at 0.5 on Open->High), Limit=90 reached on High->Low at 1 + (105-90)/(105-85) = 1.75.
        CandleData bar = Bar(95m, 105m, 85m, 92m);
        BacktestOrder order = Order(OrderSide.Buy, OrderType.StopLimit, limit: 90m, stop: 100m);

        Assert.Equal(0.5m, OrderFillEvaluator.TryFindCandidatePosition(order, bar)); // trigger - unchanged meaning
        Assert.Equal(1.75m, OrderFillEvaluator.TryFindFillPosition(order, bar));
    }

    [Fact]
    public void BuyStopLimit_TriggerAlreadyAtOrBelowLimit_FillsAtTheTriggerPosition()
    {
        CandleData bar = Bar(102m, 108m, 100m, 105m);
        BacktestOrder order = Order(OrderSide.Buy, OrderType.StopLimit, limit: 110m, stop: 100m);

        Assert.Equal(0m, OrderFillEvaluator.TryFindFillPosition(order, bar)); // Open 102 >= Stop -> triggered at Open, 102 <= Limit
    }

    [Fact]
    public void BuyStopLimit_TriggeredButLimitNeverReached_HasNoFillPosition()
    {
        CandleData bar = Bar(95m, 105m, 92m, 100m); // Low 92 stays above Limit 90
        BacktestOrder order = Order(OrderSide.Buy, OrderType.StopLimit, limit: 90m, stop: 100m);

        Assert.NotNull(OrderFillEvaluator.TryFindCandidatePosition(order, bar));
        Assert.Null(OrderFillEvaluator.TryFindFillPosition(order, bar));
    }

    [Fact]
    public void SellStopLimit_TriggeredPartwayDown_FillsOnlyOnTheLowToCloseRecovery()
    {
        // O95 H105 L85 C102, Stop=90 triggers at 1 + (105-90)/20 = 1.75 on High->Low, Limit=100: only Low->Close (85 -> 102) remains, reaching 100 at 2 + 15/17.
        CandleData bar = Bar(95m, 105m, 85m, 102m);
        BacktestOrder order = Order(OrderSide.Sell, OrderType.StopLimit, limit: 100m, stop: 90m);

        Assert.Equal(1.75m, OrderFillEvaluator.TryFindCandidatePosition(order, bar));
        Assert.Equal(2m + (15m / 17m), OrderFillEvaluator.TryFindFillPosition(order, bar));
    }

    [Fact]
    public void SellStopLimit_TriggeredPartwayDown_HighIsNeverReused_SoNoRecoveryMeansNoFill()
    {
        CandleData bar = Bar(95m, 105m, 85m, 92m); // Close 92 < Limit 100; the already-consumed High 105 must not count
        BacktestOrder order = Order(OrderSide.Sell, OrderType.StopLimit, limit: 100m, stop: 90m);

        Assert.Null(OrderFillEvaluator.TryFindFillPosition(order, bar));
    }

    [Fact]
    public void SellStopLimit_TriggeredAtOpen_FillsOnTheOpenToHighLeg()
    {
        CandleData bar = Bar(88m, 101m, 85m, 95m);
        BacktestOrder order = Order(OrderSide.Sell, OrderType.StopLimit, limit: 100m, stop: 90m);

        Assert.Equal(12m / 13m, OrderFillEvaluator.TryFindFillPosition(order, bar)); // (100-88)/(101-88)
    }

    [Fact]
    public void SellStopLimit_TriggerPriceAtOrAboveLimit_FillsAtTheTriggerPosition()
    {
        CandleData bar = Bar(110m, 115m, 60m, 105m);
        BacktestOrder order = Order(OrderSide.Sell, OrderType.StopLimit, limit: 85m, stop: 90m);

        Assert.Equal(OrderFillEvaluator.TryFindCandidatePosition(order, bar), OrderFillEvaluator.TryFindFillPosition(order, bar));
        Assert.Equal(1m + (25m / 55m), OrderFillEvaluator.TryFindFillPosition(order, bar));
    }

    [Fact]
    public void FillPosition_IsNonNullExactlyWhenEvaluateFills_AndNeverPrecedesTheTrigger_OverAnExhaustivePriceGrid()
    {
        decimal[] grid = { 80m, 85m, 90m, 95m, 100m, 105m, 110m, 115m, 120m };
        int checkedCases = 0;

        foreach (decimal low in grid)
        foreach (decimal high in grid)
        {
            if (high < low) continue;
            foreach (decimal open in grid)
            foreach (decimal close in grid)
            {
                if (open < low || open > high || close < low || close > high) continue;
                CandleData bar = Bar(open, high, low, close);

                foreach (OrderSide side in new[] { OrderSide.Buy, OrderSide.Sell })
                foreach (decimal stop in grid)
                foreach (decimal limit in grid)
                foreach (bool activated in new[] { false, true })
                {
                    BacktestOrder order = Order(side, OrderType.StopLimit, limit, stop, activated);
                    decimal? fillPosition = OrderFillEvaluator.TryFindFillPosition(order, bar);
                    OrderFillOutcome outcome = OrderFillEvaluator.Evaluate(order, bar, 0m, 0m, 0m);

                    string context = $"{side} S={stop} L={limit} act={activated} O{open} H{high} L{low} C{close}";
                    Assert.True(outcome.Filled == fillPosition.HasValue, context);
                    if (fillPosition is { } position)
                    {
                        Assert.InRange(position, 0m, 3m);
                        if (!activated)
                        {
                            Assert.True(OrderFillEvaluator.TryFindCandidatePosition(order, bar) <= position, context);
                        }
                    }
                    checkedCases++;
                }
            }
        }

        Assert.True(checkedCases > 100_000, $"grid unexpectedly small: {checkedCases}");
    }
}
