using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Analysis;

/// <summary>
/// Encapsulates the results of an HMM future trajectory projection calculation.
/// </summary>
public sealed class HmmProjectionResult
{
    public static readonly HmmProjectionResult Empty = new(
        Array.Empty<Point>(),
        Array.Empty<Point>(),
        Array.Empty<Point>(),
        Array.Empty<double>(),
        Array.Empty<double>(),
        Array.Empty<double>(),
        new double[0, 0],
        0,
        0.0,
        0);

    public IReadOnlyList<Point> ProjectedPoints { get; }
    public IReadOnlyList<Point> UpperBandPoints { get; }
    public IReadOnlyList<Point> LowerBandPoints { get; }
    public IReadOnlyList<double> FilteredStateProbabilities { get; }
    public IReadOnlyList<double> StateMeans { get; }
    public IReadOnlyList<double> StateStdDevs { get; }
    public double[,] TransitionMatrix { get; }
    public int CurrentRegimeIndex { get; }
    public double BullStateProbability { get; }
    public int SampleCount { get; }

    public HmmProjectionResult(
        IReadOnlyList<Point> projectedPoints,
        IReadOnlyList<Point> upperBandPoints,
        IReadOnlyList<Point> lowerBandPoints,
        IReadOnlyList<double> filteredStateProbabilities,
        IReadOnlyList<double> stateMeans,
        IReadOnlyList<double> stateStdDevs,
        double[,] transitionMatrix,
        int currentRegimeIndex,
        double bullStateProbability,
        int sampleCount)
    {
        ProjectedPoints = projectedPoints;
        UpperBandPoints = upperBandPoints;
        LowerBandPoints = lowerBandPoints;
        FilteredStateProbabilities = filteredStateProbabilities;
        StateMeans = stateMeans;
        StateStdDevs = stateStdDevs;
        TransitionMatrix = transitionMatrix;
        CurrentRegimeIndex = currentRegimeIndex;
        BullStateProbability = bullStateProbability;
        SampleCount = sampleCount;
    }
}

/// <summary>
/// Pure C# mathematical engine for Gaussian Hidden Markov Model (HMM) future trajectory projection.
/// Fits a Gaussian HMM on log-return series within the selected window using scaled Baum-Welch EM,
/// then extrapolates multi-step regime transition probabilities, expected log-returns, and mixture variance
/// under an explicit Independent Step Approximation with numerical overflow guardrails.
/// </summary>
public static class HmmProjectionAnalysis
{
    public const int MinSampleCount = 10;

    public static HmmProjectionResult CalculateProjection(
        IReadOnlyList<double> samples,
        IReadOnlyList<DateTime> timestamps,
        int futureSteps = 20,
        TimeSpan timeframeSpan = default,
        int states = 2,
        int maxIterations = 30,
        double tolerance = 1e-4,
        bool showConfidenceBand = true,
        decimal confidenceMultiplier = 2.0m)
    {
        if (samples == null || timestamps == null || samples.Count < MinSampleCount || samples.Count != timestamps.Count)
        {
            return HmmProjectionResult.Empty;
        }

        int n = samples.Count;
        int k = Math.Clamp(states, 2, 3);
        int w = n - 1; // number of log returns

        // 1. Extract log-return observation series
        double[] r = new double[w];
        for (int i = 0; i < w; i++)
        {
            double pCurr = samples[i + 1];
            double pPrev = samples[i];

            if (pCurr <= 1e-12 || pPrev <= 1e-12 || double.IsNaN(pCurr) || double.IsNaN(pPrev) || double.IsInfinity(pCurr) || double.IsInfinity(pPrev))
            {
                return HmmProjectionResult.Empty;
            }

            double ret = Math.Log(pCurr / pPrev);
            if (double.IsNaN(ret) || double.IsInfinity(ret))
            {
                return HmmProjectionResult.Empty;
            }
            r[i] = ret;
        }

        // 2. Global statistics initialization
        double sumX = 0.0;
        for (int m = 0; m < w; m++)
        {
            sumX += r[m];
        }
        double muG = sumX / w;

        double sumSqDiff = 0.0;
        for (int m = 0; m < w; m++)
        {
            double diff = r[m] - muG;
            sumSqDiff += diff * diff;
        }
        double sigmaG2 = Math.Max(sumSqDiff / w, 1e-6);
        double stdG = Math.Sqrt(sigmaG2);

        // Pre-allocate HMM working arrays
        double[] pi = new double[k];
        double[,] transition = new double[k, k];
        double[] mu = new double[k];
        double[] sigmaSq = new double[k];

        for (int i = 0; i < k; i++)
        {
            pi[i] = 1.0 / k;
            mu[i] = muG + stdG * ((2.0 * i) / (k - 1.0) - 1.0);
            sigmaSq[i] = sigmaG2;
            for (int j = 0; j < k; j++)
            {
                transition[i, j] = (i == j) ? 0.8 : (0.2 / (k - 1));
            }
        }

        double[,] alpha = new double[w, k];
        double[,] beta = new double[w, k];
        double[,] gamma = new double[w, k];
        double[,,] xi = new double[w, k, k];
        double[] cScale = new double[w];

        int clampedMaxIter = Math.Clamp(maxIterations, 1, 200);
        double clampedTol = Math.Max(1e-6, tolerance);
        double prevLogLikelihood = double.NegativeInfinity;

        // 3. Scaled Baum-Welch EM Loop
        for (int iter = 0; iter < clampedMaxIter; iter++)
        {
            // Forward pass
            double sumAlpha0 = 0.0;
            for (int i = 0; i < k; i++)
            {
                double b_i = ComputeEmission(r[0], mu[i], sigmaSq[i]);
                double aPrime = pi[i] * b_i;
                alpha[0, i] = aPrime;
                sumAlpha0 += aPrime;
            }

            if (sumAlpha0 > 1e-250 && !double.IsNaN(sumAlpha0) && !double.IsInfinity(sumAlpha0))
            {
                double c0 = 1.0 / sumAlpha0;
                cScale[0] = c0;
                for (int i = 0; i < k; i++)
                {
                    alpha[0, i] *= c0;
                }
            }
            else
            {
                cScale[0] = 1.0;
                for (int i = 0; i < k; i++)
                {
                    alpha[0, i] = 1.0 / k;
                }
            }

            for (int m = 1; m < w; m++)
            {
                double sumAlphaM = 0.0;
                for (int j = 0; j < k; j++)
                {
                    double prevSum = 0.0;
                    for (int i = 0; i < k; i++)
                    {
                        prevSum += alpha[m - 1, i] * transition[i, j];
                    }
                    double b_j = ComputeEmission(r[m], mu[j], sigmaSq[j]);
                    double aPrime = prevSum * b_j;
                    alpha[m, j] = aPrime;
                    sumAlphaM += aPrime;
                }

                if (sumAlphaM > 1e-250 && !double.IsNaN(sumAlphaM) && !double.IsInfinity(sumAlphaM))
                {
                    double cm = 1.0 / sumAlphaM;
                    cScale[m] = cm;
                    for (int j = 0; j < k; j++)
                    {
                        alpha[m, j] *= cm;
                    }
                }
                else
                {
                    cScale[m] = 1.0;
                    for (int j = 0; j < k; j++)
                    {
                        alpha[m, j] = 1.0 / k;
                    }
                }
            }

            // Backward pass
            for (int i = 0; i < k; i++)
            {
                beta[w - 1, i] = cScale[w - 1];
            }

            for (int m = w - 2; m >= 0; m--)
            {
                for (int i = 0; i < k; i++)
                {
                    double sumTrans = 0.0;
                    for (int j = 0; j < k; j++)
                    {
                        double b_j = ComputeEmission(r[m + 1], mu[j], sigmaSq[j]);
                        sumTrans += transition[i, j] * b_j * beta[m + 1, j];
                    }
                    beta[m, i] = cScale[m] * sumTrans;
                }
            }

            // Expectation (E-step)
            for (int m = 0; m < w; m++)
            {
                double sumGamma = 0.0;
                for (int l = 0; l < k; l++)
                {
                    sumGamma += alpha[m, l] * beta[m, l];
                }
                double denomGamma = Math.Max(sumGamma, 1e-250);
                for (int i = 0; i < k; i++)
                {
                    gamma[m, i] = (alpha[m, i] * beta[m, i]) / denomGamma;
                }
            }

            for (int m = 0; m < w - 1; m++)
            {
                double sumXi = 0.0;
                for (int l1 = 0; l1 < k; l1++)
                {
                    for (int l2 = 0; l2 < k; l2++)
                    {
                        double b_l2 = ComputeEmission(r[m + 1], mu[l2], sigmaSq[l2]);
                        sumXi += alpha[m, l1] * transition[l1, l2] * b_l2 * beta[m + 1, l2];
                    }
                }
                double denomXi = Math.Max(sumXi, 1e-250);
                for (int i = 0; i < k; i++)
                {
                    for (int j = 0; j < k; j++)
                    {
                        double b_j = ComputeEmission(r[m + 1], mu[j], sigmaSq[j]);
                        xi[m, i, j] = (alpha[m, i] * transition[i, j] * b_j * beta[m + 1, j]) / denomXi;
                    }
                }
            }

            // Maximization (M-step)
            for (int i = 0; i < k; i++)
            {
                pi[i] = gamma[0, i];
            }

            for (int i = 0; i < k; i++)
            {
                double sumGammaI = 0.0;
                for (int m = 0; m < w - 1; m++)
                {
                    sumGammaI += gamma[m, i];
                }

                if (sumGammaI > 1e-12)
                {
                    double sumRowA = 0.0;
                    for (int j = 0; j < k; j++)
                    {
                        double sumXiIJ = 0.0;
                        for (int m = 0; m < w - 1; m++)
                        {
                            sumXiIJ += xi[m, i, j];
                        }
                        transition[i, j] = sumXiIJ / sumGammaI;
                        sumRowA += transition[i, j];
                    }
                    double normRowA = Math.Max(sumRowA, 1e-12);
                    for (int j = 0; j < k; j++)
                    {
                        transition[i, j] /= normRowA;
                    }
                }
                else
                {
                    for (int j = 0; j < k; j++)
                    {
                        transition[i, j] = (i == j) ? 0.8 : (0.2 / (k - 1));
                    }
                }
            }

            for (int i = 0; i < k; i++)
            {
                double sumGammaIAll = 0.0;
                double sumGammaIX = 0.0;
                for (int m = 0; m < w; m++)
                {
                    double g = gamma[m, i];
                    sumGammaIAll += g;
                    sumGammaIX += g * r[m];
                }

                if (sumGammaIAll > 1e-12)
                {
                    double newMu = sumGammaIX / sumGammaIAll;
                    mu[i] = newMu;

                    double sumGammaIVar = 0.0;
                    for (int m = 0; m < w; m++)
                    {
                        double g = gamma[m, i];
                        double diff = r[m] - newMu;
                        sumGammaIVar += g * diff * diff;
                    }
                    sigmaSq[i] = Math.Max(sumGammaIVar / sumGammaIAll, 1e-6);
                }
                else
                {
                    sigmaSq[i] = Math.Max(sigmaG2, 1e-6);
                }
            }

            // Convergence check
            double logLikelihood = 0.0;
            for (int m = 0; m < w; m++)
            {
                logLikelihood -= Math.Log(Math.Max(1e-250, cScale[m]));
            }

            if (iter >= 1 && Math.Abs(logLikelihood - prevLogLikelihood) <= clampedTol)
            {
                break;
            }
            prevLogLikelihood = logLikelihood;
        }

        // 4. State Canonical Sorting (Sort states by mean return mu ascending: State 0 = Bearish, State K-1 = Bullish)
        int[] sortOrder = Enumerable.Range(0, k).OrderBy(i => mu[i]).ThenByDescending(i => sigmaSq[i]).ToArray();
        double[] sortedMu = new double[k];
        double[] sortedSigmaSq = new double[k];
        double[,] sortedTransition = new double[k, k];
        double[] sortedPi = new double[k];

        for (int i = 0; i < k; i++)
        {
            int oldI = sortOrder[i];
            sortedMu[i] = mu[oldI];
            sortedSigmaSq[i] = sigmaSq[oldI];
            sortedPi[i] = pi[oldI];
            for (int j = 0; j < k; j++)
            {
                int oldJ = sortOrder[j];
                sortedTransition[i, j] = transition[oldI, oldJ];
            }
        }

        // 5. Final forward filter to obtain normalized state distribution at the end of selection window
        double[] currentP = new double[k];
        double sumFinalAlpha0 = 0.0;
        for (int i = 0; i < k; i++)
        {
            double b_i = ComputeEmission(r[0], sortedMu[i], sortedSigmaSq[i]);
            double aPrime = sortedPi[i] * b_i;
            alpha[0, i] = aPrime;
            sumFinalAlpha0 += aPrime;
        }
        if (sumFinalAlpha0 > 1e-250 && !double.IsNaN(sumFinalAlpha0) && !double.IsInfinity(sumFinalAlpha0))
        {
            double finalC0 = 1.0 / sumFinalAlpha0;
            for (int i = 0; i < k; i++) alpha[0, i] *= finalC0;
        }
        else
        {
            for (int i = 0; i < k; i++) alpha[0, i] = 1.0 / k;
        }

        for (int m = 1; m < w; m++)
        {
            double sumFinalAlphaM = 0.0;
            for (int j = 0; j < k; j++)
            {
                double prevSum = 0.0;
                for (int i = 0; i < k; i++)
                {
                    prevSum += alpha[m - 1, i] * sortedTransition[i, j];
                }
                double b_j = ComputeEmission(r[m], sortedMu[j], sortedSigmaSq[j]);
                double aPrime = prevSum * b_j;
                alpha[m, j] = aPrime;
                sumFinalAlphaM += aPrime;
            }
            if (sumFinalAlphaM > 1e-250 && !double.IsNaN(sumFinalAlphaM) && !double.IsInfinity(sumFinalAlphaM))
            {
                double finalCm = 1.0 / sumFinalAlphaM;
                for (int j = 0; j < k; j++) alpha[m, j] *= finalCm;
            }
            else
            {
                for (int j = 0; j < k; j++) alpha[m, j] = 1.0 / k;
            }
        }

        for (int i = 0; i < k; i++)
        {
            currentP[i] = alpha[w - 1, i];
        }

        // Bull state is the state with the highest mean return (index k - 1)
        int bullStateIndex = k - 1;
        double bullStateProbability = currentP[bullStateIndex];

        int currentRegimeIndex = 0;
        double maxProb = currentP[0];
        for (int i = 1; i < k; i++)
        {
            if (currentP[i] > maxProb)
            {
                maxProb = currentP[i];
                currentRegimeIndex = i;
            }
        }

        // 6. Future Extrapolation & Uncertainty Cone Diffusion
        int steps = Math.Clamp(futureSteps, 1, 100);
        if (timeframeSpan <= TimeSpan.Zero)
        {
            if (timestamps.Count >= 2)
            {
                double avgMs = (timestamps[^1] - timestamps[0]).TotalMilliseconds / (timestamps.Count - 1);
                if (avgMs > 0)
                {
                    timeframeSpan = TimeSpan.FromMilliseconds(avgMs);
                }
            }
            if (timeframeSpan <= TimeSpan.Zero)
            {
                timeframeSpan = TimeSpan.FromDays(1);
            }
        }

        var projectedPoints = new List<Point>(steps + 1);
        var upperPoints = new List<Point>(steps + 1);
        var lowerPoints = new List<Point>(steps + 1);

        var lastTime = timestamps[^1];
        var lastPrice = samples[^1];
        var initialPoint = new Point((double)lastTime.Ticks, lastPrice);
        projectedPoints.Add(initialPoint);
        upperPoints.Add(initialPoint);
        lowerPoints.Add(initialPoint);

        double[] stateVec = (double[])currentP.Clone();
        double cumLogReturn = 0.0;
        double cumVariance = 0.0;
        double multiplier = Math.Max(0.0, (double)confidenceMultiplier);

        for (int step = 1; step <= steps; step++)
        {
            DateTime futureTime = lastTime + (timeframeSpan * step);

            // Step transition: stateVec = stateVec * sortedTransition
            double[] nextStateVec = new double[k];
            for (int j = 0; j < k; j++)
            {
                double sumP = 0.0;
                for (int i = 0; i < k; i++)
                {
                    sumP += stateVec[i] * sortedTransition[i, j];
                }
                nextStateVec[j] = sumP;
            }
            stateVec = nextStateVec;

            // Compute expected return and mixture variance
            double expReturn = 0.0;
            double secondMoment = 0.0;
            for (int i = 0; i < k; i++)
            {
                expReturn += stateVec[i] * sortedMu[i];
                secondMoment += stateVec[i] * (sortedSigmaSq[i] + sortedMu[i] * sortedMu[i]);
            }
            double stepVar = Math.Max(1e-8, secondMoment - (expReturn * expReturn));

            cumLogReturn += expReturn;
            cumVariance += stepVar;

            // Numerical guardrails: clamp cumulative log-return and band width to prevent exp overflow
            double clampedCumReturn = Math.Clamp(cumLogReturn, -3.0, 3.0);
            double stdDev = Math.Sqrt(Math.Max(cumVariance, 1e-8));
            double deltaBand = Math.Clamp(multiplier * stdDev, 0.0, 3.0);

            double projectedPrice = Math.Max(lastPrice * Math.Exp(clampedCumReturn), 1e-12);
            double upperPrice = showConfidenceBand ? Math.Max(lastPrice * Math.Exp(clampedCumReturn + deltaBand), 1e-12) : projectedPrice;
            double lowerPrice = showConfidenceBand ? Math.Max(lastPrice * Math.Exp(clampedCumReturn - deltaBand), 1e-12) : projectedPrice;

            projectedPoints.Add(new Point((double)futureTime.Ticks, projectedPrice));
            upperPoints.Add(new Point((double)futureTime.Ticks, upperPrice));
            lowerPoints.Add(new Point((double)futureTime.Ticks, lowerPrice));
        }

        double[] stateStdDevs = sortedSigmaSq.Select(Math.Sqrt).ToArray();

        return new HmmProjectionResult(
            projectedPoints,
            upperPoints,
            lowerPoints,
            currentP,
            sortedMu,
            stateStdDevs,
            sortedTransition,
            currentRegimeIndex,
            bullStateProbability,
            n);
    }

    private static double ComputeEmission(double x, double mu, double sigmaSq)
    {
        double denom = Math.Sqrt(2.0 * Math.PI * sigmaSq);
        double diff = x - mu;
        double exponent = -(diff * diff) / (2.0 * sigmaSq);
        double density = (1.0 / denom) * Math.Exp(exponent);
        if (double.IsNaN(density) || density < 1e-250)
        {
            return 1e-250;
        }
        return density;
    }
}
