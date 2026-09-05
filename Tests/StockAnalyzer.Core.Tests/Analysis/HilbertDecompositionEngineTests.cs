using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

public class HilbertDecompositionEngineTests
{
    private static decimal[] GenerateSinePrices(int count, double period, double amplitude = 10.0, double basePrice = 100.0)
    {
        var prices = new decimal[count];
        for (int i = 0; i < count; i++)
        {
            double angle = (2.0 * Math.PI * i) / period;
            prices[i] = (decimal)(basePrice + amplitude * Math.Sin(angle));
        }
        return prices;
    }

    [Fact]
    public void Decompose_EmptyPrices_ReturnsEmptyResult()
    {
        var result = HilbertDecompositionEngine.Decompose(ReadOnlySpan<decimal>.Empty);
        Assert.Equal(0, result.Count);
        Assert.Empty(result.Samples);
    }

    [Fact]
    public void Decompose_SinglePrice_ReturnsWarmupResult()
    {
        var prices = new decimal[] { 100m };
        var result = HilbertDecompositionEngine.Decompose(prices);

        Assert.Equal(1, result.Count);
        Assert.True(result[0].IsWarmup);
        Assert.False(result[0].IsValid);
    }

    [Fact]
    public void Decompose_PureSineWave20Bar_DetectsDominantCycleNear20()
    {
        var prices = GenerateSinePrices(150, period: 20.0);
        var result = HilbertDecompositionEngine.Decompose(prices);

        Assert.Equal(150, result.Count);

        // Warmup period (first 50 bars)
        for (int i = 0; i < 50; i++)
        {
            Assert.True(result[i].IsWarmup);
        }

        // Steady-state period (bars 70 to 140) should be near 20 bars
        for (int i = 70; i < 140; i++)
        {
            Assert.False(result[i].IsWarmup);
            Assert.True(result[i].IsValid);
            Assert.InRange(result[i].DominantCycle, 17.0m, 23.0m);
        }
    }

    [Fact]
    public void Decompose_PureSineWave30Bar_DetectsDominantCycleNear30()
    {
        var prices = GenerateSinePrices(180, period: 30.0);
        var result = HilbertDecompositionEngine.Decompose(prices);

        Assert.Equal(180, result.Count);
        for (int i = 80; i < 160; i++)
        {
            Assert.False(result[i].IsWarmup);
            Assert.InRange(result[i].DominantCycle, 26.0m, 34.0m);
        }
    }

    [Fact]
    public void Decompose_ScaleAndOffsetInvariance_DominantCycleMatches()
    {
        var basePrices = GenerateSinePrices(120, period: 20.0, amplitude: 10.0, basePrice: 100.0);
        var scaledPrices = GenerateSinePrices(120, period: 20.0, amplitude: 100.0, basePrice: 1000.0);
        var offsetPrices = GenerateSinePrices(120, period: 20.0, amplitude: 10.0, basePrice: 5000.0);

        var baseResult = HilbertDecompositionEngine.Decompose(basePrices);
        var scaledResult = HilbertDecompositionEngine.Decompose(scaledPrices);
        var offsetResult = HilbertDecompositionEngine.Decompose(offsetPrices);

        for (int i = 60; i < 120; i++)
        {
            Assert.Equal(baseResult[i].DominantCycle, scaledResult[i].DominantCycle, 1);
            Assert.Equal(baseResult[i].DominantCycle, offsetResult[i].DominantCycle, 1);
        }
    }

    [Fact]
    public void Decompose_FlatLine_MicroAmplitudeTriggersGuard()
    {
        var prices = new decimal[100];
        Array.Fill(prices, 100m);

        var result = HilbertDecompositionEngine.Decompose(prices);

        Assert.Equal(100, result.Count);
        // After detrender kicks in on flat series, amplitude collapses toward 0
        for (int i = 20; i < 100; i++)
        {
            Assert.True(result[i].Amplitude < 1e-6m);
        }
    }

    [Fact]
    public void Decompose_InvalidParameters_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HilbertDecompositionParameters { MinPeriod = 50, MaxPeriod = 20 }.Validate());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HilbertDecompositionParameters { MinPeriod = 1 }.Validate());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HilbertDecompositionParameters { SmoothBeta = 0m }.Validate());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HilbertDecompositionParameters { DeltaLimit = 0m }.Validate());
    }

    [Fact]
    public void Decompose_PhaseUnwrap_ContinuousProgression()
    {
        var prices = GenerateSinePrices(120, period: 20.0);
        var result = HilbertDecompositionEngine.Decompose(prices);

        // Phase should continuously advance for a clean forward sine wave
        for (int i = 55; i < 115; i++)
        {
            Assert.True(result[i].UnwrappedPhaseDeg >= result[i - 1].UnwrappedPhaseDeg - 1.0,
                $"Unwrapped phase must advance forward at index {i}.");
        }
    }

    [Fact]
    public void Decompose_DominantCycleMatchesCoreHilbertTransformIndicator()
    {
        var prices = GenerateSinePrices(120, period: 20.0);
        var engineResult = HilbertDecompositionEngine.Decompose(prices);

        var indicator = new CoreHilbertTransformIndicator();
        var series = Array.ConvertAll(prices, p => (decimal?)p);
        var indicatorResult = indicator.CalculateSeries(series);

        Assert.Equal(prices.Length, engineResult.Count);
        Assert.Equal(prices.Length, indicatorResult.MainValues.Count);

        for (int i = 50; i < prices.Length; i++)
        {
            if (indicatorResult.MainValues[i].HasValue)
            {
                Assert.Equal(indicatorResult.MainValues[i]!.Value, engineResult[i].DominantCycle, 2);
            }
        }
    }
}
