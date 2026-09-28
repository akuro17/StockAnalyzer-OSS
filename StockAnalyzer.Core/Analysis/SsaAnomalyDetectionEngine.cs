using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Analysis;

/// <summary>
/// State of the SSA structural anomaly hysteresis machine.
/// </summary>
public enum SsaAnomalyState
{
    Normal = 0,
    Bullish = 1,
    Bearish = 2
}

/// <summary>
/// Direction of a detected structural anomaly interval.
/// </summary>
public enum SsaAnomalyDirection
{
    Bullish = 0,
    Bearish = 1
}

/// <summary>
/// Represents a contiguous temporal interval where prices exhibited statistically significant structural anomalies.
/// </summary>
public sealed class SsaAnomalyInterval
{
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }
    public int StartIndex { get; init; }
    public int EndIndex { get; init; }
    public SsaAnomalyDirection Direction { get; init; }
    public double PeakZ { get; init; }
    public double RawPeakZScore { get; init; }
    public string BadgeText { get; init; } = string.Empty;
    public int PeakIndex { get; init; }
    public DateTime PeakTime { get; init; }
    public double MaxPriceDeviation { get; init; }
    public double PercentDeviation { get; init; }
    public int DurationBars => EndIndex - StartIndex + 1;
}

/// <summary>
/// Encapsulates the complete results of SSA structural anomaly detection.
/// </summary>
public sealed class SsaAnomalyResult
{
    public static readonly SsaAnomalyResult Empty = new(
        Array.Empty<SsaAnomalyInterval>(),
        Array.Empty<Point>(),
        Array.Empty<Point>(),
        Array.Empty<Point>(),
        Array.Empty<double>(),
        0.0, 2.0, 1.0, 100.0, true);

    public IReadOnlyList<SsaAnomalyInterval> Intervals { get; }
    public IReadOnlyList<Point> ReconstructedPoints { get; }
    public IReadOnlyList<Point> UpperBandPoints { get; }
    public IReadOnlyList<Point> LowerBandPoints { get; }
    public IReadOnlyList<double> ZScores { get; }
    public double ResidualStdDev { get; }
    public double EnterThreshold { get; }
    public double ExitThreshold { get; }
    public double Separability { get; }
    public bool IsEmpty { get; }

    public SsaAnomalyResult(
        IReadOnlyList<SsaAnomalyInterval> intervals,
        IReadOnlyList<Point> reconstructedPoints,
        IReadOnlyList<Point> upperBandPoints,
        IReadOnlyList<Point> lowerBandPoints,
        IReadOnlyList<double> zScores,
        double residualStdDev,
        double enterThreshold,
        double exitThreshold,
        double separability,
        bool isEmpty = false)
    {
        Intervals = intervals;
        ReconstructedPoints = reconstructedPoints;
        UpperBandPoints = upperBandPoints;
        LowerBandPoints = lowerBandPoints;
        ZScores = zScores;
        ResidualStdDev = residualStdDev;
        EnterThreshold = enterThreshold;
        ExitThreshold = exitThreshold;
        Separability = separability;
        IsEmpty = isEmpty;
    }
}

/// <summary>
/// Pure C# Singular Spectrum Analysis (SSA) Structural Anomaly Detection Engine.
/// Reconstructs normal market dynamics via SSA subspace decomposition, evaluates relative-scale
/// invariant structural residual Z-scores, and extracts anomalous regime intervals using a
/// hysteresis state machine with direct-reversal handling and zero allocation.
/// </summary>
public static class SsaAnomalyDetectionEngine
{
    public const int MinSampleCount = 4;
    public const double RelativeEpsilonFactor = 1e-6;
    public const double AbsoluteFloorEpsilon = 1e-12;
    public const double MaxZClamp = 100.0;

    /// <summary>
    /// Executes batch non-causal SSA structural anomaly analysis across the provided time series.
    /// </summary>
    public static SsaAnomalyResult CalculateAnomaly(
        IReadOnlyList<double> samples,
        IReadOnlyList<DateTime> timestamps,
        int embeddingDimension = 15,
        int numComponents = 2,
        bool autoRank = true,
        SsaDetrendMode detrendMode = SsaDetrendMode.LeastSquaresLinear,
        double enterThreshold = 2.0,
        double exitThreshold = 1.0,
        int coolDownPeriod = 3,
        int minDuration = 2)
    {
        if (samples == null || timestamps == null || samples.Count < MinSampleCount || samples.Count != timestamps.Count)
        {
            return SsaAnomalyResult.Empty;
        }

        int n = samples.Count;
        int l = Math.Clamp(embeddingDimension, 2, Math.Max(2, n / 2));
        int k = n - l + 1;
        if (k < 2 || n < l + 1)
        {
            return SsaAnomalyResult.Empty;
        }

        // Sanitize NaNs and Infinities with nearest valid finite value
        double[] cleanSamples = new double[n];
        double lastValid = 0.0;
        bool hasAnyFinite = false;
        for (int i = 0; i < n; i++)
        {
            if (double.IsFinite(samples[i]))
            {
                lastValid = samples[i];
                hasAnyFinite = true;
                break;
            }
        }

        if (!hasAnyFinite)
        {
            return SsaAnomalyResult.Empty;
        }

        for (int i = 0; i < n; i++)
        {
            if (double.IsFinite(samples[i]))
            {
                lastValid = samples[i];
                cleanSamples[i] = lastValid;
            }
            else
            {
                cleanSamples[i] = lastValid;
            }
        }

        // 1. Perform SSA Matrix Decomposition on sanitized series
        var decomp = SsaDecompositionEngine.Decompose(cleanSamples, l, detrendMode);
        if (decomp.SortedIndices.Length < l)
        {
            return SsaAnomalyResult.Empty;
        }

        // 2. Rank selection for normal structural subspace
        int r;
        if (autoRank)
        {
            r = SsaRankSelector.EstimateSignalRank(
                decomp.Eigenvalues,
                SsaRankSelectionMethod.CumulativeEnergy,
                targetEnergy: 0.90,
                maxRank: l - 1);
        }
        else
        {
            r = Math.Clamp(numComponents, 1, Math.Min(l - 1, k));
        }

        // 3. Reconstruct normal structural components in detrended domain
        Span<double> processed = stackalloc double[n];
        SsaDecompositionEngine.Detrend(cleanSamples, processed, detrendMode, out double slope, out double intercept);

        int[] selectedComponentIndices = decomp.SortedIndices.Take(r).ToArray();
        double[] reconstructedDetrended = new double[n];
        SsaDecompositionEngine.ReconstructGroup(processed, l, k, selectedComponentIndices, decomp.Eigenvectors, reconstructedDetrended);

        // 4. Restore original price scale: \tilde{x}_t = \tilde{y}_t + (\alpha + \beta * t)
        double[] reconstructed = new double[n];
        double sumPriceAbs = 0.0;
        for (int t = 0; t < n; t++)
        {
            double trend = intercept + slope * t;
            reconstructed[t] = reconstructedDetrended[t] + trend;
            sumPriceAbs += Math.Abs(cleanSamples[t]);
        }

        // 5. Structural residual e_t and scale-invariant Z-score
        double meanPrice = sumPriceAbs / n;
        double relativeEpsilon = Math.Max(AbsoluteFloorEpsilon, meanPrice * RelativeEpsilonFactor);

        double sumSqRes = 0.0;
        for (int t = 0; t < n; t++)
        {
            double res = cleanSamples[t] - reconstructed[t];
            sumSqRes += res * res;
        }
        double residualStdDev = Math.Sqrt(sumSqRes / n);

        double[] rawZScores = new double[n];
        double[] zScores = new double[n];
        if (residualStdDev <= relativeEpsilon)
        {
            Array.Clear(zScores, 0, n);
            Array.Clear(rawZScores, 0, n);
        }
        else
        {
            for (int t = 0; t < n; t++)
            {
                double rawZ = (cleanSamples[t] - reconstructed[t]) / residualStdDev;
                rawZScores[t] = rawZ;
                zScores[t] = Math.Clamp(rawZ, -MaxZClamp, MaxZClamp);
            }
        }

        // 6. Separability Score
        double separabilityScore = 100.0;
        if (r > 1)
        {
            double[] allRecon = new double[r * n];
            for (int m = 0; m < r; m++)
            {
                int eigIdx = decomp.SortedIndices[m];
                Span<int> singleIdx = stackalloc int[1] { eigIdx };
                SsaDecompositionEngine.ReconstructGroup(processed, l, k, singleIdx, decomp.Eigenvectors, allRecon.AsSpan(m * n, n));
            }
            Span<double> wCorr = stackalloc double[r * r];
            SsaDiagnostics.ComputeWCorrelationMatrix(allRecon, r, n, l, wCorr);
            separabilityScore = SsaDiagnostics.ComputeSeparabilityScore(wCorr, r);
        }

        // 7. Hysteresis State Machine with Direct Reversal
        var intervals = new List<SsaAnomalyInterval>();
        SsaAnomalyState state = SsaAnomalyState.Normal;
        int startIndex = 0;
        double peakZ = 0.0;
        double rawPeakZ = 0.0;
        int peakIndex = 0;
        int coolDownCount = 0;

        for (int t = 0; t < n; t++)
        {
            double z = zScores[t];

            switch (state)
            {
                case SsaAnomalyState.Normal:
                    if (z >= enterThreshold)
                    {
                        state = SsaAnomalyState.Bullish;
                        startIndex = t;
                        peakZ = z;
                        rawPeakZ = rawZScores[t];
                        peakIndex = t;
                        coolDownCount = 0;
                    }
                    else if (z <= -enterThreshold)
                    {
                        state = SsaAnomalyState.Bearish;
                        startIndex = t;
                        peakZ = z;
                        rawPeakZ = rawZScores[t];
                        peakIndex = t;
                        coolDownCount = 0;
                    }
                    break;

                case SsaAnomalyState.Bullish:
                    // Peak update (highest Z)
                    if (rawZScores[t] > rawPeakZ)
                    {
                        rawPeakZ = rawZScores[t];
                        peakZ = z;
                        peakIndex = t;
                    }

                    // Direct Reversal check (Bullish -> Bearish)
                    if (z <= -enterThreshold)
                    {
                        int endIndex = t - 1;
                        if (endIndex - startIndex + 1 >= minDuration)
                        {
                            intervals.Add(CreateInterval(startIndex, endIndex, SsaAnomalyDirection.Bullish, peakZ, rawPeakZ, peakIndex, cleanSamples, reconstructed, timestamps));
                        }

                        // Immediately start Bearish interval
                        state = SsaAnomalyState.Bearish;
                        startIndex = t;
                        peakZ = z;
                        rawPeakZ = rawZScores[t];
                        peakIndex = t;
                        coolDownCount = 0;
                    }
                    else
                    {
                        if (z < exitThreshold)
                        {
                            coolDownCount++;
                            if (coolDownCount >= coolDownPeriod)
                            {
                                int endIndex = t - coolDownPeriod;
                                if (endIndex - startIndex + 1 >= minDuration)
                                {
                                    intervals.Add(CreateInterval(startIndex, endIndex, SsaAnomalyDirection.Bullish, peakZ, rawPeakZ, peakIndex, cleanSamples, reconstructed, timestamps));
                                }
                                state = SsaAnomalyState.Normal;
                                coolDownCount = 0;
                            }
                        }
                        else
                        {
                            coolDownCount = 0;
                        }
                    }
                    break;

                case SsaAnomalyState.Bearish:
                    // Peak update (lowest Z)
                    if (rawZScores[t] < rawPeakZ)
                    {
                        rawPeakZ = rawZScores[t];
                        peakZ = z;
                        peakIndex = t;
                    }

                    // Direct Reversal check (Bearish -> Bullish)
                    if (z >= enterThreshold)
                    {
                        int endIndex = t - 1;
                        if (endIndex - startIndex + 1 >= minDuration)
                        {
                            intervals.Add(CreateInterval(startIndex, endIndex, SsaAnomalyDirection.Bearish, peakZ, rawPeakZ, peakIndex, cleanSamples, reconstructed, timestamps));
                        }

                        // Immediately start Bullish interval
                        state = SsaAnomalyState.Bullish;
                        startIndex = t;
                        peakZ = z;
                        rawPeakZ = rawZScores[t];
                        peakIndex = t;
                        coolDownCount = 0;
                    }
                    else
                    {
                        if (z > -exitThreshold)
                        {
                            coolDownCount++;
                            if (coolDownCount >= coolDownPeriod)
                            {
                                int endIndex = t - coolDownPeriod;
                                if (endIndex - startIndex + 1 >= minDuration)
                                {
                                    intervals.Add(CreateInterval(startIndex, endIndex, SsaAnomalyDirection.Bearish, peakZ, rawPeakZ, peakIndex, cleanSamples, reconstructed, timestamps));
                                }
                                state = SsaAnomalyState.Normal;
                                coolDownCount = 0;
                            }
                        }
                        else
                        {
                            coolDownCount = 0;
                        }
                    }
                    break;
            }
        }

        // 8. Series termination handling (t = N - 1)
        if (state != SsaAnomalyState.Normal)
        {
            int endIndex = n - 1;
            if (endIndex - startIndex + 1 >= minDuration)
            {
                var dir = state == SsaAnomalyState.Bullish ? SsaAnomalyDirection.Bullish : SsaAnomalyDirection.Bearish;
                intervals.Add(CreateInterval(startIndex, endIndex, dir, peakZ, rawPeakZ, peakIndex, cleanSamples, reconstructed, timestamps));
            }
        }

        // 9. Generate geometric point series
        var reconstructedPoints = new Point[n];
        var upperBandPoints = new Point[n];
        var lowerBandPoints = new Point[n];

        for (int t = 0; t < n; t++)
        {
            double ticks = (double)timestamps[t].Ticks;
            double center = reconstructed[t];
            reconstructedPoints[t] = new Point(ticks, center);
            upperBandPoints[t] = new Point(ticks, center + enterThreshold * residualStdDev);
            lowerBandPoints[t] = new Point(ticks, center - enterThreshold * residualStdDev);
        }

        return new SsaAnomalyResult(
            intervals,
            reconstructedPoints,
            upperBandPoints,
            lowerBandPoints,
            zScores,
            residualStdDev,
            enterThreshold,
            exitThreshold,
            separabilityScore,
            false);
    }

    private static SsaAnomalyInterval CreateInterval(
        int startIndex,
        int endIndex,
        SsaAnomalyDirection direction,
        double peakZ,
        double rawPeakZ,
        int peakIndex,
        double[] samples,
        double[] reconstructed,
        IReadOnlyList<DateTime> timestamps)
    {
        int validPeak = Math.Clamp(peakIndex, startIndex, endIndex);
        double xPeak = samples[validPeak];
        double xTildePeak = reconstructed[validPeak];
        double maxPriceDeviation = xPeak - xTildePeak;
        double denom = Math.Max(Math.Abs(xTildePeak), 1e-8);
        double percentDeviation = (maxPriceDeviation / denom) * 100.0;

        return new SsaAnomalyInterval
        {
            StartIndex = startIndex,
            EndIndex = endIndex,
            StartTime = timestamps[startIndex],
            EndTime = timestamps[endIndex],
            Direction = direction,
            PeakZ = peakZ,
            RawPeakZScore = rawPeakZ,
            BadgeText = peakZ.ToString("+0.0;-0.0", System.Globalization.CultureInfo.InvariantCulture) + "σ",
            PeakIndex = validPeak,
            PeakTime = timestamps[validPeak],
            MaxPriceDeviation = maxPriceDeviation,
            PercentDeviation = percentDeviation
        };
    }
}
