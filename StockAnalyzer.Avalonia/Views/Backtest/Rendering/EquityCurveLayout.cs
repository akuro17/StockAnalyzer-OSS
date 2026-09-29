using System;
using System.Collections.Immutable;
using System.Globalization;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Avalonia.Views.Backtest.Rendering;

/// <summary>Task 12 boundary-condition states from spec §5.7's draw table (width/height&lt;=0 is handled by the control itself, before this layer is even asked to build).</summary>
public enum EquityCurveDisplayState
{
    Empty,
    SinglePoint,
    Line,
    Unavailable,
}

/// <summary>One equity point projected to [0,1] fractions within the current data's own X/Y domain.</summary>
public readonly record struct EquityCurveNormalizedPoint(double XFraction, double YFraction);

/// <summary>
/// Pure decimal-Equity -&gt; normalized-fraction projection (spec §5.7 "coordinate-transform layer"):
/// zero Avalonia/Skia dependency, so Task 17's boundary-condition facts (Empty/SinglePoint/zero-range/
/// overflow) can exercise it directly. Screen-pixel projection (which needs the control's current
/// plot rect in DIP) is a second, trivial step done by <see cref="EquityCurveControl"/> every paint -
/// it only multiplies these fractions by the current plot width/height, so resizing the window never
/// requires rebuilding this layer.
/// </summary>
public sealed class EquityCurveLayout
{
    public EquityCurveDisplayState State { get; }
    public ImmutableArray<EquityCurveNormalizedPoint> Points { get; }
    public decimal YMin { get; }
    public decimal YMax { get; }
    public DateTime ViewportStartUtc { get; }
    public DateTime ViewportEndUtc { get; }

    /// <summary>Pre-formatted once per <see cref="Build"/> call so an unchanged render-cache key does not
    /// re-run <c>decimal.ToString</c>/<c>DateTime.ToString</c>.</summary>
    public string YMaxLabel { get; }
    public string YMidLabel { get; }
    public string YMinLabel { get; }
    public string ViewportStartLabel { get; }
    public string ViewportEndLabel { get; }

    private EquityCurveLayout(
        EquityCurveDisplayState state, ImmutableArray<EquityCurveNormalizedPoint> points,
        decimal yMin, decimal yMax, DateTime viewportStartUtc, DateTime viewportEndUtc,
        string yMaxLabel, string yMidLabel, string yMinLabel, string viewportStartLabel, string viewportEndLabel)
    {
        State = state;
        Points = points;
        YMin = yMin;
        YMax = yMax;
        ViewportStartUtc = viewportStartUtc;
        ViewportEndUtc = viewportEndUtc;
        YMaxLabel = yMaxLabel;
        YMidLabel = yMidLabel;
        YMinLabel = yMinLabel;
        ViewportStartLabel = viewportStartLabel;
        ViewportEndLabel = viewportEndLabel;
    }

    private static readonly EquityCurveLayout EmptyInstance = new(
        EquityCurveDisplayState.Empty, ImmutableArray<EquityCurveNormalizedPoint>.Empty, 0m, 0m, default, default,
        string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);

    private static readonly EquityCurveLayout UnavailableInstance = new(
        EquityCurveDisplayState.Unavailable, ImmutableArray<EquityCurveNormalizedPoint>.Empty, 0m, 0m, default, default,
        string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);

    /// <summary>
    /// Builds the layout for one equity sample. DT-2 order is preserved throughout: every Equity
    /// difference (range, per-point offset) is computed in <c>decimal</c> first; only the final
    /// normalized fraction is cast to <c>double</c>. A <c>decimal</c> range/offset that overflows
    /// (spec §5.7 "range overflow") yields <see cref="EquityCurveDisplayState.Unavailable"/> instead
    /// of throwing, so the caller can fall back to the ChartUnavailable message while the domain
    /// result (MetricsTable/TradeListView) stays intact.
    /// </summary>
    public static EquityCurveLayout Build(ImmutableArray<EquityPoint> points, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.InvariantCulture;
        if (points.IsDefaultOrEmpty)
        {
            return EmptyInstance;
        }

        if (points[0].Timestamp.Kind != DateTimeKind.Utc)
        {
            return UnavailableInstance;
        }

        decimal yMin = points[0].Equity;
        decimal yMax = points[0].Equity;
        for (int i = 1; i < points.Length; i++)
        {
            if (points[i].Timestamp.Kind != DateTimeKind.Utc || points[i].Timestamp <= points[i - 1].Timestamp)
            {
                return UnavailableInstance;
            }
            decimal equity = points[i].Equity;
            if (equity < yMin) yMin = equity;
            if (equity > yMax) yMax = equity;
        }

        decimal yRange;
        try
        {
            yRange = yMax - yMin;
            if (yRange == 0m)
            {
                // Spec §5.7 "Y range == 0 (constant) -> +/-1 currency-unit display band".
                yMin -= 1m;
                yMax += 1m;
                yRange = 2m;
            }
        }
        catch (OverflowException)
        {
            return UnavailableInstance;
        }

        DateTime viewportStartUtc = points[0].Timestamp;
        DateTime viewportEndUtc = points[^1].Timestamp;
        long totalSpanTicks = viewportEndUtc.Ticks - viewportStartUtc.Ticks;

        ImmutableArray<EquityCurveNormalizedPoint>.Builder builder =
            ImmutableArray.CreateBuilder<EquityCurveNormalizedPoint>(points.Length);
        foreach (EquityPoint point in points)
        {
            double xFraction = totalSpanTicks <= 0
                ? 0.0
                : (point.Timestamp.Ticks - viewportStartUtc.Ticks) / (double)totalSpanTicks;

            // Bounded by yRange (already proven representable above), so this cannot overflow.
            decimal yOffset = point.Equity - yMin;
            double yFraction = (double)yOffset / (double)yRange;
            builder.Add(new EquityCurveNormalizedPoint(xFraction, yFraction));
        }

        EquityCurveDisplayState state = points.Length == 1
            ? EquityCurveDisplayState.SinglePoint
            : EquityCurveDisplayState.Line;
        return new EquityCurveLayout(
            state, builder.MoveToImmutable(), yMin, yMax, viewportStartUtc, viewportEndUtc,
            yMaxLabel: yMax.ToString("F4", culture),
            yMidLabel: (yMin + ((yMax - yMin) / 2m)).ToString("F4", culture),
            yMinLabel: yMin.ToString("F4", culture),
            viewportStartLabel: viewportStartUtc.ToString("yyyy-MM-dd HH:mm", culture),
            viewportEndLabel: viewportEndUtc.ToString("yyyy-MM-dd HH:mm", culture));
    }
}
