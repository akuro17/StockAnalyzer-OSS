using System;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Core.Services.Backtest.Engine;

/// <summary>
/// Maintenance-margin intrabar forced-liquidation ("loss-cut") math, per
/// Y:\Temp\sa_ai_context_BacktestEngine_P1.md section 1.5.6. Derived algebraically from the
/// mark-to-market Equity formula (section 1.5.5) <c>Equity(P) = Cash + HeldMargin + q*(P-EntryPrice)</c>
/// and the maintenance requirement <c>MaintenanceReq(P) = |q|*P*MaintenanceMarginRatio</c>, solving
/// <c>Equity(P) = MaintenanceReq(P)</c> for P. Whether a breach this bar should terminate the whole
/// run (post-liquidation Equity &lt;= 0) or merely close this position while the run continues
/// (post-liquidation Equity &gt; 0) requires full account state and is decided by the engine's Step 9
/// (task #6) - this calculator only computes the threshold and the forced-fill price/commission.
/// </summary>
public static class MarginLiquidationCalculator
{
    public static decimal ComputeLiquidationPrice(TradeSide side, decimal quantity, decimal entryPrice, decimal cash, decimal heldMargin, decimal maintenanceMarginRatio)
    {
        if (quantity <= 0m) throw new ArgumentOutOfRangeException(nameof(quantity), quantity, "Quantity must be > 0.");

        return side switch
        {
            TradeSide.Long => (quantity * entryPrice - cash - heldMargin) / (quantity * (1m - maintenanceMarginRatio)),
            TradeSide.Short => (cash + heldMargin + quantity * entryPrice) / (quantity * (1m + maintenanceMarginRatio)),
            _ => throw new ArgumentOutOfRangeException(nameof(side), side, "Unknown TradeSide."),
        };
    }

    /// <summary>
    /// Long breaches on the Open-&gt;High-&gt;Low-&gt;Close path's downward leg (bar Low touching P_liq
    /// from above); Short breaches on the upward leg (bar High touching P_liq from below). Returns the
    /// same [0,2] path-position scale as <see cref="FillPathScanner"/> so it can be compared directly
    /// against a competing pending exit order's own touch position via <see cref="ExitPrecedenceResolver"/>.
    /// </summary>
    public static decimal? TryFindBreachPosition(TradeSide side, decimal liquidationPrice, CandleData bar)
    {
        TouchDirection direction = side == TradeSide.Long ? TouchDirection.Downward : TouchDirection.Upward;
        return FillPathScanner.TryFindTouchPosition(bar.Open, bar.High, bar.Low, bar.Close, liquidationPrice, direction);
    }

    /// <summary>
    /// User-confirmed execution rule (do not deviate): Long forced SELL fills at
    /// P_liq * (1 - LiquidationPenaltyRatio); Short forced BUY fills at P_liq * (1 + LiquidationPenaltyRatio).
    /// Normal commission applies on top, same as any other fill.
    /// </summary>
    public static (decimal FillPrice, decimal Commission) ComputeForcedLiquidationFill(TradeSide side, decimal liquidationPrice, decimal quantity, decimal liquidationPenaltyRatio, decimal commissionFlat, decimal commissionPerUnit)
    {
        decimal fillPrice = side switch
        {
            TradeSide.Long => liquidationPrice * (1m - liquidationPenaltyRatio),
            TradeSide.Short => liquidationPrice * (1m + liquidationPenaltyRatio),
            _ => throw new ArgumentOutOfRangeException(nameof(side), side, "Unknown TradeSide."),
        };
        decimal commission = FillPricing.ComputeCommission(commissionFlat, commissionPerUnit, quantity);
        return (fillPrice, commission);
    }
}
