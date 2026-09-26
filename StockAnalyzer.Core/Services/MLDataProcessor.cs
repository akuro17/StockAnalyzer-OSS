using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Service for processing and normalizing market data for machine learning models.
/// </summary>
public class MLDataProcessor : IMLDataProcessor
{
    private readonly ILogger<MLDataProcessor> _logger;
    private const int FeaturesPerCandle = 5;
    private const float Epsilon = IMLDataProcessor.Epsilon;
    private const float SoftmaxSumTolerance = IMLDataProcessor.SoftmaxSumTolerance;
    private const int MaxStackAllocWindowSize = IMLDataProcessor.MaxStackAllocWindowSize;

    public MLDataProcessor(ILogger<MLDataProcessor>? logger = null)
    {
        _logger = logger ?? NullLogger<MLDataProcessor>.Instance;
    }

    public float[] NormalizeCandles(IReadOnlyList<CandleData> candles)
    {
        if (candles == null || candles.Count == 0)
        {
            _logger.LogWarning("Normalization skipped: Candle list is null or empty.");
            return Array.Empty<float>();
        }

        var features = new float[candles.Count * FeaturesPerCandle];
        NormalizeCandles(candles, 0, candles.Count, features);
        return features;
    }

    public void NormalizeCandles(IReadOnlyList<CandleData> candles, Span<float> destination)
    {
        if (candles == null) return;
        NormalizeCandles(candles, 0, candles.Count, destination);
    }

    public void NormalizeCandles(IReadOnlyList<CandleData> candles, int startIndex, int count, Span<float> destination)
    {
        if (candles == null || count <= 0) return;

        if (startIndex < 0 || startIndex + count > candles.Count)
        {
            _logger.LogError("Invalid range: startIndex {Start} and count {Count} are out of bounds for candle list of size {Size}.", startIndex, count, candles.Count);
            return;
        }

        if (destination.Length < count * FeaturesPerCandle)
        {
            _logger.LogError("Buffer overflow: Destination span (size {SpanSize}) is too small for {CandleCount} candles.", destination.Length, count);
            return;
        }

        // Find min/max for normalization using for loop to avoid LINQ enumerator allocations.
        // Statistics accumulate in double to mirror the Python float64 reference pipeline
        // (dataset.normalize_ohlcv_minmax); only the value written to the float span is narrowed.
        double minPrice = double.MaxValue;
        double maxPrice = double.MinValue;
        double minVolLog = double.MaxValue;
        double maxVolLog = double.MinValue;

        Span<double> volLogs = count <= MaxStackAllocWindowSize ? stackalloc double[count] : new double[count];
        for (int i = 0; i < count; i++)
        {
            var c = candles[startIndex + i];
            if ((double)c.Low < minPrice) minPrice = (double)c.Low;
            if ((double)c.High > maxPrice) maxPrice = (double)c.High;

            decimal clampedVolume = Math.Max(0m, c.Volume);
            double volLog = Math.Log(1.0 + (double)clampedVolume);
            volLogs[i] = volLog;
            if (volLog < minVolLog) minVolLog = volLog;
            if (volLog > maxVolLog) maxVolLog = volLog;
        }

        double priceRange = maxPrice - minPrice;
        double volRange = maxVolLog - minVolLog;

        for (int i = 0; i < count; i++)
        {
            var c = candles[startIndex + i];
            int offset = i * FeaturesPerCandle;

            destination[offset + 0] = NormalizePrice((double)c.Open, minPrice, priceRange);
            destination[offset + 1] = NormalizePrice((double)c.High, minPrice, priceRange);
            destination[offset + 2] = NormalizePrice((double)c.Low, minPrice, priceRange);
            destination[offset + 3] = NormalizePrice((double)c.Close, minPrice, priceRange);
            destination[offset + 4] = volRange <= Epsilon
                ? 0.0f
                : (float)Math.Clamp((volLogs[i] - minVolLog) / volRange, 0.0, 1.0);
        }
    }

    private static float NormalizePrice(double value, double min, double range)
    {
        return range <= Epsilon
            ? 0.5f
            : (float)Math.Clamp((value - min) / range, 0.0, 1.0);
    }

    public void ComputeLogReturns(IReadOnlyList<CandleData> candles, int startIndex, int count, Span<float> destination)
    {
        if (candles == null || count <= 0) return;

        if (startIndex < 0 || startIndex + count > candles.Count)
        {
            _logger.LogError("Invalid range: startIndex {Start} and count {Count} are out of bounds for candle list of size {Size}.", startIndex, count, candles.Count);
            return;
        }

        if (destination.Length < count)
        {
            _logger.LogError("Buffer overflow: Destination span (size {SpanSize}) is too small for {CandleCount} log returns.", destination.Length, count);
            return;
        }

        for (int i = 0; i < count; i++)
        {
            if (i == 0)
            {
                destination[i] = 0.0f;
                continue;
            }

            decimal cPrev = candles[startIndex + i - 1].Close;
            decimal cCurr = candles[startIndex + i].Close;
            destination[i] = (cPrev <= 0m || cCurr <= 0m)
                ? 0.0f
                : (float)Math.Log((double)(cCurr / cPrev));
        }
    }

    private const int LogReturnOhlcChannels = 4;

    public void ComputeLogReturnsOhlc(IReadOnlyList<CandleData> candles, int startIndex, int count, Span<float> destination)
    {
        if (candles == null || count <= 0) return;

        if (startIndex < 0 || startIndex + count > candles.Count)
        {
            _logger.LogError("Invalid range: startIndex {Start} and count {Count} are out of bounds for candle list of size {Size}.", startIndex, count, candles.Count);
            return;
        }

        if (destination.Length < count * LogReturnOhlcChannels)
        {
            _logger.LogError("Buffer overflow: Destination span (size {SpanSize}) is too small for {CandleCount} intrabar log-return bars.", destination.Length, count);
            return;
        }

        for (int i = 0; i < count; i++)
        {
            var cur = candles[startIndex + i];
            decimal open = cur.Open;
            int offset = i * LogReturnOhlcChannels;

            if (i == 0)
            {
                destination[offset + 0] = 0.0f;
            }
            else
            {
                decimal prevClose = candles[startIndex + i - 1].Close;
                destination[offset + 0] = (prevClose <= 0m || open <= 0m)
                    ? 0.0f
                    : (float)Math.Log((double)(open / prevClose));
            }

            destination[offset + 1] = (open <= 0m || cur.High <= 0m)
                ? 0.0f
                : (float)Math.Log((double)(cur.High / open));
            destination[offset + 2] = (open <= 0m || cur.Low <= 0m)
                ? 0.0f
                : (float)Math.Log((double)(cur.Low / open));
            destination[offset + 3] = (open <= 0m || cur.Close <= 0m)
                ? 0.0f
                : (float)Math.Log((double)(cur.Close / open));
        }
    }

    public void ComputeZScore(ReadOnlySpan<float> values, Span<float> destination)
    {
        int n = values.Length;
        if (n == 0 || destination.Length < n) return;

        Span<double> widened = n <= MaxStackAllocWindowSize ? stackalloc double[n] : new double[n];
        for (int i = 0; i < n; i++) widened[i] = values[i];
        ZScoreInto(widened, destination);
    }

    /// <summary>
    /// Population Z-Score of <paramref name="values"/> into <paramref name="destination"/>,
    /// accumulating mean and standard deviation in double (matching the Python float64
    /// reference) and narrowing only on the final write. A standard deviation at or below
    /// <see cref="Epsilon"/> yields <c>0.0f</c>.
    /// </summary>
    private static void ZScoreInto(ReadOnlySpan<double> values, Span<float> destination)
    {
        int n = values.Length;
        double sum = 0.0;
        for (int i = 0; i < n; i++) sum += values[i];
        double mu = sum / n;

        double sumSq = 0.0;
        for (int i = 0; i < n; i++)
        {
            double diff = values[i] - mu;
            sumSq += diff * diff;
        }
        double sigma = Math.Sqrt(sumSq / n);

        for (int i = 0; i < n; i++)
        {
            destination[i] = sigma <= Epsilon ? 0.0f : (float)((values[i] - mu) / sigma);
        }
    }

    public void ComputeJointZScoreOhlcv(IReadOnlyList<CandleData> candles, int startIndex, int count, Span<float> destination)
    {
        if (candles == null || count <= 0) return;

        if (startIndex < 0 || startIndex + count > candles.Count)
        {
            _logger.LogError("Invalid range: startIndex {Start} and count {Count} are out of bounds for candle list of size {Size}.", startIndex, count, candles.Count);
            return;
        }

        if (destination.Length < count * FeaturesPerCandle)
        {
            _logger.LogError("Buffer overflow: Destination span (size {SpanSize}) is too small for {CandleCount} candles.", destination.Length, count);
            return;
        }

        // Pooled mean/std over the four O/H/L/C price channels across the whole window.
        // Accumulated in double to mirror the Python float64 reference
        // (dataset.zscore_joint_standardized); only the final span write is narrowed to float.
        int priceCount = count * 4;
        double priceSum = 0.0;
        for (int i = 0; i < count; i++)
        {
            var c = candles[startIndex + i];
            priceSum += (double)c.Open + (double)c.High + (double)c.Low + (double)c.Close;
        }
        double priceMean = priceSum / priceCount;

        double priceSumSq = 0.0;
        for (int i = 0; i < count; i++)
        {
            var c = candles[startIndex + i];
            priceSumSq += Squared((double)c.Open - priceMean)
                        + Squared((double)c.High - priceMean)
                        + Squared((double)c.Low - priceMean)
                        + Squared((double)c.Close - priceMean);
        }
        double priceSigma = Math.Sqrt(priceSumSq / priceCount);
        bool priceDegenerate = priceSigma <= Epsilon;

        // Volume standardized on its own.
        double volSum = 0.0;
        for (int i = 0; i < count; i++)
        {
            volSum += (double)candles[startIndex + i].Volume;
        }
        double volMean = volSum / count;

        double volSumSq = 0.0;
        for (int i = 0; i < count; i++)
        {
            volSumSq += Squared((double)candles[startIndex + i].Volume - volMean);
        }
        double volSigma = Math.Sqrt(volSumSq / count);
        bool volDegenerate = volSigma <= Epsilon;

        for (int i = 0; i < count; i++)
        {
            var c = candles[startIndex + i];
            int offset = i * FeaturesPerCandle;
            destination[offset + 0] = priceDegenerate ? 0.0f : (float)(((double)c.Open - priceMean) / priceSigma);
            destination[offset + 1] = priceDegenerate ? 0.0f : (float)(((double)c.High - priceMean) / priceSigma);
            destination[offset + 2] = priceDegenerate ? 0.0f : (float)(((double)c.Low - priceMean) / priceSigma);
            destination[offset + 3] = priceDegenerate ? 0.0f : (float)(((double)c.Close - priceMean) / priceSigma);
            destination[offset + 4] = volDegenerate ? 0.0f : (float)(((double)c.Volume - volMean) / volSigma);
        }
    }

    private static double Squared(double value) => value * value;

    public void NormalizeZScoreOhlcv(IReadOnlyList<CandleData> candles, int startIndex, int count, Span<float> destination)
    {
        if (candles == null || count <= 0) return;

        if (startIndex < 0 || startIndex + count > candles.Count)
        {
            _logger.LogError("Invalid range: startIndex {Start} and count {Count} are out of bounds for candle list of size {Size}.", startIndex, count, candles.Count);
            return;
        }

        if (destination.Length < count * FeaturesPerCandle)
        {
            _logger.LogError("Buffer overflow: Destination span (size {SpanSize}) is too small for {CandleCount} candles.", destination.Length, count);
            return;
        }

        // Per-channel statistics accumulate in double (mirroring dataset.zscore_standardized);
        // only the values written to the float destination span are narrowed.
        Span<double> channel = count <= MaxStackAllocWindowSize ? stackalloc double[count] : new double[count];
        Span<float> zChannel = count <= MaxStackAllocWindowSize ? stackalloc float[count] : new float[count];

        for (int feature = 0; feature < FeaturesPerCandle; feature++)
        {
            for (int i = 0; i < count; i++)
            {
                var c = candles[startIndex + i];
                channel[i] = feature switch
                {
                    0 => (double)c.Open,
                    1 => (double)c.High,
                    2 => (double)c.Low,
                    3 => (double)c.Close,
                    _ => (double)c.Volume,
                };
            }

            ZScoreInto(channel, zChannel);

            for (int i = 0; i < count; i++)
            {
                destination[i * FeaturesPerCandle + feature] = zChannel[i];
            }
        }
    }

    public void ComputeSoftmax(ReadOnlySpan<float> logits, Span<float> probabilities)
    {
        int k = logits.Length;
        if (k == 0 || probabilities.Length < k) return;

        float max = float.MinValue;
        for (int i = 0; i < k; i++)
        {
            if (logits[i] > max) max = logits[i];
        }

        float sumExp = 0f;
        for (int i = 0; i < k; i++)
        {
            sumExp += MathF.Exp(logits[i] - max);
        }

        if (sumExp <= Epsilon || float.IsNaN(sumExp))
        {
            float uniform = 1.0f / k;
            for (int i = 0; i < k; i++) probabilities[i] = uniform;
            return;
        }

        for (int i = 0; i < k; i++)
        {
            probabilities[i] = MathF.Exp(logits[i] - max) / sumExp;
        }
    }

    public (float Confidence, float Entropy) ComputeConfidenceAndEntropy(ReadOnlySpan<float> probabilities)
    {
        int k = probabilities.Length;
        if (k == 0) return (0.0f, 0.0f);

        float confidence = float.MinValue;
        float entropy = 0.0f;
        for (int i = 0; i < k; i++)
        {
            float p = probabilities[i];
            if (p > confidence) confidence = p;
            if (p > Epsilon) entropy -= p * MathF.Log(p);
        }

        return (confidence, entropy);
    }
}
