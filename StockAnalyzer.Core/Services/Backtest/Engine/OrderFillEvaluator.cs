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
    public static OrderFillOutcome Evaluate(BacktestOrder order, CandleData bar, decimal slippageRatio, decimal commissionFlat, decimal commissionPerUnit)
    {
        return order.Type switch
        {
            OrderType.Market => EvaluateMarket(order, bar, slippageRatio, commissionFlat, commissionPerUnit),
            OrderType.Limit => EvaluateLimit(order, bar, slippageRatio, commissionFlat, commissionPerUnit),
            OrderType.Stop => EvaluateStop(order, bar, slippageRatio, commissionFlat, commissionPerUnit),
            OrderType.StopLimit => EvaluateStopLimit(order, bar, slippageRatio, commissionFlat, commissionPerUnit),
            OrderType.MarketOnClose => throw new ArgumentException("MarketOnClose orders must be evaluated via EvaluateMarketOnClose (a separate bar-processing step), not Evaluate.", nameof(order)),
            _ => throw new ArgumentOutOfRangeException(nameof(order), order.Type, "Unknown OrderType."),
        };
    }

    private static OrderFillOutcome EvaluateMarket(BacktestOrder order, CandleData bar, decimal slippageRatio, decimal commissionFlat, decimal commissionPerUnit)
    {
        decimal p0 = bar.Open;
        decimal fillPrice = FillPricing.ApplySlippage(p0, order.Side, slippageRatio);
        return BuildFilled(fillPrice, p0, order.Quantity, commissionFlat, commissionPerUnit);
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

    private static OrderFillOutcome EvaluateLimit(BacktestOrder order, CandleData bar, decimal slippageRatio, decimal commissionFlat, decimal commissionPerUnit)
    {
        decimal limitPrice = order.LimitPrice ?? throw new ArgumentException("Limit orders require LimitPrice.", nameof(order));
        TouchDirection direction = order.Side == OrderSide.Buy ? TouchDirection.Downward : TouchDirection.Upward;
        decimal? position = FillPathScanner.TryFindTouchPosition(bar.Open, bar.High, bar.Low, bar.Close, limitPrice, direction);
        if (position is null) return OrderFillOutcome.NoFill;

        decimal p0 = position == 0m ? bar.Open : limitPrice;
        decimal slipped = FillPricing.ApplySlippage(p0, order.Side, slippageRatio);
        decimal fillPrice = FillPricing.ClampToLimit(slipped, order.Side, limitPrice);
        return BuildFilled(fillPrice, p0, order.Quantity, commissionFlat, commissionPerUnit);
    }

    private static OrderFillOutcome EvaluateStop(BacktestOrder order, CandleData bar, decimal slippageRatio, decimal commissionFlat, decimal commissionPerUnit)
    {
        decimal stopPrice = order.StopPrice ?? throw new ArgumentException("Stop orders require StopPrice.", nameof(order));
        TouchDirection direction = order.Side == OrderSide.Buy ? TouchDirection.Upward : TouchDirection.Downward;
        decimal? position = FillPathScanner.TryFindTouchPosition(bar.Open, bar.High, bar.Low, bar.Close, stopPrice, direction);
        if (position is null) return OrderFillOutcome.NoFill;

        decimal p0 = position == 0m ? bar.Open : stopPrice;
        // Once triggered, a Stop order becomes an unprotected Market fill - no ClampToLimit.
        decimal fillPrice = FillPricing.ApplySlippage(p0, order.Side, slippageRatio);
        return BuildFilled(fillPrice, p0, order.Quantity, commissionFlat, commissionPerUnit);
    }

    /// <summary>
    /// See the class remarks in FillPathScanner and the derivation in
    /// Y:\Temp\sa_ai_context_BacktestEngine_P1.md section 1.5.6-adjacent notes: once the Stop leg
    /// triggers, the subsequent Limit scan may NOT reuse any bar extreme already passed before the
    /// trigger point (bar extremes seen before the trigger point must never be reused for the fill).
    /// For a Buy StopLimit the trigger is
    /// an Upward touch (at Open, or partway up the Open-&gt;High leg) so High/Low are always still
    /// "ahead" and the Downward Limit scan safely reuses the whole bar's High/Low. For a Sell
    /// StopLimit the trigger is a Downward touch (at Open, or partway down the High-&gt;Low leg); if it
    /// triggers partway down that leg, High has already been consumed, so the post-trigger Upward
    /// Limit scan may only use a subsequent Low-&gt;Close recovery (bar.Close), not bar.High.
    /// </summary>
    private static OrderFillOutcome EvaluateStopLimit(BacktestOrder order, CandleData bar, decimal slippageRatio, decimal commissionFlat, decimal commissionPerUnit)
    {
        decimal limitPrice = order.LimitPrice ?? throw new ArgumentException("StopLimit orders require LimitPrice.", nameof(order));
        decimal stopPrice = order.StopPrice ?? throw new ArgumentException("StopLimit orders require StopPrice.", nameof(order));

        return order.Side == OrderSide.Buy
            ? EvaluateBuyStopLimit(order, bar, stopPrice, limitPrice, slippageRatio, commissionFlat, commissionPerUnit)
            : EvaluateSellStopLimit(order, bar, stopPrice, limitPrice, slippageRatio, commissionFlat, commissionPerUnit);
    }

    private static OrderFillOutcome EvaluateBuyStopLimit(BacktestOrder order, CandleData bar, decimal stopPrice, decimal limitPrice, decimal slippageRatio, decimal commissionFlat, decimal commissionPerUnit)
    {
        if (!order.StopActivated)
        {
            decimal? triggerPosition = FillPathScanner.TryFindTouchPosition(bar.Open, bar.High, bar.Low, bar.Close, stopPrice, TouchDirection.Upward);
            if (triggerPosition is null) return OrderFillOutcome.NoFill;

            decimal triggerPrice = triggerPosition == 0m ? bar.Open : stopPrice;
            if (triggerPrice <= limitPrice)
            {
                decimal slipped0 = FillPricing.ApplySlippage(triggerPrice, order.Side, slippageRatio);
                return BuildFilled(FillPricing.ClampToLimit(slipped0, order.Side, limitPrice), triggerPrice, order.Quantity, commissionFlat, commissionPerUnit, stopActivated: true);
            }
            // Not yet at/below the limit: the whole High->Low leg remains available (nothing consumed before an Upward trigger).
            if (bar.Low <= limitPrice)
            {
                decimal slipped = FillPricing.ApplySlippage(limitPrice, order.Side, slippageRatio);
                return BuildFilled(FillPricing.ClampToLimit(slipped, order.Side, limitPrice), limitPrice, order.Quantity, commissionFlat, commissionPerUnit, stopActivated: true);
            }
            return OrderFillOutcome.StopTriggeredNoLimitFill();
        }

        // Already activated on a prior bar: scan this whole bar fresh for the Downward Limit condition.
        decimal? position = FillPathScanner.TryFindTouchPosition(bar.Open, bar.High, bar.Low, bar.Close, limitPrice, TouchDirection.Downward);
        if (position is null) return OrderFillOutcome.StopTriggeredNoLimitFill();
        decimal p0 = position == 0m ? bar.Open : limitPrice;
        decimal slippedFill = FillPricing.ApplySlippage(p0, order.Side, slippageRatio);
        return BuildFilled(FillPricing.ClampToLimit(slippedFill, order.Side, limitPrice), p0, order.Quantity, commissionFlat, commissionPerUnit, stopActivated: true);
    }

    private static OrderFillOutcome EvaluateSellStopLimit(BacktestOrder order, CandleData bar, decimal stopPrice, decimal limitPrice, decimal slippageRatio, decimal commissionFlat, decimal commissionPerUnit)
    {
        if (!order.StopActivated)
        {
            decimal? triggerPosition = FillPathScanner.TryFindTouchPosition(bar.Open, bar.High, bar.Low, bar.Close, stopPrice, TouchDirection.Downward);
            if (triggerPosition is null) return OrderFillOutcome.NoFill;

            decimal triggerPrice = triggerPosition == 0m ? bar.Open : stopPrice;
            if (triggerPrice >= limitPrice)
            {
                decimal slipped0 = FillPricing.ApplySlippage(triggerPrice, order.Side, slippageRatio);
                return BuildFilled(FillPricing.ClampToLimit(slipped0, order.Side, limitPrice), triggerPrice, order.Quantity, commissionFlat, commissionPerUnit, stopActivated: true);
            }

            if (triggerPosition == 0m)
            {
                // Triggered at Open: nothing consumed yet, the whole bar's Open->High leg is still available.
                decimal? wholeBarPosition = FillPathScanner.TryFindTouchPosition(bar.Open, bar.High, bar.Low, bar.Close, limitPrice, TouchDirection.Upward);
                if (wholeBarPosition is not null)
                {
                    decimal p0 = wholeBarPosition == 0m ? bar.Open : limitPrice;
                    decimal slipped = FillPricing.ApplySlippage(p0, order.Side, slippageRatio);
                    return BuildFilled(FillPricing.ClampToLimit(slipped, order.Side, limitPrice), p0, order.Quantity, commissionFlat, commissionPerUnit, stopActivated: true);
                }
                return OrderFillOutcome.StopTriggeredNoLimitFill();
            }

            // Triggered partway down the High->Low leg: High is already consumed (spec: bar extremes
            // seen before the trigger point must never be reused for the fill). Only a Low->Close
            // recovery up to Close remains available.
            if (bar.Close >= limitPrice)
            {
                decimal slipped = FillPricing.ApplySlippage(limitPrice, order.Side, slippageRatio);
                return BuildFilled(FillPricing.ClampToLimit(slipped, order.Side, limitPrice), limitPrice, order.Quantity, commissionFlat, commissionPerUnit, stopActivated: true);
            }
            return OrderFillOutcome.StopTriggeredNoLimitFill();
        }

        // Already activated on a prior bar: scan this whole bar fresh for the Upward Limit condition.
        decimal? position = FillPathScanner.TryFindTouchPosition(bar.Open, bar.High, bar.Low, bar.Close, limitPrice, TouchDirection.Upward);
        if (position is null) return OrderFillOutcome.StopTriggeredNoLimitFill();
        decimal fillP0 = position == 0m ? bar.Open : limitPrice;
        decimal slippedFill = FillPricing.ApplySlippage(fillP0, order.Side, slippageRatio);
        return BuildFilled(FillPricing.ClampToLimit(slippedFill, order.Side, limitPrice), fillP0, order.Quantity, commissionFlat, commissionPerUnit, stopActivated: true);
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

    private static OrderFillOutcome BuildFilled(decimal fillPrice, decimal p0, decimal quantity, decimal commissionFlat, decimal commissionPerUnit, bool stopActivated = false)
    {
        decimal commission = FillPricing.ComputeCommission(commissionFlat, commissionPerUnit, quantity);
        decimal slippageAmount = FillPricing.ComputeSlippageAmount(fillPrice, p0, quantity);
        return new OrderFillOutcome(Filled: true, FillPrice: fillPrice, Commission: commission, SlippageAmount: slippageAmount, StopActivated: stopActivated);
    }
}
