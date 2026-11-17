using System;
using System.Buffers;
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

    /// <summary>
    /// Computes Lo (1991) Modified R/S Hurst exponent H_Lo = clamp(ln(Q_n(q)) / ln(n), 0, 1), adjusting short-range autocorrelation
    /// via Newey-West long-run variance estimator with Bartlett kernel. Returns NaN when window length &lt; 8 or any value is non-finite,
    /// and 0.5 on constant / zero-variance windows.
    /// </summary>
    public static double ModifiedRescaledRangeExponent(ReadOnlySpan<double> window, int neweyWestLag = 0)
    {
        int n = window.Length;
        if (n < MinSegmentLength || neweyWestLag < 0 || !TrySummarizeFiniteWindow(window, out double mean, out bool isConstant))
        {
            return double.NaN;
        }

        if (isConstant)
        {
            return 0.5;
        }

        int maxLag = n / 4;
        int q = neweyWestLag <= 0
            ? Math.Max(1, Math.Min((int)Math.Floor(4.0 * Math.Pow(n / 100.0, 2.0 / 9.0)), maxLag))
            : Math.Min(neweyWestLag, maxLag);

        double cumulative = 0.0;
        double max = double.NegativeInfinity;
        double min = double.PositiveInfinity;
        double sumSq = 0.0;
        for (int i = 0; i < n; i++)
        {
            double dev = window[i] - mean;
            cumulative += dev;
            if (cumulative > max) max = cumulative;
            if (cumulative < min) min = cumulative;
            sumSq += dev * dev;
        }

        double range = max - min;
        double gamma0 = sumSq / n;
        if (!(gamma0 > 1e-30) || !(range > 0.0) || !double.IsFinite(gamma0) || !double.IsFinite(range))
        {
            return 0.5;
        }

        double longRunVar = gamma0;
        for (int j = 1; j <= q; j++)
        {
            double autoCovSum = 0.0;
            for (int i = j; i < n; i++)
            {
                autoCovSum += (window[i] - mean) * (window[i - j] - mean);
            }

            double gammaJ = autoCovSum / n;
            double weight = 1.0 - (double)j / (q + 1);
            longRunVar += 2.0 * weight * gammaJ;
        }

        if (!(longRunVar > 1e-16 * gamma0) || !double.IsFinite(longRunVar))
        {
            longRunVar = gamma0;
        }

        if (!(longRunVar > 1e-30))
        {
            return 0.5;
        }

        double qStat = range / Math.Sqrt(longRunVar);
        double logScale = Math.Log(n);
        if (!(qStat > 0.0) || !(logScale > 0.0) || !double.IsFinite(qStat))
        {
            return 0.5;
        }

        double hLo = Math.Log(qStat) / logScale;
        return double.IsFinite(hLo) ? Math.Clamp(hLo, LowerBound, UpperBound) : double.NaN;
    }

    /// <summary>
    /// Computes Lo-MacKinlay (1988) heteroskedasticity-robust Variance Ratio VR(q) and test statistic Z*(q).
    /// Returns NaN if q &lt; 2, window length &lt; 2*q + 2, any value is non-finite, or sample variance vanishes.
    /// </summary>
    public static double VarianceRatio(ReadOnlySpan<double> window, int q, out double zStar)
    {
        zStar = double.NaN;
        int n = window.Length;
        if (q < 2 || n < 2 * q + 2 || !TrySummarizeFiniteWindow(window, out double mean, out bool isConstant) || isConstant)
        {
            return double.NaN;
        }

        double sumSq = 0.0;
        for (int i = 0; i < n; i++)
        {
            double d = window[i] - mean;
            sumSq += d * d;
        }

        double sigmaA2 = sumSq / (n - 1);
        if (!(sigmaA2 > 1e-30) || !double.IsFinite(sigmaA2))
        {
            return double.NaN;
        }

        double m = q * (double)(n - q + 1) * (1.0 - (double)q / n);
        if (!(m > 1e-12))
        {
            return double.NaN;
        }

        double qSum = 0.0;
        for (int t = 0; t < q; t++)
        {
            qSum += window[t] - mean;
        }

        double qSumSq = qSum * qSum;
        for (int t = q; t < n; t++)
        {
            qSum += (window[t] - mean) - (window[t - q] - mean);
            qSumSq += qSum * qSum;
        }

        double sigmaC2 = qSumSq / m;
        double vr = sigmaC2 / sigmaA2;
        if (!double.IsFinite(vr))
        {
            return double.NaN;
        }

        double denom = sumSq * sumSq;
        if (denom > 1e-60 && double.IsFinite(denom))
        {
            double theta = 0.0;
            for (int j = 1; j < q; j++)
            {
                double crossSq = 0.0;
                for (int t = j; t < n; t++)
                {
                    double dt = window[t] - mean;
                    double dtLag = window[t - j] - mean;
                    crossSq += (dt * dt) * (dtLag * dtLag);
                }

                double deltaJ = crossSq / denom;
                double w = 2.0 * (q - j) / q;
                theta += w * w * deltaJ;
            }

            if (theta > 1e-30 && double.IsFinite(theta))
            {
                double z = (vr - 1.0) / Math.Sqrt(theta);
                if (double.IsFinite(z))
                {
                    zStar = z;
                }
            }
        }

        return vr;
    }

    /// <summary>
    /// Computes first-order Detrended Fluctuation Analysis (DFA-1) scaling exponent alpha clamped to [0, 2].
    /// Returns NaN when window length &lt; 16 or any value is non-finite, and 0.5 when fewer than 3 positive-fluctuation scales exist.
    /// </summary>
    public static double DetrendedFluctuationExponent(ReadOnlySpan<double> window)
    {
        int n = window.Length;
        if (n < 16 || !TrySummarizeFiniteWindow(window, out double mean, out bool isConstant))
        {
            return double.NaN;
        }

        if (isConstant)
        {
            return 0.5;
        }

        double[]? rented = n > MathBufferLimits.StackAllocThreshold ? ArrayPool<double>.Shared.Rent(n) : null;
        try
        {
            Span<double> profile = rented != null ? rented.AsSpan(0, n) : stackalloc double[n];
            double cumulative = 0.0;
            for (int i = 0; i < n; i++)
            {
                cumulative += window[i] - mean;
                profile[i] = cumulative;
            }

            Span<double> logS = stackalloc double[MaxScaleCount];
            Span<double> logF = stackalloc double[MaxScaleCount];
            int points = 0;

            for (int s = n / 2; s >= 4 && points < MaxScaleCount; s /= 2)
            {
                int numSegments = n / s;
                if (numSegments <= 0)
                {
                    continue;
                }

                double jMean = 0.5 * (s - 1);
                double sjj = (double)s * ((double)s * s - 1.0) / 12.0;
                double totalVariance = 0.0;

                for (int v = 0; v < numSegments; v++)
                {
                    int offset = v * s;
                    double ySum = 0.0;
                    for (int j = 0; j < s; j++)
                    {
                        ySum += profile[offset + j];
                    }

                    double yMean = ySum / s;
                    double sjy = 0.0;
                    for (int j = 0; j < s; j++)
                    {
                        sjy += (j - jMean) * (profile[offset + j] - yMean);
                    }

                    double slopeSeg = sjy / sjj;
                    double interceptSeg = yMean - slopeSeg * jMean;
                    double residualSq = 0.0;
                    for (int j = 0; j < s; j++)
                    {
                        double residual = profile[offset + j] - (interceptSeg + slopeSeg * j);
                        residualSq += residual * residual;
                    }

                    totalVariance += residualSq / s;
                }

                double f = Math.Sqrt(totalVariance / numSegments);
                if (f > 0.0 && double.IsFinite(f))
                {
                    logS[points] = Math.Log(s);
                    logF[points] = Math.Log(f);
                    points++;
                }
            }

            if (points < 3)
            {
                return 0.5;
            }

            double slope = ComputeOlsSlope(logS[..points], logF[..points]);
            return double.IsFinite(slope) ? Math.Clamp(slope, 0.0, 2.0) : 0.5;
        }
        finally
        {
            if (rented != null)
            {
                ArrayPool<double>.Shared.Return(rented);
            }
        }
    }

    private static bool TrySummarizeFiniteWindow(ReadOnlySpan<double> window, out double mean, out bool isConstant)
    {
        double lowest = window[0];
        double highest = window[0];
        double sum = 0.0;
        for (int i = 0; i < window.Length; i++)
        {
            double value = window[i];
            if (!double.IsFinite(value))
            {
                mean = double.NaN;
                isConstant = false;
                return false;
            }

            if (value < lowest) lowest = value;
            if (value > highest) highest = value;
            sum += value;
        }

        mean = sum / window.Length;
        isConstant = lowest == highest;
        return double.IsFinite(mean);
    }

    private static double ComputeOlsSlope(ReadOnlySpan<double> xs, ReadOnlySpan<double> ys)
    {
        int count = xs.Length;
        double meanX = 0.0;
        double meanY = 0.0;
        for (int k = 0; k < count; k++)
        {
            meanX += xs[k];
            meanY += ys[k];
        }

        meanX /= count;
        meanY /= count;

        double covariance = 0.0;
        double variance = 0.0;
        for (int k = 0; k < count; k++)
        {
            double dx = xs[k] - meanX;
            covariance += dx * (ys[k] - meanY);
            variance += dx * dx;
        }

        return variance > 0.0 ? covariance / variance : double.NaN;
    }
}
