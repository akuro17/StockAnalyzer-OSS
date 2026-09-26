using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Moves objects so that their anchor point (AP) coincides with a reference object's AP.
/// The AP is the control point designated by <see cref="IChartObject.AnchorPointIndex"/> (the same candidate
/// points the "AP" button cycles through). Whole objects are translated, so their shape is preserved.
/// </summary>
public static class AnchorPointSnapper
{
    /// <summary>
    /// Resolves the AP of <paramref name="obj"/>. An out-of-range <see cref="IChartObject.AnchorPointIndex"/> falls back to
    /// the first candidate point (the documented default 0). Returns false when the object has no candidate point.
    /// </summary>
    public static bool TryGetAnchorPoint(IChartObject obj, out ChartPoint anchorPoint)
    {
        var candidates = ChartObjectManager.GetAnchorCandidatePoints(obj);
        if (candidates.Count == 0)
        {
            anchorPoint = default;
            return false;
        }

        int index = obj.AnchorPointIndex;
        anchorPoint = candidates[index >= 0 && index < candidates.Count ? index : 0];
        return true;
    }

    /// <summary>
    /// Translates every child so that its AP equals the parent's AP; the parent never moves. Children without an AP are left
    /// untouched. Bar-locked <see cref="FixedRangeVolumeProfileObject"/> / <see cref="TimeAtPriceObject"/> children move by whole
    /// bars only (the same rule as a drag), using <paramref name="historyProvider"/> for the bar index lookup.
    /// </summary>
    public static void SnapChildrenToParent(
        IChartObject parent,
        IReadOnlyList<IChartObject> children,
        Func<IReadOnlyList<CoreCandleData>?>? historyProvider = null)
    {
        if (!TryGetAnchorPoint(parent, out var target)) return;

        IReadOnlyList<CoreCandleData>? history = null;
        bool historyResolved = false;

        for (int i = 0; i < children.Count; i++)
        {
            var child = children[i];
            if (!TryGetAnchorPoint(child, out var source)) continue;

            if (BarLockedTranslation.IsBarLocked(child))
            {
                if (!historyResolved)
                {
                    history = historyProvider?.Invoke();
                    historyResolved = true;
                }

                if (BarLockedTranslation.TryTranslate(child, source.Time, target.Time, history))
                {
                    continue;
                }
            }

            child.Translate(target.Time - source.Time, target.Price - source.Price);
        }
    }
}
