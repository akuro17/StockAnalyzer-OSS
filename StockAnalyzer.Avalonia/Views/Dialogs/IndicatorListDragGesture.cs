using System;
using Avalonia.Controls;
using Avalonia.VisualTree;
using StockAnalyzer.Avalonia.ViewModels.Backtest;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

/// <summary>
/// Pointer arithmetic and visual hit-testing helper for dragging indicator rows in IndicatorSettingsWindow.
/// Reuses <see cref="StockAnalyzer.Avalonia.Views.Backtest.ConditionDragGesture.HasMovedFarEnough"/> for distance checks,
/// and uses <see cref="ConditionDropPlacement"/> to represent drop placement relative to target rows.
/// </summary>
public static class IndicatorListDragGesture
{
    public const string RowClass = "IndicatorRow";
    public const string DropBeforeClass = "DropBefore";
    public const string DropAfterClass = "DropAfter";

    /// <summary>
    /// Checks whether pointer press should initiate drag.
    /// Returns false if pointer started inside an interactive child element (e.g. CheckBox or Button)
    /// before reaching rowBoundary, preventing accidental drags on checkbox toggles or delete buttons.
    /// </summary>
    public static bool ShouldStartOnPress(Control? sourceControl, Control rowBoundary)
    {
        if (sourceControl == null) return false;

        var current = sourceControl;
        while (current != null && !ReferenceEquals(current, rowBoundary))
        {
            if (current is CheckBox || current is Button)
            {
                return false;
            }
            current = current.GetVisualParent() as Control;
        }

        return true;
    }

    /// <summary>
    /// Computes whether the drop pointer position corresponds to Before or After the target row.
    /// If relativeY is less than half the row height, returns Before; otherwise After.
    /// Degenerate heights (&lt;= 0 or NaN) safely default to After.
    /// </summary>
    public static ConditionDropPlacement GetRowPlacement(double relativeY, double height)
    {
        if (double.IsNaN(relativeY) || double.IsNaN(height) || height <= 0.0)
        {
            return ConditionDropPlacement.After;
        }

        return relativeY < height * 0.5
            ? ConditionDropPlacement.Before
            : ConditionDropPlacement.After;
    }
}
