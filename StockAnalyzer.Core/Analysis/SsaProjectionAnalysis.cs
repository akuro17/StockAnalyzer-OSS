using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Analysis;

/// <summary>
/// Specifies the trend removal (detrending) algorithm applied prior to SSA embedding.
/// </summary>
public enum SsaDetrendMode
{
    /// <summary>
    /// Ordinary Least Squares (OLS) single linear regression: y_t = x_t - (alpha + beta * t).
    /// Highly robust against endpoint spikes and wicks.
    /// </summary>
    LeastSquaresLinear = 0,

    /// <summary>
    /// Endpoint matching linear detrend: y_t = x_t - (x_0 + (x_{N-1} - x_0)/(N-1) * t).
    /// </summary>
    EndpointLinear = 1,

    /// <summary>
    /// Mean subtraction centering (no slope removal): y_t = x_t - mean(x).
    /// </summary>
    None = 2
}

/// <summary>
/// Specifies the future extrapolation algorithm used in SSA projection.
/// </summary>
public enum SsaForecastMode
{
    /// <summary>
    /// Recurrent SSA Forecasting (LRR): Extrapolates via linear recurrence scalar relations.
    /// </summary>
    Recurrent = 0,

    /// <summary>
    /// Vector SSA Forecasting: Extrapolates via orthogonal projection in the reconstructed signal subspace.
    /// </summary>
    Vector = 1
}

/// <summary>
/// Represents a single reconstructed principal component extracted from SSA decomposition.
/// </summary>
public sealed record SsaComponentInfo(
    int ComponentIndex,
    double SingularValue,
    double VarianceRatio);

/// <summary>
/// Encapsulates the results of an SSA future trajectory projection calculation.
/// </summary>
public sealed class SsaProjectionResult
{
    public static readonly SsaProjectionResult Empty = new(
        Array.Empty<Point>(),
        Array.Empty<Point>(),
        Array.Empty<Point>(),
        Array.Empty<Point>(),
        Array.Empty<SsaComponentInfo>(),
        0.0, 0.0, 0.0, 0, 0, 0, 0.0, 0.0, true);

    public IReadOnlyList<Point> ProjectedPoints { get; }
    public IReadOnlyList<Point> UpperBandPoints { get; }
    public IReadOnlyList<Point> LowerBandPoints { get; }
    public IReadOnlyList<Point> ReconstructedPoints { get; }
    public IReadOnlyList<SsaComponentInfo> Components { get; }
    public double ResidualStdDev { get; }
    public double Slope { get; }
    public double Intercept { get; }
    public int SampleCount { get; }
    public int EmbeddingDimension { get; }
    public int NumComponents { get; }
    public double CumulativeVarianceRatio { get; }
    public double NuSquared { get; }
    public bool IsStable { get; }

    public SsaProjectionResult(
        IReadOnlyList<Point> projectedPoints,
        IReadOnlyList<Point> upperBandPoints,
        IReadOnlyList<Point> lowerBandPoints,
        IReadOnlyList<Point> reconstructedPoints,
        IReadOnlyList<SsaComponentInfo> components,
        double residualStdDev,
        double slope,
        double intercept,
        int sampleCount,
        int embeddingDimension,
        int numComponents,
        double cumulativeVarianceRatio,
        double nuSquared,
        bool isStable)
    {
        ProjectedPoints = projectedPoints;
        UpperBandPoints = upperBandPoints;
        LowerBandPoints = lowerBandPoints;
        ReconstructedPoints = reconstructedPoints;
        Components = components;
        ResidualStdDev = residualStdDev;
        Slope = slope;
        Intercept = intercept;
        SampleCount = sampleCount;
        EmbeddingDimension = embeddingDimension;
        NumComponents = numComponents;
        CumulativeVarianceRatio = cumulativeVarianceRatio;
        NuSquared = nuSquared;
        IsStable = isStable;
    }
}

/// <summary>
/// Pure C# mathematical engine for Singular Spectrum Analysis (SSA) future trajectory projection.
/// Decomposes in-sample price series into principal components via trajectory matrix embedding and SVD / Jacobi eigendecomposition,
/// then performs recurrent linear recurrence extrapolation (LRR) and confidence diffusion cone estimation forward into future coordinates.
/// </summary>
public static class SsaProjectionAnalysis
{
    public const int MinSampleCount = 4;
    public const int MaxJacobiSweeps = 50;
    public const double JacobiConvergenceTolerance = 1e-12;
    public const double LrrDenominatorFloor = 1e-4;
    public const double LrrRidgeRegularization = 1e-6;
    public const double NuSquaredStabilityThreshold = 0.95;
    public const double DegenerateSeriesEpsilon = 1e-10;

    /// <summary>
    /// Computes the Singular Spectrum Analysis decomposition and projects the trajectory forward into future steps.
    /// </summary>
    public static SsaProjectionResult CalculateProjection(
        IReadOnlyList<double> samples,
        IReadOnlyList<DateTime> timestamps,
        int futureSteps = 20,
        TimeSpan timeframeSpan = default,
        int embeddingDimension = 10,
        int numComponents = 2,
        SsaDetrendMode detrendMode = SsaDetrendMode.LeastSquaresLinear,
        bool showConfidenceBand = true,
        decimal confidenceMultiplier = 2.0m,
        SsaForecastMode forecastMode = SsaForecastMode.Recurrent)
    {
        if (samples == null || timestamps == null || samples.Count < MinSampleCount || samples.Count != timestamps.Count)
        {
            return SsaProjectionResult.Empty;
        }

        for (int i = 0; i < samples.Count; i++)
        {
            if (!double.IsFinite(samples[i]))
            {
                return SsaProjectionResult.Empty;
            }
        }

        int n = samples.Count;
        double[] sampleArray = (samples is double[] arr) ? arr : samples.ToArray();

        // 1. Determine Effective Embedding Dimension (L) and Components Count (r)
        int l = Math.Clamp(embeddingDimension, 2, Math.Max(2, n / 2));
        int k = n - l + 1;
        if (k < 2)
        {
            return SsaProjectionResult.Empty;
        }

        // Enforce r <= L - 1 so LRR projection is always geometrically well-defined
        int r = Math.Clamp(numComponents, 1, Math.Min(l - 1, k));

        // 2. Perform SSA Matrix Decomposition via SsaDecompositionEngine
        var decomp = SsaDecompositionEngine.Decompose(sampleArray, l, detrendMode);
        if (decomp.SortedIndices.Length < l)
        {
            return SsaProjectionResult.Empty;
        }

        double slope = decomp.Slope;
        double intercept = decomp.Intercept;

        Span<double> processed = stackalloc double[n];
        SsaDecompositionEngine.Detrend(sampleArray, processed, detrendMode, out _, out _);

        var componentList = new List<SsaComponentInfo>(r);
        double cumulativeVariance = 0.0;
        for (int m = 0; m < r; m++)
        {
            double singularVal = decomp.SingularValues[m];
            double varRatio = decomp.ComponentEnergies[m];
            cumulativeVariance += varRatio;
            componentList.Add(new SsaComponentInfo(m + 1, singularVal, varRatio));
        }

        // 3. In-Sample Signal Reconstruction via Diagonal Averaging (Hankelization)
        double[] reconstructed = new double[n];
        SsaDecompositionEngine.ReconstructGroup(processed, l, k, decomp.SortedIndices.AsSpan(0, r), decomp.Eigenvectors, reconstructed);

        // Build in-sample reconstructed points list: P~_t = reconstructed[t] + Trend(t)
        var reconstructedPoints = new List<Point>(n);
        for (int t = 0; t < n; t++)
        {
            double reconPrice = reconstructed[t] + (intercept + slope * t);
            reconstructedPoints.Add(new Point((double)timestamps[t].Ticks, reconPrice));
        }

        // Compute in-sample residuals and residual standard deviation
        double sumSqErr = 0.0;
        for (int t = 0; t < n; t++)
        {
            double diff = processed[t] - reconstructed[t];
            sumSqErr += diff * diff;
        }
        double residualStdDev = Math.Sqrt(sumSqErr / n);
        double minStdDev = Math.Max(0.001, Math.Abs(samples[^1]) * 0.001);
        if (residualStdDev < minStdDev)
        {
            residualStdDev = minStdDev;
        }

        // 4. Linear Recurrence Relation / Subspace Orthogonal Operator Setup
        // Let U_m = (pi_m, nu_m)^T where nu_m is the last component (row l-1) of eigenvector m.
        // nu2 = sum_{m=0}^{r-1} (nu_m)^2
        // R = 1 / (1 - nu2) * sum_{m=0}^{r-1} nu_m * pi_m
        double nu2 = 0.0;
        for (int m = 0; m < r; m++)
        {
            int eigIdx = decomp.SortedIndices[m];
            double nuM = decomp.Eigenvectors[l - 1, eigIdx];
            nu2 += nuM * nuM;
        }

        // Clamp nu2 to strictly prevent exceeding 0.99999 due to floating-point rounding
        nu2 = Math.Min(nu2, 0.99999);
        bool isStable = nu2 < NuSquaredStabilityThreshold;

        // Ridge-type regularization on the denominator to safeguard against singularity
        double lrrDenom = Math.Max(1.0 - nu2, LrrDenominatorFloor) + LrrRidgeRegularization;
        double[] lrrCoeffs = new double[l - 1];

        for (int row = 0; row < l - 1; row++)
        {
            double sum = 0.0;
            for (int m = 0; m < r; m++)
            {
                int eigIdx = decomp.SortedIndices[m];
                double nuM = decomp.Eigenvectors[l - 1, eigIdx];
                double piM = decomp.Eigenvectors[row, eigIdx];
                sum += nuM * piM;
            }
            lrrCoeffs[row] = sum / lrrDenom;
        }

        // 5. Future Extrapolation (Recurrent or Vector SSA)
        int steps = Math.Clamp(futureSteps, 1, 100);
        var projectedPoints = new List<Point>(steps + 1);
        var upperBandPoints = new List<Point>(steps + 1);
        var lowerBandPoints = new List<Point>(steps + 1);

        // Initial point aligns with the last in-sample candle
        var lastTime = timestamps[^1];
        var lastPrice = samples[^1];
        var initialPt = new Point((double)lastTime.Ticks, lastPrice);
        projectedPoints.Add(initialPt);
        upperBandPoints.Add(initialPt);
        lowerBandPoints.Add(initialPt);

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

        // Determine allowable price swing bounds for runaway detection
        double minSample = samples[0];
        double maxSample = samples[0];
        for (int i = 1; i < n; i++)
        {
            if (samples[i] < minSample) minSample = samples[i];
            if (samples[i] > maxSample) maxSample = samples[i];
        }
        double sampleRange = Math.Max(1.0, maxSample - minSample);
        double maxAllowableDelta = Math.Max(sampleRange * 5.0, Math.Abs(lastPrice) * 1.5);

        double multiplier = (double)Math.Max(0m, confidenceMultiplier);

        if (forecastMode == SsaForecastMode.Vector)
        {
            // Vector SSA: State vector evolution via signal subspace orthogonal projection
            Span<double> stateZ = stackalloc double[l];
            for (int i = 0; i < l; i++)
            {
                int srcIdx = n - l + i;
                stateZ[i] = (srcIdx >= 0 && srcIdx < n) ? reconstructed[srcIdx] : 0.0;
            }
            Span<double> projZ = stackalloc double[l];

            for (int step = 1; step <= steps; step++)
            {
                var targetTime = lastTime + (timeframeSpan * step);

                // Subspace projection: P * Z = sum_{m=0}^{r-1} (Z . U_m) * U_m
                projZ.Clear();
                for (int m = 0; m < r; m++)
                {
                    int eigIdx = decomp.SortedIndices[m];
                    double dot = 0.0;
                    for (int i = 0; i < l; i++)
                    {
                        dot += stateZ[i] * decomp.Eigenvectors[i, eigIdx];
                    }
                    for (int i = 0; i < l; i++)
                    {
                        projZ[i] += dot * decomp.Eigenvectors[i, eigIdx];
                    }
                }

                // Extrapolate next element y_{next} from projected vector:
                // y_{next} = 1 / (1 - nu2) * sum_{m=0}^{r-1} nu_m * (sum_{j=0}^{l-2} u_m[j] * projZ[j+1])
                double sumV = 0.0;
                for (int m = 0; m < r; m++)
                {
                    int eigIdx = decomp.SortedIndices[m];
                    double nuM = decomp.Eigenvectors[l - 1, eigIdx];
                    double inner = 0.0;
                    for (int j = 0; j < l - 1; j++)
                    {
                        inner += decomp.Eigenvectors[j, eigIdx] * projZ[j + 1];
                    }
                    sumV += nuM * inner;
                }
                double nextDetrended = sumV / lrrDenom;

                // Guard against extreme explosive instability or NaN
                if (double.IsNaN(nextDetrended) || double.IsInfinity(nextDetrended) || Math.Abs(nextDetrended) > maxAllowableDelta)
                {
                    nextDetrended = projZ[l - 1] * 0.90;
                }

                // Shift state vector for next iteration: [projZ[1], ..., projZ[L-1], nextDetrended]
                for (int i = 0; i < l - 1; i++)
                {
                    stateZ[i] = projZ[i + 1];
                }
                stateZ[l - 1] = nextDetrended;

                // Reconstruct total price by adding trend back
                double projectedPrice = nextDetrended + (intercept + slope * (n - 1 + step));

                // Calculate uncertainty expanding with window-scaled time diffusion: sqrt(1 + step / L)
                double diffFactor = Math.Sqrt(1.0 + (double)step / Math.Max(l, 1));
                double uncertainty = residualStdDev * diffFactor * multiplier;
                double upperPrice = projectedPrice + uncertainty;
                double lowerPrice = projectedPrice - uncertainty;

                projectedPoints.Add(new Point((double)targetTime.Ticks, projectedPrice));
                upperBandPoints.Add(new Point((double)targetTime.Ticks, upperPrice));
                lowerBandPoints.Add(new Point((double)targetTime.Ticks, lowerPrice));
            }
        }
        else
        {
            // Recurrent SSA: Extrapolates via linear recurrence relations on rolling buffer
            double[] buffer = new double[l - 1];
            for (int i = 0; i < l - 1; i++)
            {
                int srcIdx = n - (l - 1) + i;
                buffer[i] = (srcIdx >= 0 && srcIdx < n) ? reconstructed[srcIdx] : 0.0;
            }

            for (int step = 1; step <= steps; step++)
            {
                var targetTime = lastTime + (timeframeSpan * step);

                // Compute next extrapolated detrended value via LRR:
                // nextVal = sum_{row=0}^{l-2} lrrCoeffs[row] * buffer[row]
                double nextDetrended = 0.0;
                for (int row = 0; row < l - 1; row++)
                {
                    nextDetrended += lrrCoeffs[row] * buffer[row];
                }

                // Guard against extreme explosive instability or NaN
                if (double.IsNaN(nextDetrended) || double.IsInfinity(nextDetrended) || Math.Abs(nextDetrended) > maxAllowableDelta)
                {
                    // Soft damped fallback towards zero detrended deviation
                    nextDetrended = buffer[l - 2] * 0.90;
                }

                // Shift rolling buffer
                for (int row = 0; row < l - 2; row++)
                {
                    buffer[row] = buffer[row + 1];
                }
                buffer[l - 2] = nextDetrended;

                // Reconstruct total price by adding trend back
                double projectedPrice = nextDetrended + (intercept + slope * (n - 1 + step));

                // Calculate uncertainty expanding with window-scaled time diffusion: sqrt(1 + step / L)
                double diffFactor = Math.Sqrt(1.0 + (double)step / Math.Max(l, 1));
                double uncertainty = residualStdDev * diffFactor * multiplier;
                double upperPrice = projectedPrice + uncertainty;
                double lowerPrice = projectedPrice - uncertainty;

                projectedPoints.Add(new Point((double)targetTime.Ticks, projectedPrice));
                upperBandPoints.Add(new Point((double)targetTime.Ticks, upperPrice));
                lowerBandPoints.Add(new Point((double)targetTime.Ticks, lowerPrice));
            }
        }

        return new SsaProjectionResult(
            projectedPoints,
            upperBandPoints,
            lowerBandPoints,
            reconstructedPoints,
            componentList,
            residualStdDev,
            slope,
            intercept,
            n,
            l,
            r,
            cumulativeVariance,
            nu2,
            isStable);
    }

    /// <summary>
    /// Legacy overload for backward compatibility with existing bool applyDetrend.
    /// </summary>
    public static SsaProjectionResult CalculateProjection(
        IReadOnlyList<double> samples,
        IReadOnlyList<DateTime> timestamps,
        int futureSteps,
        TimeSpan timeframeSpan,
        int embeddingDimension,
        int numComponents,
        bool applyDetrend,
        bool showConfidenceBand,
        decimal confidenceMultiplier)
    {
        return CalculateProjection(
            samples,
            timestamps,
            futureSteps,
            timeframeSpan,
            embeddingDimension,
            numComponents,
            applyDetrend ? SsaDetrendMode.LeastSquaresLinear : SsaDetrendMode.None,
            showConfidenceBand,
            confidenceMultiplier,
            SsaForecastMode.Recurrent);
    }
}
