using System;
using Avalonia;
using StockAnalyzer.Avalonia.ViewModels.Backtest;

namespace StockAnalyzer.Avalonia.Views.Backtest;

/// <summary>Pure pointer arithmetic of dragging a condition row (no control, no tree access), so it is testable without a drag loop.</summary>
internal static class ConditionDragGesture
{
    /// <summary>Style class of a condition row (the Border the drop indicator is drawn on); the selectors of BacktestResultsView.axaml use the same names.</summary>
    public const string RowClass = "ConditionRow";

    /// <summary>Style classes of the drop indicator: a line above the row, a line below it, an outline around the row; see BacktestResultsView.axaml.</summary>
    public const string DropBeforeClass = "DropBefore";
    public const string DropAfterClass = "DropAfter";
    public const string DropInsideClass = "DropInside";

    /// <summary>True once the pointer has travelled at least <paramref name="distance"/> DIPs from where it was pressed, along either axis.</summary>
    public static bool HasMovedFarEnough(global::Avalonia.Point pressed, global::Avalonia.Point current, double distance)
        => Math.Max(Math.Abs(current.X - pressed.X), Math.Abs(current.Y - pressed.Y)) >= distance;

    /// <summary>
    /// The placement for a drop on <paramref name="target"/> at vertical position <paramref name="y"/> of its row (height <paramref name="rowHeight"/>):
    /// a group row takes the condition at its end; a comparison row takes it before (upper half) or after (lower half).
    /// </summary>
    public static ConditionDropPlacement PlacementFor(BacktestConditionNodeViewModel target, double y, double rowHeight)
        => target is BacktestConditionGroupViewModel
            ? ConditionDropPlacement.Inside
            : y < rowHeight / 2 ? ConditionDropPlacement.Before : ConditionDropPlacement.After;
}
