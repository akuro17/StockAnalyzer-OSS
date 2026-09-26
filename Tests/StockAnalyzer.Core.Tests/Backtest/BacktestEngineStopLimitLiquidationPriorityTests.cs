#nullable enable
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
/// T7a (A1 (StopLimit fill priority), owner decision G1): a pending StopLimit Exit competes with a
/// margin liquidation by where it would ACTUALLY FILL, not where its Stop triggers. Account: E0=1000, Q=20 entered at 100, r=0.3, m=0.2,
/// no fees/slippage/penalty -> Cash 400, HeldMargin 600, Long liquidation price 62.5, Short liquidation price 125.
/// </summary>
public class BacktestEngineStopLimitLiquidationPriorityTests
{
    private static readonly DateTime Bar0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeInForce Gtc = new(true, -1);

    private static CandleData Bar(int dayOffset, decimal open, decimal high, decimal low, decimal close) => new(Bar0.AddDays(dayOffset), open, high, low, close, 1000);

    private static BacktestConfiguration MakeConfig() => new()
    {
        InitialCapital = 1000m,
        SizingModel = PositionSizingModel.FixedQuantity,
        SizingParameter = 20m,
        InitialMarginRatio = 0.3m,
        MaintenanceMarginRatio = 0.2m,
        LiquidationPenaltyRatio = 0m,
    };

    /// <summary>bar0 (flat 100) signals the entry, bar1 (flat 100) fills it and signals the StopLimit exit, bar2 is the contested bar.</summary>
    private static BacktestResult RunContestedBar(SignalType entry, SignalType exit, decimal stopPrice, decimal limitPrice, CandleData contestedBar)
    {
        ImmutableArray<CandleData> bars = ImmutableArray.Create(Bar(0, 100m, 100m, 100m, 100m), Bar(1, 100m, 100m, 100m, 100m), contestedBar);
        var script = new Dictionary<int, StrategyOrderRequest>
        {
            [0] = new(entry, OrderType.Market, null, null, Gtc, "enter"),
            [1] = new(exit, OrderType.StopLimit, limitPrice, stopPrice, Gtc, "exit"),
        };
        var input = new BacktestInput(bars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion, Bar0, Bar0.AddDays(bars.Length + 1), 0, 0);
        return VerificationHarness.CreateEngine().Run(input, MakeConfig(), new ScriptedStrategy(script));
    }

    [Fact]
    public void Long_SellStopLimitTriggersBeforeLiquidation_ButItsLimitFillComesAfter_LiquidationWins()
    {
        // Stop 90 triggers on High->Low (1.4545), liquidation 62.5 is reached later on the same leg (1.9545); the Limit 100 can only
        // fill on the Low->Close recovery (> 2). The old trigger rule closed normally at 100; the fill rule force-closes at 62.5.
        BacktestResult result = RunContestedBar(SignalType.LongEntry, SignalType.LongExit, stopPrice: 90m, limitPrice: 100m, Bar(2, 110m, 115m, 60m, 105m));

        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.True(trade.IsForcedLiquidation);
        Assert.Equal(62.5m, trade.ExitPrice);
        Assert.Equal(RunStatus.Completed, result.Status); // Equity stays > 0: a partial loss-cut, the run continues
        Assert.Equal(250m, result.EquityPoints[^1].Cash); // 400 + 600 + 20 * (62.5 - 100)
        Assert.Equal(OrderStatus.Cancelled, result.Orders[^1].Status);
    }

    [Fact]
    public void Long_SellStopLimitFillsBeforeLiquidation_OrderWinsAndClosesNormally()
    {
        // Limit 85 <= Stop 90, so the order fills right at the trigger (1.4545) - before the liquidation point (1.9545).
        BacktestResult result = RunContestedBar(SignalType.LongEntry, SignalType.LongExit, stopPrice: 90m, limitPrice: 85m, Bar(2, 110m, 115m, 60m, 105m));

        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.False(trade.IsForcedLiquidation);
        Assert.Equal(90m, trade.ExitPrice);
        Assert.Equal(800m, result.EquityPoints[^1].Cash); // 400 + 600 + 20 * (90 - 100)
        Assert.Equal(OrderStatus.Filled, result.Orders[^1].Status);
    }

    [Fact]
    public void Short_BuyStopLimitTriggersBeforeLiquidation_ButItsLimitFillComesAfter_LiquidationWins()
    {
        // Stop 110 triggers at 0.2 on Open->High, Short liquidation 125 is reached at 0.8, the Limit 100 fills only on High->Low (1.857).
        BacktestResult result = RunContestedBar(SignalType.ShortEntry, SignalType.ShortExit, stopPrice: 110m, limitPrice: 100m, Bar(2, 105m, 130m, 95m, 120m));

        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.Equal(TradeSide.Short, trade.Side);
        Assert.True(trade.IsForcedLiquidation);
        Assert.Equal(125m, trade.ExitPrice);
        Assert.Equal(500m, result.EquityPoints[^1].Cash); // 400 + 600 + 20 * (100 - 125)
        Assert.Equal(OrderStatus.Cancelled, result.Orders[^1].Status);
    }

    [Fact]
    public void Long_SellStopLimitThatOnlyActivates_StillFallsBackToLiquidation()
    {
        // Existing user-confirmed fallback, unchanged: trigger without any Limit fill never shields the account from a real breach.
        BacktestResult result = RunContestedBar(SignalType.LongEntry, SignalType.LongExit, stopPrice: 90m, limitPrice: 100m, Bar(2, 95m, 105m, 60m, 70m));

        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.True(trade.IsForcedLiquidation);
        Assert.Equal(62.5m, trade.ExitPrice);
    }

    [Fact]
    public void Long_SellStopLimitThatOnlyActivates_WithoutBreach_PersistsActivationAndStaysPending()
    {
        BacktestResult result = RunContestedBar(SignalType.LongEntry, SignalType.LongExit, stopPrice: 90m, limitPrice: 100m, Bar(2, 95m, 105m, 85m, 92m));

        Assert.Empty(result.Trades);
        BacktestOrder exitOrder = result.Orders[^1];
        Assert.Equal(ExpiredReason.EndOfData, exitOrder.ExpiredReason);
        Assert.True(exitOrder.StopActivated); // end of data closes the order, but the activation was persisted first
    }
}
