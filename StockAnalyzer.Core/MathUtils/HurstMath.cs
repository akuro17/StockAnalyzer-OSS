using System;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Hurst exponent by rescaled-range (R/S) analysis with a log-log regression over halving sub-interval lengths (no Anis-Lloyd or other small-sample correction).
/// Unit: dimensionless; the result is clamped to [0, 1].
/// </summary>
public static class HurstMath
{
    /// <summary>Smallest sub-interval length that is used (lengths are n, floor(n/2), floor(n/4), ... while they are at least this).</summary>
    public const int MinSegmentLength = 8;

    // Upper bound of the number of sub-interval lengths: halving from the largest Period (2000) down to MinSegmentLength gives 8 lengths; the margin keeps the
    // stack buffer valid for any larger window.
    private const int MaxScaleCount = 32;

    // The result is a ratio on the fixed 0..1 axis of the indicator (one definition: IndicatorDefaultConstants.UnitInterval*).
    private const double LowerBound = (double)IndicatorDefaultConstants.UnitIntervalMinValue;
    private const double UpperBound = (double)IndicatorDefaultConstants.UnitIntervalMaxValue;

    /// <summary>
    /// R/S of one segment: mean mu, cumulative deviations Y_j = sum_(t=1..j) (x_t - mu) for j = 1..m, R = max Y_j - min Y_j, S = sqrt(sum (x_t - mu)^2 / m).
    /// Returns NaN when the segment is empty, a value is not finite, or S = 0.
    /// </summary>
    public static double RescaledRange(ReadOnlySpan<double> segment)
    {
        int m = segment.Length;
        if (m == 0)
        {
            return double.NaN;
        }

        // S = 0 exactly when every value is equal. Detected on the values themselves: the computed mean of equal values can differ from them in the last bit,
        // which would turn a mathematically zero S into rounding noise and the R/S of a constant segment into an arbitrary number.
        double lowest = segment[0];
        double highest = segment[0];
        double sum = 0.0;
        for (int t = 0; t < m; t++)
        {
            double value = segment[t];
            if (value < lowest) lowest = value;
            if (value > highest) highest = value;
            sum += value;
        }

        if (lowest == highest)
        {
            return double.NaN;
        }

        double mean = sum / m;
        double cumulative = 0.0;
        double max = double.NegativeInfinity;
        double min = double.PositiveInfinity;
        double squares = 0.0;
        for (int t = 0; t < m; t++)
        {
            double deviation = segment[t] - mean;
            cumulative += deviation;
            if (cumulative > max) max = cumulative;
            if (cumulative < min) min = cumulative;
            squares += deviation * deviation;
        }

        double s = Math.Sqrt(squares / m);
        if (!(s > 0.0) || !double.IsFinite(s))
        {
            return double.NaN;
        }

        double rescaled = (max - min) / s;
        return double.IsFinite(rescaled) ? rescaled : double.NaN;
    }

    /// <summary>
    /// Hurst exponent of <paramref name="window"/> (n returns). Sub-interval lengths m_0 = n, m_(j+1) = floor(m_j / 2) while m &gt;= <see cref="MinSegmentLength"/>.
    /// For each m, floor(n / m) non-overlapping segments of length m are taken from the END of the window; segments with S = 0 are dropped and the remaining R/S values are
    /// averaged; a length with no segment or a mean &lt;= 0 is dropped. With fewer than two lengths left the result is NaN; otherwise it is the least-squares slope of
    /// ln (R/S)_m against ln m, NaN when not finite, clamped to [0, 1].
    /// </summary>
    public static double Exponent(ReadOnlySpan<double> window)
    {
        int n = window.Length;
        Span<double> logLength = stackalloc double[MaxScaleCount];
        Span<double> logRescaled = stackalloc double[MaxScaleCount];
        int points = 0;

        for (int m = n; m >= MinSegmentLength && points < MaxScaleCount; m /= 2)
        {
            int segments = n / m;
            double total = 0.0;
            int used = 0;
            for (int s = 0; s < segments; s++)
            {
                int end = n - s * m;
                double rescaled = RescaledRange(window.Slice(end - m, m));
                if (double.IsNaN(rescaled))
                {
                    continue;
                }

                total += rescaled;
                used++;
            }

            if (used == 0)
            {
                continue;
            }

            double mean = total / used;
            if (!(mean > 0.0))
            {
                continue;
            }

            logLength[points] = Math.Log(m);
            logRescaled[points] = Math.Log(mean);
            points++;
        }

        if (points < 2)
        {
            return double.NaN;
        }

        double meanX = 0.0;
        double meanY = 0.0;
        for (int k = 0; k < points; k++)
        {
            meanX += logLength[k];
            meanY += logRescaled[k];
        }

        meanX /= points;
        meanY /= points;

        double covariance = 0.0;
        double variance = 0.0;
        for (int k = 0; k < points; k++)
        {
            double dx = logLength[k] - meanX;
            covariance += dx * (logRescaled[k] - meanY);
            variance += dx * dx;
        }

        if (!(variance > 0.0))
        {
            return double.NaN;
        }

        double slope = covariance / variance;
        return double.IsFinite(slope) ? Math.Clamp(slope, LowerBound, UpperBound) : double.NaN;
    }
}
