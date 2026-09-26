using System;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Core.Services.Backtest.Engine;

/// <summary>
/// Evaluates one pending <see cref="BacktestOrder"/> against one bar, per
/// Y:\0915 Backtesting\01_P1_SimulationEngine.md section 5.6 (fill price table). Pure function of
/// (order, bar, config) - no mutable state; the caller (future BacktestEngine, task #6) is
/// responsible for sequencing this across bars, assigning FillId/OrderId/BarIndex/FillTime, and
/// persisting <see cref="OrderFillOutcome.StopActivated"/> back onto the order between bars.
/// MarketOnClose is intentionally evaluated by a separate method
/// (<see cref="EvaluateMarketOnClose"/>): the source spec runs it in a distinct bar-processing step
/// (Step 4, after the Step 2 Market/Limit/Stop/StopLimit scan), not through the same O-&gt;H-&gt;L-&gt;C
/// path scan.
/// </summary>
public static class OrderFillEvaluator
{
    /// <summary>
    /// The outcome of the order over the WHOLE bar path. It is the residual evaluation <see cref="EvaluateFrom"/> started at the Open vertex, so the
    /// whole-bar and the residual-path rules can never drift apart (single source of truth for every fill formula).
    /// </summary>
    public static OrderFillOutcome Evaluate(BacktestOrder order, CandleData bar, decimal slippageRatio, decimal commissionFlat, decimal commissionPerUnit)
    {
        return order.Type switch
        {
            OrderType.MarketOnClose => throw new ArgumentException("MarketOnClose orders must be evaluated via EvaluateMarketOnClose (a separate bar-processing step), not Evaluate.", nameof(order)),
            _ => EvaluateFrom(order, bar, FillPathScanner.OpenPoint(bar.Open), slippageRatio, commissionFlat, commissionPerUnit).Outcome,
        };
    }

    /// <summary>Step 4 of the bar-processing order: unconditional fill at Close, no path scan.</summary>
    public static OrderFillOutcome EvaluateMarketOnClose(BacktestOrder order, CandleData bar, decimal slippageRatio, decimal commissionFlat, decimal commissionPerUnit)
    {
        if (order.Type != OrderType.MarketOnClose)
        {
            throw new ArgumentException("EvaluateMarketOnClose requires an order of Type MarketOnClose.", nameof(order));
        }
        if (order.LimitPrice.HasValue || order.StopPrice.HasValue)
        {
            throw new ArgumentException("MarketOnClose orders must not carry a LimitPrice or StopPrice.", nameof(order));
        }
        decimal p0 = bar.Close;
        decimal fillPrice = FillPricing.ApplySlippage(p0, order.Side, slippageRatio);
        return BuildFilled(fillPrice, p0, order.Quantity, commissionFlat, commissionPerUnit);
    }

    /// <summary>
    /// Read-only "would this order touch the bar's path, and where" query - task #6 (BacktestEngine)
    /// amendment to task #5, needed so the engine can compare a pending exit order against a competing
    /// margin-liquidation threshold via <see cref="ExitPrecedenceResolver"/> BEFORE committing to either
    /// one (see Y:\Temp\sa_ai_context_BacktestEngine_P1.md section 1.5.6). Performs no commission/slippage
    /// computation and does not persist StopActivated - it only reports a path position. Market always
    /// fills unconditionally at Open (position 0); MarketOnClose is evaluated in a separate bar-processing
    /// step (Step 4) and never competes with margin liquidation on the O-&gt;H-&gt;L path, so it reports null
    /// here (a breach anywhere on that path is always caught before Close is reached - see
    /// MarginLiquidationCalculator's class remarks).
    /// For a not-yet-activated StopLimit this reports the Stop leg's trigger position (the earliest
    /// meaningful event for that order this bar), not the eventual Limit fill position.
    /// </summary>
    public static decimal? TryFindCandidatePosition(BacktestOrder order, CandleData bar)
    {
        switch (order.Type)
        {
            case OrderType.Market:
                return 0m;
            case OrderType.MarketOnClose:
                return null;
            case OrderType.Limit:
            {
                decimal limitPrice = order.LimitPrice ?? throw new ArgumentException("Limit orders require LimitPrice.", nameof(order));
                TouchDirection direction = order.Side == OrderSide.Buy ? TouchDirection.Downward : TouchDirection.Upward;
                return FillPathScanner.TryFindTouchPosition(bar.Open, bar.High, bar.Low, bar.Close, limitPrice, direction);
            }
            case OrderType.Stop:
            {
                decimal stopPrice = order.StopPrice ?? throw new ArgumentException("Stop orders require StopPrice.", nameof(order));
                TouchDirection direction = order.Side == OrderSide.Buy ? TouchDirection.Upward : TouchDirection.Downward;
                return FillPathScanner.TryFindTouchPosition(bar.Open, bar.High, bar.Low, bar.Close, stopPrice, direction);
            }
            case OrderType.StopLimit:
            {
                decimal stopPrice = order.StopPrice ?? throw new ArgumentException("StopLimit orders require StopPrice.", nameof(order));
                decimal limitPrice = order.LimitPrice ?? throw new ArgumentException("StopLimit orders require LimitPrice.", nameof(order));
                if (!order.StopActivated)
                {
                    TouchDirection triggerDirection = order.Side == OrderSide.Buy ? TouchDirection.Upward : TouchDirection.Downward;
                    return FillPathScanner.TryFindTouchPosition(bar.Open, bar.High, bar.Low, bar.Close, stopPrice, triggerDirection);
                }
                TouchDirection limitDirection = order.Side == OrderSide.Buy ? TouchDirection.Downward : TouchDirection.Upward;
                return FillPathScanner.TryFindTouchPosition(bar.Open, bar.High, bar.Low, bar.Close, limitPrice, limitDirection);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(order), order.Type, "Unknown OrderType.");
        }
    }

    /// <summary>
    /// Path position where the order would ACTUALLY FILL on this bar (null = no fill), on the <see cref="FillPathScanner"/> scale [0, 3].
    /// Used by the engine to rank a pending Exit against a margin liquidation: unlike <see cref="TryFindCandidatePosition"/> it never
    /// reports a StopLimit's Stop-trigger point, because a trigger alone closes nothing (owner decision G1,
    /// A1 (StopLimit fill priority)). MarketOnClose never fills on the path scan, so it reports null.
    /// </summary>
    public static decimal? TryFindFillPosition(BacktestOrder order, CandleData bar)
        => order.Type == OrderType.MarketOnClose
            ? null
            : EvaluateFrom(order, bar, FillPathScanner.OpenPoint(bar.Open), 0m, 0m, 0m).FillPoint?.Position;

    /// <summary>
    /// Evaluates the order on the part of the bar's path that starts at <paramref name="start"/> (inclusive) and returns both the outcome and the
    /// path point of the fill. Starting at <c>(0, Open)</c> is exactly <see cref="Evaluate"/> (proved by test over an exhaustive grid); a later start
    /// serves an order that cannot fill before an earlier event - a reversal Entry behind its Exit (A1 (lifetime and reversal chronology)).
    /// A Market order's only event is at Open, so from any later start it does not fill (it waits for a later bar). A StopLimit's Limit is only
    /// eligible at or after its Stop trigger (or, once activated on a prior bar, anywhere on the residual path).
    /// </summary>
    public static ResidualFill EvaluateFrom(BacktestOrder order, CandleData bar, PathPoint start, decimal slippageRatio, decimal commissionFlat, decimal commissionPerUnit)
    {
        switch (order.Type)
        {
            case OrderType.Market:
                return start.Position > 0m
                    ? ResidualFill.None(OrderFillOutcome.NoFill)
                    : Filled(FillPathScanner.OpenPoint(bar.Open), order, slippageRatio, commissionFlat, commissionPerUnit, clampLimit: null, stopActivated: false);
            case OrderType.Limit:
            {
                decimal limitPrice = order.LimitPrice ?? throw new ArgumentException("Limit orders require LimitPrice.", nameof(order));
                PathPoint? touch = FillPathScanner.TryFindTouchFrom(bar.Open, bar.High, bar.Low, bar.Close, limitPrice, order.Side == OrderSide.Buy ? TouchDirection.Downward : TouchDirection.Upward, start);
                return touch is { } point
                    ? Filled(point, order, slippageRatio, commissionFlat, commissionPerUnit, clampLimit: limitPrice, stopActivated: false)
                    : ResidualFill.None(OrderFillOutcome.NoFill);
            }
            case OrderType.Stop:
            {
                decimal stopPrice = order.StopPrice ?? throw new ArgumentException("Stop orders require StopPrice.", nameof(order));
                PathPoint? touch = FillPathScanner.TryFindTouchFrom(bar.Open, bar.High, bar.Low, bar.Close, stopPrice, order.Side == OrderSide.Buy ? TouchDirection.Upward : TouchDirection.Downward, start);
                // Once triggered, a Stop order becomes an unprotected Market fill - no clamp.
                return touch is { } point
                    ? Filled(point, order, slippageRatio, commissionFlat, commissionPerUnit, clampLimit: null, stopActivated: false)
                    : ResidualFill.None(OrderFillOutcome.NoFill);
            }
            case OrderType.StopLimit:
                return EvaluateStopLimitFrom(order, bar, start, slippageRatio, commissionFlat, commissionPerUnit);
            case OrderType.MarketOnClose:
                throw new ArgumentException("MarketOnClose orders must be evaluated via EvaluateMarketOnClose (a separate bar-processing step), not EvaluateFrom.", nameof(order));
            default:
                throw new ArgumentOutOfRangeException(nameof(order), order.Type, "Unknown OrderType.");
        }
    }

    /// <summary>
    /// Once the Stop leg triggers, the Limit scan may NOT reuse any bar extreme already passed before the trigger point. A Buy StopLimit triggers on an
    /// Upward touch (at Open or partway up Open-&gt;High) so High/Low are still ahead; a Sell StopLimit triggers on a Downward touch, and if that is
    /// partway down High-&gt;Low the High is consumed, so only a later Low-&gt;Close recovery can reach the Limit. Both follow from scanning the Limit from
    /// the trigger point.
    /// </summary>
    private static ResidualFill EvaluateStopLimitFrom(BacktestOrder order, CandleData bar, PathPoint start, decimal slippageRatio, decimal commissionFlat, decimal commissionPerUnit)
    {
        decimal limitPrice = order.LimitPrice ?? throw new ArgumentException("StopLimit orders require LimitPrice.", nameof(order));
        decimal stopPrice = order.StopPrice ?? throw new ArgumentException("StopLimit orders require StopPrice.", nameof(order));
        bool buy = order.Side == OrderSide.Buy;

        PathPoint limitScanStart = start;
        if (!order.StopActivated)
        {
            PathPoint? trigger = FillPathScanner.TryFindTouchFrom(bar.Open, bar.High, bar.Low, bar.Close, stopPrice, buy ? TouchDirection.Upward : TouchDirection.Downward, start);
            if (trigger is null) return ResidualFill.None(OrderFillOutcome.NoFill);
            limitScanStart = trigger.Value; // bar extremes seen before the trigger point are never reused for the Limit leg
        }

        PathPoint? limitTouch = FillPathScanner.TryFindTouchFrom(bar.Open, bar.High, bar.Low, bar.Close, limitPrice, buy ? TouchDirection.Downward : TouchDirection.Upward, limitScanStart);
        return limitTouch is { } fillPoint
            ? Filled(fillPoint, order, slippageRatio, commissionFlat, commissionPerUnit, clampLimit: limitPrice, stopActivated: true)
            : ResidualFill.None(OrderFillOutcome.StopTriggeredNoLimitFill());
    }

    private static ResidualFill Filled(PathPoint fillPoint, BacktestOrder order, decimal slippageRatio, decimal commissionFlat, decimal commissionPerUnit, decimal? clampLimit, bool stopActivated)
    {
        decimal slipped = FillPricing.ApplySlippage(fillPoint.Price, order.Side, slippageRatio);
        decimal fillPrice = clampLimit is { } limit ? FillPricing.ClampToLimit(slipped, order.Side, limit) : slipped;
        return new ResidualFill(BuildFilled(fillPrice, fillPoint.Price, order.Quantity, commissionFlat, commissionPerUnit, stopActivated), fillPoint);
    }

    private static OrderFillOutcome BuildFilled(decimal fillPrice, decimal p0, decimal quantity, decimal commissionFlat, decimal commissionPerUnit, bool stopActivated = false)
    {
        decimal commission = FillPricing.ComputeCommission(commissionFlat, commissionPerUnit, quantity);
        decimal slippageAmount = FillPricing.ComputeSlippageAmount(fillPrice, p0, quantity);
        return new OrderFillOutcome(Filled: true, FillPrice: fillPrice, Commission: commission, SlippageAmount: slippageAmount, StopActivated: stopActivated);
    }
}
