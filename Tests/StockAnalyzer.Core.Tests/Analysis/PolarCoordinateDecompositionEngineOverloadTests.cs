using System;
using StockAnalyzer.Core.Analysis;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

/// <summary>
/// The price-taking <c>Decompose(prices, parameters)</c> entry point is a thin wrapper over the core
/// <c>Decompose(HilbertDecompositionResult)</c> overload. These tests prove the two entry points are
/// observationally identical and that the wrapper still validates parameters.
/// </summary>
public class PolarCoordinateDecompositionEngineOverloadTests
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
    public void BothEntryPoints_ProduceIdenticalSamples()
    {
        var prices = GenerateSinePrices(200, period: 20.0);
        var parameters = new HilbertDecompositionParameters();

        var viaPrices = PolarCoordinateDecompositionEngine.Decompose(prices, parameters);
        var hilbert = HilbertDecompositionEngine.Decompose(prices, parameters);
        var viaResult = PolarCoordinateDecompositionEngine.Decompose(hilbert);

        Assert.Equal(viaPrices.Count, viaResult.Count);
        Assert.Equal(viaPrices.WarmupBars, viaResult.WarmupBars);
        for (int i = 0; i < viaPrices.Count; i++)
        {
            // PolarSampleResult is a readonly record struct: structural equality covers every
            // field, including double NaN (double.NaN.Equals(double.NaN) == true).
            Assert.Equal(viaPrices[i], viaResult[i]);
        }
    }

    [Fact]
    public void ResultOverload_NullArgument_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => PolarCoordinateDecompositionEngine.Decompose((HilbertDecompositionResult)null!));
    }

    [Fact]
    public void ResultOverload_EmptyDecomposition_ReturnsEmptyResult()
    {
        var hilbert = HilbertDecompositionEngine.Decompose(ReadOnlySpan<decimal>.Empty);

        var result = PolarCoordinateDecompositionEngine.Decompose(hilbert);

        Assert.Equal(0, result.Count);
        Assert.Empty(result.Samples);
    }

    [Fact]
    public void PricesWrapper_StillValidatesParameters()
    {
        var prices = GenerateSinePrices(60, period: 20.0);
        var invalid = new HilbertDecompositionParameters(DefaultPeriod: 100, MinPeriod: 6, MaxPeriod: 50);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => PolarCoordinateDecompositionEngine.Decompose(prices, invalid));
    }
}
