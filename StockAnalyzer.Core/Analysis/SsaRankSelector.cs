using System;

namespace StockAnalyzer.Core.Analysis;

/// <summary>
/// Method used to adaptively estimate the effective rank (number of principal components) in SSA decomposition.
/// </summary>
public enum SsaRankSelectionMethod
{
    /// <summary>
    /// Selects components whose eigenvalue exceeds the mean eigenvalue of the trajectory covariance matrix.
    /// </summary>
    KaiserGuttman = 0,

    /// <summary>
    /// Selects components up to the maximum curvature point (elbow) on the logarithmic scree plot.
    /// </summary>
    ScreeMaxCurvature = 1,

    /// <summary>
    /// Selects components until the cumulative energy ratio reaches the target energy threshold.
    /// </summary>
    CumulativeEnergy = 2
}

/// <summary>
/// Provides adaptive rank estimation and noise floor separation algorithms for Singular Spectrum Analysis (SSA).
/// </summary>
public static class SsaRankSelector
{
    public const double EigenvalueEpsilon = 1e-12;

    /// <summary>
    /// Default cumulative-energy target (fraction of the total eigenvalue sum) used by <see cref="SsaRankSelectionMethod.CumulativeEnergy"/>.
    /// </summary>
    public const double DefaultTargetEnergy = 0.90;

    /// <summary>
    /// Estimates the effective signal rank given descending sorted eigenvalues.
    /// </summary>
    public static int EstimateSignalRank(
        ReadOnlySpan<double> eigenvalues,
        SsaRankSelectionMethod method,
        double targetEnergy = DefaultTargetEnergy,
        int maxRank = int.MaxValue)
    {
        int l = eigenvalues.Length;
        if (l < 2) return 1;

        double sumLambda = 0.0;
        for (int i = 0; i < l; i++) sumLambda += Math.Max(0.0, eigenvalues[i]);
        if (sumLambda <= EigenvalueEpsilon) return 1;

        int r = method switch
        {
            SsaRankSelectionMethod.KaiserGuttman => SelectKaiser(eigenvalues, sumLambda, l),
            SsaRankSelectionMethod.ScreeMaxCurvature => SelectScree(eigenvalues, l),
            SsaRankSelectionMethod.CumulativeEnergy => SelectEnergy(eigenvalues, sumLambda, targetEnergy, l),
            _ => 2
        };

        return Math.Clamp(r, 1, Math.Min(maxRank, l));
    }

    /// <summary>
    /// Estimates the effective signal rank of a decomposition. <see cref="SsaDecompositionResult.Eigenvalues"/> is in the raw Jacobi order, so the
    /// eigenvalues are first read in descending order through <see cref="SsaDecompositionResult.SortedIndices"/>.
    /// </summary>
    public static int EstimateSignalRank(
        SsaDecompositionResult decomposition,
        SsaRankSelectionMethod method,
        int maxRank,
        double targetEnergy = DefaultTargetEnergy)
    {
        ArgumentNullException.ThrowIfNull(decomposition);

        return EstimateSignalRank(decomposition.Eigenvalues, decomposition.SortedIndices, method, maxRank, targetEnergy);
    }

    /// <summary>
    /// Estimates the effective signal rank from eigenvalues in raw (Jacobi) order plus the indices that sort them descending.
    /// This is the single place that reads the eigenvalues in descending order; callers that hold the two arrays without a
    /// <see cref="SsaDecompositionResult"/> (for example a drawing that builds its own lag matrix) use it instead of sorting by hand.
    /// </summary>
    public static int EstimateSignalRank(
        double[] eigenvalues,
        int[] sortedIndices,
        SsaRankSelectionMethod method,
        int maxRank,
        double targetEnergy = DefaultTargetEnergy)
    {
        ArgumentNullException.ThrowIfNull(eigenvalues);
        ArgumentNullException.ThrowIfNull(sortedIndices);

        int l = sortedIndices.Length;
        double[] sorted = new double[l];
        for (int i = 0; i < l; i++)
        {
            sorted[i] = eigenvalues[sortedIndices[i]];
        }

        return EstimateSignalRank(sorted, method, targetEnergy, maxRank);
    }

    private static int SelectKaiser(ReadOnlySpan<double> lambda, double sumLambda, int l)
    {
        double mean = sumLambda / l;
        int count = 0;
        for (int i = 0; i < l; i++)
        {
            if (lambda[i] >= mean) count++;
            else break; // Descending sorted
        }
        return Math.Max(1, count);
    }

    private static int SelectScree(ReadOnlySpan<double> lambda, int l)
    {
        if (l < 3) return 1;
        double maxD2 = 0.05; // Noise floor significance threshold
        int bestIdx = 1;

        for (int m = 1; m < l - 1; m++)
        {
            double vPrev = Math.Max(lambda[m - 1], EigenvalueEpsilon);
            double vCurr = Math.Max(lambda[m], EigenvalueEpsilon);
            double vNext = Math.Max(lambda[m + 1], EigenvalueEpsilon);

            double d2 = Math.Log(vPrev) - 2.0 * Math.Log(vCurr) + Math.Log(vNext);
            if (d2 > maxD2)
            {
                maxD2 = d2;
                bestIdx = m;
            }
        }
        return bestIdx;
    }

    private static int SelectEnergy(ReadOnlySpan<double> lambda, double sumLambda, double target, int l)
    {
        double running = 0.0;
        double threshold = sumLambda * Math.Clamp(target, 0.01, 1.0);
        for (int i = 0; i < l; i++)
        {
            running += Math.Max(0.0, lambda[i]);
            if (running >= threshold) return i + 1;
        }
        return l;
    }
}
