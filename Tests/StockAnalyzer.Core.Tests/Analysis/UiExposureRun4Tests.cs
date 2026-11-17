using System;
using System.Linq;
using StockAnalyzer.Core.Analysis;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

/// <summary>WebAI UI-exposure follow-up Run 4: the descending-read overload (R-11) and the shared embedding-dimension clamp (R-12).</summary>
public class UiExposureRun4Tests
{
    [Fact]
    public void EstimateSignalRank_WithSortedIndices_ReadsTheEigenvaluesInDescendingOrder()
    {
        // Raw (Jacobi-like) order: the dominant values are not first.
        double[] raw = { 0.1, 50.0, 0.2, 30.0, 0.05, 15.0 };
        int[] sortedIndices = Enumerable.Range(0, raw.Length).OrderByDescending(i => raw[i]).ToArray();
        double[] descending = sortedIndices.Select(i => raw[i]).ToArray();

        foreach (var method in Enum.GetValues<SsaRankSelectionMethod>())
        {
            int viaIndices = SsaRankSelector.EstimateSignalRank(raw, sortedIndices, method, maxRank: raw.Length - 1);
            int viaDescendingSpan = SsaRankSelector.EstimateSignalRank(descending, method, SsaRankSelector.DefaultTargetEnergy, raw.Length - 1);

            Assert.Equal(viaDescendingSpan, viaIndices);
        }
    }

    [Fact]
    public void EstimateSignalRank_DecompositionOverload_DelegatesToTheSameDefinition()
    {
        var samples = Enumerable.Range(0, 80).Select(i => 100.0 + 5.0 * Math.Sin(i * 0.5) + 0.01 * i).ToArray();
        var decomp = SsaDecompositionEngine.Decompose(samples, 12, SsaDetrendMode.LeastSquaresLinear);

        int viaResult = SsaRankSelector.EstimateSignalRank(decomp, SsaRankSelectionMethod.CumulativeEnergy, 11);
        int viaArrays = SsaRankSelector.EstimateSignalRank(decomp.Eigenvalues, decomp.SortedIndices, SsaRankSelectionMethod.CumulativeEnergy, 11);

        Assert.Equal(viaArrays, viaResult);
    }

    [Fact]
    public void EstimateSignalRank_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => SsaRankSelector.EstimateSignalRank((double[])null!, new[] { 0 }, SsaRankSelectionMethod.KaiserGuttman, 1));
        Assert.Throws<ArgumentNullException>(() => SsaRankSelector.EstimateSignalRank(new[] { 1.0 }, (int[])null!, SsaRankSelectionMethod.KaiserGuttman, 1));
    }

    [Theory]
    [InlineData(1, 100, 2)]    // below the minimum
    [InlineData(15, 100, 15)]  // inside the range
    [InlineData(60, 100, 50)]  // above N / 2
    [InlineData(10, 3, 2)]     // N / 2 below the minimum: the minimum wins
    [InlineData(2, 0, 2)]
    public void ClampEmbeddingDimension_KeepsTheDimensionBetweenTheMinimumAndHalfTheSamples(int requested, int sampleCount, int expected)
    {
        Assert.Equal(expected, SsaDecompositionEngine.ClampEmbeddingDimension(requested, sampleCount));
        Assert.Equal(2, SsaDecompositionEngine.MinEmbeddingDimension);
    }
}
