using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Analysis;

/// <summary>
/// Represents a single historical pattern match discovered by Pearson correlation search.
/// </summary>
public sealed record MatchedPatternInfo(
    int StartIndex,
    int EndIndex,
    DateTime StartTime,
    DateTime EndTime,
    double Correlation,
    IReadOnlyList<double> FutureReturns);

/// <summary>
/// Encapsulates the results of a Pearson Correlation future trajectory projection calculation.
/// </summary>
public sealed class PearsonProjectionResult
{
    public static readonly PearsonProjectionResult Empty = new(
        Array.Empty<Point>(),
        Array.Empty<Point>(),
        Array.Empty<Point>(),
        Array.Empty<MatchedPatternInfo>(),
        0.0,
        null,
        null,
        0);

    public IReadOnlyList<Point> ProjectedPoints { get; }
    public IReadOnlyList<Point> UpperBandPoints { get; }
    public IReadOnlyList<Point> LowerBandPoints { get; }
    public IReadOnlyList<MatchedPatternInfo> MatchedPatterns { get; }
    public double BestCorrelation { get; }
    public DateTime? MatchedStartTime { get; }
    public DateTime? MatchedEndTime { get; }
    public int SampleCount { get; }
    public bool HasMatch => MatchedPatterns.Count > 0 && ProjectedPoints.Count > 0;

    public PearsonProjectionResult(
        IReadOnlyList<Point> projectedPoints,
        IReadOnlyList<Point> upperBandPoints,
        IReadOnlyList<Point> lowerBandPoints,
        IReadOnlyList<MatchedPatternInfo> matchedPatterns,
        double bestCorrelation,
        DateTime? matchedStartTime,
        DateTime? matchedEndTime,
        int sampleCount)
    {
        ProjectedPoints = projectedPoints;
        UpperBandPoints = upperBandPoints;
        LowerBandPoints = lowerBandPoints;
        MatchedPatterns = matchedPatterns;
        BestCorrelation = bestCorrelation;
        MatchedStartTime = matchedStartTime;
        MatchedEndTime = matchedEndTime;
        SampleCount = sampleCount;
    }
}

/// <summary>
/// Pure C# mathematical engine for Pearson Product-Moment Correlation Pattern Matching and Future Projection.
/// Scans historical candles using an O(T) prefix-sums accelerated sliding window to identify segments whose
/// geometric wave shape has highest Pearson correlation with the query pattern, and extrapolates forward
/// trajectories with volatility-adjusted scaling, r^2 ensemble weighting, and time-diffusion prediction bands.
/// </summary>
public static class PearsonProjectionAnalysis
{
    public const int MinSampleCount = 3;

    /// <summary>
    /// Calculates the Pearson Correlation projection from a historical sample series.
    /// </summary>
    public static PearsonProjectionResult CalculateProjection(
        IReadOnlyList<double> samples,
        IReadOnlyList<DateTime> timestamps,
        int queryStartIndex,
        int queryEndIndex,
        int futureSteps = 20,
        double minCorrelation = 0.7,
        int topK = 1,
        bool applyVolatilityScaling = true,
        bool applyDetrend = false,
        bool showConfidenceBand = true,
        decimal confidenceMultiplier = 2.0m,
        TimeSpan timeframeSpan = default)
    {
        if (samples == null || timestamps == null || samples.Count != timestamps.Count)
        {
            return PearsonProjectionResult.Empty;
        }

        if (queryStartIndex < 0 || queryEndIndex < queryStartIndex || queryEndIndex >= samples.Count)
        {
            return PearsonProjectionResult.Empty;
        }

        int queryLength = queryEndIndex - queryStartIndex + 1;
        if (queryLength < MinSampleCount)
        {
            return PearsonProjectionResult.Empty;
        }

        futureSteps = Math.Clamp(futureSteps, 1, 100);
        topK = Math.Clamp(topK, 1, 20);
        double threshold = Math.Clamp(minCorrelation, -1.0, 1.0);

        int totalCount = samples.Count;
        int maxCandidateStart = totalCount - queryLength - futureSteps;
        if (maxCandidateStart < 0)
        {
            return PearsonProjectionResult.Empty;
        }

        // 1. Build Prefix Sums for O(1) sliding window moment calculations
        var prefixSum = new double[totalCount + 1];
        var prefixSqSum = new double[totalCount + 1];
        for (int i = 0; i < totalCount; i++)
        {
            double v = samples[i];
            prefixSum[i + 1] = prefixSum[i] + v;
            prefixSqSum[i + 1] = prefixSqSum[i] + v * v;
        }

        // 2. Prepare Query Vector (with optional Detrending)
        var queryVec = new double[queryLength];
        double querySum = prefixSum[queryEndIndex + 1] - prefixSum[queryStartIndex];
        double queryMean = querySum / queryLength;

        if (applyDetrend)
        {
            // Linear regression detrending: q(i) - (a * i + b)
            double sumI = (queryLength - 1) * queryLength / 2.0;
            double sumI2 = (queryLength - 1) * queryLength * (2 * queryLength - 1) / 6.0;
            double sumIQ = 0.0;
            for (int i = 0; i < queryLength; i++)
            {
                sumIQ += i * samples[queryStartIndex + i];
            }
            double denom = (queryLength * sumI2 - sumI * sumI);
            double slope = Math.Abs(denom) > 1e-12 ? (queryLength * sumIQ - sumI * querySum) / denom : 0.0;
            double intercept = queryMean - slope * sumI / queryLength;

            for (int i = 0; i < queryLength; i++)
            {
                queryVec[i] = samples[queryStartIndex + i] - (slope * i + intercept);
            }
        }
        else
        {
            for (int i = 0; i < queryLength; i++)
            {
                queryVec[i] = samples[queryStartIndex + i] - queryMean;
            }
        }

        double querySqDiff = 0.0;
        for (int i = 0; i < queryLength; i++)
        {
            querySqDiff += queryVec[i] * queryVec[i];
        }

        if (querySqDiff <= 1e-12)
        {
            // Flat query - undefined correlation
            return PearsonProjectionResult.Empty;
        }

        double queryStdDev = Math.Sqrt(querySqDiff);
        double queryNormalizedVol = (queryStdDev / Math.Sqrt(queryLength)) / Math.Max(1.0, Math.Abs(queryMean));

        // 3. Sliding window scan over entire historical series
        var candidateMatches = new List<(int StartIndex, double Correlation, double WindowStdDev, double WindowMean)>();

        // Precompute regression denominator for detrending if active
        double detrendSumI = (queryLength - 1) * queryLength / 2.0;
        double detrendSumI2 = (queryLength - 1) * queryLength * (2 * queryLength - 1) / 6.0;
        double detrendDenom = (queryLength * detrendSumI2 - detrendSumI * detrendSumI);

        for (int t = 0; t <= maxCandidateStart; t++)
        {
            // Disallow overlap with query region to avoid self-matching or trivial single-bar shifts
            int candEnd = t + queryLength - 1;
            if (candEnd >= queryStartIndex && t <= queryEndIndex)
            {
                continue;
            }

            double winSum = prefixSum[t + queryLength] - prefixSum[t];
            double winSqSum = prefixSqSum[t + queryLength] - prefixSqSum[t];
            double winMean = winSum / queryLength;
            double winSqDiff = Math.Max(0.0, winSqSum - (winSum * winSum / queryLength));

            if (winSqDiff <= 1e-12)
            {
                continue;
            }

            double winStdDev = Math.Sqrt(winSqDiff);
            double covSum = 0.0;

            if (applyDetrend && Math.Abs(detrendDenom) > 1e-12)
            {
                double sumIW = 0.0;
                for (int i = 0; i < queryLength; i++)
                {
                    sumIW += i * samples[t + i];
                }
                double wSlope = (queryLength * sumIW - detrendSumI * winSum) / detrendDenom;
                double wIntercept = winMean - wSlope * detrendSumI / queryLength;

                double wDetrendSqSum = 0.0;
                for (int i = 0; i < queryLength; i++)
                {
                    double wVal = samples[t + i] - (wSlope * i + wIntercept);
                    covSum += queryVec[i] * wVal;
                    wDetrendSqSum += wVal * wVal;
                }

                if (wDetrendSqSum <= 1e-12)
                {
                    continue;
                }
                winStdDev = Math.Sqrt(wDetrendSqSum);
            }
            else
            {
                for (int i = 0; i < queryLength; i++)
                {
                    covSum += queryVec[i] * (samples[t + i] - winMean);
                }
            }

            double r = covSum / (queryStdDev * winStdDev);
            if (double.IsNaN(r) || double.IsInfinity(r))
            {
                continue;
            }

            r = Math.Clamp(r, -1.0, 1.0);

            if (r >= threshold)
            {
                candidateMatches.Add((t, r, winStdDev, winMean));
            }
        }

        if (candidateMatches.Count == 0)
        {
            return PearsonProjectionResult.Empty;
        }

        // 4. Sort by correlation descending and apply Non-Maximum Suppression (NMS)
        candidateMatches.Sort((a, b) => b.Correlation.CompareTo(a.Correlation));

        var selectedMatches = new List<MatchedPatternInfo>();
        int suppressionDistance = Math.Max(3, queryLength / 2);

        foreach (var candidate in candidateMatches)
        {
            bool isTooClose = false;
            foreach (var sel in selectedMatches)
            {
                if (Math.Abs(candidate.StartIndex - sel.StartIndex) < suppressionDistance)
                {
                    isTooClose = true;
                    break;
                }
            }

            if (!isTooClose)
            {
                int endIdx = candidate.StartIndex + queryLength - 1;
                double baseHistPrice = samples[endIdx];
                if (Math.Abs(baseHistPrice) < 1e-8)
                {
                    continue;
                }

                // Volatility Scaling Ratio: Scale past return amplitude to match query volatility
                double volRatio = 1.0;
                if (applyVolatilityScaling)
                {
                    double winNormalizedVol = (candidate.WindowStdDev / Math.Sqrt(queryLength)) / Math.Max(1.0, Math.Abs(candidate.WindowMean));
                    if (winNormalizedVol > 1e-8)
                    {
                        volRatio = Math.Clamp(queryNormalizedVol / winNormalizedVol, 0.2, 5.0);
                    }
                }

                var futureReturns = new double[futureSteps];
                for (int k = 0; k < futureSteps; k++)
                {
                    int fIdx = endIdx + 1 + k;
                    double futurePrice = samples[fIdx];
                    double rawReturn = (futurePrice - baseHistPrice) / baseHistPrice;
                    futureReturns[k] = rawReturn * volRatio;
                }

                selectedMatches.Add(new MatchedPatternInfo(
                    candidate.StartIndex,
                    endIdx,
                    timestamps[candidate.StartIndex],
                    timestamps[endIdx],
                    candidate.Correlation,
                    futureReturns));

                if (selectedMatches.Count >= topK)
                {
                    break;
                }
            }
        }

        if (selectedMatches.Count == 0)
        {
            return PearsonProjectionResult.Empty;
        }

        // 5. Resolve timeframe delta for future timestamps
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

        // 6. Build Projected Future Points and Prediction Bands with r^2 Weighting
        double currentBasePrice = samples[queryEndIndex];
        var projectedPoints = new List<Point>(futureSteps + 1);
        var upperBandPoints = new List<Point>(futureSteps + 1);
        var lowerBandPoints = new List<Point>(futureSteps + 1);

        var initialPoint = new Point((double)timestamps[queryEndIndex].Ticks, currentBasePrice);
        projectedPoints.Add(initialPoint);
        upperBandPoints.Add(initialPoint);
        lowerBandPoints.Add(initialPoint);

        // Normalize weights based on r^2 (coefficient of determination over threshold)
        double totalWeight = 0.0;
        var weights = new double[selectedMatches.Count];
        for (int m = 0; m < selectedMatches.Count; m++)
        {
            double diffR = Math.Max(0.0, selectedMatches[m].Correlation - threshold);
            double w = Math.Pow(diffR / (1.0 - threshold + 0.01), 2.0) + 0.01;
            weights[m] = w;
            totalWeight += w;
        }
        for (int m = 0; m < selectedMatches.Count; m++)
        {
            weights[m] /= totalWeight;
        }

        double multiplier = Math.Max(0.0, (double)confidenceMultiplier);

        for (int k = 0; k < futureSteps; k++)
        {
            int targetIndex = queryEndIndex + 1 + k;
            DateTime targetTime;
            if (targetIndex < timestamps.Count)
            {
                targetTime = timestamps[targetIndex];
            }
            else
            {
                int extendedSteps = targetIndex - timestamps.Count + 1;
                targetTime = timestamps[^1] + (timeframeSpan * extendedSteps);
            }

            double meanReturn = 0.0;
            for (int m = 0; m < selectedMatches.Count; m++)
            {
                meanReturn += weights[m] * selectedMatches[m].FutureReturns[k];
            }

            double returnVariance = 0.0;
            for (int m = 0; m < selectedMatches.Count; m++)
            {
                double diff = selectedMatches[m].FutureReturns[k] - meanReturn;
                returnVariance += weights[m] * diff * diff;
            }

            double returnStdDev = Math.Sqrt(returnVariance);

            // Time-diffusion fallback when variance is near zero or single candidate
            double timeDiffusionStd = queryNormalizedVol * Math.Sqrt((double)(k + 1) / queryLength);
            double effectiveStdDev = Math.Max(returnStdDev, timeDiffusionStd);

            double projPrice = currentBasePrice * (1.0 + meanReturn);
            double margin = currentBasePrice * effectiveStdDev * multiplier;

            double upperPrice = projPrice + margin;
            double lowerPrice = Math.Max(0.0, projPrice - margin); // Lower price clamp >= 0

            projectedPoints.Add(new Point((double)targetTime.Ticks, projPrice));
            upperBandPoints.Add(new Point((double)targetTime.Ticks, upperPrice));
            lowerBandPoints.Add(new Point((double)targetTime.Ticks, lowerPrice));
        }

        var bestMatch = selectedMatches[0];

        return new PearsonProjectionResult(
            projectedPoints,
            upperBandPoints,
            lowerBandPoints,
            selectedMatches,
            bestMatch.Correlation,
            bestMatch.StartTime,
            bestMatch.EndTime,
            queryLength);
    }
}
