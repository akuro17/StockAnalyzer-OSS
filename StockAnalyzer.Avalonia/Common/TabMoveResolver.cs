using System;

namespace StockAnalyzer.Avalonia.Common;

/// <summary>Result of resolving a requested tab move against the current tab count.</summary>
public enum TabMoveOutcome
{
    /// <summary>A move from the source index to the resolved target index must be applied.</summary>
    Move,

    /// <summary>The request resolves to the source index; nothing to do.</summary>
    NoChange,

    /// <summary>The source index is not a valid index of the current tab collection.</summary>
    SourceOutOfRange
}

/// <summary>Clamp steps that changed the requested target while resolving a tab move (diagnostics only).</summary>
[Flags]
public enum TabMoveClamps
{
    None = 0,

    /// <summary>The requested index distance exceeded the maximum and was limited.</summary>
    DistanceLimited = 1,

    /// <summary>The (possibly distance-limited) target was negative and was raised to the first index.</summary>
    RaisedToFirst = 2,

    /// <summary>The (possibly distance-limited) target was beyond the last index and was lowered to it.</summary>
    LoweredToLast = 4
}

/// <summary>
/// Pure resolution of a tab move request (bar-independent, UI-independent) and the single definition of the
/// tab-move rules shared by panel tabs (<c>MainWindowViewModel.ReorderTab</c>) and Tab Window tabs
/// (<c>DetachedWindowViewModel.MoveTab</c>).
/// Step order: bounds check, no-op check, distance clamp, index clamp, no-op check.
/// </summary>
public static class TabMoveResolver
{
    /// <param name="count">Current number of tabs.</param>
    /// <param name="sourceIndex">0-based index of the dragged tab.</param>
    /// <param name="requestedTarget">0-based requested target index (may be out of range).</param>
    /// <param name="maxDistance">Maximum allowed index distance of one move (must be &gt;= 0).</param>
    /// <param name="resolvedTarget">Target index to move to; equals <paramref name="sourceIndex"/> unless the result is <see cref="TabMoveOutcome.Move"/>.</param>
    public static TabMoveOutcome Resolve(int count, int sourceIndex, int requestedTarget, int maxDistance, out int resolvedTarget)
        => Resolve(count, sourceIndex, requestedTarget, maxDistance, out resolvedTarget, out _);

    /// <summary>
    /// Same as the five-argument overload, additionally reporting which clamp steps changed the requested target.
    /// <paramref name="clamps"/> is <see cref="TabMoveClamps.None"/> when the outcome is
    /// <see cref="TabMoveOutcome.SourceOutOfRange"/> or when the request was already equal to the source index.
    /// </summary>
    public static TabMoveOutcome Resolve(int count, int sourceIndex, int requestedTarget, int maxDistance, out int resolvedTarget, out TabMoveClamps clamps)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxDistance);

        resolvedTarget = sourceIndex;
        clamps = TabMoveClamps.None;

        if (sourceIndex < 0 || sourceIndex >= count)
        {
            return TabMoveOutcome.SourceOutOfRange;
        }

        if (requestedTarget == sourceIndex)
        {
            return TabMoveOutcome.NoChange;
        }

        long distance = Math.Abs((long)requestedTarget - sourceIndex);
        if (distance > maxDistance)
        {
            requestedTarget = sourceIndex + (maxDistance * (requestedTarget > sourceIndex ? 1 : -1));
            clamps |= TabMoveClamps.DistanceLimited;
        }

        if (requestedTarget < 0)
        {
            requestedTarget = 0;
            clamps |= TabMoveClamps.RaisedToFirst;
        }
        else if (requestedTarget >= count)
        {
            requestedTarget = count - 1;
            clamps |= TabMoveClamps.LoweredToLast;
        }

        if (requestedTarget == sourceIndex)
        {
            return TabMoveOutcome.NoChange;
        }

        resolvedTarget = requestedTarget;
        return TabMoveOutcome.Move;
    }
}
