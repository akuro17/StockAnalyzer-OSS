using System;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Avalonia.Views.Backtest.Rendering;

/// <summary>
/// The single definition of a drawable equity series: every timestamp is UTC and the timestamps strictly increase (chronological order is
/// never repaired, an invalid series is reported). The layout, the trade markers and the viewport all depend on exactly this rule.
/// </summary>
public static class EquitySeriesRule
{
    /// <summary>True for a non-empty series that satisfies the rule.</summary>
    public static bool IsValid(ImmutableArray<EquityPoint> points) => TryValidate(points, out _);

    /// <summary>
    /// Validates in one pass and reports the smallest spacing between two neighbouring points in ticks (0 for a one-point series).
    /// False for an empty or default array and for any violation.
    /// </summary>
    public static bool TryValidate(ImmutableArray<EquityPoint> points, out long minSpacingTicks)
    {
        minSpacingTicks = 0;
        if (points.IsDefaultOrEmpty || points[0].Timestamp.Kind != DateTimeKind.Utc)
        {
            return false;
        }

        long minSpacing = long.MaxValue;
        for (int i = 1; i < points.Length; i++)
        {
            long spacing = points[i].Timestamp.Ticks - points[i - 1].Timestamp.Ticks;
            if (points[i].Timestamp.Kind != DateTimeKind.Utc || spacing <= 0)
            {
                return false;
            }
            if (spacing < minSpacing) minSpacing = spacing;
        }

        minSpacingTicks = points.Length == 1 ? 0 : minSpacing;
        return true;
    }
}
