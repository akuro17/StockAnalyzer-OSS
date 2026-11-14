using System;
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
}
