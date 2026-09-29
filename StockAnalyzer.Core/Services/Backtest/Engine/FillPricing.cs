using System;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Core.Services.Backtest.Engine;

/// <summary>
/// Shared slippage/fee arithmetic for order fills and forced margin liquidations, per
/// Y:\0915 Backtesting\01_P1_SimulationEngine.md section 5.6. Kept as a single small static class
/// (rather than duplicated inline in each evaluator) because both <see cref="OrderFillEvaluator"/>
/// and <see cref="MarginLiquidationCalculator"/> need the identical Commission formula and the
/// identical directional-slippage formula.
/// </summary>
public static class FillPricing
{
    /// <summary>Buy: P = P0 x (1 + s). Sell: P = P0 x (1 - s).</summary>
    public static decimal ApplySlippage(decimal p0, OrderSide side, decimal slippageRatio)
        => side == OrderSide.Buy ? p0 * (1m + slippageRatio) : p0 * (1m - slippageRatio);

    /// <summary>Limit/StopLimit fills are price-protected: Buy min(P, L), Sell max(P, L). Not applied to plain Stop (which becomes an unprotected Market fill once triggered).</summary>
    public static decimal ClampToLimit(decimal slippedPrice, OrderSide side, decimal limitPrice)
        => side == OrderSide.Buy ? Math.Min(slippedPrice, limitPrice) : Math.Max(slippedPrice, limitPrice);

    public static decimal ComputeCommission(decimal commissionFlat, decimal commissionPerUnit, decimal quantity)
        => commissionFlat + (commissionPerUnit * quantity);

    /// <summary>Reference-only value carried on BacktestFill; not re-deducted anywhere else.</summary>
    public static decimal ComputeSlippageAmount(decimal executedPrice, decimal p0, decimal quantity)
        => Math.Abs(executedPrice - p0) * quantity;
}
