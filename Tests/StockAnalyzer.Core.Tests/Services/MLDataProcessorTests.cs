using System;
using System.Collections.Generic;
using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Tests.Services;

[Collection("Non-Parallel ONNX Tests")]
public class MLDataProcessorTests
{
    private readonly MLDataProcessor _processor = new();

    private static CandleData Candle(decimal open, decimal high, decimal low, decimal close, long volume, int daysOffset = 0)
        => new(new DateTime(2024, 1, 1).AddDays(daysOffset), open, high, low, close, volume);

    [Fact]
    public void NormalizeCandles_FlatlinePriceRange_ReturnsExactly0Point5f()
    {
        var candles = new List<CandleData>
        {
            Candle(100m, 100m, 100m, 100m, 1000, 0),
            Candle(100m, 100m, 100m, 100m, 1000, 1),
        };

        var destination = new float[candles.Count * 5];
        _processor.NormalizeCandles(candles, destination);

        for (int i = 0; i < candles.Count; i++)
        {
            int offset = i * 5;
            Assert.Equal(0.5f, destination[offset + 0]);
            Assert.Equal(0.5f, destination[offset + 1]);
            Assert.Equal(0.5f, destination[offset + 2]);
            Assert.Equal(0.5f, destination[offset + 3]);
        }
    }

    [Fact]
    public void NormalizeCandles_ZeroVolumeAcrossWindow_ReturnsExactly0f()
    {
        var candles = new List<CandleData>
        {
            Candle(100m, 110m, 90m, 105m, 0, 0),
            Candle(100m, 110m, 90m, 105m, 0, 1),
        };

        var destination = new float[candles.Count * 5];
        _processor.NormalizeCandles(candles, destination);

        Assert.Equal(0.0f, destination[4]);
        Assert.Equal(0.0f, destination[9]);
    }

    [Fact]
    public void ComputeZScore_ZeroVariance_ReturnsExactly0f()
    {
        Span<float> values = stackalloc float[] { 5f, 5f, 5f, 5f };
        Span<float> destination = stackalloc float[4];

        _processor.ComputeZScore(values, destination);

        foreach (var z in destination)
        {
            Assert.Equal(0.0f, z);
        }
    }

    [Fact]
    public void ComputeLogReturns_FirstBarAndNonPositiveClose_YieldZero()
    {
        var candles = new List<CandleData>
        {
            Candle(100m, 100m, 100m, 100m, 1000, 0),
            Candle(100m, 100m, 100m, 0m, 1000, 1), // non-positive close
            Candle(100m, 100m, 100m, 110m, 1000, 2),
        };

        var destination = new float[3];
        _processor.ComputeLogReturns(candles, 0, 3, destination);

        Assert.Equal(0.0f, destination[0]); // first bar
        Assert.Equal(0.0f, destination[1]); // c_curr <= 0
    }

    [Fact]
    public void ComputeSoftmax_LargeLogits_DoesNotOverflowAndSumsToOne()
    {
        Span<float> logits = stackalloc float[] { 1000f, -1000f, 0f };
        Span<float> probabilities = stackalloc float[3];

        _processor.ComputeSoftmax(logits, probabilities);

        float sum = probabilities[0] + probabilities[1] + probabilities[2];
        Assert.True(MathF.Abs(1.0f - sum) <= 1e-4f);
        Assert.False(float.IsNaN(sum));
        Assert.True(probabilities[0] > probabilities[1]);
        Assert.True(probabilities[0] > probabilities[2]);
    }

    [Fact]
    public void ComputeConfidenceAndEntropy_ZeroProbabilityElements_DoNotProduceNaN()
    {
        Span<float> probabilities = stackalloc float[] { 1.0f, 0.0f, 0.0f };

        var (confidence, entropy) = _processor.ComputeConfidenceAndEntropy(probabilities);

        Assert.Equal(1.0f, confidence);
        Assert.Equal(0.0f, entropy);
        Assert.False(float.IsNaN(entropy));
    }
}
