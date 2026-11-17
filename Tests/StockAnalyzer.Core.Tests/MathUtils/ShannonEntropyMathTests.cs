using System;
using System.Linq;
using StockAnalyzer.Core.MathUtils;
using Xunit;

namespace StockAnalyzer.Core.Tests.MathUtils;

/// <summary>
/// The sum -sum p ln p shared by Entropy and SSAEntropy. The hand values pin the definition; the two "same as the loop it replaced" tests pin that sharing the sum changed no
/// value (bit for bit), because the two indicators computed it with these exact operations before.
/// </summary>
public class ShannonEntropyMathTests
{
    [Fact]
    public void FairCoin_IsLnTwo()
    {
        Assert.Equal(Math.Log(2.0), ShannonEntropyMath.Nats(new[] { 5, 5 }, 10), 12);
    }

    [Fact]
    public void ACertainOutcomeAndEmptyCategories_GiveZero()
    {
        Assert.Equal(0.0, ShannonEntropyMath.Nats(new[] { 0, 7, 0 }, 7));
        Assert.Equal(0.0, ShannonEntropyMath.Nats(ReadOnlySpan<int>.Empty, 1));
    }

    [Fact]
    public void WeightsBelowTheShare_AreIgnored()
    {
        // shares 0.5, 0.5 and 1e-13 (not above the 1e-12 floor): only the first two count.
        double[] weights = { 0.5, 0.5, 1e-13 };
        double total = 0.5 + 0.5 + 1e-13;

        double withFloor = ShannonEntropyMath.Nats(weights, total, ShannonEntropyMath.NegligibleShare);
        double withoutFloor = ShannonEntropyMath.Nats(weights, total, 0.0);

        double p = 0.5 / total;
        Assert.Equal(-2.0 * p * Math.Log(p), withFloor, 15);
        Assert.True(withoutFloor > withFloor);
    }

    [Fact]
    public void CountsOverload_IsBitIdenticalToTheLoopTheEntropyIndicatorHad()
    {
        var random = new Random(4242);
        for (int round = 0; round < 200; round++)
        {
            var counts = new int[2 + random.Next(30)];
            int total = 0;
            for (int i = 0; i < counts.Length; i++) { counts[i] = random.Next(0, 9); total += counts[i]; }
            if (total == 0) { counts[0] = 1; total = 1; }

            double expected = 0.0;
            for (int b = 0; b < counts.Length; b++)
            {
                if (counts[b] == 0) continue;
                double p = (double)counts[b] / total;
                expected -= p * Math.Log(p);
            }

            Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(ShannonEntropyMath.Nats(counts, total)));
        }
    }

    [Fact]
    public void WeightsOverload_IsBitIdenticalToTheLoopTheSsaEntropyIndicatorHad()
    {
        var random = new Random(777);
        for (int round = 0; round < 200; round++)
        {
            var weights = new double[2 + random.Next(30)];
            double total = 0.0;
            for (int i = 0; i < weights.Length; i++) { weights[i] = random.NextDouble() < 0.2 ? 1e-14 * random.NextDouble() : random.NextDouble(); total += weights[i]; }

            double expected = 0.0;
            for (int j = 0; j < weights.Length; j++)
            {
                double p = weights[j] / total;
                if (p > 1e-12)
                {
                    expected -= p * Math.Log(p);
                }
            }

            Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(ShannonEntropyMath.Nats(weights, total, ShannonEntropyMath.NegligibleShare)));
        }
    }

    [Fact]
    public void PermutationEntropy_StrictlyMonotoneIsZero_AndMaxEntropyIsNearOne()
    {
        // Strictly increasing returns => every 3-tuple has ordinal pattern 0 => entropy 0.
        var increasing = Enumerable.Range(0, 30).Select(i => 0.001 * i).ToArray();
        Assert.Equal(0.0, ShannonEntropyMath.PermutationEntropy(increasing, 3), 12);

        // Balanced occurrence of all 6 permutations of length 3 in 120 windows.
        var random = new Random(12345);
        var noise = Enumerable.Range(0, 500).Select(_ => random.NextDouble()).ToArray();
        double perm = ShannonEntropyMath.PermutationEntropy(noise, 3);
        Assert.InRange(perm, 0.95, 1.0);

        // Guard checks: window < d! + d - 1 or invalid d returns NaN.
        Assert.True(double.IsNaN(ShannonEntropyMath.PermutationEntropy(increasing[..6], 3)));
        Assert.True(double.IsNaN(ShannonEntropyMath.PermutationEntropy(increasing, 2)));
        Assert.True(double.IsNaN(ShannonEntropyMath.PermutationEntropy(increasing, 6)));
    }

    [Fact]
    public void SampleEntropy_ConstantIsZero_ZeroMatchAppliesFloor_AndNoiseExceedsPeriodic()
    {
        Assert.Equal(0.0, ShannonEntropyMath.SampleEntropy(Enumerable.Repeat(0.01, 30).ToArray()), 12);
        Assert.True(double.IsNaN(ShannonEntropyMath.SampleEntropy(new double[] { 0.1, 0.2, 0.3, 0.4, 0.5 }, 2, 0.2)));

        var periodic = Enumerable.Range(0, 60).Select(i => Math.Sin(i * Math.PI / 4.0) * 0.02).ToArray();
        var random = new Random(987);
        var noisy = Enumerable.Range(0, 60).Select(_ => (random.NextDouble() - 0.5) * 0.04).ToArray();

        double sePeriodic = ShannonEntropyMath.SampleEntropy(periodic, 2, 0.2);
        double seNoisy = ShannonEntropyMath.SampleEntropy(noisy, 2, 0.2);
        Assert.True(double.IsFinite(sePeriodic));
        Assert.True(double.IsFinite(seNoisy));
        Assert.True(seNoisy > sePeriodic, $"Expected noisy SampleEntropy ({seNoisy}) > periodic ({sePeriodic}).");
    }
}
