using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// SSoT for moving bar-locked drawing objects. A <see cref="FixedRangeVolumeProfileObject"/> or
/// <see cref="TimeAtPriceObject"/> with <c>LockRange</c> keeps its bar count, so it is translated by a whole number of
/// bars (resolved against the full candle history) and never vertically, instead of by a time/price displacement.
/// </summary>
public static class BarLockedTranslation
{
    /// <summary>True for a range-locked object whose move is expressed in bars.</summary>
    public static bool IsBarLocked(IChartObject obj)
        => obj is FixedRangeVolumeProfileObject { LockRange: true } || obj is TimeAtPriceObject { LockRange: true };

    /// <summary>
    /// Translates a bar-locked object by the bar distance between <paramref name="fromTime"/> and <paramref name="toTime"/>.
    /// Returns false (object untouched) when the object is not bar-locked or <paramref name="history"/> is empty.
    /// </summary>
    public static bool TryTranslate(
        IChartObject obj,
        DateTime fromTime,
        DateTime toTime,
        IReadOnlyList<CoreCandleData>? history)
    {
        if (history == null || history.Count == 0) return false;

        switch (obj)
        {
            case FixedRangeVolumeProfileObject { LockRange: true } frvp:
                frvp.TranslateBars(
                    FixedRangeVolumeProfileObject.FindNearestBarIndex(toTime, history)
                    - FixedRangeVolumeProfileObject.FindNearestBarIndex(fromTime, history), history);
                return true;
            case TimeAtPriceObject { LockRange: true } tap:
                tap.TranslateBars(
                    TimeAtPriceObject.FindNearestBarIndex(toTime, history)
                    - TimeAtPriceObject.FindNearestBarIndex(fromTime, history), history);
                return true;
            default:
                return false;
        }
    }
}
