using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

/// <summary>
/// Regression for the SSA auto-rank bug: <see cref="SsaRankSelector.EstimateSignalRank(ReadOnlySpan{double}, SsaRankSelectionMethod, double, int)"/>
/// requires DESCENDING eigenvalues, but <see cref="SsaDecompositionResult.Eigenvalues"/> is in raw Jacobi order. Every auto-rank caller must
/// select the rank from the descending order (decomposition overload).
/// </summary>
public class SsaAutoRankEigenvalueOrderTests
{
    private const int EmbeddingDimension = 8;
    private const int SampleCount = 80;
    private const int FirstSeed = 1;
    private const int LastSeed = 200;

    private static double[] MakeSamples(int seed, int count)
    {
        var rnd = new Random(seed);
        var samples = new double[count];
        double x = 100;
        for (int i = 0; i < count; i++)
        {
            x += rnd.NextDouble() - 0.5 + 0.8 * Math.Sin(i * 2 * Math.PI / (6 + seed % 9));
            samples[i] = Math.Round(x, 4);
        }

        return samples;
    }

    private static List<DateTime> MakeTimestamps(int count)
        => Enumerable.Range(0, count).Select(i => new DateTime(2024, 1, 1).AddDays(i)).ToList();

    private static (int Raw, int Sorted) Ranks(IReadOnlyList<double> window)
    {
        var decomp = SsaDecompositionEngine.Decompose(window.ToArray(), EmbeddingDimension, SsaDetrendMode.LeastSquaresLinear);
        int raw = SsaRankSelector.EstimateSignalRank(decomp.Eigenvalues, SsaRankSelectionMethod.CumulativeEnergy, SsaRankSelector.DefaultTargetEnergy, EmbeddingDimension - 1);
        int sorted = SsaRankSelector.EstimateSignalRank(decomp, SsaRankSelectionMethod.CumulativeEnergy, EmbeddingDimension - 1);
        return (raw, sorted);
    }

    [Fact]
    public void DecompositionEigenvalues_AreNotInDescendingOrder_SoRawRankDiffers()
    {
        int differs = 0;
        for (int seed = FirstSeed; seed <= LastSeed; seed++)
        {
            var (raw, sorted) = Ranks(MakeSamples(seed, SampleCount));
            if (raw != sorted) differs++;
        }

        Assert.True(differs > 0, "The raw Jacobi order is expected to change the selected rank for at least one series.");
    }

    [Fact]
    public void SupportResistanceEngine_AutoRank_UsesTheRankOfTheDescendingEigenvalues()
    {
        int checkedSeries = 0;
        for (int seed = FirstSeed; seed <= LastSeed && checkedSeries < 5; seed++)
        {
            var samples = MakeSamples(seed, SampleCount);
            var (raw, sorted) = Ranks(samples);
            if (raw == sorted) continue;

            var result = SsaSupportResistanceEngine.Calculate(
                samples, MakeTimestamps(SampleCount), embeddingDimension: EmbeddingDimension, autoRank: true);

            Assert.False(result.IsEmpty);
            Assert.Equal(sorted, result.NumComponents);
            checkedSeries++;
        }

        Assert.True(checkedSeries > 0);
    }

    [Fact]
    public void AnomalyEngine_AutoRank_EqualsFixedRankOfTheDescendingEigenvalues()
    {
        int checkedSeries = 0;
        for (int seed = FirstSeed; seed <= LastSeed && checkedSeries < 5; seed++)
        {
            var samples = MakeSamples(seed, SampleCount);
            var (raw, sorted) = Ranks(samples);
            if (raw == sorted) continue;

            var timestamps = MakeTimestamps(SampleCount);
            var auto = SsaAnomalyDetectionEngine.CalculateAnomaly(samples, timestamps, embeddingDimension: EmbeddingDimension, autoRank: true);
            var fixedRank = SsaAnomalyDetectionEngine.CalculateAnomaly(samples, timestamps, embeddingDimension: EmbeddingDimension, numComponents: sorted, autoRank: false);

            Assert.Equal(fixedRank.ResidualStdDev, auto.ResidualStdDev, 12);
            Assert.Equal(fixedRank.ZScores.Count, auto.ZScores.Count);
            for (int i = 0; i < fixedRank.ZScores.Count; i++)
            {
                Assert.Equal(fixedRank.ZScores[i], auto.ZScores[i], 9);
            }

            checkedSeries++;
        }

        Assert.True(checkedSeries > 0);
    }

    [Fact]
    public void AnomalyIndicator_AutoRank_EqualsFixedRankOfTheDescendingEigenvalues()
    {
        const int window = 40;
        const int candleCount = window + 2;
        bool found = false;
        for (int seed = FirstSeed; seed <= LastSeed && !found; seed++)
        {
            var closes = MakeSamples(seed, candleCount);
            var ranks = Enumerable.Range(0, candleCount - window + 1)
                .Select(start => Ranks(closes.Skip(start).Take(window).ToArray()))
                .ToList();

            // Precondition: the same descending-order rank in every evaluated window (so a fixed-rank run is the reference),
            // and the raw order would have selected another rank in at least one window.
            if (ranks.Select(r => r.Sorted).Distinct().Count() != 1 || ranks.All(r => r.Raw == r.Sorted)) continue;

            found = true;
            var candles = closes
                .Select((c, i) => new CoreCandleData(new DateTime(2024, 1, 1).AddDays(i), (decimal)c, (decimal)c, (decimal)c, (decimal)c, 1000))
                .ToList();

            var auto = new CoreSSAAnomalyIndicator { WindowSize = window, EmbeddingDimension = EmbeddingDimension, AutoRank = true };
            var fixedRank = new CoreSSAAnomalyIndicator { WindowSize = window, EmbeddingDimension = EmbeddingDimension, AutoRank = false, NumComponents = ranks[0].Sorted };
            auto.Calculate(candles);
            fixedRank.Calculate(candles);

            Assert.Equal(fixedRank.ZScore.Count, auto.ZScore.Count);
            for (int i = 0; i < fixedRank.ZScore.Count; i++)
            {
                Assert.Equal(fixedRank.ZScore[i], auto.ZScore[i]);
            }
        }

        Assert.True(found, "No series met the precondition.");
    }
}
