namespace StockAnalyzer.Core.Models.Backtest.Engine;

/// <summary>
/// Which role a <see cref="BacktestConditionEntry"/> plays: it can gate a new position
/// (<see cref="EntryOnly"/>), gate closing an existing one (<see cref="ExitOnly"/>), do both
/// side-agnostically off one shared boolean (<see cref="Both"/>) — e.g. a single "Close &lt; SMA(20)"
/// entry used as both the short-entry trigger and the long-exit trigger — or drive a same-bar
/// directional reversal (<see cref="Reversal"/>). See
/// Y:\Temp\sa_implementation_plan_BacktestComparisonSignals.md section 4.3 for how <see cref="EntryOnly"/>/
/// <see cref="ExitOnly"/>/<see cref="Both"/> combine per direction inside
/// <c>ConditionBasedBacktestStrategy</c>.
/// </summary>
public enum BacktestConditionRole
{
    EntryOnly,
    ExitOnly,
    Both,

    /// <summary>
    /// New role (Y:\Temp\sa_implementation_plan_BacktestPositionDirectionReversal.md section 2.2, Task 2
    /// correction): distinct from <see cref="Both"/> by design, rather than overloading Both's existing,
    /// already-relied-upon side-agnostic meaning with a second, runtime-conditional meaning. A Reversal
    /// entry's own <see cref="BacktestConditionEntry.Position"/> is the direction it opens: while flat or
    /// while holding that SAME direction already, it behaves like an <see cref="EntryOnly"/> entry (no
    /// exit contribution at all — never joins the generic side-agnostic exit chain <see cref="Both"/> and
    /// <see cref="ExitOnly"/> share); while holding the OPPOSITE direction, it requests opening its own
    /// Position's direction AND, independently, closing the currently-held opposite side the same bar
    /// (the exact "Both button, Position selected -> Entry=Position, Exit=opposite" behavior the user
    /// originally requested for the UI's Both button, which now emits this role instead of
    /// <see cref="Both"/>). Kept as its own enum value instead of a flag on <see cref="Both"/> so every
    /// existing <see cref="Both"/> entry — including ones predating this field — keeps its exact original,
    /// unconditional behavior with zero new branching.
    /// </summary>
    Reversal,
}
