namespace StockAnalyzer.Core.Models.Backtest.Engine;

/// <summary>
/// The three top-level sections of a <see cref="BacktestConditionTree"/>. Each section has one root per
/// <see cref="TradeSide"/>: Entry opens that side, Exit closes that side, Reverse opens that side while the
/// opposite side is held (and closes the held side in the same bar, i.e. the legacy
/// <see cref="BacktestConditionRole.Reversal"/> behavior).
/// </summary>
public enum BacktestConditionSection
{
    // The numeric values are fixed for readability only: the persisted and hashed order of the roots is BacktestConditionTree.CanonicalRoots
    // (written explicitly by root name), never the enum value, so reordering or renumbering this enum cannot change a saved file or a fingerprint.
    Entry = 0,
    Exit = 1,
    Reverse = 2,
}
