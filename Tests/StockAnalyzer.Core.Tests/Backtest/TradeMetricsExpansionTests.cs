using System;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

public sealed class TradeMetricsExpansionTests
{
    [Fact]
    public void BacktestTrade_ComputedProperties_CalculatesCorrectlyForLongWin()
    {
        var entryTime = new DateTime(2025, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var exitTime = new DateTime(2025, 1, 3, 15, 30, 0, DateTimeKind.Utc);

        var trade = new BacktestTrade(
            TradeId: 1,
            Side: TradeSide.Long,
            EntryBar: 0,
            EntryTime: entryTime,
            EntryPrice: 100m,
            ExitBar: 2,
            ExitTime: exitTime,
            ExitPrice: 110m,
            Quantity: 10m,
            ClosedGross: 100m,
            ClosedNet: 95m,
            EntryFee: 2m,
            ExitFee: 3m,
            HoldingBars: 2,
            IsForcedLiquidation: false)
        {
            EntrySlippage = 1m,
            ExitSlippage = 1.5m,
            MfePrice = 15m,
            MaePrice = 2m,
            EntrySignal = "Golden Cross",
            ExitReason = "Take-Profit breach"
        };

        Assert.Equal(1000m, trade.EntryValue);
        Assert.Equal(1100m, trade.ExitValue);
        Assert.Equal(5m, trade.TotalCommission);
        Assert.Equal(2.5m, trade.TotalSlippage);
        Assert.Equal(7.5m, trade.TotalCost);
        Assert.Equal(9.5m, trade.PnLPercent);
        Assert.Equal(15m, trade.MfePercent);
        Assert.Equal(2m, trade.MaePercent);
        Assert.Equal(TimeSpan.FromDays(2) + TimeSpan.FromHours(5.5), trade.Duration);
        Assert.Equal("Golden Cross", trade.EntrySignal);
        Assert.Equal("Take-Profit breach", trade.ExitReason);
    }

    [Fact]
    public void BacktestTrade_InvalidOrZeroPrice_ReturnsNullRatios_FailSoftSemantics()
    {
        var trade = new BacktestTrade(
            TradeId: 2,
            Side: TradeSide.Long,
            EntryBar: 0,
            EntryTime: DateTime.UtcNow,
            EntryPrice: 0m,
            ExitBar: 1,
            ExitTime: DateTime.UtcNow,
            ExitPrice: 10m,
            Quantity: 0m,
            ClosedGross: 0m,
            ClosedNet: 0m,
            EntryFee: 0m,
            ExitFee: 0m,
            HoldingBars: 1,
            IsForcedLiquidation: false);

        Assert.Equal(0m, trade.EntryValue);
        Assert.Null(trade.PnLPercent);
        Assert.Null(trade.MfePercent);
        Assert.Null(trade.MaePercent);
    }

    [Fact]
    public void BacktestTrade_SweeneyStandard_NonNegativeFloorInvariant()
    {
        var trade = new BacktestTrade(
            TradeId: 3,
            Side: TradeSide.Short,
            EntryBar: 0,
            EntryTime: DateTime.UtcNow,
            EntryPrice: 50m,
            ExitBar: 1,
            ExitTime: DateTime.UtcNow,
            ExitPrice: 55m,
            Quantity: 2m,
            ClosedGross: -10m,
            ClosedNet: -12m,
            EntryFee: 1m,
            ExitFee: 1m,
            HoldingBars: 1,
            IsForcedLiquidation: false)
        {
            MfePrice = 0m,
            MaePrice = 7m
        };

        Assert.True(trade.MfePrice >= 0m);
        Assert.True(trade.MaePrice >= 0m);
        Assert.Equal(0m, trade.MfePercent);
        Assert.Equal(14m, trade.MaePercent); // 7 / 50 * 100 = 14%
    }
}
