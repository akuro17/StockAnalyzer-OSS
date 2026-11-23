using System;
using System.Buffers;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Bivariate Johansen (1988) fixed lag-1 VECM cointegration engine solving the closed-form 2x2 generalized eigenproblem
/// for Trace statistic, Maximum Eigenvalue statistic, and first-eigenvector equilibrium spread Z-score.
/// Note: CriticalValue95 (15.4947m) is the MacKinnon, Haug & Michelis (1999) Table II Case III (unrestricted constant, r=0) 5% critical value for TraceStat only (Osterwald-Lenum 1992 two-decimal value is 15.41); MaxEigenStat has a distinct critical value not emitted here.
/// Causal: bar t uses only levels in [t - period + 1 .. t].
/// </summary>
public static class JohansenMath
{
    public static void Compute(
        IReadOnlyList<decimal?> primaryPrices,
        IReadOnlyList<decimal?> comparisonPrices,
        int period,
        bool useLogPrices,
        IList<decimal?> traceStat,
        IList<decimal?> maxEigenStat,
        IList<decimal?> criticalValue95,
        IList<decimal?> spreadZScore)
    {
        ArgumentNullException.ThrowIfNull(primaryPrices);
        ArgumentNullException.ThrowIfNull(comparisonPrices);
        ArgumentNullException.ThrowIfNull(traceStat);
        ArgumentNullException.ThrowIfNull(maxEigenStat);
        ArgumentNullException.ThrowIfNull(criticalValue95);
        ArgumentNullException.ThrowIfNull(spreadZScore);

        traceStat.Clear();
        maxEigenStat.Clear();
        criticalValue95.Clear();
        spreadZScore.Clear();

        int n = Math.Min(primaryPrices.Count, comparisonPrices.Count);
        if (n == 0)
        {
            return;
        }

        double[] y1 = ArrayPool<double>.Shared.Rent(n);
        double[] y2 = ArrayPool<double>.Shared.Rent(n);
        try
        {
            TransformSeries(primaryPrices, y1, n, useLogPrices);
            TransformSeries(comparisonPrices, y2, n, useLogPrices);

            for (int t = 0; t < n; t++)
            {
                int start = t - period + 1;
                if (start < 0 || !AreLevelsFinite(y1, y2, start, t))
                {
                    traceStat.Add(null);
                    maxEigenStat.Add(null);
                    criticalValue95.Add(null);
                    spreadZScore.Add(null);
                    continue;
                }

                ComputeWindow(y1.AsSpan(start, period), y2.AsSpan(start, period),
                    out double trace, out double maxEig, out double zScore);

                traceStat.Add(IndicatorCalculationHelper.ToDecimalOrNull(trace));
                maxEigenStat.Add(IndicatorCalculationHelper.ToDecimalOrNull(maxEig));
                criticalValue95.Add(IndicatorDefaultConstants.JohansenCriticalValue95m);
                spreadZScore.Add(IndicatorCalculationHelper.ToDecimalOrNull(zScore));
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(y1);
            ArrayPool<double>.Shared.Return(y2);
        }
    }

    private static void TransformSeries(IReadOnlyList<decimal?> prices, double[] dest, int n, bool useLog)
    {
        for (int i = 0; i < n; i++)
        {
            decimal? p = prices[i];
            if (p == null)
            {
                dest[i] = double.NaN;
                continue;
            }

            if (useLog)
            {
                if (p.Value <= 0m)
                {
                    dest[i] = double.NaN;
                }
                else
                {
                    double v = Math.Log((double)p.Value);
                    dest[i] = double.IsFinite(v) ? v : double.NaN;
                }
            }
            else
            {
                double v = (double)p.Value;
                dest[i] = double.IsFinite(v) ? v : double.NaN;
            }
        }
    }

    private static bool AreLevelsFinite(double[] a, double[] b, int start, int end)
    {
        for (int i = start; i <= end; i++)
        {
            if (!double.IsFinite(a[i]) || !double.IsFinite(b[i]))
            {
                return false;
            }
        }
        return true;
    }

    internal static void ComputeWindow(
        ReadOnlySpan<double> y1,
        ReadOnlySpan<double> y2,
        out double traceStat,
        out double maxEigenStat,
        out double spreadZScore)
    {
        int w = y1.Length;
        int capT = w - 1;
        if (capT < 2)
        {
            traceStat = double.NaN;
            maxEigenStat = double.NaN;
            spreadZScore = double.NaN;
            return;
        }

        double meanD1 = 0.0, meanD2 = 0.0, meanL1 = 0.0, meanL2 = 0.0;
        for (int i = 1; i < w; i++)
        {
            meanD1 += y1[i] - y1[i - 1];
            meanD2 += y2[i] - y2[i - 1];
            meanL1 += y1[i - 1];
            meanL2 += y2[i - 1];
        }
        meanD1 /= capT;
        meanD2 /= capT;
        meanL1 /= capT;
        meanL2 /= capT;

        double s00_00 = 0.0, s00_01 = 0.0, s00_11 = 0.0;
        double s11_00 = 0.0, s11_01 = 0.0, s11_11 = 0.0;
        double s01_00 = 0.0, s01_01 = 0.0, s01_10 = 0.0, s01_11 = 0.0;

        for (int i = 1; i < w; i++)
        {
            double r0_0 = (y1[i] - y1[i - 1]) - meanD1;
            double r0_1 = (y2[i] - y2[i - 1]) - meanD2;
            double r1_0 = y1[i - 1] - meanL1;
            double r1_1 = y2[i - 1] - meanL2;

            s00_00 += r0_0 * r0_0;
            s00_01 += r0_0 * r0_1;
            s00_11 += r0_1 * r0_1;

            s11_00 += r1_0 * r1_0;
            s11_01 += r1_0 * r1_1;
            s11_11 += r1_1 * r1_1;

            s01_00 += r0_0 * r1_0;
            s01_01 += r0_0 * r1_1;
            s01_10 += r0_1 * r1_0;
            s01_11 += r0_1 * r1_1;
        }

        double invT = 1.0 / capT;
        double ridge = IndicatorDefaultConstants.JohansenMomentRidge;

        s00_00 = s00_00 * invT + ridge;
        s00_01 *= invT;
        s00_11 = s00_11 * invT + ridge;

        s11_00 = s11_00 * invT + ridge;
        s11_01 *= invT;
        s11_11 = s11_11 * invT + ridge;

        s01_00 *= invT;
        s01_01 *= invT;
        s01_10 *= invT;
        s01_11 *= invT;

        if (!InvertSymmetric2x2(s00_00, s00_01, s00_11, out double i00_00, out double i00_01, out double i00_11) ||
            !InvertSymmetric2x2(s11_00, s11_01, s11_11, out double i11_00, out double i11_01, out double i11_11))
        {
            traceStat = double.NaN;
            maxEigenStat = double.NaN;
            spreadZScore = double.NaN;
            return;
        }

        // B = S_00^{-1} * S_01
        double b00 = i00_00 * s01_00 + i00_01 * s01_10;
        double b01 = i00_00 * s01_01 + i00_01 * s01_11;
        double b10 = i00_01 * s01_00 + i00_11 * s01_10;
        double b11 = i00_01 * s01_01 + i00_11 * s01_11;

        // C = S_10 * B where S_10 = S_01^T
        double c00 = s01_00 * b00 + s01_10 * b10;
        double c01 = s01_00 * b01 + s01_10 * b11;
        double c10 = s01_01 * b00 + s01_11 * b10;
        double c11 = s01_01 * b01 + s01_11 * b11;

        // M = S_11^{-1} * C
        double m00 = i11_00 * c00 + i11_01 * c10;
        double m01 = i11_00 * c01 + i11_01 * c11;
        double m10 = i11_01 * c00 + i11_11 * c10;
        double m11 = i11_01 * c01 + i11_11 * c11;

        double tr = m00 + m11;
        double det = m00 * m11 - m01 * m10;
        double disc = Math.Sqrt(Math.Max(0.0, tr * tr - 4.0 * det));

        double maxClamp = IndicatorDefaultConstants.JohansenMaxEigenvalueClamp;
        double lambda1 = Math.Clamp(0.5 * (tr + disc), 0.0, maxClamp);
        double lambda2 = Math.Clamp(0.5 * (tr - disc), 0.0, lambda1);

        maxEigenStat = -capT * Math.Log(1.0 - lambda1);
        traceStat = -capT * (Math.Log(1.0 - lambda1) + Math.Log(1.0 - lambda2));

        // Extract eigenvector for lambda1: (M - lambda1 * I) * v = 0
        double row0Diag = m00 - lambda1;
        double row1Diag = m11 - lambda1;
        double v1, v2;
        if (Math.Abs(m01) + Math.Abs(row0Diag) >= Math.Abs(m10) + Math.Abs(row1Diag))
        {
            v1 = -m01;
            v2 = row0Diag;
        }
        else
        {
            v1 = row1Diag;
            v2 = -m10;
        }

        double beta;
        if (Math.Abs(v1) > IndicatorDefaultConstants.JohansenEigenvectorPivotFloor)
        {
            beta = -v2 / v1;
        }
        else
        {
            beta = ComputeOlsSlope(y1, y2);
        }

        spreadZScore = ComputeLastSpreadZScore(y1, y2, beta);
    }

    private static bool InvertSymmetric2x2(
        double a00, double a01, double a11,
        out double inv00, out double inv01, out double inv11)
    {
        double det = a00 * a11 - a01 * a01;
        if (!(det > IndicatorDefaultConstants.JohansenDeterminantFloor) || !double.IsFinite(det))
        {
            inv00 = inv01 = inv11 = 0.0;
            return false;
        }

        double invDet = 1.0 / det;
        inv00 = a11 * invDet;
        inv01 = -a01 * invDet;
        inv11 = a00 * invDet;
        return true;
    }

    private static double ComputeOlsSlope(ReadOnlySpan<double> y1, ReadOnlySpan<double> y2)
    {
        int n = y1.Length;
        double sum1 = 0.0, sum2 = 0.0;
        for (int i = 0; i < n; i++)
        {
            sum1 += y1[i];
            sum2 += y2[i];
        }
        double m1 = sum1 / n;
        double m2 = sum2 / n;
        double cov = 0.0, var2 = 0.0;
        for (int i = 0; i < n; i++)
        {
            double d2 = y2[i] - m2;
            cov += (y1[i] - m1) * d2;
            var2 += d2 * d2;
        }
        return var2 > IndicatorDefaultConstants.JohansenDeterminantFloor ? cov / var2 : 1.0;
    }

    private static double ComputeLastSpreadZScore(ReadOnlySpan<double> y1, ReadOnlySpan<double> y2, double beta)
    {
        if (!double.IsFinite(beta))
        {
            return double.NaN;
        }

        int n = y1.Length;
        double sum = 0.0;
        for (int i = 0; i < n; i++)
        {
            sum += y1[i] - beta * y2[i];
        }
        double mean = sum / n;

        double sumSq = 0.0;
        for (int i = 0; i < n; i++)
        {
            double diff = (y1[i] - beta * y2[i]) - mean;
            sumSq += diff * diff;
        }

        double std = Math.Sqrt(sumSq / n);
        if (std <= ZNormalization.FlatSigmaEpsilon)
        {
            return 0.0;
        }

        double lastSpread = y1[n - 1] - beta * y2[n - 1];
        return (lastSpread - mean) / std;
    }
}
