using System;
using System.Buffers;

namespace StockAnalyzer.Core.Analysis;

/// <summary>
/// Provides diagnostic metrics and separability evaluation tools for Singular Spectrum Analysis (SSA),
/// including the weighted correlation (w-correlation) matrix.
/// </summary>
public static class SsaDiagnostics
{
    public const double CorrelationEpsilon = 1e-12;

    /// <summary>
    /// Computes the normalized w-correlation matrix between reconstructed SSA components.
    /// Values close to 0 indicate strong separability (orthogonality in the Hankel inner product space),
    /// while values near 1 indicate strong correlation (components should likely be grouped together).
    /// </summary>
    /// <param name="reconstructedComponentsFlat">Flat array of length componentCount * windowSize containing reconstructed time series.</param>
    /// <param name="componentCount">Number of components r.</param>
    /// <param name="windowSize">Window size W (length of each reconstructed component).</param>
    /// <param name="embeddingDimension">Lag window length L (2 &lt;= L &lt;= W / 2).</param>
    /// <param name="destinationMatrixFlat">Destination matrix of length componentCount * componentCount for the symmetric correlation coefficients.</param>
    public static void ComputeWCorrelationMatrix(
        ReadOnlySpan<double> reconstructedComponentsFlat,
        int componentCount,
        int windowSize,
        int embeddingDimension,
        Span<double> destinationMatrixFlat)
    {
        if (componentCount <= 0 || windowSize <= 0 || embeddingDimension <= 0)
        {
            return;
        }

        int l = embeddingDimension;
        int k = windowSize - l + 1;
        int lStar = Math.Min(l, k);
        int kStar = Math.Max(l, k);

        // Calculate w-weights
        Span<double> weights = stackalloc double[Math.Min(windowSize, 256)];
        double[]? pooledWeights = null;
        if (windowSize > 256)
        {
            pooledWeights = ArrayPool<double>.Shared.Rent(windowSize);
            weights = pooledWeights.AsSpan(0, windowSize);
        }

        try
        {
            for (int t = 0; t < windowSize; t++)
            {
                if (t < lStar) weights[t] = t + 1;
                else if (t < kStar) weights[t] = lStar;
                else weights[t] = windowSize - t;
            }

            // Compute w-norms
            Span<double> norms = stackalloc double[componentCount];
            for (int m = 0; m < componentCount; m++)
            {
                ReadOnlySpan<double> comp = reconstructedComponentsFlat.Slice(m * windowSize, windowSize);
                double sumSq = 0.0;
                for (int t = 0; t < windowSize; t++)
                {
                    sumSq += weights[t] * comp[t] * comp[t];
                }
                norms[m] = Math.Sqrt(sumSq);
            }

            // Compute normalized w-correlation matrix
            for (int i = 0; i < componentCount; i++)
            {
                destinationMatrixFlat[i * componentCount + i] = 1.0;
                ReadOnlySpan<double> compI = reconstructedComponentsFlat.Slice(i * windowSize, windowSize);
                double normI = norms[i];

                for (int j = i + 1; j < componentCount; j++)
                {
                    ReadOnlySpan<double> compJ = reconstructedComponentsFlat.Slice(j * windowSize, windowSize);
                    double normJ = norms[j];

                    double denom = normI * normJ;
                    double rho = 0.0;
                    if (denom > CorrelationEpsilon)
                    {
                        double dot = 0.0;
                        for (int t = 0; t < windowSize; t++)
                        {
                            dot += weights[t] * compI[t] * compJ[t];
                        }
                        rho = Math.Clamp(dot / denom, -1.0, 1.0);
                    }

                    destinationMatrixFlat[i * componentCount + j] = rho;
                    destinationMatrixFlat[j * componentCount + i] = rho;
                }
            }
        }
        finally
        {
            if (pooledWeights != null)
            {
                ArrayPool<double>.Shared.Return(pooledWeights);
            }
        }
    }

    /// <summary>
    /// Computes the overall separability score (0.0% to 100.0%) for reconstructed SSA components from a flat w-correlation matrix.
    /// Higher scores indicate minimal cross-correlation leakage between distinct components (ideal separability).
    /// </summary>
    /// <param name="wCorrMatrixFlat">Flat w-correlation matrix of length at least componentCount * componentCount.</param>
    /// <param name="componentCount">Number of components r.</param>
    /// <returns>Separability score as a percentage in the range [0.0, 100.0].</returns>
    public static double ComputeSeparabilityScore(ReadOnlySpan<double> wCorrMatrixFlat, int componentCount)
    {
        if (componentCount <= 1)
        {
            return 100.0;
        }

        if (wCorrMatrixFlat.Length < componentCount * componentCount)
        {
            throw new ArgumentException(
                $"Matrix length ({wCorrMatrixFlat.Length}) must be at least {componentCount * componentCount}.",
                nameof(wCorrMatrixFlat));
        }

        double sumOffDiag = 0.0;
        int pairCount = 0;

        for (int i = 0; i < componentCount; i++)
        {
            for (int j = i + 1; j < componentCount; j++)
            {
                sumOffDiag += Math.Abs(wCorrMatrixFlat[i * componentCount + j]);
                pairCount++;
            }
        }

        if (pairCount == 0)
        {
            return 100.0;
        }

        double meanOffDiag = sumOffDiag / pairCount;
        return Math.Clamp((1.0 - meanOffDiag) * 100.0, 0.0, 100.0);
    }

    /// <summary>
    /// Maps a separability score to a qualitative descriptive grade.
    /// </summary>
    /// <param name="score">Separability score in [0.0, 100.0].</param>
    /// <returns>"Excellent", "Good", "Moderate", or "Poor".</returns>
    public static string GetSeparabilityGrade(double score)
    {
        return score switch
        {
            >= 90.0 => "Excellent",
            >= 75.0 => "Good",
            >= 60.0 => "Moderate",
            _ => "Poor"
        };
    }
}
