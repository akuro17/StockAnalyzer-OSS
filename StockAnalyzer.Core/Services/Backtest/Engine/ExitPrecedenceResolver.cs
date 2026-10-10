using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Core.Services.Backtest.Engine;

public enum ExitWinner { None, PendingOrder, MarginLiquidation }

public enum SlTpWinner { None, StopLoss, TakeProfit }

/// <summary>
/// User-confirmed rule (Y:\Temp\sa_ai_context_BacktestEngine_P1.md section 1.5.6): when a pending
/// exit order (Limit/Stop/StopLimit) and the maintenance-margin liquidation threshold both apply to the same
/// open position on the same bar, whichever ACTUALLY HAPPENS first (the order's fill, not its activation) along the fixed
/// Open-&gt;High-&gt;Low-&gt;Close path wins; the loser is superseded/cancelled for that bar rather than
/// evaluated normally.
/// </summary>
public static class ExitPrecedenceResolver
{
    /// <param name="pendingOrderPosition">Path position where the resting exit order would actually fill (<see cref="OrderFillEvaluator.TryFindFillPosition"/>), or null if it does not fill this bar.</param>
    /// <param name="marginLiquidationPosition">Result of <see cref="MarginLiquidationCalculator.TryFindBreachPosition"/>, or null if the maintenance margin is not breached this bar.</param>
    public static ExitWinner Resolve(decimal? pendingOrderPosition, decimal? marginLiquidationPosition)
    {
        if (pendingOrderPosition is null && marginLiquidationPosition is null) return ExitWinner.None;
        if (pendingOrderPosition is null) return ExitWinner.MarginLiquidation;
        if (marginLiquidationPosition is null) return ExitWinner.PendingOrder;

        if (pendingOrderPosition.Value < marginLiquidationPosition.Value) return ExitWinner.PendingOrder;
        if (marginLiquidationPosition.Value < pendingOrderPosition.Value) return ExitWinner.MarginLiquidation;

        // Exact tie (e.g. both conditions are already true at Open). USER-CONFIRMED (2026-09-16):
        // margin liquidation is treated as the broker-mandated compliance action and wins the tie.
        return ExitWinner.MarginLiquidation;
    }

    /// <summary>
    /// Resolves precedence between Stop-Loss (SL) and Take-Profit (TP) candidates on the same bar.
    /// Invariant: If SL and TP both trigger within the same segment or at the exact same position,
    /// SL (loss realization) unconditionally wins (pessimistic / conservative risk management principle).
    /// </summary>
    public static SlTpWinner ResolveSlTp(decimal? stopLossPosition, decimal? takeProfitPosition)
    {
        if (stopLossPosition is null && takeProfitPosition is null) return SlTpWinner.None;
        if (stopLossPosition is null) return SlTpWinner.TakeProfit;
        if (takeProfitPosition is null) return SlTpWinner.StopLoss;

        // Exact tie or same segment: SL unconditionally wins
        if (stopLossPosition.Value == takeProfitPosition.Value ||
            (int)stopLossPosition.Value == (int)takeProfitPosition.Value)
        {
            return SlTpWinner.StopLoss;
        }

        return stopLossPosition.Value < takeProfitPosition.Value ? SlTpWinner.StopLoss : SlTpWinner.TakeProfit;
    }

    /// <summary>
    /// Resolves precedence between Stop-Loss (SL) and Take-Profit (TP) breaches on the same candle.
    /// When both thresholds are breached:
    /// - Conservative: SL unconditionally wins (loss realization / worst-case principle).
    /// - CanonicalOHLC: Follows standard OHLC path progression:
    ///   - Bullish candle (Close &gt; Open): O -&gt; L -&gt; H -&gt; C.
    ///     Long: touches SL (Low) first =&gt; StopLoss wins.
    ///     Short: touches TP (Low) first =&gt; TakeProfit wins.
    ///   - Bearish candle (Close &lt; Open): O -&gt; H -&gt; L -&gt; C.
    ///     Long: touches TP (High) first =&gt; TakeProfit wins.
    ///     Short: touches SL (High) first =&gt; StopLoss wins.
    ///   - Doji (Close == Open): Falls back to Conservative (StopLoss wins).
    /// Gaps through thresholds at Open resolve immediately at Open before intrabar path.
    /// </summary>
    public static SlTpWinner ResolveIntrabarSlTp(
        CandleData bar,
        TradeSide side,
        decimal stopPrice,
        decimal limitPrice,
        PricePathConflictMode mode = PricePathConflictMode.Conservative)
    {
        bool isLong = side == TradeSide.Long;
        bool slBreached = isLong ? bar.Low <= stopPrice : bar.High >= stopPrice;
        bool tpBreached = isLong ? bar.High >= limitPrice : bar.Low <= limitPrice;

        if (!slBreached && !tpBreached) return SlTpWinner.None;
        if (slBreached && !tpBreached) return SlTpWinner.StopLoss;
        if (!slBreached && tpBreached) return SlTpWinner.TakeProfit;

        // Both breached on the same bar
        if (mode == PricePathConflictMode.Conservative)
        {
            return SlTpWinner.StopLoss;
        }

        // CanonicalOHLC mode:
        if (isLong)
        {
            if (bar.Open <= stopPrice) return SlTpWinner.StopLoss;
            if (bar.Open >= limitPrice) return SlTpWinner.TakeProfit;

            if (bar.Close > bar.Open)
            {
                // Bullish: O -> L -> H -> C. Low (SL) visited first.
                return SlTpWinner.StopLoss;
            }
            if (bar.Close < bar.Open)
            {
                // Bearish: O -> H -> L -> C. High (TP) visited first.
                return SlTpWinner.TakeProfit;
            }

            // Doji (Close == Open): fallback to conservative
            return SlTpWinner.StopLoss;
        }
        else // Short
        {
            if (bar.Open >= stopPrice) return SlTpWinner.StopLoss;
            if (bar.Open <= limitPrice) return SlTpWinner.TakeProfit;

            if (bar.Close > bar.Open)
            {
                // Bullish: O -> L -> H -> C. Low (TP) visited first.
                return SlTpWinner.TakeProfit;
            }
            if (bar.Close < bar.Open)
            {
                // Bearish: O -> H -> L -> C. High (SL) visited first.
                return SlTpWinner.StopLoss;
            }

            // Doji (Close == Open): fallback to conservative
            return SlTpWinner.StopLoss;
        }
    }
}
