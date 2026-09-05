using System;
using System.Buffers;
using System.Collections.Generic;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.Analysis;

/// <summary>
/// Encapsulates the complete result of an ARIMA multi-step future projection calculation.
/// </summary>
public sealed class ArimaProjectionResult
{
    public static readonly ArimaProjectionResult Empty = new(
        Array.Empty<Point>(),
        Array.Empty<Point>(),
        Array.Empty<Point>(),
        0, 0, 0, 0.0, 0.0, 0.0, 0, false);

    public IReadOnlyList<Point> ProjectedPoints { get; }
    public IReadOnlyList<Point> UpperBandPoints { get; }
    public IReadOnlyList<Point> LowerBandPoints { get; }
    public int P { get; }
    public int D { get; }
    public int Q { get; }
    public double InnovationVariance { get; }
    public double ResidualStdDev { get; }
    public double TargetPrice { get; }
    public int SampleCount { get; }
    public bool IsLowerBandClamped { get; }

    public ArimaProjectionResult(
        IReadOnlyList<Point> projectedPoints,
        IReadOnlyList<Point> upperBandPoints,
        IReadOnlyList<Point> lowerBandPoints,
        int p, int d, int q,
        double innovationVariance,
        double residualStdDev,
        double targetPrice,
        int sampleCount,
        bool isLowerBandClamped)
    {
        ProjectedPoints = projectedPoints;
        UpperBandPoints = upperBandPoints;
        LowerBandPoints = lowerBandPoints;
        P = p;
        D = d;
        Q = q;
        InnovationVariance = innovationVariance;
        ResidualStdDev = residualStdDev;
        TargetPrice = targetPrice;
        SampleCount = sampleCount;
        IsLowerBandClamped = isLowerBandClamped;
    }
}

/// <summary>
/// High-level analysis engine for ARIMA future trajectory projection.
/// Extracts price samples according to SSoT PriceType, computes multi-step recursive forecasts,
/// and extrapolates future time coordinates with C0 continuity.
/// </summary>
public static class ArimaProjectionAnalysis
{
    private const int StackAllocThreshold = 512;

    public static ArimaProjectionResult CalculateProjection(
        IReadOnlyList<CoreCandleData> candles,
        int startIndex,
        int endIndex,
        int p = 1,
        int d = 1,
        int q = 1,
        int futureSteps = 20,
        PriceType priceSource = PriceType.Close,
        TimeSpan timeframeSpan = default,
        bool showConfidenceBand = true,
        decimal confidenceMultiplier = 2.0m)
    {
        if (candles == null || candles.Count == 0 || startIndex < 0 || endIndex >= candles.Count || startIndex > endIndex)
        {
            return ArimaProjectionResult.Empty;
        }

        int count = endIndex - startIndex + 1;
        if (count <= d)
        {
            return ArimaProjectionResult.Empty;
        }

        int steps = Math.Clamp(futureSteps, 1, 100);

        // Extract price series using SSoT PriceDataHelper
        double[]? rentedPrices = null;
        Span<double> prices = (count <= StackAllocThreshold)
            ? stackalloc double[count]
            : (rentedPrices = ArrayPool<double>.Shared.Rent(count)).AsSpan(0, count);

        try
        {
            for (int i = 0; i < count; i++)
            {
                int candleIdx = startIndex + i;
                decimal? prevClose = (candleIdx > 0) ? candles[candleIdx - 1].Close : null;
                prices[i] = (double)PriceDataHelper.ExtractPrice(candles[candleIdx], priceSource, prevClose);
            }

            Span<double> forecasted = stackalloc double[steps];
            Span<double> errorVariances = stackalloc double[steps];

            bool success = ArimaMath.EstimateArimaMultiStepForecast(
                prices,
                p, d, q,
                steps,
                forecasted,
                errorVariances,
                out double innovationVariance,
                out double residualStdDev);

            if (!success)
            {
                return ArimaProjectionResult.Empty;
            }

            // Derive timeframe span if not explicitly provided
            if (timeframeSpan <= TimeSpan.Zero)
            {
                if (candles.Count >= 2)
                {
                    double avgMs = (candles[^1].Timestamp - candles[0].Timestamp).TotalMilliseconds / (candles.Count - 1);
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

            // First point (h=0) connects seamlessly from the last candle of the selection window (C0 continuity)
            var lastCandle = candles[endIndex];
            decimal? lastPrevClose = (endIndex > 0) ? candles[endIndex - 1].Close : null;
            double lastPrice = (double)PriceDataHelper.ExtractPrice(lastCandle, priceSource, lastPrevClose);
            var initialPoint = new Point((double)lastCandle.Timestamp.Ticks, lastPrice);

            projectedPoints.Add(initialPoint);
            upperPoints.Add(initialPoint);
            lowerPoints.Add(initialPoint);

            double multiplier = Math.Max(0.0, (double)confidenceMultiplier);
            bool isLowerClamped = false;

            for (int k = 1; k <= steps; k++)
            {
                int targetIndex = endIndex + k;
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

                double predPrice = forecasted[k - 1];
                double stdDev = Math.Sqrt(Math.Max(0.0, errorVariances[k - 1]));
                double margin = stdDev * multiplier;

                double upperPrice = predPrice + margin;
                double rawLower = predPrice - margin;
                double lowerPrice = Math.Max(0.0, rawLower);

                if (rawLower < 0.0)
                {
                    isLowerClamped = true;
                }

                projectedPoints.Add(new Point((double)targetTime.Ticks, predPrice));
                upperPoints.Add(new Point((double)targetTime.Ticks, upperPrice));
                lowerPoints.Add(new Point((double)targetTime.Ticks, lowerPrice));
            }

            double targetPrice = forecasted[steps - 1];

            return new ArimaProjectionResult(
                projectedPoints,
                upperPoints,
                lowerPoints,
                p, d, q,
                innovationVariance,
                residualStdDev,
                targetPrice,
                count,
                isLowerClamped);
        }
        finally
        {
            if (rentedPrices != null) ArrayPool<double>.Shared.Return(rentedPrices);
        }
    }
}
