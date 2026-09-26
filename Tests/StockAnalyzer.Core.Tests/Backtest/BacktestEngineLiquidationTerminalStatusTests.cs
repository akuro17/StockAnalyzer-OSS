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
/// G6 (owner decision B1, the P1 correctness plan): the orders still pending when a forced liquidation
/// closes the position end as Expired(Insolvency) when the liquidation leaves the account insolvent (Cash &lt;= 0, run stops), and as Cancelled
/// when the account survives. Account: E0=1000, Q=20 entered at 100, r=0.3, m=0.2 -> Cash 400, HeldMargin 600, Long liquidation price 62.5,
/// Short liquidation price 125. A penalty of 0.3 pushes the forced fill to 43.75 (Long) / 162.5 (Short), i.e. Cash below zero.
/// </summary>
public class BacktestEngineLiquidationTerminalStatusTests
{
    private static readonly DateTime Bar0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeInForce Gtc = new(true, -1);

    private const decimal InsolventPenaltyRatio = 0.3m;
    private const decimal SolventPenaltyRatio = 0m;

    private static CandleData Bar(int dayOffset, decimal open, decimal high, decimal low, decimal close) => new(Bar0.AddDays(dayOffset), open, high, low, close, 1000);

    private static BacktestConfiguration MakeConfig(decimal liquidationPenaltyRatio) => new()
    {
        InitialCapital = 1000m,
        SizingModel = PositionSizingModel.FixedQuantity,
        SizingParameter = 20m,
        InitialMarginRatio = 0.3m,
        MaintenanceMarginRatio = 0.2m,
        LiquidationPenaltyRatio = liquidationPenaltyRatio,
    };

    /// <summary>
    /// bar0 signals the entry, bar1 fills it at 100 and leaves BOTH slots pending (an unreachable Exit Limit and an unreachable opposite-side
    /// Entry Limit), bar2 breaches the maintenance margin. Orders: [0] entry, [1] opposite Entry (Evaluate), [2] Exit (EvaluateExit).
    /// </summary>
    private static BacktestResult RunWithBothSlotsPendingAtLiquidation(bool longSide, decimal penaltyRatio)
    {
        (SignalType entry, SignalType oppositeEntry, SignalType exit, decimal exitLimit, decimal oppositeLimit, CandleData breachBar) = longSide
            ? (SignalType.LongEntry, SignalType.ShortEntry, SignalType.LongExit, 200m, 300m, Bar(2, 100m, 100m, 60m, 70m))
            : (SignalType.ShortEntry, SignalType.LongEntry, SignalType.ShortExit, 50m, 20m, Bar(2, 100m, 130m, 100m, 120m));

        ImmutableArray<CandleData> bars = ImmutableArray.Create(Bar(0, 100m, 100m, 100m, 100m), Bar(1, 100m, 100m, 100m, 100m), breachBar);
        var strategy = new ScriptedStrategy(
            new Dictionary<int, StrategyOrderRequest>
            {
                [0] = new(entry, OrderType.Market, null, null, Gtc, "enter"),
                [1] = new(oppositeEntry, OrderType.Limit, oppositeLimit, null, Gtc, "reverse"),
            },
            new Dictionary<int, StrategyOrderRequest>
            {
                [1] = new(exit, OrderType.Limit, exitLimit, null, Gtc, "exit"),
            });
        var input = new BacktestInput(bars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion, Bar0, Bar0.AddDays(bars.Length + 1), 0, 0);
        return VerificationHarness.CreateEngine().Run(input, MakeConfig(penaltyRatio), strategy);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InsolventForcedLiquidation_ExpiresBothPendingOrders_WithInsolvencyReason(bool longSide)
    {
        BacktestResult result = RunWithBothSlotsPendingAtLiquidation(longSide, InsolventPenaltyRatio);

        Assert.Equal(RunStatus.Insolvent, result.Status);
        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.True(trade.IsForcedLiquidation);
        Assert.True(result.EquityPoints[^1].Cash <= 0m);
        Assert.Equal(3, result.Orders.Length);
        foreach (BacktestOrder pending in new[] { result.Orders[1], result.Orders[2] })
        {
            Assert.Equal(OrderStatus.Expired, pending.Status);
            Assert.Equal(ExpiredReason.Insolvency, pending.ExpiredReason);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SolventForcedLiquidation_StillCancelsBothPendingOrders_AndTheRunContinues(bool longSide)
    {
        BacktestResult result = RunWithBothSlotsPendingAtLiquidation(longSide, SolventPenaltyRatio);

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.True(Assert.Single(result.Trades).IsForcedLiquidation);
        Assert.True(result.EquityPoints[^1].Cash > 0m);
        foreach (BacktestOrder pending in new[] { result.Orders[1], result.Orders[2] })
        {
            Assert.Equal(OrderStatus.Cancelled, pending.Status);
            Assert.Null(pending.ExpiredReason);
        }
    }
}
