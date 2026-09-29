using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Interface for processing and normalizing market data for machine learning models.
/// </summary>
public interface IMLDataProcessor
{
    /// <summary>
    /// Numerical stability epsilon shared by all boundary-guarded formulas.
    /// </summary>
    const float Epsilon = 1e-7f;

    /// <summary>
    /// Absolute tolerance for deciding whether raw model output already sums to 1.0 (i.e. is a probability distribution).
    /// </summary>
    const float SoftmaxSumTolerance = 1e-4f;

    /// <summary>
    /// Maximum window/element count eligible for stackalloc'd scratch buffers before falling back
    /// to a heap array, chosen to keep worst-case stack usage per call well within default thread stack limits.
    /// Shared by <see cref="MLDataProcessor"/> and <see cref="PredictionService"/> to avoid duplicated thresholds.
    /// </summary>
    const int MaxStackAllocWindowSize = 256;

    /// <summary>
    /// Normalizes candle data into a flat float array suitable for tensor input.
    /// Performs min-max normalization on price (OHLC) and volume.
    /// </summary>
    /// <param name="candles">The candles to normalize.</param>
    /// <returns>A flat float array with 5 features (OHLCV) per candle.</returns>
    float[] NormalizeCandles(IReadOnlyList<CandleData> candles);

    /// <summary>
    /// Normalizes candle data into the provided destination span.
    /// </summary>
    /// <param name="candles">The candles to normalize.</param>
    /// <param name="destination">The destination span for normalized features.</param>
    void NormalizeCandles(IReadOnlyList<CandleData> candles, Span<float> destination);

    /// <summary>
    /// Normalizes a specific range of candle data into the provided destination span.
    /// This avoids unnecessary list slicing or copying.
    /// </summary>
    /// <param name="candles">The source candles.</param>
    /// <param name="startIndex">The starting index in the source list.</param>
    /// <param name="count">The number of candles to process.</param>
    /// <param name="destination">The destination span for normalized features.</param>
    void NormalizeCandles(IReadOnlyList<CandleData> candles, int startIndex, int count, Span<float> destination);

    /// <summary>
    /// Applies numerically stable softmax to raw logits, writing the resulting probability
    /// distribution into <paramref name="probabilities"/>. Falls back to a uniform distribution
    /// when the exponential sum underflows or is not finite.
    /// </summary>
    void ComputeSoftmax(ReadOnlySpan<float> logits, Span<float> probabilities);

    /// <summary>
    /// Computes the maximum-probability confidence and Shannon entropy of a probability distribution,
    /// excluding near-zero probabilities from the entropy sum to avoid log(0).
    /// </summary>
    (float Confidence, float Entropy) ComputeConfidenceAndEntropy(ReadOnlySpan<float> probabilities);

    /// <summary>
    /// Computes per-bar logarithmic close-to-close returns for a windowed range of candles.
    /// The first bar in the window and any non-positive close price yield 0.0f.
    /// </summary>
    void ComputeLogReturns(IReadOnlyList<CandleData> candles, int startIndex, int count, Span<float> destination);

    /// <summary>
    /// Computes four intrabar OHLC log-return channels per bar, written bar-major as
    /// [gap, high/open, low/open, close/open]: <c>ln(Open_i / Close_{i-1})</c> (0.0f on the
    /// first bar), <c>ln(High_i / Open_i)</c>, <c>ln(Low_i / Open_i)</c>,
    /// <c>ln(Close_i / Open_i)</c>. Any channel with a non-positive numerator or denominator
    /// yields 0.0f.
    /// </summary>
    void ComputeLogReturnsOhlc(IReadOnlyList<CandleData> candles, int startIndex, int count, Span<float> destination);

    /// <summary>
    /// Computes the population Z-Score standardization of <paramref name="values"/>.
    /// When the population standard deviation is at or below <see cref="Epsilon"/>, outputs 0.0f.
    /// </summary>
    void ComputeZScore(ReadOnlySpan<float> values, Span<float> destination);

    /// <summary>
    /// Standardizes a windowed candle range with a single pooled mean/std over all four
    /// O/H/L/C price channels at once (Volume standardized separately), then writes the
    /// five OHLCV channels interleaved per bar. A pooled affine transform is monotonic,
    /// so candle geometry is preserved (unlike per-channel <see cref="ComputeZScore"/>).
    /// A pooled price std or a volume std at or below <see cref="Epsilon"/> yields 0.0f
    /// for that block.
    /// </summary>
    void ComputeJointZScoreOhlcv(IReadOnlyList<CandleData> candles, int startIndex, int count, Span<float> destination);

    /// <summary>
    /// Standardizes a windowed candle range with an independent population Z-Score per OHLCV
    /// channel (Open, High, Low, Close, Volume each standardized across the window on its own),
    /// then writes the five channels interleaved per bar. A channel whose standard deviation is
    /// at or below <see cref="Epsilon"/> yields 0.0f for that channel. Unlike
    /// <see cref="ComputeJointZScoreOhlcv"/>, per-channel scaling does not preserve candle geometry.
    /// </summary>
    void NormalizeZScoreOhlcv(IReadOnlyList<CandleData> candles, int startIndex, int count, Span<float> destination);
}
