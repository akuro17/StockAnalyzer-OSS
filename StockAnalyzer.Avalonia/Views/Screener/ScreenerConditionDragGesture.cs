using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Avalonia.ViewModels.Screener;

namespace StockAnalyzer.Avalonia.Views.Screener;

/// <summary>
/// Pure pointer arithmetic of dragging a Filters condition row (no control, no tree access), so it is testable without a
/// drag loop. Mirrors <see cref="StockAnalyzer.Avalonia.Views.Backtest.ConditionDragGesture"/>
/// (Y:\Temp\sa_implementation_plan_ScreenerFilterConditionDragMove.md Task 4): the pointer-distance check
/// (<see cref="StockAnalyzer.Avalonia.Views.Backtest.ConditionDragGesture.HasMovedFarEnough"/>) is type-independent and
/// reused directly rather than duplicated here, but <c>PlacementFor</c> is bound to <see cref="ScreenerConditionNodeViewModel"/>
/// (a type the Backtest version does not know), so it is a new, parallel method rather than an extension of the shared one.
/// </summary>
internal static class ScreenerConditionDragGesture
{
    /// <summary>Style class of a condition row (the Border the drop indicator is drawn on); matches the class already applied to both
    /// row templates in ScreenerWindow.axaml and the selectors that style the drop indicator there.</summary>
    public const string RowClass = "ConditionRow";

    /// <summary>Style classes of the drop indicator: a line above the row, a line below it, an outline around the row; see ScreenerWindow.axaml.</summary>
    public const string DropBeforeClass = "DropBefore";
    public const string DropAfterClass = "DropAfter";
    public const string DropInsideClass = "DropInside";

    /// <summary>
    /// The placement for a drop on <paramref name="target"/> at vertical position <paramref name="y"/> of its row (height <paramref name="rowHeight"/>):
    /// a group row takes the condition at its end; a leaf row takes it before (upper half) or after (lower half).
    /// </summary>
    public static ConditionDropPlacement PlacementFor(ScreenerConditionNodeViewModel target, double y, double rowHeight)
        => target is ScreenerConditionGroupViewModel
            ? ConditionDropPlacement.Inside
            : y < rowHeight / 2 ? ConditionDropPlacement.Before : ConditionDropPlacement.After;
}
