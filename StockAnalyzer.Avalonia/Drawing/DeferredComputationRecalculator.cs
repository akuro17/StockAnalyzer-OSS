using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// SSoT dispatcher for recalculating drawing objects whose derived state (regression fit,
/// extracted spline points, volume histogram) is deferred out of the constructor into a
/// separate Recalculate(candles) call. Several independent call sites -- two-click
/// creation live preview, drag-release, and restored-drawing recalculation -- each need to
/// dispatch by concrete type to the matching Recalculate() overload with the same
/// IReadOnlyList&lt;CoreCandleData&gt; candle set; this keeps that type-dispatch mapping in
/// one place instead of duplicated inline at every call site.
///
/// Deliberately NOT used by ChartInteractionController.HandleObjectDrag's live-during-drag
/// path: that path intentionally excludes FixedRangeVolumeProfileObject (its 50-bin
/// histogram recompute is still deferred to release for performance), so it keeps its own
/// narrower two-type dispatch rather than risking silently recalculating FRVP live too.
/// </summary>
public static class DeferredComputationRecalculator
{
    /// <summary>
    /// Recalculates <paramref name="obj"/> if it is one of the known deferred-computation
    /// drawing object types (<see cref="RegressionTrendObject"/>, <see cref="RangeSplineObject"/>,
    /// <see cref="FixedRangeVolumeProfileObject"/>). Returns true if a recalculation was
    /// performed, false if <paramref name="obj"/> is not one of those types.
    /// </summary>
    public static bool TryRecalculate(IChartObject obj, IReadOnlyList<CoreCandleData> candles)
    {
        switch (obj)
        {
            case RegressionTrendObject reg:
                reg.Recalculate(candles);
                return true;
            case RangeSplineObject rangeSpline:
                rangeSpline.Recalculate(candles);
                return true;
            case FixedRangeVolumeProfileObject frvp:
                frvp.Recalculate(candles);
                return true;
            case Objects.KalmanFilterProjectionObject kalman:
                kalman.Recalculate(candles);
                return true;
            default:
                return false;
        }
    }
}
