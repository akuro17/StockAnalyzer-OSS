using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.MarketStructure;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Structural DTW pattern search on plain <see cref="double"/> series (native port of the former Python <c>calculate_structural_dtw</c> handler).
/// The window length is the dominant cycle of the history (Burg spectrum of the recent mid prices); every earlier segment is Z-normalized and compared
/// with the current one by banded DTW; the RMSE-scale distance gets a log volatility-ratio penalty and is turned into a probability, exp(−distance).
/// The volatility series is supplied by the caller (native EGARCH, or <see cref="RollingStdVolatility"/> when no estimate is usable).
/// </summary>
public static class StructuralDtwMath
{
    /// <summary>
    /// Dominant cycle period in bars: Burg (maximum entropy) all-pole model of the last <c>min(window, n / 2)</c> demeaned mid prices, its power response on a
    /// grid of <see cref="IndicatorDefaultConstants.StructuralDtwMesaResponsePoints"/> frequencies over [0, π), and the period of the strongest peak above the
    /// low-frequency cut. The result is clamped to [min period, n / divisor].
    /// </summary>
    public static int EstimateDominantPeriod(ReadOnlySpan<double> midPrices)
    {
        int n = midPrices.Length;
        int order = IndicatorDefaultConstants.StructuralDtwMesaOrder;
        int points = IndicatorDefaultConstants.StructuralDtwMesaResponsePoints;
        int windowLength = Math.Min(IndicatorDefaultConstants.StructuralDtwMesaWindow, n / 2);
        if (windowLength <= order)
        {
            throw new ArgumentException($"At least {2 * (order + 1)} mid prices are required for a Burg model of order {order}.", nameof(midPrices));
        }

        var data = new double[windowLength];
        double mean = 0.0;
        for (int i = 0; i < windowLength; i++)
        {
            data[i] = midPrices[n - windowLength + i];
            mean += data[i];
        }
        mean /= Math.Max(windowLength, 1);
        for (int i = 0; i < windowLength; i++)
        {
            data[i] -= mean;
        }

        double[] coefficients = BurgCoefficients(data, order);

        int minIndex = Math.Max((int)(points * IndicatorDefaultConstants.StructuralDtwMesaMinFrequencyFraction), 1);
        double step = Math.PI / points;
        int peakIndex = -1;
        double peakPower = double.NegativeInfinity;
        for (int k = minIndex; k < points; k++)
        {
            double w = k * step;
            double re = 1.0;
            double im = 0.0;
            for (int i = 1; i <= order; i++)
            {
                re += coefficients[i - 1] * Math.Cos(w * i);
                im -= coefficients[i - 1] * Math.Sin(w * i);
            }

            double power = 1.0 / (re * re + im * im);
            if (power > peakPower)
            {
                peakPower = power;
                peakIndex = k;
            }
        }

        double peakFrequency = peakIndex >= 0 ? peakIndex * step / (2 * Math.PI) : 0.0;
        int period = peakFrequency > 0 ? (int)(1.0 / peakFrequency) : IndicatorDefaultConstants.StructuralDtwFallbackPeriod;
        return Math.Max(IndicatorDefaultConstants.StructuralDtwMinDominantPeriod,
            Math.Min(period, n / IndicatorDefaultConstants.StructuralDtwMaxDominantPeriodDivisor));
    }

    /// <summary>Burg reflection recursion; returns the order-<paramref name="order"/> prediction-error filter coefficients a[1..order] (a[0] = 1 implied).</summary>
    private static double[] BurgCoefficients(double[] data, int order)
    {
        int length = data.Length;
        var a = new double[order + 1, order + 1];
        var forward = (double[])data.Clone();
        var backward = (double[])data.Clone();

        for (int m = 1; m <= order; m++)
        {
            double numerator = 0.0;
            double denominator = 0.0;
            for (int t = m; t < length; t++)
            {
                double f = forward[t];
                double b = backward[t - 1];
                numerator += f * b;
                denominator += f * f + b * b;
            }

            double reflection = denominator != 0 ? -2.0 * numerator / denominator : 0.0;
            a[m, m] = reflection;
            for (int k = 1; k < m; k++)
            {
                a[m, k] = a[m - 1, k] + reflection * a[m - 1, m - k];
            }

            // Both updates read the old values of the other vector, so compute them before writing.
            int count = length - m;
            var newForward = new double[count];
            var newBackward = new double[count];
            for (int i = 0; i < count; i++)
            {
                newForward[i] = forward[m + i] + reflection * backward[m - 1 + i];
                newBackward[i] = backward[m - 1 + i] + reflection * forward[m + i];
            }

            for (int i = 0; i < count; i++)
            {
                forward[m + i] = newForward[i];
                backward[m - 1 + i] = newBackward[i];
            }
        }

        var coefficients = new double[order];
        for (int i = 1; i <= order; i++)
        {
            coefficients[i - 1] = a[order, i];
        }

        return coefficients;
    }

    /// <summary>
    /// Fallback volatility (percent per bar) when no model-based estimate is usable: the rolling population standard deviation of the log returns over
    /// <c>min(window, n / divisor)</c> bars, aligned one-to-one with <paramref name="closes"/> (the first value is repeated).
    /// </summary>
    public static double[] RollingStdVolatility(ReadOnlySpan<double> closes)
    {
        int n = closes.Length;
        var volatility = new double[n];
        if (n < 2)
        {
            return volatility;
        }

        var returns = new double[n - 1];
        for (int i = 1; i < n; i++)
        {
            returns[i - 1] = IndicatorDefaultConstants.StructuralDtwPercentFactor
                * (Math.Log(closes[i] + IndicatorDefaultConstants.StructuralDtwLogPriceEpsilon) - Math.Log(closes[i - 1] + IndicatorDefaultConstants.StructuralDtwLogPriceEpsilon));
        }

        int window = Math.Min(IndicatorDefaultConstants.StructuralDtwFallbackVolatilityWindow, n / IndicatorDefaultConstants.StructuralDtwFallbackVolatilityWindowDivisor);
        var rolling = new double[returns.Length];
        for (int i = 1; i < returns.Length; i++)
        {
            int from = Math.Max(0, i - window);
            rolling[i] = PopulationStandardDeviation(returns.AsSpan(from, i + 1 - from));
        }

        volatility[0] = rolling[0];
        for (int k = 1; k < n; k++)
        {
            volatility[k] = rolling[k - 1];
        }

        return volatility;
    }

    /// <summary>
    /// Finds the most similar earlier segments of <paramref name="closes"/> to its last <c>dominant period</c> bars.
    /// </summary>
    /// <param name="closes">Close prices, oldest first.</param>
    /// <param name="midPrices">(High + Low) / 2 per bar (the cycle estimate input); same length as <paramref name="closes"/>.</param>
    /// <param name="volatility">Conditional volatility per bar; same length as <paramref name="closes"/>, all finite.</param>
    /// <param name="topK">Number of matches returned (best first).</param>
    /// <param name="threshold">Minimum probability, exp(−distance), for a candidate.</param>
    /// <param name="futureSteps">Bars after each matched segment reported as its future path (percent change from the match end); negative counts as 0.</param>
    /// <param name="warpingRadius">Sakoe-Chiba half-width; negative = unconstrained.</param>
    public static StructuralDtwResult Calculate(
        ReadOnlySpan<double> closes,
        ReadOnlySpan<double> midPrices,
        ReadOnlySpan<double> volatility,
        int topK,
        double threshold,
        int futureSteps,
        int warpingRadius)
    {
        int n = closes.Length;
        if (n < IndicatorDefaultConstants.StructuralDtwMinCandles)
        {
            return StructuralDtwResult.Failure($"Insufficient data: need >= {IndicatorDefaultConstants.StructuralDtwMinCandles} candles, got {n}");
        }

        if (midPrices.Length != n || volatility.Length != n)
        {
            return StructuralDtwResult.Failure("Price and volatility series must have the same length.");
        }

        futureSteps = Math.Max(futureSteps, 0);
        int dominantPeriod = EstimateDominantPeriod(midPrices);
        int window = dominantPeriod;
        int queryStart = n - window;
        if (queryStart < 0)
        {
            return StructuralDtwResult.Failure("Query window exceeds data length");
        }

        int radius = warpingRadius >= 0 ? warpingRadius : DtwMath.UnconstrainedRadius;
        var queryZ = new double[window];
        NormalizeSegment(closes.Slice(queryStart, window), queryZ, flatSigmaReplacement: 1.0);
        double queryVolatility = Mean(volatility.Slice(queryStart));

        var segmentZ = new double[window];
        var candidates = new List<(double Distance, SimilarPatternResult Pattern)>();
        int stride = Math.Max(1, window / IndicatorDefaultConstants.StructuralDtwStepDivisor);
        double sqrtWindow = Math.Sqrt(window);

        for (int start = 0; start < queryStart - window - futureSteps; start += stride)
        {
            int end = start + window;
            if (!NormalizeSegment(closes.Slice(start, window), segmentZ, flatSigmaReplacement: null))
            {
                continue;
            }

            double rmse = DtwMath.Calculate(queryZ, segmentZ, radius) / sqrtWindow;
            double segmentVolatility = Mean(volatility.Slice(start, window));
            double ratio = Math.Max(queryVolatility, segmentVolatility)
                / Math.Max(Math.Min(queryVolatility, segmentVolatility), IndicatorDefaultConstants.StructuralDtwVolatilityFloor);
            // ratio >= 1, so log(ratio) is the former log1p(ratio - 1): 0 for identical regimes, growing with the divergence.
            double structuralRmse = rmse + Math.Log(ratio) * IndicatorDefaultConstants.StructuralDtwVolatilityPenaltyWeight;
            double probability = Math.Exp(-structuralRmse);
            if (!(probability >= threshold))
            {
                continue;
            }

            int futureEnd = Math.Min(end + futureSteps, n);
            if (futureEnd <= end)
            {
                continue;
            }

            double basePrice = closes[end - 1];
            var futurePath = new double[futureEnd - end];
            for (int i = 0; i < futurePath.Length; i++)
            {
                futurePath[i] = Round((closes[end + i] / basePrice - 1.0) * IndicatorDefaultConstants.StructuralDtwPercentFactor);
            }

            double distance = Round(structuralRmse);
            candidates.Add((distance, new SimilarPatternResult
            {
                Distance = distance,
                Probability = Round(probability),
                StartIndex = start,
                EndIndex = end - 1,
                FuturePath = futurePath
            }));
        }

        // Stable ordering on the rounded distance keeps ties in chronological order, as the Python sort did.
        var matches = candidates.OrderBy(c => c.Distance).Take(Math.Max(topK, 0)).Select(c => c.Pattern).ToList();
        return StructuralDtwResult.Success(dominantPeriod, window, Round(queryVolatility), matches);
    }

    private static double Round(double value) => Math.Round(value, IndicatorDefaultConstants.StructuralDtwResultDecimals, MidpointRounding.ToEven);

    private static double Mean(ReadOnlySpan<double> values)
    {
        double sum = 0.0;
        for (int i = 0; i < values.Length; i++)
        {
            sum += values[i];
        }

        return sum / values.Length;
    }

    private static double PopulationStandardDeviation(ReadOnlySpan<double> values)
    {
        double mean = Mean(values);
        double squares = 0.0;
        for (int i = 0; i < values.Length; i++)
        {
            double d = values[i] - mean;
            squares += d * d;
        }

        return Math.Sqrt(squares / values.Length);
    }

    /// <summary>
    /// Z-normalizes with the population sigma. A flat segment (sigma at or below the floor) either uses <paramref name="flatSigmaReplacement"/> as its
    /// sigma (the query) or is rejected with false (<c>null</c>).
    /// </summary>
    private static bool NormalizeSegment(ReadOnlySpan<double> input, Span<double> output, double? flatSigmaReplacement)
    {
        double mean = Mean(input);
        double sigma = PopulationStandardDeviation(input);
        if (sigma < IndicatorDefaultConstants.StructuralDtwSigmaFloor)
        {
            if (flatSigmaReplacement is null)
            {
                return false;
            }

            sigma = flatSigmaReplacement.Value;
        }

        for (int i = 0; i < input.Length; i++)
        {
            output[i] = (input[i] - mean) / sigma;
        }

        return true;
    }
}
