using System;
using System.Linq;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>Softmax weights quantized to four decimals. Cases use logits whose probabilities are exactly representable, so the floor is unambiguous.</summary>
public sealed class RlPortfolioWeightsTests
{
    [Fact]
    public void EqualLogitsGiveEqualQuantizedWeightsAndCashAbsorbsTheRemainder()
    {
        // Three equal logits (cash + two assets): p = 1/3 each, floor(3333.33) = 3333 -> 0.3333; cash = 1 - 0.6666 = 0.3334.
        var weights = RlPortfolioWeights.FromLogits(new float[] { 0, 0, 0 }, new[] { true, true }, 100);
        Assert.Equal(new[] { 0.3333m, 0.3333m }, weights.Assets.ToArray());
        Assert.Equal(0.3334m, weights.Cash);
    }

    [Fact]
    public void FourEqualLogitsGiveAQuarterEach()
    {
        var weights = RlPortfolioWeights.FromLogits(new float[] { 0, 0, 0, 0 }, new[] { true, true, true }, 100);
        Assert.Equal(new[] { 0.25m, 0.25m, 0.25m }, weights.Assets.ToArray());
        Assert.Equal(0.25m, weights.Cash);
    }

    [Fact]
    public void MaskedSlotsAreRemovedFromTheSoftmaxWhateverTheirLogit()
    {
        // The second asset is masked: only cash and the third asset compete, 0.5 each, although the masked logit is huge.
        var weights = RlPortfolioWeights.FromLogits(new float[] { 0, 1000, 0 }, new[] { false, true }, 100);
        Assert.Equal(new[] { 0m, 0.5m }, weights.Assets.ToArray());
        Assert.Equal(0.5m, weights.Cash);
    }

    [Fact]
    public void AllAssetsMaskedMeansAllCash()
    {
        var weights = RlPortfolioWeights.FromLogits(new float[] { -5, 3, 4 }, new[] { false, false }, 100);
        Assert.Equal(new[] { 0m, 0m }, weights.Assets.ToArray());
        Assert.Equal(1m, weights.Cash);
    }

    [Fact]
    public void ThePerAssetCapIsAppliedAndCashTakesTheExcess()
    {
        // Equal logits would give 0.3333 each; capped at 25 percent: 0.25 each, cash 0.5.
        var weights = RlPortfolioWeights.FromLogits(new float[] { 0, 0, 0 }, new[] { true, true }, 25);
        Assert.Equal(new[] { 0.25m, 0.25m }, weights.Assets.ToArray());
        Assert.Equal(0.5m, weights.Cash);
    }

    [Fact]
    public void LargeLogitsAreStableAndSumToOne()
    {
        var weights = RlPortfolioWeights.FromLogits(new float[] { 0, 1e30f, 1e30f }, new[] { true, true }, 100);
        Assert.Equal(new[] { 0.5m, 0.5m }, weights.Assets.ToArray());
        Assert.Equal(0m, weights.Cash);
        Assert.Equal(1m, weights.Cash + weights.Assets[0] + weights.Assets[1]);
    }

    [Fact]
    public void AnyInputGivesNonNegativeWeightsSummingToExactlyOne()
    {
        var random = new Random(7);
        for (int run = 0; run < 200; run++)
        {
            int assets = random.Next(2, 12);
            var logits = new float[assets + 1];
            for (int i = 0; i < logits.Length; i++) logits[i] = (float)(random.NextDouble() * 20 - 10);
            var tradable = new bool[assets];
            for (int i = 0; i < assets; i++) tradable[i] = random.Next(4) != 0;
            var weights = RlPortfolioWeights.FromLogits(logits, tradable, random.Next(5, 101));
            Assert.All(weights.Assets, w => Assert.InRange(w, 0m, 1m));
            Assert.True(weights.Cash >= 0m);
            Assert.Equal(1m, weights.Cash + weights.Assets.Sum());
            for (int i = 0; i < assets; i++) if (!tradable[i]) Assert.Equal(0m, weights.Assets[i]);
            Assert.All(weights.Assets, w => Assert.Equal(w, decimal.Round(w, 4)));
        }
    }

    [Fact]
    public void InvalidInputIsRejected()
    {
        Assert.Throws<ArgumentException>(() => RlPortfolioWeights.FromLogits(new float[] { 0, 0 }, new[] { true, true }, 100));
        Assert.Throws<ArgumentException>(() => RlPortfolioWeights.FromLogits(new float[] { 0, float.NaN, 0 }, new[] { true, true }, 100));
        Assert.Throws<ArgumentException>(() => RlPortfolioWeights.FromLogits(new float[] { 0, float.PositiveInfinity, 0 }, new[] { true, true }, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => RlPortfolioWeights.FromLogits(new float[] { 0, 0, 0 }, new[] { true, true }, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RlPortfolioWeights.FromLogits(new float[] { 0, 0, 0 }, new[] { true, true }, 101));
    }
}
