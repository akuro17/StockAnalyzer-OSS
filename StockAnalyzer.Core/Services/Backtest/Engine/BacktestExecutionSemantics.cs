namespace StockAnalyzer.Core.Services.Backtest.Engine;

/// <summary>
/// Single source of truth for the version of the engine's EXECUTION RULES (owner decision G5, A1 (Run fingerprint)).
/// The run fingerprint encodes it, so two runs that differ only in how the engine executes the same inputs never share an identity. Bump it whenever a
/// change alters the outcome of an existing input (fill path priority, liquidation timing, terminal statuses, reversal chronology, cancellation rules...).
/// The legacy ReproducibilityHash is not versioned and is unaffected.
/// </summary>
public static class BacktestExecutionSemantics
{
    /// <summary>
    /// 1 = the P1 correctness set: StopLimit fills rank against a liquidation by their actual fill point (G1/T7a); a liquidation that leaves the account
    /// insolvent expires the superseded orders as Insolvency (G6); a new position is exposed to margin liquidation from its own fill point, a MarketOnClose
    /// Entry breached at its Close is liquidated at the next Open, and a reversal Entry behind a mid-bar Exit fills only as a marketable Limit at the Exit's
    /// reference price (G2/T7b); strategy-requested cancellation is queued at Close and applied at the next Step 1 (G4/T8).
    /// 2 = 1 plus the gap rule: a forced liquidation whose threshold is already at/through the price where it becomes live (the Open of a bar, or the
    /// fill point of a new position) trades at that market price with the liquidation penalty, never at the better threshold price.
    /// 3 = 2 plus strict condition-output resolution: UI aliases for an indicator's primary output are canonicalized to Main,
    /// while an unknown non-primary output is rejected instead of silently falling back to Main.
    /// </summary>
    public const int Version = 3;
}
