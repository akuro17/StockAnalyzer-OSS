using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Bayesian Online Changepoint Detection (Adams &amp; MacKay 2007) with a Normal-Inverse-Gamma conjugate prior
/// and two-sided Page's recursive CUSUM detector on rolling Z-standardized log returns.
/// Uses stack-allocated buffers exclusively (zero heap allocation in the bar loop).
/// </summary>
public static class BocpdMath
{
    private static readonly double HalfLogTwoPi = 0.5 * Math.Log(2.0 * Math.PI);

    /// <summary>Lanczos approximation of ln Gamma(z) accurate to double precision for positive inputs.</summary>
    public static double LogGamma(double z)
    {
        if (z < 0.5)
        {
            return Math.Log(Math.PI / Math.Sin(Math.PI * z)) - LogGamma(1.0 - z);
        }

        z -= 1.0;
        double x = 0.99999999999980993
            + 676.5203681218851 / (z + 1.0)
            - 1259.1392167224028 / (z + 2.0)
            + 771.32342877765313 / (z + 3.0)
            - 176.61502916214059 / (z + 4.0)
            + 12.507343278686905 / (z + 5.0)
            - 0.13857109526572012 / (z + 6.0)
            + 9.9843695780195716e-6 / (z + 7.0)
            + 1.5056327351493116e-7 / (z + 8.0);
        double t = z + 7.5;
        return HalfLogTwoPi + (z + 0.5) * Math.Log(t) - t + Math.Log(x);
    }

    /// <summary>
    /// Computes causal rolling BOCPD changepoint probability (<paramref name="changepointProb"/>, bounded in [0, 1]),
    /// run-length summaries (<paramref name="expectedRunLength"/>, <paramref name="mapRunLength"/>),
    /// and two-sided recursive CUSUM scores (<paramref name="cusumUp"/>, <paramref name="cusumDown"/>).
    /// </summary>
    public static void Compute(
        double[] returns,
        Func<int, int, bool> isWindowValid,
        int warmupPeriod,
        double hazardLambda,
        int maxRunLength,
        double cusumDrift,
        double cusumThreshold,
        IList<decimal?> changepointProb,
        IList<decimal?> expectedRunLength,
        IList<decimal?> mapRunLength,
        IList<decimal?> cusumUp,
        IList<decimal?> cusumDown)
    {
        ArgumentNullException.ThrowIfNull(returns);
        ArgumentNullException.ThrowIfNull(isWindowValid);
        ArgumentNullException.ThrowIfNull(changepointProb);
        ArgumentNullException.ThrowIfNull(expectedRunLength);
        ArgumentNullException.ThrowIfNull(mapRunLength);
        ArgumentNullException.ThrowIfNull(cusumUp);
        ArgumentNullException.ThrowIfNull(cusumDown);

        changepointProb.Clear();
        expectedRunLength.Clear();
        mapRunLength.Clear();
        cusumUp.Clear();
        cusumDown.Clear();

        int count = returns.Length;
        if (count == 0)
        {
            return;
        }

        if (maxRunLength < 1 || maxRunLength >= MathBufferLimits.StackAllocThreshold)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxRunLength),
                $"MaxRunLength must be in [1, {MathBufferLimits.StackAllocThreshold - 1}].");
        }

        int len = maxRunLength + 1;
        Span<double> pRun = stackalloc double[len];
        Span<double> pRunNext = stackalloc double[len];
        Span<double> mu = stackalloc double[len];
        Span<double> kappa = stackalloc double[len];
        Span<double> alpha = stackalloc double[len];
        Span<double> beta = stackalloc double[len];

        double hazard = 1.0 / hazardLambda;
        int activeMaxR = 0;
        int initialRunIndex = 0;
        ResetPosteriorState(pRun, mu, kappa, alpha, beta, out activeMaxR);
        double sPlus = 0.0;
        double sMinus = 0.0;

        for (int t = 0; t < count; t++)
        {
            int start = t - warmupPeriod + 1;
            if (t < warmupPeriod || start < 1 || !isWindowValid(start, t))
            {
                ResetPosteriorState(pRun, mu, kappa, alpha, beta, out activeMaxR);
                initialRunIndex = 0;
                sPlus = 0.0;
                sMinus = 0.0;
                changepointProb.Add(null);
                expectedRunLength.Add(null);
                mapRunLength.Add(null);
                cusumUp.Add(null);
                cusumDown.Add(null);
                continue;
            }

            double x = StandardizeLatestReturn(returns, start, warmupPeriod, t);
            if (!double.IsFinite(x))
            {
                ResetPosteriorState(pRun, mu, kappa, alpha, beta, out activeMaxR);
                initialRunIndex = 0;
                sPlus = 0.0;
                sMinus = 0.0;
                changepointProb.Add(null);
                expectedRunLength.Add(null);
                mapRunLength.Add(null);
                cusumUp.Add(null);
                cusumDown.Add(null);
                continue;
            }

            int nextActiveMaxR = Math.Min(activeMaxR + 1, maxRunLength);
            pRunNext.Slice(0, nextActiveMaxR + 1).Clear();

            double growthMass = 0.0;
            for (int r = 0; r <= activeMaxR; r++)
            {
                double pred = StudentTPredictivePdf(x, mu[r], kappa[r], alpha[r], beta[r]);
                double weighted = pRun[r] * pred;
                int destR = r < maxRunLength ? r + 1 : maxRunLength;
                pRunNext[destR] += weighted * (1.0 - hazard);
                growthMass += weighted * hazard;
            }

            pRunNext[0] = growthMass;

            double totalMass = 0.0;
            for (int r = 0; r <= nextActiveMaxR; r++)
            {
                totalMass += pRunNext[r];
            }

            if (!(totalMass > IndicatorDefaultConstants.BocpdProbabilityFloor) || !double.IsFinite(totalMass))
            {
                ResetPosteriorState(pRun, mu, kappa, alpha, beta, out activeMaxR);
                initialRunIndex = 0;
            }
            else
            {
                for (int r = 0; r <= nextActiveMaxR; r++)
                {
                    pRun[r] = pRunNext[r] / totalMass;
                }

                for (int r = nextActiveMaxR; r >= 1; r--)
                {
                    double kPrev = kappa[r - 1];
                    double muPrev = mu[r - 1];
                    double kNew = kPrev + 1.0;
                    double diff = x - muPrev;

                    kappa[r] = kNew;
                    mu[r] = (kPrev * muPrev + x) / kNew;
                    alpha[r] = alpha[r - 1] + 0.5;
                    beta[r] = beta[r - 1] + (kPrev * diff * diff) / (2.0 * kNew);
                }

                mu[0] = IndicatorDefaultConstants.BocpdPriorMu0;
                kappa[0] = IndicatorDefaultConstants.BocpdPriorKappa0;
                alpha[0] = IndicatorDefaultConstants.BocpdPriorAlpha0;
                beta[0] = IndicatorDefaultConstants.BocpdPriorBeta0;
                activeMaxR = nextActiveMaxR;
                if (initialRunIndex <= maxRunLength)
                {
                    initialRunIndex++;
                }
            }

            int shortEnd = Math.Min(IndicatorDefaultConstants.BocpdShortRunCutoff, activeMaxR);
            double cpProb = 0.0;
            for (int r = 0; r <= shortEnd; r++)
            {
                // Exclude the unbroken initial survival bin during the first Cutoff steps after reset
                // (including r == 0 right after an underflow reset) to prevent a deterministic 1.0m spike.
                if (r == initialRunIndex && initialRunIndex <= IndicatorDefaultConstants.BocpdShortRunCutoff)
                {
                    continue;
                }

                cpProb += pRun[r];
            }
            cpProb = Math.Clamp(cpProb, 0.0, 1.0);

            double expRun = 0.0;
            int mapR = 0;
            double mapWeight = pRun[0];
            for (int r = 0; r <= activeMaxR; r++)
            {
                double w = pRun[r];
                expRun += r * w;
                if (w > mapWeight)
                {
                    mapWeight = w;
                    mapR = r;
                }
            }

            sPlus = Math.Max(0.0, sPlus + x - cusumDrift);
            sMinus = Math.Max(0.0, sMinus - x - cusumDrift);

            changepointProb.Add(IndicatorCalculationHelper.ToDecimalOrNull(cpProb));
            expectedRunLength.Add(IndicatorCalculationHelper.ToDecimalOrNull(expRun));
            mapRunLength.Add(mapR);
            cusumUp.Add(IndicatorCalculationHelper.ToDecimalOrNull(sPlus));
            cusumDown.Add(IndicatorCalculationHelper.ToDecimalOrNull(sMinus));

            if (sPlus > cusumThreshold)
            {
                sPlus = 0.0;
            }
            if (sMinus > cusumThreshold)
            {
                sMinus = 0.0;
            }
        }
    }

    private static double StandardizeLatestReturn(double[] returns, int start, int warmupPeriod, int currentIdx)
    {
        double sum = 0.0;
        for (int j = start; j <= currentIdx; j++)
        {
            sum += returns[j];
        }

        double mean = sum / warmupPeriod;
        double sumSq = 0.0;
        for (int j = start; j <= currentIdx; j++)
        {
            double dev = returns[j] - mean;
            sumSq += dev * dev;
        }

        double stdDev = Math.Sqrt(sumSq / (warmupPeriod - 1));
        if (!(stdDev > IndicatorDefaultConstants.BocpdMinStdDev) || !double.IsFinite(stdDev))
        {
            return 0.0;
        }

        return (returns[currentIdx] - mean) / stdDev;
    }

    private static double StudentTPredictivePdf(double x, double mu, double kappa, double alpha, double beta)
    {
        double nu = 2.0 * alpha;
        double sigmaSq = (beta * (kappa + 1.0)) / (alpha * kappa);
        double diff = x - mu;
        double z = (diff * diff) / (nu * sigmaSq);
        double logPdf = LogGamma((nu + 1.0) * 0.5)
            - LogGamma(nu * 0.5)
            - 0.5 * Math.Log(Math.PI * nu * sigmaSq)
            - ((nu + 1.0) * 0.5) * Math.Log(1.0 + z);
        double pdf = Math.Exp(logPdf);
        return double.IsFinite(pdf) && pdf > IndicatorDefaultConstants.BocpdProbabilityFloor
            ? pdf
            : IndicatorDefaultConstants.BocpdProbabilityFloor;
    }

    private static void ResetPosteriorState(
        Span<double> pRun,
        Span<double> mu,
        Span<double> kappa,
        Span<double> alpha,
        Span<double> beta,
        out int activeMaxR)
    {
        pRun.Clear();
        pRun[0] = 1.0;
        mu[0] = IndicatorDefaultConstants.BocpdPriorMu0;
        kappa[0] = IndicatorDefaultConstants.BocpdPriorKappa0;
        alpha[0] = IndicatorDefaultConstants.BocpdPriorAlpha0;
        beta[0] = IndicatorDefaultConstants.BocpdPriorBeta0;
        activeMaxR = 0;
    }
}
