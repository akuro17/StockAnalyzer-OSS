using System;
using System.Buffers;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Bivariate Granger causality F-tests (restricted AR(p) vs. unrestricted VAR(p) over within-window
/// Z-standardized log returns with slope-only ridge 1e-9) and Engle (2002) DCC(1,1) conditional correlation
/// (fixed EWMA lambda=0.94, alpha=0.05, beta=0.90 outputting 1-step-ahead Q_{t|t-1}).
/// Causal: bar t uses only log returns in [t - period + 1 .. t].
/// </summary>
public static class GrangerDccMath
{
    public static void Compute(
        IReadOnlyList<decimal?> primaryPrices,
        IReadOnlyList<decimal?> comparisonPrices,
        int period,
        int lagOrder,
        IList<decimal?> forwardF,
        IList<decimal?> reverseF,
        IList<decimal?> directionalDiff,
        IList<decimal?> dccCorrelation)
    {
        ArgumentNullException.ThrowIfNull(primaryPrices);
        ArgumentNullException.ThrowIfNull(comparisonPrices);
        ArgumentNullException.ThrowIfNull(forwardF);
        ArgumentNullException.ThrowIfNull(reverseF);
        ArgumentNullException.ThrowIfNull(directionalDiff);
        ArgumentNullException.ThrowIfNull(dccCorrelation);

        forwardF.Clear();
        reverseF.Clear();
        directionalDiff.Clear();
        dccCorrelation.Clear();

        int n = Math.Min(primaryPrices.Count, comparisonPrices.Count);
        if (n == 0)
        {
            return;
        }

        double[] retA = ArrayPool<double>.Shared.Rent(n);
        double[] retB = ArrayPool<double>.Shared.Rent(n);
        try
        {
            BuildLogReturns(primaryPrices, retA, n);
            BuildLogReturns(comparisonPrices, retB, n);

            int tEff = period - lagOrder;
            int df2 = tEff - 2 * lagOrder - 1;

            for (int t = 0; t < n; t++)
            {
                int start = t - period + 1;
                if (start < 1 || df2 < IndicatorDefaultConstants.GrangerCausalityMinDf2 || !AreReturnsFinite(retA, retB, start, t))
                {
                    forwardF.Add(null);
                    reverseF.Add(null);
                    directionalDiff.Add(null);
                    dccCorrelation.Add(null);
                    continue;
                }

                double fYX = ComputeGrangerFStatistic(retA, retB, start, period, lagOrder, df2);
                double fXY = ComputeGrangerFStatistic(retB, retA, start, period, lagOrder, df2);
                double diff = (double.IsFinite(fYX) && double.IsFinite(fXY)) ? fYX - fXY : double.NaN;
                double rho = ComputeDccCorrelation(retA.AsSpan(start, period), retB.AsSpan(start, period));

                forwardF.Add(IndicatorCalculationHelper.ToDecimalOrNull(fYX));
                reverseF.Add(IndicatorCalculationHelper.ToDecimalOrNull(fXY));
                directionalDiff.Add(IndicatorCalculationHelper.ToDecimalOrNull(diff));
                dccCorrelation.Add(IndicatorCalculationHelper.ToDecimalOrNull(rho));
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(retA);
            ArrayPool<double>.Shared.Return(retB);
        }
    }

    private static void BuildLogReturns(IReadOnlyList<decimal?> prices, double[] returns, int n)
    {
        returns[0] = double.NaN;
        for (int i = 1; i < n; i++)
        {
            decimal? prev = prices[i - 1];
            decimal? curr = prices[i];
            if (prev is > 0m && curr is > 0m)
            {
                double lr = Math.Log((double)curr.Value / (double)prev.Value);
                returns[i] = double.IsFinite(lr) ? lr : double.NaN;
            }
            else
            {
                returns[i] = double.NaN;
            }
        }
    }

    private static bool AreReturnsFinite(double[] a, double[] b, int start, int end)
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

    private static double ComputeGrangerFStatistic(
        double[] target,
        double[] exogenous,
        int start,
        int period,
        int lagOrder,
        int df2)
    {
        if (period <= 0)
        {
            return 0.0;
        }

        int end = start + period - 1;
        double sumT = 0.0;
        double sumE = 0.0;
        for (int u = start; u <= end; u++)
        {
            sumT += target[u];
            sumE += exogenous[u];
        }

        double meanT = sumT / period;
        double meanE = sumE / period;
        double varT = 0.0;
        double varE = 0.0;
        for (int u = start; u <= end; u++)
        {
            double dt = target[u] - meanT;
            double de = exogenous[u] - meanE;
            varT += dt * dt;
            varE += de * de;
        }

        double stdT = Math.Sqrt(varT / period);
        double stdE = Math.Sqrt(varE / period);
        if (!(stdT > ZNormalization.FlatSigmaEpsilon) ||
            !(stdE > ZNormalization.FlatSigmaEpsilon) ||
            !double.IsFinite(stdT) ||
            !double.IsFinite(stdE))
        {
            return 0.0;
        }

        double invStdT = 1.0 / stdT;
        double invStdE = 1.0 / stdE;

        int dimR = 1 + lagOrder;
        int dimU = 1 + 2 * lagOrder;

        Span<double> aR = stackalloc double[dimR * dimR];
        Span<double> bR = stackalloc double[dimR];
        Span<double> coefR = stackalloc double[dimR];
        Span<double> xR = stackalloc double[dimR];

        Span<double> aU = stackalloc double[dimU * dimU];
        Span<double> bU = stackalloc double[dimU];
        Span<double> coefU = stackalloc double[dimU];
        Span<double> xU = stackalloc double[dimU];

        aR.Clear();
        bR.Clear();
        aU.Clear();
        bU.Clear();

        for (int k = start + lagOrder; k <= end; k++)
        {
            double y = PopulateStandardizedGrangerRow(
                target, exogenous, k, lagOrder, meanT, invStdT, meanE, invStdE, xR, xU);

            AccumulateNormalEquations(aR, bR, xR, y, dimR);
            AccumulateNormalEquations(aU, bU, xU, y, dimU);
        }

        // Apply ridge strictly to slope coefficients (i >= 1) to preserve intercept unbiasedness.
        for (int i = 1; i < dimR; i++)
        {
            aR[i * dimR + i] += IndicatorDefaultConstants.GrangerCausalityRidgeLambda;
        }
        for (int i = 1; i < dimU; i++)
        {
            aU[i * dimU + i] += IndicatorDefaultConstants.GrangerCausalityRidgeLambda;
        }

        if (!ArimaMath.SolveLinearSystem(aR, bR, coefR, dimR) ||
            !ArimaMath.SolveLinearSystem(aU, bU, coefU, dimU))
        {
            return double.NaN;
        }

        double rssR = 0.0;
        double rssU = 0.0;
        for (int k = start + lagOrder; k <= end; k++)
        {
            double y = PopulateStandardizedGrangerRow(
                target, exogenous, k, lagOrder, meanT, invStdT, meanE, invStdE, xR, xU);

            double errR = y - Dot(xR, coefR, dimR);
            double errU = y - Dot(xU, coefU, dimU);
            rssR += errR * errR;
            rssU += errU * errU;
        }

        if (rssR < rssU)
        {
            rssR = rssU;
        }

        if (rssU <= IndicatorDefaultConstants.GrangerCausalityRssFloor)
        {
            return 0.0;
        }

        double fStat = ((rssR - rssU) / lagOrder) / (rssU / df2);
        return double.IsFinite(fStat) ? Math.Max(0.0, fStat) : double.NaN;
    }

    private static double PopulateStandardizedGrangerRow(
        double[] target,
        double[] exogenous,
        int k,
        int lagOrder,
        double meanT,
        double invStdT,
        double meanE,
        double invStdE,
        Span<double> xR,
        Span<double> xU)
    {
        xR[0] = 1.0;
        xU[0] = 1.0;
        for (int lag = 1; lag <= lagOrder; lag++)
        {
            double tLag = (target[k - lag] - meanT) * invStdT;
            double eLag = (exogenous[k - lag] - meanE) * invStdE;
            xR[lag] = tLag;
            xU[lag] = tLag;
            xU[lagOrder + lag] = eLag;
        }

        return (target[k] - meanT) * invStdT;
    }

    /// <summary>
    /// Computes Engle (2002) 1-step-ahead conditional correlation Q_{t|t-1} predicted for bar t
    /// (window end index len - 1) using EWMA-standardized innovations z_0 .. z_{len - 2} (loop i = 1 .. len - 1).
    /// This outputs conditional Q_{t|t-1} given F_{t-1}, distinct from post-innovation filtered Q_{t+1|t}.
    /// </summary>
    private static double ComputeDccCorrelation(ReadOnlySpan<double> r1, ReadOnlySpan<double> r2)
    {
        int len = r1.Length;
        double[]? pooled = len * 2 > MathBufferLimits.StackAllocThreshold ? ArrayPool<double>.Shared.Rent(len * 2) : null;
        try
        {
            Span<double> buf = pooled != null ? pooled.AsSpan(0, len * 2) : stackalloc double[len * 2];
            Span<double> z1 = buf.Slice(0, len);
            Span<double> z2 = buf.Slice(len, len);

            double h1 = InitialVariance(r1, Math.Min(IndicatorDefaultConstants.GrangerCausalityEwmaSeedBars, len));
            double h2 = InitialVariance(r2, Math.Min(IndicatorDefaultConstants.GrangerCausalityEwmaSeedBars, len));
            double ewma = IndicatorDefaultConstants.GrangerCausalityDccEwmaLambda;
            double oneMinusEwma = 1.0 - ewma;

            double sumZ1Sq = 0.0;
            double sumZ2Sq = 0.0;
            double sumZ1Z2 = 0.0;
            for (int i = 0; i < len; i++)
            {
                double x = r1[i];
                double y = r2[i];
                double u1 = x / Math.Sqrt(Math.Max(h1, IndicatorDefaultConstants.GrangerCausalityVarianceFloor));
                double u2 = y / Math.Sqrt(Math.Max(h2, IndicatorDefaultConstants.GrangerCausalityVarianceFloor));
                z1[i] = u1;
                z2[i] = u2;
                sumZ1Sq += u1 * u1;
                sumZ2Sq += u2 * u2;
                sumZ1Z2 += u1 * u2;

                h1 = ewma * h1 + oneMinusEwma * x * x;
                h2 = ewma * h2 + oneMinusEwma * y * y;
            }

            double denomBar = Math.Sqrt(Math.Max(sumZ1Sq * sumZ2Sq, IndicatorDefaultConstants.GrangerCausalityVarianceFloor));
            double qBar12 = Math.Clamp(sumZ1Z2 / denomBar, -0.999999, 0.999999);

            double a = IndicatorDefaultConstants.GrangerCausalityDccAlpha;
            double b = IndicatorDefaultConstants.GrangerCausalityDccBeta;
            double persistence = 1.0 - a - b;

            double q11 = 1.0;
            double q22 = 1.0;
            double q12 = qBar12;

            for (int i = 1; i < len; i++)
            {
                double prev1 = z1[i - 1];
                double prev2 = z2[i - 1];
                q11 = persistence + a * prev1 * prev1 + b * q11;
                q22 = persistence + a * prev2 * prev2 + b * q22;
                q12 = persistence * qBar12 + a * prev1 * prev2 + b * q12;
            }

            double qDenom = Math.Sqrt(Math.Max(q11 * q22, IndicatorDefaultConstants.GrangerCausalityVarianceFloor));
            double rho = q12 / qDenom;
            return double.IsFinite(rho) ? Math.Clamp(rho, -1.0, 1.0) : double.NaN;
        }
        finally
        {
            if (pooled != null)
            {
                ArrayPool<double>.Shared.Return(pooled);
            }
        }
    }

    private static double InitialVariance(ReadOnlySpan<double> r, int seedCount)
    {
        double sum = 0.0;
        for (int i = 0; i < seedCount; i++)
        {
            sum += r[i];
        }
        double mean = sum / seedCount;
        double sumSq = 0.0;
        for (int i = 0; i < seedCount; i++)
        {
            double d = r[i] - mean;
            sumSq += d * d;
        }
        double var = seedCount > 1 ? sumSq / (seedCount - 1) : r[0] * r[0];
        return Math.Max(var, IndicatorDefaultConstants.GrangerCausalityVarianceFloor);
    }

    private static void AccumulateNormalEquations(Span<double> a, Span<double> b, ReadOnlySpan<double> x, double y, int dim)
    {
        for (int i = 0; i < dim; i++)
        {
            double xi = x[i];
            b[i] += xi * y;
            int row = i * dim;
            for (int j = 0; j < dim; j++)
            {
                a[row + j] += xi * x[j];
            }
        }
    }

    private static double Dot(ReadOnlySpan<double> a, ReadOnlySpan<double> b, int dim)
    {
        double sum = 0.0;
        for (int i = 0; i < dim; i++)
        {
            sum += a[i] * b[i];
        }
        return sum;
    }
}
