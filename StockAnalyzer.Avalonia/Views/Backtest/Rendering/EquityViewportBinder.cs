using System;
using System.Collections.Immutable;
using Avalonia;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Avalonia.Views.Backtest.Rendering;

/// <summary>The time extent of an equity series: first/last instant (UTC) and the smallest positive spacing between two points.</summary>
public readonly record struct EquityDataBounds(DateTime First, DateTime Last, TimeSpan Interval);

/// <summary>
/// Drives the shared <see cref="ViewportManager"/> for the equity chart (which is independent of the main chart's viewport). The
/// manager is not modified; this class only fixes the order in which it must be fed and the equity-specific policies around it.
/// Units are DateTime ticks, as for the main chart. Avalonia-UI-free (needs no UI session).
/// </summary>
public static class EquityViewportBinder
{
    /// <summary>
    /// Reads the extent of a drawable series. False when there are fewer than two points or the series breaks <see cref="EquitySeriesRule"/>
    /// (the layout reports such a series as Unavailable, so there is nothing to zoom). A true result also proves the series valid.
    /// </summary>
    public static bool TryResolveDataBounds(ImmutableArray<EquityPoint> points, out EquityDataBounds bounds)
    {
        bounds = default;
        if (points.IsDefaultOrEmpty || points.Length < 2 || !EquitySeriesRule.TryValidate(points, out long minSpacingTicks))
        {
            return false;
        }

        bounds = new EquityDataBounds(points[0].Timestamp, points[^1].Timestamp, TimeSpan.FromTicks(minSpacingTicks));
        return true;
    }

    /// <summary>
    /// Loads a new series into <paramref name="viewport"/> and shows all of it. Order matters (the manager keeps hidden state):
    /// ResetScale (suspends its default 30-day initialisation), SetDataBounds, SetFutureOffset(0) (otherwise a previous, longer series'
    /// right limit survives), SetDataInterval (minimum zoom = 2 spacings), ScreenBounds, ForceVisibleRange (derives the scale and ends the reset).
    /// </summary>
    public static void Reset(ViewportManager viewport, EquityDataBounds bounds, Rect plotBounds)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        viewport.ResetScale();
        viewport.SetDataBounds(bounds.First, bounds.Last);
        viewport.SetFutureOffset(TimeSpan.Zero);
        viewport.SetDataInterval(bounds.Interval);
        viewport.ScreenBounds = plotBounds;
        viewport.ForceVisibleRange(bounds.First.Ticks, bounds.Last.Ticks);
    }

    /// <summary>Shows the whole series again (double-click), keeping the current plot rectangle.</summary>
    public static void ShowAll(ViewportManager viewport, EquityDataBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        viewport.ForceVisibleRange(bounds.First.Ticks, bounds.Last.Ticks);
    }

    /// <summary>
    /// Applies a new plot rectangle while keeping the visible time range (the scale changes instead). The manager's own resize rule keeps
    /// the scale and moves the start, which would show other times after a window resize.
    /// </summary>
    public static void Resize(ViewportManager viewport, Rect plotBounds)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        double start = viewport.VisibleStartUnit;
        double end = viewport.VisibleEndUnit;
        viewport.ScreenBounds = plotBounds;
        viewport.ForceVisibleRange(start, end);
    }

    /// <summary>Scale multiplier of one wheel step: zoom in for a positive delta, its reciprocal for a negative one, none for zero.</summary>
    public static double ZoomFactor(double wheelDeltaY)
    {
        if (wheelDeltaY > 0d) return BacktestUiConstants.EquityZoomInFactor;
        if (wheelDeltaY < 0d) return 1d / BacktestUiConstants.EquityZoomInFactor;
        return 1d;
    }

    /// <summary>
    /// Converts the manager's visible range (double ticks, which cannot hold every tick above 2^53) to exact ticks. A bound that equals the
    /// double image of the data's first/last instant is snapped to that instant, so the full-range view is bit-identical to a layout built
    /// from the data alone; a non-finite or empty range falls back to the full data range.
    /// </summary>
    public static (long StartTicks, long EndTicks) ResolveVisibleTicks(double startUnit, double endUnit, EquityDataBounds bounds)
    {
        long first = bounds.First.Ticks;
        long last = bounds.Last.Ticks;
        if (!double.IsFinite(startUnit) || !double.IsFinite(endUnit))
        {
            return (first, last);
        }

        long start = startUnit == first ? first : ToTicks(startUnit);
        long end = endUnit == last ? last : ToTicks(endUnit);
        return end > start ? (start, end) : (first, last);
    }

    private static long ToTicks(double unit) =>
        (long)Math.Clamp(Math.Round(unit), DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks);
}
