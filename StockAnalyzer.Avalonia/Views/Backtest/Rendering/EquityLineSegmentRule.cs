using System;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Avalonia.Views.Backtest.Rendering;

/// <summary>
/// Pure (Avalonia-free, allocation-free) decision of which color class an equity-curve segment belongs to.
/// Segment k (1 &lt;= k &lt;= N-1) joins drawn points k-1 and k and reads only those two points (and their flags), so the
/// result never depends on later bars and does not change when a viewport slice later starts or ends at either point.
/// Drawdown reads the end point k only: a segment that reaches the running high (or ties it) is up, one that ends below it is down.
/// </summary>
public static class EquityLineSegmentRule
{
    /// <summary>
    /// Whether the curve is drawn in two classes (up/down) for this input. <see cref="BacktestEquityColorMode.Single"/>, fewer than two
    /// points, and a <see cref="BacktestEquityColorMode.Drawdown"/> request whose flags are unavailable or not one per point all
    /// draw a single-color line.
    /// </summary>
    public static bool IsSegmented(BacktestEquityColorMode mode, int pointCount, ImmutableArray<bool> underwaterFlags) =>
        pointCount >= 2 && mode switch
        {
            BacktestEquityColorMode.PreviousBar => true,
            BacktestEquityColorMode.Drawdown => !underwaterFlags.IsDefault && underwaterFlags.Length == pointCount,
            _ => false,
        };

    /// <summary>
    /// True when segment <paramref name="k"/> uses the up color. PreviousBar: end equity &gt;= start equity (equal is up).
    /// Drawdown: the end point is not in drawdown, i.e. it sets or ties the running high (the segment that recovers to the high is therefore up).
    /// Single: always up (the caller draws it in the line color and never asks per segment).
    /// </summary>
    public static bool IsUpSegment(BacktestEquityColorMode mode, ImmutableArray<EquityPoint> points, ImmutableArray<bool> underwaterFlags, int k)
    {
        if (k < 1 || k >= points.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(k), k, "Segment index must join two drawn points.");
        }

        switch (mode)
        {
            case BacktestEquityColorMode.PreviousBar:
                return points[k].Equity >= points[k - 1].Equity;
            case BacktestEquityColorMode.Drawdown:
                if (underwaterFlags.IsDefault || underwaterFlags.Length != points.Length)
                {
                    throw new ArgumentException("Drawdown segments need one underwater flag per point.", nameof(underwaterFlags));
                }
                return !underwaterFlags[k];
            default:
                return true;
        }
    }
}
