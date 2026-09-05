using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Analysis;

/// <summary>
/// Encapsulates the results of a Singular Spectrum Analysis (SSA) matrix decomposition.
/// </summary>
public sealed record SsaDecompositionResult(
    double[] Eigenvalues,
    double[] SingularValues,
    double[,] Eigenvectors,
    double[] ComponentEnergies,
    int[] SortedIndices,
    int EffectiveRank,
    double Slope,
    double Intercept,
    bool IsDegenerate);

/// <summary>
/// Pure C# mathematical core engine for Singular Spectrum Analysis (SSA) decomposition.
/// Provides building blocks for detrending, lag-covariance construction, Jacobi eigendecomposition,
/// diagonal averaging (Hankelization) reconstruction, and causal endpoint estimation.
/// </summary>
public static class SsaDecompositionEngine
{
    public const int MinSampleCount = 4;
    public const int MaxJacobiSweeps = 50;
    public const double JacobiConvergenceTolerance = 1e-12;
    public const double DegenerateSeriesEpsilon = 1e-10;

    /// <summary>
    /// Applies detrending preprocessing to an input price series.
    /// </summary>
    public static void Detrend(
        ReadOnlySpan<double> input,
        Span<double> destination,
        SsaDetrendMode mode,
        out double slope,
        out double intercept)
    {
        int n = input.Length;
        slope = 0.0;
        intercept = 0.0;

        if (n == 0)
        {
            return;
        }

        if (mode == SsaDetrendMode.LeastSquaresLinear)
        {
            // Closed-form Ordinary Least Squares (OLS) Linear Regression: Trend(i) = intercept + slope * i
            // Uses analytical sums to minimize division errors:
            // sum_i = N(N - 1) / 2, mean_i = (N - 1) / 2, D/N = N(N^2 - 1) / 12
            double sumY = 0.0;
            double sumTiYi = 0.0;
            for (int i = 0; i < n; i++)
            {
                double val = input[i];
                sumY += val;
                sumTiYi += (double)i * val;
            }

            double meanI = (n - 1) * 0.5;
            double olsDenom = (double)n * (n * (double)n - 1.0) / 12.0;
            if (olsDenom > JacobiConvergenceTolerance)
            {
                slope = (sumTiYi - meanI * sumY) / olsDenom;
                intercept = (sumY / n) - slope * meanI;
            }
            else
            {
                slope = 0.0;
                intercept = sumY / n;
            }

            for (int i = 0; i < n; i++)
            {
                destination[i] = input[i] - (intercept + slope * i);
            }
        }
        else if (mode == SsaDetrendMode.EndpointLinear)
        {
            // Endpoint Matching Detrending: Trend(i) = y[0] + ((y[n-1] - y[0]) / (n - 1)) * i
            slope = (n > 1) ? (input[n - 1] - input[0]) / (n - 1) : 0.0;
            intercept = input[0];
            for (int i = 0; i < n; i++)
            {
                destination[i] = input[i] - (intercept + slope * i);
            }
        }
        else // None / Mean Centering
        {
            double mean = 0.0;
            for (int i = 0; i < n; i++) mean += input[i];
            mean /= n;
            intercept = mean;
            slope = 0.0;
            for (int i = 0; i < n; i++)
            {
                destination[i] = input[i] - mean;
            }
        }

        // Degenerate Series Protection: Zero-out residual noise for flat series
        double minProcessed = destination[0];
        double maxProcessed = destination[0];
        for (int i = 1; i < n; i++)
        {
            if (destination[i] < minProcessed) minProcessed = destination[i];
            if (destination[i] > maxProcessed) maxProcessed = destination[i];
        }
        if (maxProcessed - minProcessed <= DegenerateSeriesEpsilon)
        {
            destination.Clear();
        }
    }

    /// <summary>
    /// Computes the lag-covariance matrix S = X * X^T (L x L) for the given detrended series.
    /// Trajectory matrix X[i, j] = processed[i + j] for 0 &lt;= i &lt; l, 0 &lt;= j &lt; k.
    /// Explicitly enforces symmetry: S[i, j] == S[j, i].
    /// </summary>
    public static void BuildLagCovarianceMatrix(ReadOnlySpan<double> processed, int l, int k, double[,] sMatrix)
    {
        for (int i = 0; i < l; i++)
        {
            for (int j = i; j < l; j++)
            {
                double sum = 0.0;
                for (int col = 0; col < k; col++)
                {
                    sum += processed[i + col] * processed[j + col];
                }
                sMatrix[i, j] = sum;
                sMatrix[j, i] = sum;
            }
        }

        // Explicit symmetrization pass to guard against minor floating-point divergence
        for (int i = 0; i < l; i++)
        {
            for (int j = i + 1; j < l; j++)
            {
                double avg = 0.5 * (sMatrix[i, j] + sMatrix[j, i]);
                sMatrix[i, j] = avg;
                sMatrix[j, i] = avg;
            }
        }
    }

    /// <summary>
    /// Solves the real symmetric eigensystem A * V = V * D using the classic Jacobi eigenvalue method.
    /// </summary>
    public static void ComputeJacobiEigensystem(double[,] a, int n, double[] d, double[,] v)
    {
        double[,] matrix = (double[,])a.Clone();

        // Initialize eigenvector matrix V to identity
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                v[i, j] = (i == j) ? 1.0 : 0.0;
            }
            d[i] = matrix[i, i];
        }

        for (int sweep = 0; sweep < MaxJacobiSweeps; sweep++)
        {
            // Sum off-diagonal elements
            double sumOffDiag = 0.0;
            for (int i = 0; i < n - 1; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    sumOffDiag += Math.Abs(matrix[i, j]);
                }
            }

            if (sumOffDiag < JacobiConvergenceTolerance)
            {
                break;
            }

            double threshold = (sweep < 3) ? 0.2 * sumOffDiag / (n * n) : 0.0;

            for (int p = 0; p < n - 1; p++)
            {
                for (int q = p + 1; q < n; q++)
                {
                    double apq = matrix[p, q];
                    double absApq = Math.Abs(apq);

                    if (sweep > 3 && absApq <= JacobiConvergenceTolerance * Math.Abs(d[p]) && absApq <= JacobiConvergenceTolerance * Math.Abs(d[q]))
                    {
                        matrix[p, q] = 0.0;
                        matrix[q, p] = 0.0;
                        continue;
                    }

                    if (absApq > threshold)
                    {
                        double h = d[q] - d[p];
                        double t;
                        if (absApq <= JacobiConvergenceTolerance * Math.Abs(h))
                        {
                            t = apq / h;
                        }
                        else
                        {
                            double theta = 0.5 * h / apq;
                            t = 1.0 / (Math.Abs(theta) + Math.Sqrt(1.0 + theta * theta));
                            if (theta < 0.0) t = -t;
                        }

                        double c = 1.0 / Math.Sqrt(1.0 + t * t);
                        double s = t * c;
                        double tau = s / (1.0 + c);

                        d[p] -= t * apq;
                        d[q] += t * apq;
                        matrix[p, q] = 0.0;
                        matrix[q, p] = 0.0;

                        for (int j = 0; j < p; j++)
                        {
                            double g = matrix[j, p];
                            double valH = matrix[j, q];
                            matrix[j, p] = g - s * (valH + g * tau);
                            matrix[j, q] = valH + s * (g - valH * tau);
                            matrix[p, j] = matrix[j, p];
                            matrix[q, j] = matrix[q, j];
                        }

                        for (int j = p + 1; j < q; j++)
                        {
                            double g = matrix[p, j];
                            double valH = matrix[j, q];
                            matrix[p, j] = g - s * (valH + g * tau);
                            matrix[j, q] = valH + s * (g - valH * tau);
                            matrix[p, j] = matrix[p, j];
                            matrix[q, j] = matrix[q, j];
                        }

                        for (int j = q + 1; j < n; j++)
                        {
                            double g = matrix[p, j];
                            double valH = matrix[q, j];
                            matrix[p, j] = g - s * (valH + g * tau);
                            matrix[q, j] = valH + s * (g - valH * tau);
                            matrix[p, j] = matrix[p, j];
                            matrix[q, j] = matrix[q, j];
                        }

                        for (int j = 0; j < n; j++)
                        {
                            double g = v[j, p];
                            double valH = v[j, q];
                            v[j, p] = g - s * (valH + g * tau);
                            v[j, q] = valH + s * (g - valH * tau);
                        }
                    }
                }
            }
        }

        // Post-processing 1: Non-negative eigenvalue clamping to prevent NaN in singular value square roots
        for (int i = 0; i < n; i++)
        {
            d[i] = Math.Max(0.0, d[i]);
        }

        // Post-processing 2: Deterministic eigenvector sign normalization (enforce V[0, j] >= 0)
        for (int j = 0; j < n; j++)
        {
            if (v[0, j] < 0.0)
            {
                for (int i = 0; i < n; i++)
                {
                    v[i, j] = -v[i, j];
                }
            }
        }
    }

    /// <summary>
    /// Performs complete SSA matrix decomposition on an input series, returning structured eigensystem,
    /// singular values, component energies (variance ratios), sorted principal indices, and detrending parameters.
    /// </summary>
    public static SsaDecompositionResult Decompose(
        ReadOnlySpan<double> input,
        int embeddingDimension,
        SsaDetrendMode mode = SsaDetrendMode.LeastSquaresLinear)
    {
        int n = input.Length;
        if (n < MinSampleCount)
        {
            return new SsaDecompositionResult(
                Array.Empty<double>(),
                Array.Empty<double>(),
                new double[0, 0],
                Array.Empty<double>(),
                Array.Empty<int>(),
                0, 0.0, 0.0, true);
        }

        Span<double> processed = stackalloc double[n];
        Detrend(input, processed, mode, out double slope, out double intercept);

        int l = Math.Clamp(embeddingDimension, 2, Math.Max(2, n / 2));
        int k = n - l + 1;
        if (k < 2)
        {
            return new SsaDecompositionResult(
                Array.Empty<double>(),
                Array.Empty<double>(),
                new double[0, 0],
                Array.Empty<double>(),
                Array.Empty<int>(),
                0, slope, intercept, true);
        }

        // Check if series is completely flat / degenerate
        bool isDegenerate = true;
        for (int i = 0; i < n; i++)
        {
            if (Math.Abs(processed[i]) > DegenerateSeriesEpsilon)
            {
                isDegenerate = false;
                break;
            }
        }

        double[,] sMatrix = new double[l, l];
        BuildLagCovarianceMatrix(processed, l, k, sMatrix);

        double[] eigenvalues = new double[l];
        double[,] eigenvectors = new double[l, l];
        ComputeJacobiEigensystem(sMatrix, l, eigenvalues, eigenvectors);

        int[] sortedIndices = Enumerable.Range(0, l)
            .OrderByDescending(idx => eigenvalues[idx])
            .ThenBy(idx => idx)
            .ToArray();

        double sumEigenvalues = 0.0;
        for (int i = 0; i < l; i++)
        {
            sumEigenvalues += eigenvalues[i];
        }

        double[] singularValues = new double[l];
        double[] componentEnergies = new double[l];
        int effectiveRank = 0;

        for (int i = 0; i < l; i++)
        {
            int idx = sortedIndices[i];
            double ev = eigenvalues[idx];
            singularValues[i] = Math.Sqrt(Math.Max(0.0, ev));
            componentEnergies[i] = (sumEigenvalues > JacobiConvergenceTolerance) ? (ev / sumEigenvalues) : 0.0;
            if (ev > JacobiConvergenceTolerance)
            {
                effectiveRank++;
            }
        }

        return new SsaDecompositionResult(
            eigenvalues,
            singularValues,
            eigenvectors,
            componentEnergies,
            sortedIndices,
            effectiveRank,
            slope,
            intercept,
            isDegenerate);
    }

    /// <summary>
    /// Reconstructs the time-series signal for a selected subset of principal components via diagonal averaging (Hankelization).
    /// </summary>
    public static void ReconstructGroup(
        ReadOnlySpan<double> processed,
        int l,
        int k,
        ReadOnlySpan<int> componentIndices,
        double[,] eigenvectors,
        Span<double> destination)
    {
        int n = processed.Length;
        destination.Clear();

        double[] diagonalWeights = new double[n];
        int numSelected = componentIndices.Length;

        for (int m = 0; m < numSelected; m++)
        {
            int eigIdx = componentIndices[m];
            // Compute factor vector V = X^T * U
            double[] factorV = new double[k];
            for (int col = 0; col < k; col++)
            {
                double dot = 0.0;
                for (int row = 0; row < l; row++)
                {
                    dot += processed[row + col] * eigenvectors[row, eigIdx];
                }
                factorV[col] = dot;
            }

            // Elementary reconstructed matrix X_m = U * V^T
            // Accumulate anti-diagonals: t = row + col
            for (int row = 0; row < l; row++)
            {
                double uVal = eigenvectors[row, eigIdx];
                for (int col = 0; col < k; col++)
                {
                    destination[row + col] += uVal * factorV[col];
                }
            }
        }

        for (int row = 0; row < l; row++)
        {
            for (int col = 0; col < k; col++)
            {
                diagonalWeights[row + col] += 1.0;
            }
        }

        for (int t = 0; t < n; t++)
        {
            if (diagonalWeights[t] > 0.0)
            {
                destination[t] /= diagonalWeights[t];
            }
        }
    }

    /// <summary>
    /// Computes the causal endpoint reconstructed value in O(r) dot product without full trajectory reconstruction.
    /// At causal endpoint k = w - 1, the anti-diagonal set {(i, j) | i+j = w-1} has size 1 (only (l-1, k-1)).
    /// </summary>
    public static double ComputeCausalEndpoint(
        ReadOnlySpan<double> input,
        int embeddingDimension,
        int numComponents,
        SsaDetrendMode detrendMode = SsaDetrendMode.LeastSquaresLinear)
    {
        int n = input.Length;
        if (n < MinSampleCount)
        {
            return double.NaN;
        }

        int l = Math.Clamp(embeddingDimension, 2, Math.Max(2, n / 2));
        var decomp = Decompose(input, l, detrendMode);
        return ComputeCausalEndpoint(input, decomp, l, numComponents, detrendMode);
    }

    /// <summary>
    /// Computes the causal endpoint reconstructed value using a pre-computed decomposition result,
    /// eliminating redundant Jacobi eigensystem calculations.
    /// </summary>
    public static double ComputeCausalEndpoint(
        ReadOnlySpan<double> input,
        SsaDecompositionResult decomp,
        int l,
        int numComponents,
        SsaDetrendMode detrendMode = SsaDetrendMode.LeastSquaresLinear)
    {
        int n = input.Length;
        if (n < MinSampleCount || decomp == null || decomp.SortedIndices.Length < l)
        {
            return double.NaN;
        }

        int k = n - l + 1;
        if (k < 2)
        {
            return double.NaN;
        }

        Span<double> processed = stackalloc double[n];
        Detrend(input, processed, detrendMode, out double slope, out double intercept);

        int r = Math.Clamp(numComponents, 1, Math.Min(l - 1, k));

        // At endpoint t = n - 1 (row = l - 1, col = k - 1):
        // reconstructed[n - 1] = sum_{m=0}^{r-1} U[l-1, m] * V_m[k-1]
        // where V_m[k-1] = sum_{row=0}^{l-1} processed[row + k - 1] * U[row, m]
        double endpointDetrended = 0.0;
        for (int m = 0; m < r; m++)
        {
            int eigIdx = decomp.SortedIndices[m];
            double uLast = decomp.Eigenvectors[l - 1, eigIdx];

            double vLast = 0.0;
            for (int row = 0; row < l; row++)
            {
                vLast += processed[row + k - 1] * decomp.Eigenvectors[row, eigIdx];
            }

            endpointDetrended += uLast * vLast;
        }

        return endpointDetrended + (intercept + slope * (n - 1));
    }
}
