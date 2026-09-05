using System;
using System.Buffers;
using System.Collections.Generic;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.Analysis;

/// <summary>
/// Represents a single projected future point and its confidence interval bands.
/// </summary>
public readonly record struct FrechetProjectionPoint(
    int TargetIndex,
    DateTime Timestamp,
    decimal PredictedPrice,
    decimal UpperBand,
    decimal LowerBand);

/// <summary>
/// Encapsulates the results of a Discrete Fréchet Distance historical pattern match and forward projection.
/// </summary>
public sealed record FrechetProjectionResult(
    int MatchedStartIndex,
    int MatchedEndIndex,
    double Distance,
    IReadOnlyList<FrechetProjectionPoint> Projections);

/// <summary>
/// Mathematical analysis engine for Discrete Fréchet Distance pattern discovery and future trajectory projection.
/// Scans historical candles strictly prior to the query pattern to eliminate lookahead leakage,
/// evaluates geometric bottleneck deviations via pure dynamic programming, and extrapolates forward paths
/// with volatility-adjusted scaling and empirical time-diffusion prediction bands.
/// </summary>
public static class FrechetDistanceAnalysis
{
    private const int StackAllocThreshold = 512;
    public const int MinSampleCount = 3;
    public const double EpsilonVariance = 1e-12;
    public const double EpsilonDenominator = 1e-6;
    public const double MinVolatilityScale = 0.2;
    public const double MaxVolatilityScale = 5.0;
    public const decimal MinRelativeReturn = -0.9999m;
    public const decimal MaxRelativeReturn = 10.0m;
    public const double PredictionBandMultiplier = 2.0;

    /// <summary>
    /// Calculates the Discrete Fréchet Distance future projection based on historical candle patterns.
    /// </summary>
    /// <param name="candles">Historical candle dataset.</param>
    /// <param name="queryStartIndex">0-based start index of the query pattern.</param>
    /// <param name="queryEndIndex">0-based end index of the query pattern.</param>
    /// <param name="horizon">Number of future bars to extrapolate.</param>
    /// <param name="priceType">Price field type to extract (default: Close).</param>
    /// <param name="timeframeSpan">Time delta between candles for timestamp extrapolation.</param>
    /// <param name="maxDistance">Maximum acceptable Discrete Fréchet Distance threshold (default: double.MaxValue).</param>
    /// <param name="confidenceMultiplier">Multiplier for prediction diffusion band width (default: 2.0).</param>
    /// <returns>A <see cref="FrechetProjectionResult"/> if a match was found; otherwise, null.</returns>
    public static FrechetProjectionResult? CalculateProjection(
        IReadOnlyList<CoreCandleData> candles,
        int queryStartIndex,
        int queryEndIndex,
        int horizon,
        PriceType priceType = PriceType.Close,
        TimeSpan timeframeSpan = default,
        double maxDistance = double.MaxValue,
        double confidenceMultiplier = PredictionBandMultiplier)
    {
        if (candles == null || candles.Count == 0)
        {
            return null;
        }

        if (queryStartIndex < 0 || queryEndIndex >= candles.Count || queryStartIndex >= queryEndIndex)
        {
            return null;
        }

        if (horizon <= 0)
        {
            return null;
        }

        int length = queryEndIndex - queryStartIndex + 1;
        if (length < MinSampleCount)
        {
            return null;
        }

        // Strict non-lookahead constraint: Candidate's future window (s + length - 1 + horizon)
        // must strictly precede queryStartIndex.
        // s + length - 1 + horizon < queryStartIndex => s <= queryStartIndex - length - horizon
        int maxSearchStart = queryStartIndex - length - horizon;
        if (maxSearchStart < 0)
        {
            return null;
        }

        // Extract query prices into double buffer
        double[]? queryHeapBuffer = null;
        Span<double> queryPrices = length <= StackAllocThreshold
            ? stackalloc double[length]
            : (queryHeapBuffer = ArrayPool<double>.Shared.Rent(length)).AsSpan(0, length);

        double[]? queryNormHeapBuffer = null;
        Span<double> queryNorm = length <= StackAllocThreshold
            ? stackalloc double[length]
            : (queryNormHeapBuffer = ArrayPool<double>.Shared.Rent(length)).AsSpan(0, length);

        try
        {
            double querySum = 0.0;
            for (int i = 0; i < length; i++)
            {
                double p = (double)PriceDataHelper.ExtractPrice(candles[queryStartIndex + i], priceType);
                queryPrices[i] = p;
                querySum += p;
            }

            double queryMean = querySum / length;
            double querySqDiffSum = 0.0;
            for (int i = 0; i < length; i++)
            {
                double d = queryPrices[i] - queryMean;
                querySqDiffSum += d * d;
            }

            double queryStdDev = Math.Sqrt(Math.Max(0.0, querySqDiffSum / length));
            if (queryStdDev <= EpsilonVariance)
            {
                // Flat query cannot match geometric shapes
                return null;
            }

            for (int i = 0; i < length; i++)
            {
                queryNorm[i] = (queryPrices[i] - queryMean) / queryStdDev;
            }

            // Sliding window search
            double bestDistance = double.MaxValue;
            int bestStartIndex = -1;

            double[]? candHeapBuffer = null;
            Span<double> candPrices = length <= StackAllocThreshold
                ? stackalloc double[length]
                : (candHeapBuffer = ArrayPool<double>.Shared.Rent(length)).AsSpan(0, length);

            double[]? candNormHeapBuffer = null;
            Span<double> candNorm = length <= StackAllocThreshold
                ? stackalloc double[length]
                : (candNormHeapBuffer = ArrayPool<double>.Shared.Rent(length)).AsSpan(0, length);

            try
            {
                for (int s = 0; s <= maxSearchStart; s++)
                {
                    double candSum = 0.0;
                    for (int i = 0; i < length; i++)
                    {
                        double p = (double)PriceDataHelper.ExtractPrice(candles[s + i], priceType);
                        candPrices[i] = p;
                        candSum += p;
                    }

                    double candMean = candSum / length;
                    double candSqDiffSum = 0.0;
                    for (int i = 0; i < length; i++)
                    {
                        double d = candPrices[i] - candMean;
                        candSqDiffSum += d * d;
                    }

                    double candStdDev = Math.Sqrt(Math.Max(0.0, candSqDiffSum / length));
                    if (candStdDev <= EpsilonVariance)
                    {
                        // Skip flat candidates
                        continue;
                    }

                    for (int i = 0; i < length; i++)
                    {
                        candNorm[i] = (candPrices[i] - candMean) / candStdDev;
                    }

                    double dist = FrechetMath.CalculateDiscreteFrechetDistance(queryNorm, candNorm);
                    if (double.IsNaN(dist))
                    {
                        continue;
                    }

                    // Tie-break preferring more recent segment (higher s)
                    if (dist < bestDistance || (Math.Abs(dist - bestDistance) < 1e-12 && s > bestStartIndex))
                    {
                        bestDistance = dist;
                        bestStartIndex = s;
                    }
                }
            }
            finally
            {
                if (candHeapBuffer != null) ArrayPool<double>.Shared.Return(candHeapBuffer);
                if (candNormHeapBuffer != null) ArrayPool<double>.Shared.Return(candNormHeapBuffer);
            }

            if (bestStartIndex < 0 || bestDistance == double.MaxValue || bestDistance > maxDistance)
            {
                return null;
            }

            // Compute best match statistics for extrapolation
            double matchSum = 0.0;
            for (int i = 0; i < length; i++)
            {
                matchSum += (double)PriceDataHelper.ExtractPrice(candles[bestStartIndex + i], priceType);
            }
            double matchMean = matchSum / length;

            double matchSqDiffSum = 0.0;
            for (int i = 0; i < length; i++)
            {
                double p = (double)PriceDataHelper.ExtractPrice(candles[bestStartIndex + i], priceType);
                double d = p - matchMean;
                matchSqDiffSum += d * d;
            }
            double matchStdDev = Math.Sqrt(Math.Max(0.0, matchSqDiffSum / length));

            // Compute residual standard deviation (sigma_R) between normalized query and matched segment
            double residualSqSum = 0.0;
            for (int i = 0; i < length; i++)
            {
                double p = (double)PriceDataHelper.ExtractPrice(candles[bestStartIndex + i], priceType);
                double zMatch = matchStdDev <= EpsilonVariance ? 0.0 : (p - matchMean) / matchStdDev;
                double diff = queryNorm[i] - zMatch;
                residualSqSum += diff * diff;
            }
            double sigmaR = Math.Sqrt(Math.Max(0.0, residualSqSum / length));

            // Volatility scaling ratio with bounded clamp
            double volRatio = queryStdDev / Math.Max(matchStdDev, EpsilonDenominator);
            volRatio = Math.Clamp(volRatio, MinVolatilityScale, MaxVolatilityScale);

            // Timeframe resolution
            if (timeframeSpan <= TimeSpan.Zero)
            {
                if (candles.Count >= 2)
                {
                    double avgMs = (candles[^1].Timestamp - candles[0].Timestamp).TotalMilliseconds / (candles.Count - 1);
                    timeframeSpan = avgMs > 0 ? TimeSpan.FromMilliseconds(avgMs) : TimeSpan.FromDays(1);
                }
                else
                {
                    timeframeSpan = TimeSpan.FromDays(1);
                }
            }

            decimal matchBasePrice = PriceDataHelper.ExtractPrice(candles[bestStartIndex + length - 1], priceType);
            decimal currentPrice = PriceDataHelper.ExtractPrice(candles[queryEndIndex], priceType);

            // Relative volatility scaling with absolute mean guard
            double relVol = queryStdDev / Math.Max(Math.Abs(queryMean), EpsilonDenominator);
            var projections = new List<FrechetProjectionPoint>(horizon);

            decimal denom = Math.Max(Math.Abs(matchBasePrice), (decimal)EpsilonDenominator);

            for (int k = 0; k < horizon; k++)
            {
                int fIdx = bestStartIndex + length + k;
                if (fIdx >= candles.Count)
                {
                    break;
                }

                decimal futurePrice = PriceDataHelper.ExtractPrice(candles[fIdx], priceType);
                decimal rawReturn = Math.Clamp((futurePrice - matchBasePrice) / denom, MinRelativeReturn, MaxRelativeReturn);
                decimal scaledReturn = rawReturn * (decimal)volRatio;
                decimal predictedPrice = currentPrice * (1.0m + scaledReturn);

                double diffusionFactor = sigmaR * relVol * Math.Sqrt((k + 1.0) / length) * confidenceMultiplier;
                decimal width = Math.Abs(predictedPrice) * (decimal)Math.Max(0.0, diffusionFactor);

                decimal upperBand = predictedPrice + width;
                decimal lowerBand = Math.Max(0.0m, predictedPrice - width);

                int targetIndex = queryEndIndex + 1 + k;
                DateTime targetTime;
                if (targetIndex < candles.Count)
                {
                    targetTime = candles[targetIndex].Timestamp;
                }
                else
                {
                    int extendedSteps = targetIndex - candles.Count + 1;
                    targetTime = candles[^1].Timestamp + (timeframeSpan * extendedSteps);
                }

                projections.Add(new FrechetProjectionPoint(
                    targetIndex,
                    targetTime,
                    predictedPrice,
                    upperBand,
                    lowerBand));
            }

            return new FrechetProjectionResult(
                bestStartIndex,
                bestStartIndex + length - 1,
                bestDistance,
                projections);
        }
        finally
        {
            if (queryHeapBuffer != null) ArrayPool<double>.Shared.Return(queryHeapBuffer);
            if (queryNormHeapBuffer != null) ArrayPool<double>.Shared.Return(queryNormHeapBuffer);
        }
    }
}
