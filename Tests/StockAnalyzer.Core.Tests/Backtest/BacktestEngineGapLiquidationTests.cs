#nullable enable
using System.Collections.Generic;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Tests.Backtest.Verification;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

/// <summary>
/// Gap rule (owner decision 2026-09-20; BacktestExecutionSemantics.Version 2): when a bar OPENS already at/through the maintenance-margin threshold, the forced
/// liquidation trades at the Open with the liquidation penalty - never at the (better) threshold price. E0=1000, Q=20, r=0.3, m=0.2: after a Long/Short at 100
/// Cash is 400 and HeldMargin 600, so the Long threshold is 62.5 and the Short threshold 125.
/// </summary>
public class BacktestEngineGapLiquidationTests
{
    private static BacktestConfiguration Config(decimal penaltyRatio = 0m)
        => VerificationHarness.MakeConfig(initialCapital: 1000m, sizingParameter: 20m, initialMarginRatio: 0.3m, maintenanceMarginRatio: 0.2m, liquidationPenaltyRatio: penaltyRatio);

    private static CandleData Flat(int day, decimal price) => SyntheticBars.Bar(day, price, price, price, price);

    private static BacktestResult Run(SignalType entry, ImmutableArray<CandleData> bars, decimal penaltyRatio = 0m)
        => VerificationHarness.Run(VerificationHarness.MakeInput(bars), Config(penaltyRatio),
            new ScriptedStrategy(new Dictionary<int, StrategyOrderRequest> { [0] = VerificationHarness.Req(entry) }));

    [Fact]
    public void LongGapDownThroughTheThreshold_LiquidatesAtTheOpen_NotAtTheThreshold()
    {
        // Bar 2 opens at 55, below the threshold 62.5: the Long trades at 55 (the threshold price would be a fill the market never offered).
        BacktestResult result = Run(SignalType.LongEntry, ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m), Flat(2, 55m)));

        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.True(trade.IsForcedLiquidation);
        Assert.Equal(2, trade.ExitBar);
        Assert.Equal(55m, trade.ExitPrice);
        Assert.Equal(100m, result.EquityPoints[2].Cash); // 400 + 600 + 20 * (55 - 100)
        Assert.Equal(RunStatus.Completed, result.Status);
    }

    [Fact]
    public void ShortGapUpThroughTheThreshold_LiquidatesAtTheOpen()
    {
        BacktestResult result = Run(SignalType.ShortEntry, ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m), Flat(2, 130m)));

        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.Equal(TradeSide.Short, trade.Side);
        Assert.Equal(130m, trade.ExitPrice);
        Assert.Equal(400m, result.EquityPoints[2].Cash); // 400 + 600 + 20 * (100 - 130)
    }

    [Fact]
    public void GapLiquidation_AppliesThePenaltyToTheOpen_AndRecordsTheSlippageAgainstIt()
    {
        BacktestResult result = Run(SignalType.LongEntry, ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m), Flat(2, 55m)), penaltyRatio: 0.02m);

        Assert.Equal(53.9m, Assert.Single(result.Trades).ExitPrice); // 55 * (1 - 0.02)
        BacktestFill fill = result.Fills[^1];
        Assert.Equal(22m, fill.SlippageAmount); // |53.9 - 55| * 20
    }

    [Fact]
    public void IntrabarBreach_WithoutAGap_StillFillsAtTheThreshold()
    {
        // Open 100 is above the threshold; the Low of 60 reaches 62.5 on the way down: the usual threshold fill.
        BacktestResult result = Run(SignalType.LongEntry, ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m), SyntheticBars.Bar(2, 100m, 100m, 60m, 100m)));

        Assert.Equal(62.5m, Assert.Single(result.Trades).ExitPrice);
    }

    [Fact]
    public void GapLiquidation_LeavingTheAccountInsolvent_StopsTheRun()
    {
        // Open 45: 400 + 600 + 20 * (45 - 100) = -100 <= 0.
        BacktestResult result = Run(SignalType.LongEntry, ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m), Flat(2, 45m), Flat(3, 45m)));

        Assert.Equal(RunStatus.Insolvent, result.Status);
        Assert.Equal(45m, Assert.Single(result.Trades).ExitPrice);
        Assert.Equal(3, result.EquityPoints.Length);
    }
}
