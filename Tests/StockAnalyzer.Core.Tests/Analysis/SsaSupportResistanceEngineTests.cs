using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

public class SsaSupportResistanceEngineTests
{
    [Fact]
    public void Calculate_WithEmptyOrInsufficientSamples_ReturnsEmpty()
    {
        var result1 = SsaSupportResistanceEngine.Calculate(
            Array.Empty<double>(),
            Array.Empty<DateTime>());
        Assert.True(result1.IsEmpty);
        Assert.Equal(0, result1.SampleCount);
        Assert.Empty(result1.ResistanceLevels);
        Assert.Empty(result1.SupportLevels);

        var result2 = SsaSupportResistanceEngine.Calculate(
            new double[] { 10.0, 11.0, 12.0 },
            new DateTime[] { DateTime.Now, DateTime.Now.AddDays(1), DateTime.Now.AddDays(2) });
        Assert.True(result2.IsEmpty);
        Assert.Equal(0, result2.SampleCount);
    }

    [Fact]
    public void Calculate_WithNaNOrInfinity_ReturnsEmpty()
    {
        var now = DateTime.Now;
        var samples = new double[] { 10.0, 12.0, double.NaN, 14.0, 15.0, 16.0, 17.0, 18.0 };
        var timestamps = Enumerable.Range(0, samples.Length).Select(i => now.AddDays(i)).ToArray();

        var result = SsaSupportResistanceEngine.Calculate(samples, timestamps);
        Assert.True(result.IsEmpty);

        samples[2] = double.PositiveInfinity;
        var resultInf = SsaSupportResistanceEngine.Calculate(samples, timestamps);
        Assert.True(resultInf.IsEmpty);
    }

    [Fact]
    public void Calculate_PlateauExtrema_DetectsExtrema()
    {
        // Series with flat plateau at peak: 10, 20, 30, 30, 30, 20, 10
        // And repeated cycle
        int n = 35;
        var baseDate = new DateTime(2025, 1, 1);
        var samples = new List<double>(n);
        var timestamps = new List<DateTime>(n);

        double[] pattern = new double[] { 10.0, 20.0, 30.0, 30.0, 30.0, 20.0, 10.0 };
        for (int i = 0; i < n; i++)
        {
            samples.Add(pattern[i % pattern.Length]);
            timestamps.Add(baseDate.AddDays(i));
        }

        var result = SsaSupportResistanceEngine.Calculate(
            samples,
            timestamps,
            mode: SsaSupportResistanceMode.StructuralPivots,
            embeddingDimension: 7,
            numComponents: 2,
            autoRank: false,
            detrendMode: SsaDetrendMode.None,
            maxLevelsPerSide: 2,
            clusterTolerance: 0.5m);

        Assert.False(result.IsEmpty);
        Assert.Equal(n, result.SampleCount);
        Assert.NotEmpty(result.ResistanceLevels);
        Assert.NotEmpty(result.SupportLevels);

        // Peak levels should be near 30.0, support levels near 10.0
        var topRes = result.ResistanceLevels.OrderByDescending(r => r.StrengthScore).First();
        Assert.InRange(topRes.Price, 25.0, 35.0);
        Assert.True(topRes.Hits >= 1);

        var topSup = result.SupportLevels.OrderByDescending(s => s.StrengthScore).First();
        Assert.InRange(topSup.Price, 5.0, 15.0);
        Assert.True(topSup.Hits >= 1);
    }

    [Fact]
    public void ClusterExtrema_CloseLevels_MergesCorrectlyWithArithmeticMean()
    {
        var now = DateTime.Now;
        var timestamps = new List<DateTime> { now, now.AddDays(1), now.AddDays(2), now.AddDays(3), now.AddDays(4) };

        // 3 candidates within deltaCluster (100.0, 100.4, 100.2)
        var candidates = new List<(double Price, int Index)>
        {
            (100.0, 1),
            (100.4, 3),
            (100.2, 2)
        };

        double delta = 0.5;
        var clusters = SsaSupportResistanceEngine.ClusterExtrema(candidates, delta, 5, timestamps, isResistance: true);

        Assert.Single(clusters);
        Assert.Equal(3, clusters[0].Hits);
        Assert.Equal(100.2, clusters[0].Price, 3);
        Assert.Equal(3, clusters[0].LatestIndex);
        Assert.True(clusters[0].IsResistance);
        Assert.True(clusters[0].StrengthScore > 3.0);
    }

    [Fact]
    public void Calculate_Mode1_ExtractsPivotsAndActiveLevels()
    {
        // 60 samples sine wave
        int n = 60;
        var samples = new List<double>(n);
        var timestamps = new List<DateTime>(n);
        var baseDate = new DateTime(2025, 1, 1);

        for (int i = 0; i < n; i++)
        {
            double val = 100.0 + 10.0 * Math.Sin(2.0 * Math.PI * i / 12.0);
            samples.Add(val);
            timestamps.Add(baseDate.AddDays(i));
        }

        var result = SsaSupportResistanceEngine.Calculate(
            samples,
            timestamps,
            mode: SsaSupportResistanceMode.StructuralPivots,
            embeddingDimension: 12,
            numComponents: 2,
            autoRank: false,
            detrendMode: SsaDetrendMode.None,
            maxLevelsPerSide: 2,
            clusterTolerance: 0.5m,
            currentPrice: 100.0);

        Assert.False(result.IsEmpty);
        Assert.NotEmpty(result.ResistanceLevels);
        Assert.NotEmpty(result.SupportLevels);

        // Active resistance should be above 100.0, active support below 100.0
        Assert.NotNull(result.ActiveResistance);
        Assert.True(result.ActiveResistance.Value > 100.0);

        Assert.NotNull(result.ActiveSupport);
        Assert.True(result.ActiveSupport.Value < 100.0);
    }

    [Fact]
    public void Calculate_Mode2_DynamicEnvelopes_MatchesMultiplierWidth()
    {
        int n = 40;
        var samples = new List<double>(n);
        var timestamps = new List<DateTime>(n);
        var baseDate = new DateTime(2025, 1, 1);

        for (int i = 0; i < n; i++)
        {
            double val = 50.0 + 2.0 * Math.Sin(2.0 * Math.PI * i / 10.0) + (i % 2 == 0 ? 0.5 : -0.5);
            samples.Add(val);
            timestamps.Add(baseDate.AddDays(i));
        }

        decimal mult = 2.5m;
        var result = SsaSupportResistanceEngine.Calculate(
            samples,
            timestamps,
            mode: SsaSupportResistanceMode.DynamicEnvelopes,
            embeddingDimension: 10,
            numComponents: 2,
            autoRank: false,
            multiplier: mult);

        Assert.False(result.IsEmpty);
        Assert.Equal(n, result.CenterBand.Count);
        Assert.Equal(n, result.UpperBand.Count);
        Assert.Equal(n, result.LowerBand.Count);

        double expectedWidth = (double)mult * result.ResidualStdDev;
        for (int t = 0; t < n; t++)
        {
            double center = result.CenterBand[t].Y;
            double upper = result.UpperBand[t].Y;
            double lower = result.LowerBand[t].Y;

            Assert.Equal(upper - center, expectedWidth, 3);
            Assert.Equal(center - lower, expectedWidth, 3);
        }
    }

    [Fact]
    public void Calculate_Mode3_TiedProjectedExtrema_SelectsEarliestFutureStep()
    {
        int n = 48;
        var samples = new List<double>(n);
        var timestamps = new List<DateTime>(n);
        var baseDate = new DateTime(2025, 1, 1);

        // Sine wave repeating every 12 bars
        for (int i = 0; i < n; i++)
        {
            double val = 100.0 + 10.0 * Math.Sin(2.0 * Math.PI * i / 12.0);
            samples.Add(val);
            timestamps.Add(baseDate.AddDays(i));
        }

        // Projecting 25 steps ahead (over 2 full cycles)
        var result = SsaSupportResistanceEngine.Calculate(
            samples,
            timestamps,
            mode: SsaSupportResistanceMode.ProjectedTargets,
            embeddingDimension: 12,
            numComponents: 2,
            autoRank: false,
            detrendMode: SsaDetrendMode.None,
            futureSteps: 25,
            forecastMode: SsaForecastMode.Recurrent);

        Assert.False(result.IsEmpty);
        Assert.NotEmpty(result.ProjectedPath);
        Assert.Single(result.ResistanceLevels);
        Assert.Single(result.SupportLevels);

        var res = result.ResistanceLevels[0];
        var sup = result.SupportLevels[0];

        // Earliest peak and trough should be in the first cycle (h <= 12)
        int hRes = res.LatestIndex - (n - 1);
        int hSup = sup.LatestIndex - (n - 1);

        Assert.InRange(hRes, 1, 12);
        Assert.InRange(hSup, 1, 12);
        Assert.InRange(res.Price, 108.0, 112.0);
        Assert.InRange(sup.Price, 88.0, 92.0);
    }
}
