using System.Collections.Generic;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// A range-analysis drawing whose range is always exactly <see cref="Period"/> bars long after an edit (the Robust Regression Trend; settings expansion DS-6 = B). The snap changes the two anchor times and
/// possibly <see cref="Period"/>; it is called explicitly after a user-confirmed edit (see <see cref="DrawingRecalculation.SnapRangeAfterEdit"/>) and never from a recalculation, a load or a data refresh.
/// </summary>
public interface IPeriodSnappedRange
{
    /// <summary>The number of bars of the range.</summary>
    int Period { get; set; }

    /// <summary>
    /// Snaps the anchors to <see cref="Period"/> bars of <paramref name="candles"/> (see <see cref="DrawingRangeSnap"/>). Returns true when an anchor time or the period changed. Does not recalculate.
    /// </summary>
    bool SnapToPeriod(IReadOnlyList<CoreCandleData> candles, RangeSnapKind kind, int draggedPointIndex = -1);
}
