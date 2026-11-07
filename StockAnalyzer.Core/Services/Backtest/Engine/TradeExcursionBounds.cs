using System;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Core.Services.Backtest.Engine;

/// <summary>
/// The single definition of which prices of the EXIT bar count as held when a trade's MFE / MAE is measured (the engine's <c>ClosePosition</c> and the RL
/// parity check of the trade ledger both use it). The bar-resolution approximation of the documented excursion stays (the order of High and Low inside a
/// bar is unknown), except for prices that are CERTAIN to come after the exit:
/// a Stop (and a forced margin liquidation) fires when the price reaches its level (Long: falling, Short: rising), so the exit bar's extreme beyond the
/// fill on that adverse side is after the exit; a Limit fires on the favourable side; a Market exit, or any exit filled AT the bar Open (a gap through the
/// level, a liquidation carried over to the Open), holds nothing else of the bar. The boundary is the actual exit fill price (slippage and the
/// liquidation penalty applied). Any other exit type (for example MarketOnClose) holds the whole bar.
/// </summary>
internal static class TradeExcursionBounds
{
    /// <summary>Extends the held range <paramref name="high"/> / <paramref name="low"/> (the entry fill and the bars held before the exit bar) by the exit bar.</summary>
    public static (decimal High, decimal Low) ExtendByExitBar(
        decimal high, decimal low, TradeSide side, CandleData exitBar, decimal fillPrice, OrderType? orderType, bool isForcedLiquidation, bool exitedAtOpen)
    {
        if (orderType == OrderType.Market || exitedAtOpen)
        {
            return (Math.Max(high, fillPrice), Math.Min(low, fillPrice));
        }

        bool adverseExit = orderType == OrderType.Stop || isForcedLiquidation;
        bool limitExit = orderType == OrderType.Limit;
        bool isLong = side == TradeSide.Long;
        bool clipLow = (isLong && adverseExit) || (!isLong && limitExit);
        bool clipHigh = (!isLong && adverseExit) || (isLong && limitExit);
        return (
            Math.Max(high, clipHigh ? fillPrice : Math.Max(fillPrice, exitBar.High)),
            Math.Min(low, clipLow ? fillPrice : Math.Min(fillPrice, exitBar.Low)));
    }
}
