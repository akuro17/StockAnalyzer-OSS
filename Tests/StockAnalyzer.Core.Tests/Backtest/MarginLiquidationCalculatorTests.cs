using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Engine;
using System;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

public class MarginLiquidationCalculatorTests
{
    private static readonly DateTime Bar0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static CandleData Bar(decimal open, decimal high, decimal low, decimal close)
        => new(Bar0, open, high, low, close, 1000);

    // ---- Algebra verification (Y:\Temp\sa_ai_context_BacktestEngine_P1.md section 1.5.6 requires
    // this be checked with a unit test before trusting it) ----

    [Fact]
    public void Long_LiquidationPrice_SolvesEquityEqualsMaintenanceRequirement()
    {
        // Q=40 @ EntryPrice=100 (notional 4000), InitialMarginRatio=0.25 -> HeldMargin=1000, Cash=0.
        const decimal q = 40m, entryPrice = 100m, cash = 0m, heldMargin = 1000m, maintenanceRatio = 0.20m;

        decimal pLiq = MarginLiquidationCalculator.ComputeLiquidationPrice(TradeSide.Long, q, entryPrice, cash, heldMargin, maintenanceRatio);

        Assert.Equal(93.75m, pLiq);

        decimal equity = cash + heldMargin + q * (pLiq - entryPrice);
        decimal maintenanceReq = q * pLiq * maintenanceRatio;
        Assert.Equal(equity, maintenanceReq);
    }

    [Fact]
    public void Short_LiquidationPrice_SolvesEquityEqualsMaintenanceRequirement()
    {
        // Short Q=40 @ EntryPrice=100 (notional 4000), InitialMarginRatio=0.25 -> HeldMargin=1000, Cash=0.
        const decimal q = 40m, entryPrice = 100m, cash = 0m, heldMargin = 1000m, maintenanceRatio = 0.20m;

        decimal pLiq = MarginLiquidationCalculator.ComputeLiquidationPrice(TradeSide.Short, q, entryPrice, cash, heldMargin, maintenanceRatio);

        decimal equity = cash + heldMargin + (-q) * (pLiq - entryPrice);
        decimal maintenanceReq = q * pLiq * maintenanceRatio;
        Assert.Equal(Math.Round(equity, 10), Math.Round(maintenanceReq, 10));
    }

    [Fact]
    public void Long_FullyCollateralized_OneTimesLeverage_LiquidatesOnlyAtZero()
    {
        // Notional == InitialCapital (no leverage at all): the position can absorb a drop all the
        // way to 0 before a modest maintenance ratio is breached.
        const decimal q = 10m, entryPrice = 100m, cash = 700m, heldMargin = 300m, maintenanceRatio = 0.20m;

        decimal pLiq = MarginLiquidationCalculator.ComputeLiquidationPrice(TradeSide.Long, q, entryPrice, cash, heldMargin, maintenanceRatio);

        Assert.Equal(0m, pLiq);
    }

    // ---- Breach detection (intrabar, via FillPathScanner) ----

    [Fact]
    public void Long_BarLowTouchesLiquidationPrice_BreachDetected()
    {
        var bar = Bar(open: 100m, high: 102m, low: 93m, close: 95m);
        decimal? position = MarginLiquidationCalculator.TryFindBreachPosition(TradeSide.Long, liquidationPrice: 93.75m, bar);
        Assert.NotNull(position);
    }

    [Fact]
    public void Long_BarLowDoesNotReachLiquidationPrice_NoBreach()
    {
        var bar = Bar(open: 100m, high: 102m, low: 95m, close: 96m);
        decimal? position = MarginLiquidationCalculator.TryFindBreachPosition(TradeSide.Long, liquidationPrice: 93.75m, bar);
        Assert.Null(position);
    }

    [Fact]
    public void Short_BarHighTouchesLiquidationPrice_BreachDetected()
    {
        var bar = Bar(open: 100m, high: 106m, low: 98m, close: 103m);
        decimal? position = MarginLiquidationCalculator.TryFindBreachPosition(TradeSide.Short, liquidationPrice: 104.1666667m, bar);
        Assert.NotNull(position);
    }

    // ---- Forced-fill pricing (user-confirmed rule) ----

    [Fact]
    public void Long_ForcedLiquidationFill_AppliesAdversePenaltyBelowThreshold()
    {
        var (fillPrice, commission) = MarginLiquidationCalculator.ComputeForcedLiquidationFill(
            TradeSide.Long, liquidationPrice: 100m, quantity: 10m, liquidationPenaltyRatio: 0.005m,
            commissionFlat: 1m, commissionPerUnit: 0.1m);

        Assert.Equal(99.5m, fillPrice); // 100 * (1 - 0.005)
        Assert.Equal(2m, commission); // 1 + 0.1*10
    }

    [Fact]
    public void Short_ForcedLiquidationFill_AppliesAdversePenaltyAboveThreshold()
    {
        var (fillPrice, _) = MarginLiquidationCalculator.ComputeForcedLiquidationFill(
            TradeSide.Short, liquidationPrice: 100m, quantity: 10m, liquidationPenaltyRatio: 0.005m,
            commissionFlat: 0m, commissionPerUnit: 0m);

        Assert.Equal(100.5m, fillPrice); // 100 * (1 + 0.005)
    }
}
