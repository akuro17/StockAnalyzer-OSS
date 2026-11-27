using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.Drawing.Values;

/// <summary>The interpolation of an anchor curve.</summary>
public enum InterpolationCurveKind
{
    /// <summary>Monotone piecewise cubic Hermite (Fritsch-Carlson): no overshoot between anchors of monotone data.</summary>
    Pchip = 0,

    /// <summary>Natural cubic spline (second derivative 0 at both ends).</summary>
    CubicSpline = 1,
}

/// <summary>How the anchors of an anchor curve are connected.</summary>
public enum InterpolationCurveMode
{
    /// <summary>
    /// A function of time: the anchors are sorted by their position on the bar axis and the curve is the interpolation y(x) of the sorted anchors. An anchor placed before the previous one is
    /// sorted in between the others; the curve never loops (the behaviour of the first versions of the tools).
    /// </summary>
    TimeSeries = 0,

    /// <summary>
    /// The path through the anchors in the order they were placed: a parametric curve (x(s), y(s)) where s is the chord length of the polyline of the anchors, measured in the data space with each axis
    /// divided by the extent of the anchors on that axis. The curve may turn back and may close into a loop. It is an OPEN parametric curve: even when the last anchor equals the first one,
    /// the tangent is not continuous at the joint.
    /// </summary>
    Path = 1,
}

/// <summary>Why an anchor curve has no (smooth) curve.</summary>
public enum InterpolationCurveStatus
{
    /// <summary>The curve was sampled.</summary>
    Ok,

    /// <summary>There is no bar to map the anchors to.</summary>
    NoData,

    /// <summary>Fewer than two distinct anchors.</summary>
    TooFewAnchors,

    /// <summary>More than <see cref="InterpolationCurveValues.MaxAnchors"/> distinct anchors.</summary>
    TooManyAnchors,

    /// <summary>The kernel refused the grid or produced a non-finite value.</summary>
    FitFailed,
}

/// <summary>The sampled curve through the anchors (chart coordinates), or the reason it has none.</summary>
public sealed class InterpolationCurve
{
    public static readonly InterpolationCurve Empty = new(InterpolationCurveStatus.NoData, 0, Array.Empty<DateTime>(), Array.Empty<decimal>());

    public InterpolationCurve(InterpolationCurveStatus status, int distinctAnchors, DateTime[] times, decimal[] prices)
    {
        Status = status;
        DistinctAnchors = distinctAnchors;
        Times = times;
        Prices = prices;
    }

    public InterpolationCurveStatus Status { get; }

    /// <summary>The number of distinct anchors (two anchors on the same bar count once; the last placed wins).</summary>
    public int DistinctAnchors { get; }

    /// <summary>The time of every sample, the first and the last sample are the first and the last anchor.</summary>
    public DateTime[] Times { get; }

    public decimal[] Prices { get; }

    public int Count => Prices.Length;

    public bool IsOk => Status == InterpolationCurveStatus.Ok;
}

/// <summary>
/// The PCHIP / natural cubic spline curve through the anchors of a drawing (the single definition). An anchor is mapped to a position on the bar axis (the bar index plus the fraction between two bars,
/// see <see cref="DrawingRangeBars.BarPositionOf"/>), so the curve is smooth in the bar space the chart draws. The anchors are sorted by position; two anchors on the same position keep the LAST placed one;
/// fewer than two distinct anchors draw nothing, exactly two a straight segment, three or more the kernel (<see cref="DslInterpolationMath"/>) sampled <see cref="SamplesPerSegment"/> times per segment
/// between the first and the last anchor (no extrapolation).
/// </summary>
public static class InterpolationCurveValues
{
    /// <summary>The samples of one segment between two neighbouring anchors.</summary>
    public const int SamplesPerSegment = 16;

    /// <summary>The most distinct anchors (the spline solves a system of this size on the stack for every sample).</summary>
    public const int MaxAnchors = 64;

    /// <summary>Two anchors closer than this on the bar axis (in bars) are the same anchor.</summary>
    public const double SamePositionTolerance = 1e-9;

    private const string StateKey = "State";
    private const string AnchorsKey = "AnchorCount";
    private const string SamplesKey = "SampleCount";
    private const string StartKey = "CurveStart";
    private const string EndKey = "CurveEnd";
    private const string PriceFormat = "F3";

    /// <summary>The state text of a curve without a smooth curve.</summary>
    public static string StatusText(InterpolationCurveStatus status) => status switch
    {
        InterpolationCurveStatus.Ok => string.Empty,
        InterpolationCurveStatus.NoData => "No chart data",
        InterpolationCurveStatus.TooFewAnchors => "Needs at least 2 distinct anchors",
        InterpolationCurveStatus.TooManyAnchors => string.Create(DrawingValueFormat.Culture, $"Too many anchors (> {MaxAnchors})"),
        _ => "Interpolation failed",
    };

    /// <summary>
    /// Samples the curve of <paramref name="kind"/> through <paramref name="anchors"/> (placement order) in <paramref name="mode"/>. In <see cref="InterpolationCurveMode.Path"/> only
    /// ADJACENT identical anchors (the same bar position and the same price) are merged: an anchor that returns to an earlier point later in the sequence stays, so a loop can be closed;
    /// fewer than two anchors left draw nothing, exactly two a straight segment.
    /// </summary>
    public static InterpolationCurve Calculate(IReadOnlyList<CoreCandleData> candles, IReadOnlyList<DrawingAnchor> anchors, InterpolationCurveKind kind, InterpolationCurveMode mode = InterpolationCurveMode.TimeSeries)
    {
        if (candles == null || candles.Count == 0 || anchors == null)
        {
            return InterpolationCurve.Empty;
        }

        TimeSpan span = DrawingRangeBars.MeanBarSpan(candles);
        if (mode == InterpolationCurveMode.Path)
        {
            return CalculatePath(candles, anchors, kind, span);
        }
        var items = new List<(double Position, int Order, DrawingAnchor Anchor)>(anchors.Count);
        for (int i = 0; i < anchors.Count; i++)
        {
            items.Add((DrawingRangeBars.BarPositionOf(candles, anchors[i].Time, span), i, anchors[i]));
        }

        items.Sort(static (a, b) =>
        {
            int byPosition = a.Position.CompareTo(b.Position);
            return byPosition != 0 ? byPosition : a.Order.CompareTo(b.Order);
        });

        // The same position: the last placed anchor wins (the sort keeps placement order among equal positions).
        var distinct = new List<(double Position, int Order, DrawingAnchor Anchor)>(items.Count);
        foreach (var item in items)
        {
            if (distinct.Count > 0 && item.Position - distinct[distinct.Count - 1].Position <= SamePositionTolerance)
            {
                distinct[distinct.Count - 1] = (distinct[distinct.Count - 1].Position, item.Order, item.Anchor);
            }
            else
            {
                distinct.Add(item);
            }
        }

        int m = distinct.Count;
        if (m < 2)
        {
            return new InterpolationCurve(InterpolationCurveStatus.TooFewAnchors, m, Array.Empty<DateTime>(), Array.Empty<decimal>());
        }

        if (m > MaxAnchors)
        {
            return new InterpolationCurve(InterpolationCurveStatus.TooManyAnchors, m, Array.Empty<DateTime>(), Array.Empty<decimal>());
        }

        if (m == 2)
        {
            return new InterpolationCurve(
                InterpolationCurveStatus.Ok,
                m,
                new[] { distinct[0].Anchor.Time, distinct[1].Anchor.Time },
                new[] { distinct[0].Anchor.Price, distinct[1].Anchor.Price });
        }

        var xs = new double[m];
        var ys = new double[m];
        for (int i = 0; i < m; i++)
        {
            xs[i] = distinct[i].Position;
            ys[i] = (double)distinct[i].Anchor.Price;
        }

        int total = ((m - 1) * SamplesPerSegment) + 1;
        var times = new DateTime[total];
        var prices = new decimal[total];
        int index = 0;
        for (int segment = 0; segment < m - 1; segment++)
        {
            for (int k = 0; k < SamplesPerSegment; k++)
            {
                if (k == 0)
                {
                    times[index] = distinct[segment].Anchor.Time;
                    prices[index] = distinct[segment].Anchor.Price;
                    index++;
                    continue;
                }

                double x = xs[segment] + ((xs[segment + 1] - xs[segment]) * k / SamplesPerSegment);
                MathResult<double> value = kind == InterpolationCurveKind.Pchip
                    ? DslInterpolationMath.InterpPchip(xs, ys, x)
                    : DslInterpolationMath.InterpCubicSpline(xs, ys, x);
                decimal? price = value.IsOk ? IndicatorCalculationHelper.ToDecimalOrNull(value.Value) : null;
                if (!price.HasValue)
                {
                    return new InterpolationCurve(InterpolationCurveStatus.FitFailed, m, Array.Empty<DateTime>(), Array.Empty<decimal>());
                }

                times[index] = DrawingRangeBars.TimeAtBarPosition(candles, x, span);
                prices[index] = price.Value;
                index++;
            }
        }

        times[index] = distinct[m - 1].Anchor.Time;
        prices[index] = distinct[m - 1].Anchor.Price;
        return new InterpolationCurve(InterpolationCurveStatus.Ok, m, times, prices);
    }

    /// <summary>The path mode: the parametric curve (x(s), y(s)) through the anchors in placement order (see <see cref="InterpolationCurveMode.Path"/>).</summary>
    private static InterpolationCurve CalculatePath(IReadOnlyList<CoreCandleData> candles, IReadOnlyList<DrawingAnchor> anchors, InterpolationCurveKind kind, TimeSpan span)
    {
        var points = new List<(double Position, DrawingAnchor Anchor)>(anchors.Count);
        for (int i = 0; i < anchors.Count; i++)
        {
            double position = DrawingRangeBars.BarPositionOf(candles, anchors[i].Time, span);
            if (points.Count > 0
                && points[points.Count - 1].Anchor.Price == anchors[i].Price
                && Math.Abs(position - points[points.Count - 1].Position) <= SamePositionTolerance)
            {
                continue;   // an adjacent identical anchor (a double click, a repeated point)
            }

            points.Add((position, anchors[i]));
        }

        int m = points.Count;
        if (m < 2)
        {
            return new InterpolationCurve(InterpolationCurveStatus.TooFewAnchors, m, Array.Empty<DateTime>(), Array.Empty<decimal>());
        }

        if (m > MaxAnchors)
        {
            return new InterpolationCurve(InterpolationCurveStatus.TooManyAnchors, m, Array.Empty<DateTime>(), Array.Empty<decimal>());
        }

        if (m == 2)
        {
            return new InterpolationCurve(
                InterpolationCurveStatus.Ok,
                m,
                new[] { points[0].Anchor.Time, points[1].Anchor.Time },
                new[] { points[0].Anchor.Price, points[1].Anchor.Price });
        }

        var positions = new double[m];
        var prices = new double[m];
        double minPosition = double.MaxValue;
        double maxPosition = double.MinValue;
        double minPrice = double.MaxValue;
        double maxPrice = double.MinValue;
        for (int i = 0; i < m; i++)
        {
            positions[i] = points[i].Position;
            prices[i] = (double)points[i].Anchor.Price;
            minPosition = Math.Min(minPosition, positions[i]);
            maxPosition = Math.Max(maxPosition, positions[i]);
            minPrice = Math.Min(minPrice, prices[i]);
            maxPrice = Math.Max(maxPrice, prices[i]);
        }

        // Each axis is divided by the extent of the anchors on it (1 when the anchors do not spread on the axis), so the chord length does not depend on the unit of the axes.
        double positionExtent = maxPosition - minPosition > 0.0 ? maxPosition - minPosition : 1.0;
        double priceExtent = maxPrice - minPrice > 0.0 ? maxPrice - minPrice : 1.0;
        var chord = new double[m];
        for (int i = 1; i < m; i++)
        {
            double dx = (positions[i] - positions[i - 1]) / positionExtent;
            double dy = (prices[i] - prices[i - 1]) / priceExtent;
            chord[i] = chord[i - 1] + Math.Sqrt((dx * dx) + (dy * dy));
            if (!double.IsFinite(chord[i]) || !(chord[i] > chord[i - 1]))
            {
                return new InterpolationCurve(InterpolationCurveStatus.FitFailed, m, Array.Empty<DateTime>(), Array.Empty<decimal>());
            }
        }

        int total = ((m - 1) * SamplesPerSegment) + 1;
        var times = new DateTime[total];
        var values = new decimal[total];
        int index = 0;
        for (int segment = 0; segment < m - 1; segment++)
        {
            for (int k = 0; k < SamplesPerSegment; k++)
            {
                if (k == 0)
                {
                    times[index] = points[segment].Anchor.Time;
                    values[index] = points[segment].Anchor.Price;
                    index++;
                    continue;
                }

                double s = chord[segment] + ((chord[segment + 1] - chord[segment]) * k / SamplesPerSegment);
                MathResult<double> x = kind == InterpolationCurveKind.Pchip ? DslInterpolationMath.InterpPchip(chord, positions, s) : DslInterpolationMath.InterpCubicSpline(chord, positions, s);
                MathResult<double> y = kind == InterpolationCurveKind.Pchip ? DslInterpolationMath.InterpPchip(chord, prices, s) : DslInterpolationMath.InterpCubicSpline(chord, prices, s);
                decimal? price = x.IsOk && y.IsOk && double.IsFinite(x.Value) ? IndicatorCalculationHelper.ToDecimalOrNull(y.Value) : null;
                if (!price.HasValue)
                {
                    return new InterpolationCurve(InterpolationCurveStatus.FitFailed, m, Array.Empty<DateTime>(), Array.Empty<decimal>());
                }

                times[index] = DrawingRangeBars.TimeAtBarPosition(candles, x.Value, span);
                values[index] = price.Value;
                index++;
            }
        }

        times[index] = points[m - 1].Anchor.Time;
        values[index] = points[m - 1].Anchor.Price;
        return new InterpolationCurve(InterpolationCurveStatus.Ok, m, times, values);
    }

    /// <summary>The Data-tab values of a curve.</summary>
    public static IReadOnlyList<DrawingCalculatedValue> Calculate(InterpolationCurve curve, IndicatorColor color)
    {
        if (curve.Status == InterpolationCurveStatus.NoData)
        {
            return Array.Empty<DrawingCalculatedValue>();
        }

        var culture = DrawingValueFormat.Culture;
        var rows = new List<DrawingCalculatedValue>(5);
        if (!curve.IsOk)
        {
            rows.Add(new DrawingCalculatedValue(StateKey, "State", null, StatusText(curve.Status), IndicatorColor.Gray));
            rows.Add(new DrawingCalculatedValue(AnchorsKey, "Anchors", curve.DistinctAnchors, curve.DistinctAnchors.ToString(culture), IndicatorColor.Gray));
            return rows;
        }

        rows.Add(new DrawingCalculatedValue(AnchorsKey, "Anchors", curve.DistinctAnchors, curve.DistinctAnchors.ToString(culture), IndicatorColor.Gray));
        rows.Add(new DrawingCalculatedValue(SamplesKey, "Samples", curve.Count, curve.Count.ToString(culture), IndicatorColor.Gray));
        rows.Add(new DrawingCalculatedValue(StartKey, "Curve Start", curve.Prices[0], curve.Prices[0].ToString(PriceFormat, culture), color));
        rows.Add(new DrawingCalculatedValue(EndKey, "Curve End", curve.Prices[curve.Count - 1], curve.Prices[curve.Count - 1].ToString(PriceFormat, culture), color));
        return rows;
    }
}
