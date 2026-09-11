using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Analysis;

/// <summary>
/// Specifies the operational calculation mode for Singular Spectrum Analysis (SSA) Support and Resistance detection.
/// </summary>
public enum SsaSupportResistanceMode
{
    /// <summary>
    /// Mode 1: Extracts structural pivot horizontal support and resistance levels from in-sample reconstructed extrema.
    /// </summary>
    StructuralPivots = 0,

    /// <summary>
    /// Mode 2: Constructs dynamic volatility envelope bands (+/- M * sigma_res) around the SSA center trend curve.
    /// </summary>
    DynamicEnvelopes = 1,

    /// <summary>
    /// Mode 3: Extrapolates future trajectory and identifies the earliest turning point target high and low price levels.
    /// </summary>
    ProjectedTargets = 2
}

/// <summary>
/// Represents a single support or resistance price level derived from SSA analysis.
/// </summary>
public sealed record SsaSupportResistanceLevel(
    double Price,
    int Hits,
    double StrengthScore,
    int LatestIndex,
    DateTime? TargetTime,
    bool IsResistance,
    string Label = "");

/// <summary>
/// Encapsulates the complete results of an SSA Support and Resistance calculation.
/// </summary>
public sealed class SsaSupportResistanceResult
{
    public static readonly SsaSupportResistanceResult Empty = new(
        isEmpty: true,
        mode: SsaSupportResistanceMode.StructuralPivots,
        resistanceLevels: Array.Empty<SsaSupportResistanceLevel>(),
        supportLevels: Array.Empty<SsaSupportResistanceLevel>(),
        centerBand: Array.Empty<Point>(),
        upperBand: Array.Empty<Point>(),
        lowerBand: Array.Empty<Point>(),
        projectedPath: Array.Empty<Point>(),
        residualStdDev: 0.0,
        activeResistance: null,
        activeSupport: null,
        separabilityScore: 100.0,
        slope: 0.0,
        intercept: 0.0,
        sampleCount: 0,
        embeddingDimension: 0,
        numComponents: 0,
        cumulativeVarianceRatio: 0.0,
        nuSquared: 0.0,
        isStable: true);

    public bool IsEmpty { get; }
    public SsaSupportResistanceMode Mode { get; }
    public IReadOnlyList<SsaSupportResistanceLevel> ResistanceLevels { get; }
    public IReadOnlyList<SsaSupportResistanceLevel> SupportLevels { get; }
    public IReadOnlyList<Point> CenterBand { get; }
    public IReadOnlyList<Point> UpperBand { get; }
    public IReadOnlyList<Point> LowerBand { get; }
    public IReadOnlyList<Point> ProjectedPath { get; }
    public double ResidualStdDev { get; }
    public double? ActiveResistance { get; }
    public double? ActiveSupport { get; }
    public double SeparabilityScore { get; }
    public double Slope { get; }
    public double Intercept { get; }
    public int SampleCount { get; }
    public int EmbeddingDimension { get; }
    public int NumComponents { get; }
    public double CumulativeVarianceRatio { get; }
    public double NuSquared { get; }
    public bool IsStable { get; }

    public SsaSupportResistanceResult(
        bool isEmpty,
        SsaSupportResistanceMode mode,
        IReadOnlyList<SsaSupportResistanceLevel> resistanceLevels,
        IReadOnlyList<SsaSupportResistanceLevel> supportLevels,
        IReadOnlyList<Point> centerBand,
        IReadOnlyList<Point> upperBand,
        IReadOnlyList<Point> lowerBand,
        IReadOnlyList<Point> projectedPath,
        double residualStdDev,
        double? activeResistance,
        double? activeSupport,
        double separabilityScore,
        double slope,
        double intercept,
        int sampleCount,
        int embeddingDimension,
        int numComponents,
        double cumulativeVarianceRatio,
        double nuSquared,
        bool isStable)
    {
        IsEmpty = isEmpty;
        Mode = mode;
        ResistanceLevels = resistanceLevels;
        SupportLevels = supportLevels;
        CenterBand = centerBand;
        UpperBand = upperBand;
        LowerBand = lowerBand;
        ProjectedPath = projectedPath;
        ResidualStdDev = residualStdDev;
        ActiveResistance = activeResistance;
        ActiveSupport = activeSupport;
        SeparabilityScore = separabilityScore;
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
/// Mathematical engine for computing SSA-derived support and resistance levels, dynamic volatility envelopes,
/// and turning point price targets.
/// </summary>
public static class SsaSupportResistanceEngine
{
    public const int MinSampleCount = 4;
    public const double MinClusterToleranceEpsilon = 1e-8;

    /// <summary>
    /// Computes support and resistance levels according to the specified SSA mode.
    /// </summary>
    public static SsaSupportResistanceResult Calculate(
        IReadOnlyList<double> samples,
        IReadOnlyList<DateTime> timestamps,
        SsaSupportResistanceMode mode = SsaSupportResistanceMode.StructuralPivots,
        int embeddingDimension = 15,
        int numComponents = 2,
        bool autoRank = true,
        SsaDetrendMode detrendMode = SsaDetrendMode.LeastSquaresLinear,
        int maxLevelsPerSide = 2,
        decimal clusterTolerance = 0.5m,
        decimal multiplier = 2.0m,
        int futureSteps = 20,
        SsaForecastMode forecastMode = SsaForecastMode.Vector,
        TimeSpan timeframeSpan = default,
        double? currentPrice = null)
    {
        if (samples == null || timestamps == null || samples.Count < MinSampleCount || samples.Count != timestamps.Count)
        {
            return SsaSupportResistanceResult.Empty;
        }

        int n = samples.Count;
        for (int i = 0; i < n; i++)
        {
            if (!double.IsFinite(samples[i]))
            {
                return SsaSupportResistanceResult.Empty;
            }
        }

        int l = Math.Clamp(embeddingDimension, 2, Math.Max(2, n / 2));
        int k = n - l + 1;
        if (k < 2 || n < l + 1)
        {
            return SsaSupportResistanceResult.Empty;
        }

        double[] sampleArray = (samples is double[] arr) ? arr : samples.ToArray();

        // 1. Perform SSA Matrix Decomposition via SsaDecompositionEngine
        var decomp = SsaDecompositionEngine.Decompose(sampleArray, l, detrendMode);
        if (decomp.SortedIndices.Length < l)
        {
            return SsaSupportResistanceResult.Empty;
        }

        // 2. Rank selection
        int r;
        if (autoRank)
        {
            r = SsaRankSelector.EstimateSignalRank(decomp.Eigenvalues, SsaRankSelectionMethod.CumulativeEnergy, targetEnergy: 0.90, maxRank: l - 1);
        }
        else
        {
            r = Math.Clamp(numComponents, 1, Math.Min(l - 1, k));
        }

        double slope = decomp.Slope;
        double intercept = decomp.Intercept;

        Span<double> processed = stackalloc double[n];
        SsaDecompositionEngine.Detrend(sampleArray, processed, detrendMode, out _, out _);

        double cumulativeVariance = 0.0;
        for (int m = 0; m < r; m++)
        {
            cumulativeVariance += decomp.ComponentEnergies[m];
        }

        // 3. In-Sample Signal Reconstruction via Diagonal Averaging
        double[] reconstructed = new double[n];
        SsaDecompositionEngine.ReconstructGroup(processed, l, k, decomp.SortedIndices.AsSpan(0, r), decomp.Eigenvectors, reconstructed);

        // Center curve: \tilde{x}_t = reconstructed[t] + (intercept + slope * t)
        var centerPoints = new List<Point>(n);
        double[] centerPriceArray = new double[n];
        for (int t = 0; t < n; t++)
        {
            double cp = reconstructed[t] + (intercept + slope * t);
            centerPriceArray[t] = cp;
            centerPoints.Add(new Point((double)timestamps[t].Ticks, cp));
        }

        // Compute residual standard deviation \sigma_res
        double sumSqErr = 0.0;
        for (int t = 0; t < n; t++)
        {
            double diff = sampleArray[t] - centerPriceArray[t];
            sumSqErr += diff * diff;
        }
        double residualStdDev = Math.Max(MinClusterToleranceEpsilon, Math.Sqrt(sumSqErr / n));

        // Separability Score
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

        // Stability nu2
        double nu2 = 0.0;
        for (int m = 0; m < r; m++)
        {
            int eigIdx = decomp.SortedIndices[m];
            double nuM = decomp.Eigenvectors[l - 1, eigIdx];
            nu2 += nuM * nuM;
        }
        nu2 = Math.Min(nu2, 0.99999);
        bool isStable = nu2 < SsaProjectionAnalysis.NuSquaredStabilityThreshold;

        // Effective reference price for Active S/R resolution
        double refPrice = currentPrice ?? centerPriceArray[n - 1];

        var resistanceList = new List<SsaSupportResistanceLevel>();
        var supportList = new List<SsaSupportResistanceLevel>();
        var upperBandPoints = new List<Point>();
        var lowerBandPoints = new List<Point>();
        var projectedPoints = new List<Point>();

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

        switch (mode)
        {
            case SsaSupportResistanceMode.StructuralPivots:
                ComputeStructuralPivots(
                    centerPriceArray,
                    timestamps,
                    residualStdDev,
                    (double)clusterTolerance,
                    Math.Max(1, maxLevelsPerSide),
                    resistanceList,
                    supportList);
                break;

            case SsaSupportResistanceMode.DynamicEnvelopes:
                ComputeDynamicEnvelopes(
                    centerPoints,
                    residualStdDev,
                    (double)multiplier,
                    timestamps,
                    upperBandPoints,
                    lowerBandPoints,
                    resistanceList,
                    supportList);
                break;

            case SsaSupportResistanceMode.ProjectedTargets:
                ComputeProjectedTargets(
                    samples,
                    timestamps,
                    futureSteps,
                    timeframeSpan,
                    l,
                    r,
                    detrendMode,
                    forecastMode,
                    projectedPoints,
                    resistanceList,
                    supportList);
                break;
        }

        // Resolve Active Resistance and Active Support
        double? activeResistance = null;
        if (resistanceList.Count > 0)
        {
            var candidates = resistanceList.Where(lvl => lvl.Price > refPrice).ToList();
            if (candidates.Count > 0)
            {
                activeResistance = candidates.Min(lvl => lvl.Price);
            }
        }

        double? activeSupport = null;
        if (supportList.Count > 0)
        {
            var candidates = supportList.Where(lvl => lvl.Price < refPrice).ToList();
            if (candidates.Count > 0)
            {
                activeSupport = candidates.Max(lvl => lvl.Price);
            }
        }

        return new SsaSupportResistanceResult(
            isEmpty: false,
            mode: mode,
            resistanceLevels: resistanceList,
            supportLevels: supportList,
            centerBand: centerPoints,
            upperBand: upperBandPoints,
            lowerBand: lowerBandPoints,
            projectedPath: projectedPoints,
            residualStdDev: residualStdDev,
            activeResistance: activeResistance,
            activeSupport: activeSupport,
            separabilityScore: separabilityScore,
            slope: slope,
            intercept: intercept,
            sampleCount: n,
            embeddingDimension: l,
            numComponents: r,
            cumulativeVarianceRatio: cumulativeVariance,
            nuSquared: nu2,
            isStable: isStable);
    }

    /// <summary>
    /// Mode 1: Plateau-safe local extrema extraction and 1D clustering into horizontal levels.
    /// </summary>
    private static void ComputeStructuralPivots(
        double[] centerPrice,
        IReadOnlyList<DateTime> timestamps,
        double residualStdDev,
        double clusterToleranceMultiplier,
        int maxLevelsPerSide,
        List<SsaSupportResistanceLevel> outResistance,
        List<SsaSupportResistanceLevel> outSupport)
    {
        int n = centerPrice.Length;
        var peakCandidates = new List<(double Price, int Index)>();
        var troughCandidates = new List<(double Price, int Index)>();

        // 1. Determine initial non-zero slope sign and plateau start
        int prevSign = 0;
        int plateauStart = 0;
        for (int i = 0; i < n - 1; i++)
        {
            double diff = centerPrice[i + 1] - centerPrice[i];
            if (Math.Abs(diff) > 1e-12)
            {
                prevSign = Math.Sign(diff);
                plateauStart = i;
                break;
            }
        }

        // 2. Scan t in [1, N-2] tracking three-value state transitions (+1 -> 0* -> -1 or -1 -> 0* -> +1)
        for (int t = 1; t <= n - 2; t++)
        {
            double diff = centerPrice[t + 1] - centerPrice[t];
            int currSign = Math.Abs(diff) > 1e-12 ? Math.Sign(diff) : 0;

            if (currSign != 0)
            {
                if (prevSign == +1 && currSign == -1)
                {
                    // Plateau-centered peak candidate
                    int tMid = (plateauStart + t) / 2;
                    peakCandidates.Add((centerPrice[tMid], tMid));
                }
                else if (prevSign == -1 && currSign == +1)
                {
                    // Plateau-centered trough candidate
                    int tMid = (plateauStart + t) / 2;
                    troughCandidates.Add((centerPrice[tMid], tMid));
                }

                prevSign = currSign;
                plateauStart = t;
            }
        }

        double deltaCluster = Math.Max(MinClusterToleranceEpsilon, clusterToleranceMultiplier * residualStdDev);

        // 3. Cluster peak candidates into Resistance levels with formatted labels
        var clusteredPeaks = ClusterExtrema(peakCandidates, deltaCluster, n, timestamps, isResistance: true);
        var selectedPeaks = clusteredPeaks
            .OrderByDescending(lvl => lvl.StrengthScore)
            .Take(maxLevelsPerSide)
            .OrderBy(lvl => lvl.Price)
            .ToList();

        for (int i = 0; i < selectedPeaks.Count; i++)
        {
            var p = selectedPeaks[i];
            outResistance.Add(p with { Label = $"R{i + 1}: {p.Price:F2} (Hits: {p.Hits})" });
        }

        // 4. Cluster trough candidates into Support levels with formatted labels
        var clusteredTroughs = ClusterExtrema(troughCandidates, deltaCluster, n, timestamps, isResistance: false);
        var selectedTroughs = clusteredTroughs
            .OrderByDescending(lvl => lvl.StrengthScore)
            .Take(maxLevelsPerSide)
            .OrderBy(lvl => lvl.Price)
            .ToList();

        for (int i = 0; i < selectedTroughs.Count; i++)
        {
            var p = selectedTroughs[i];
            outSupport.Add(p with { Label = $"S{i + 1}: {p.Price:F2} (Hits: {p.Hits})" });
        }
    }

    /// <summary>
    /// Performs deterministic 1D clustering on extracted extrema candidates.
    /// </summary>
    public static List<SsaSupportResistanceLevel> ClusterExtrema(
        List<(double Price, int Index)> candidates,
        double deltaCluster,
        int n,
        IReadOnlyList<DateTime> timestamps,
        bool isResistance)
    {
        if (candidates.Count == 0) return new List<SsaSupportResistanceLevel>();

        // Sort by price ascending
        var sorted = candidates.OrderBy(c => c.Price).ToList();
        var clusters = new List<List<(double Price, int Index)>>();

        var currentCluster = new List<(double Price, int Index)> { sorted[0] };
        for (int i = 1; i < sorted.Count; i++)
        {
            var prev = currentCluster[^1];
            var curr = sorted[i];

            if (Math.Abs(curr.Price - prev.Price) <= deltaCluster)
            {
                currentCluster.Add(curr);
            }
            else
            {
                clusters.Add(currentCluster);
                currentCluster = new List<(double Price, int Index)> { curr };
            }
        }
        clusters.Add(currentCluster);

        var result = new List<SsaSupportResistanceLevel>(clusters.Count);
        foreach (var cl in clusters)
        {
            double meanPrice = cl.Average(item => item.Price);
            int latestIdx = cl.Max(item => item.Index);
            int hits = cl.Count;
            double strengthScore = hits * (1.0 + 0.5 * ((double)latestIdx / Math.Max(1, n)));
            DateTime? targetTime = (latestIdx >= 0 && latestIdx < timestamps.Count) ? timestamps[latestIdx] : null;

            result.Add(new SsaSupportResistanceLevel(
                Price: meanPrice,
                Hits: hits,
                StrengthScore: strengthScore,
                LatestIndex: latestIdx,
                TargetTime: targetTime,
                IsResistance: isResistance));
        }

        return result;
    }

    /// <summary>
    /// Mode 2: Dynamic volatility envelopes (+/- M * sigma_res).
    /// </summary>
    private static void ComputeDynamicEnvelopes(
        IReadOnlyList<Point> centerPoints,
        double residualStdDev,
        double multiplier,
        IReadOnlyList<DateTime> timestamps,
        List<Point> outUpperBand,
        List<Point> outLowerBand,
        List<SsaSupportResistanceLevel> outResistance,
        List<SsaSupportResistanceLevel> outSupport)
    {
        int n = centerPoints.Count;
        double width = multiplier * residualStdDev;

        for (int t = 0; t < n; t++)
        {
            double ticks = centerPoints[t].X;
            double center = centerPoints[t].Y;

            outUpperBand.Add(new Point(ticks, center + width));
            outLowerBand.Add(new Point(ticks, center - width));
        }

        if (n > 0)
        {
            double currentUpper = outUpperBand[^1].Y;
            double currentLower = outLowerBand[^1].Y;
            DateTime latestTime = timestamps[^1];

            outResistance.Add(new SsaSupportResistanceLevel(
                Price: currentUpper,
                Hits: 1,
                StrengthScore: 1.0,
                LatestIndex: n - 1,
                TargetTime: latestTime,
                IsResistance: true,
                Label: $"Upper: {currentUpper:F2}"));

            outSupport.Add(new SsaSupportResistanceLevel(
                Price: currentLower,
                Hits: 1,
                StrengthScore: 1.0,
                LatestIndex: n - 1,
                TargetTime: latestTime,
                IsResistance: false,
                Label: $"Lower: {currentLower:F2}"));
        }
    }

    /// <summary>
    /// Mode 3: Future projection trajectory extrapolation and earliest extrema target identification.
    /// </summary>
    private static void ComputeProjectedTargets(
        IReadOnlyList<double> samples,
        IReadOnlyList<DateTime> timestamps,
        int futureSteps,
        TimeSpan timeframeSpan,
        int embeddingDimension,
        int numComponents,
        SsaDetrendMode detrendMode,
        SsaForecastMode forecastMode,
        List<Point> outProjectedPoints,
        List<SsaSupportResistanceLevel> outResistance,
        List<SsaSupportResistanceLevel> outSupport)
    {
        int steps = Math.Clamp(futureSteps, 1, 100);
        var projResult = SsaProjectionAnalysis.CalculateProjection(
            samples,
            timestamps,
            futureSteps: steps,
            timeframeSpan: timeframeSpan,
            embeddingDimension: embeddingDimension,
            numComponents: numComponents,
            detrendMode: detrendMode,
            showConfidenceBand: false,
            confidenceMultiplier: 2.0m,
            forecastMode: forecastMode);

        if (projResult.ProjectedPoints.Count <= 1)
        {
            return;
        }

        outProjectedPoints.AddRange(projResult.ProjectedPoints);

        // Scan future steps h in [1, H]
        // ProjectedPoints[0] is in-sample anchor, ProjectedPoints[1..steps] are future points
        int futureCount = projResult.ProjectedPoints.Count - 1;
        if (futureCount < 1) return;

        // 1. First scan for true geometric turning points (Peak / Trough)
        int? peakH = null;
        double? peakPrice = null;
        int? troughH = null;
        double? troughPrice = null;

        int prevSign = 0;
        int plateauStart = 0;
        for (int i = 0; i < futureCount; i++)
        {
            double diff = projResult.ProjectedPoints[i + 1].Y - projResult.ProjectedPoints[i].Y;
            if (Math.Abs(diff) > 1e-12)
            {
                prevSign = Math.Sign(diff);
                plateauStart = i;
                break;
            }
        }

        for (int h = 1; h < futureCount; h++)
        {
            double diff = projResult.ProjectedPoints[h + 1].Y - projResult.ProjectedPoints[h].Y;
            int currSign = Math.Abs(diff) > 1e-12 ? Math.Sign(diff) : 0;

            if (currSign != 0)
            {
                if (prevSign == +1 && currSign == -1 && peakH == null)
                {
                    int tMid = (plateauStart + h) / 2;
                    peakH = Math.Max(1, tMid);
                    peakPrice = projResult.ProjectedPoints[peakH.Value].Y;
                }
                else if (prevSign == -1 && currSign == +1 && troughH == null)
                {
                    int tMid = (plateauStart + h) / 2;
                    troughH = Math.Max(1, tMid);
                    troughPrice = projResult.ProjectedPoints[troughH.Value].Y;
                }

                prevSign = currSign;
                plateauStart = h;
            }
        }

        // 2. Global extrema fallback for monotonic segments
        double globalMaxPrice = double.MinValue;
        int minHMax = 1;
        double globalMinPrice = double.MaxValue;
        int minHMin = 1;

        for (int h = 1; h <= futureCount; h++)
        {
            double price = projResult.ProjectedPoints[h].Y;

            if (price > globalMaxPrice)
            {
                globalMaxPrice = price;
                minHMax = h;
            }

            if (price < globalMinPrice)
            {
                globalMinPrice = price;
                minHMin = h;
            }
        }

        int finalResH = peakH ?? minHMax;
        double finalResPrice = peakPrice ?? globalMaxPrice;

        int finalSupH = troughH ?? minHMin;
        double finalSupPrice = troughPrice ?? globalMinPrice;

        DateTime lastTime = timestamps[^1];
        DateTime targetResistanceTime = lastTime + (timeframeSpan * finalResH);
        DateTime targetSupportTime = lastTime + (timeframeSpan * finalSupH);
        int n = samples.Count;

        outResistance.Add(new SsaSupportResistanceLevel(
            Price: finalResPrice,
            Hits: 1,
            StrengthScore: 1.0,
            LatestIndex: n - 1 + finalResH,
            TargetTime: targetResistanceTime,
            IsResistance: true,
            Label: $"Target R: {finalResPrice:F2}"));

        outSupport.Add(new SsaSupportResistanceLevel(
            Price: finalSupPrice,
            Hits: 1,
            StrengthScore: 1.0,
            LatestIndex: n - 1 + finalSupH,
            TargetTime: targetSupportTime,
            IsResistance: false,
            Label: $"Target S: {finalSupPrice:F2}"));
    }
}
