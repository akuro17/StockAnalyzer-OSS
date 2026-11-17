using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.MarketStructure;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.MathUtils;

public class MatrixProfileDbaTests
{
    [Fact]
    public void FindTopKMatches_LocatesPlantedScaledAndShiftedMotifs_RespectingExclusionAndHorizon()
    {
        int n = 220;
        int m = 20;
        int h = 10;
        var prices = new double[n];
        for (int i = 0; i < n; i++)
        {
            prices[i] = 100.0 + 0.05 * i + 0.3 * Math.Sin(i * 0.13);
        }

        // Define query pattern shape at [200..219] and plant affine copies at [30..49] and [100..119].
        var motif = new double[m];
        for (int u = 0; u < m; u++)
        {
            motif[u] = Math.Sin(u * 0.45) + 0.6 * Math.Cos(u * 0.9);
            prices[200 + u] = 120.0 + 2.0 * motif[u];
            prices[30 + u] = 95.0 + 1.5 * motif[u];
            prices[100 + u] = 110.0 + 3.0 * motif[u];
        }

        // Plant continuation upward drift after matches [30..49] and [100..119].
        for (int s = 1; s <= h; s++)
        {
            prices[30 + m - 1 + s] = prices[30 + m - 1] * (1.0 + 0.01 * s);
            prices[100 + m - 1 + s] = prices[100 + m - 1] * (1.0 + 0.015 * s);
        }

        int ez = Math.Max(1, m / IndicatorDefaultConstants.MatrixProfileExclusionDivisor);
        var matches = new List<HistoricalMatch>();
        MatrixProfileMath.FindTopKMatches(prices, queryStart: 200, queryLength: m, horizon: h, topK: 5, matches);

        Assert.NotEmpty(matches);
        Assert.Equal(5, matches.Count);
        Assert.Contains(matches.Take(2), mt => mt.StartIndex == 30 || mt.StartIndex == 100);
        Assert.True(matches[0].Distance < 1e-6, $"Expected exact affine motif distance ~0, got {matches[0].Distance}");

        for (int i = 0; i < matches.Count; i++)
        {
            Assert.True(200 - (matches[i].StartIndex + m) >= h, "Continuation horizon must precede queryStart.");
            for (int j = i + 1; j < matches.Count; j++)
            {
                Assert.True(Math.Abs(matches[i].StartIndex - matches[j].StartIndex) >= ez, "Matches must respect exclusion zone.");
            }
        }
    }

    [Fact]
    public void DtwBarycenter_AlignsPhaseShiftedPeaksAndProducesOrderedQuantileBand()
    {
        int h = 15;
        // Build 4 similar spike trajectories shifted slightly in time + 1 spread-out trajectory.
        var seq1 = new double[h];
        var seq2 = new double[h];
        var seq3 = new double[h];
        var seq4 = new double[h];

        seq1[5] = 0.10; seq1[6] = 0.05;
        seq2[6] = 0.12; seq2[7] = 0.06;
        seq3[7] = 0.10; seq3[8] = 0.04;
        seq4[6] = 0.02; seq4[7] = 0.01;

        var sequences = new List<double[]> { seq1, seq2, seq3, seq4 };
        double[] distances = { 0.10, 0.08, 0.12, 0.30 };

        var bary = new double[h];
        var lower = new double[h];
        var upper = new double[h];

        bool ok = DtwBarycenterMath.ComputeEnsemble(sequences, distances, h, bary, lower, upper);
        Assert.True(ok);

        for (int i = 0; i < h; i++)
        {
            Assert.True(double.IsFinite(bary[i]));
            Assert.True(lower[i] <= upper[i] + 1e-12, $"Expected lower[{i}] <= upper[{i}].");
        }

        double maxBary = bary.Max();
        Assert.True(maxBary > 0.07, $"Expected DBA barycenter to preserve aligned peak amplitude (>0.07), got {maxBary}.");
    }

    [Fact]
    public async Task MarketStructureService_UseDbaEnsemble_RunsNativelyWithoutPython()
    {
        var candles = new List<CandleData>(200);
        double price = 100.0;
        for (int i = 0; i < 200; i++)
        {
            double wave = Math.Sin(i * 0.35) * 3.0;
            price = 100.0 + 0.05 * i + wave;
            decimal c = (decimal)Math.Round(price, 4);
            candles.Add(CandleData.CreateUnchecked(new DateTime(2023, 1, 1).AddDays(i), c, c + 0.5m, c - 0.5m, c, 1000L));
        }

        var service = new MarketStructureService(UnavailablePythonService.Instance);
        PatternOverlayResult result = await service.SearchSimilarPatternsAsync(
            candles,
            futureSteps: 12,
            queryLength: 20,
            queryStartIndex: 180,
            useDbaEnsemble: true,
            topKMatches: 5);

        Assert.True(result.IsSuccessful, result.ErrorMessage);
        Assert.True(result.Patterns.Count >= 2);
        Assert.Equal(12, result.DbaBarycenterPercentChange.Count);
        Assert.Equal(12, result.DbaLowerBandPercentChange.Count);
        Assert.Equal(12, result.DbaUpperBandPercentChange.Count);

        for (int i = 0; i < 12; i++)
        {
            Assert.True(result.DbaLowerBandPercentChange[i] <= result.DbaUpperBandPercentChange[i] + 1e-9);
            Assert.True(result.DbaLowerBandPercentChange[i] <= result.DbaBarycenterPercentChange[i] + 1e-9);
            Assert.True(result.DbaBarycenterPercentChange[i] <= result.DbaUpperBandPercentChange[i] + 1e-9);
        }
    }

    [Fact]
    public void ComputeEnsemble_PhaseShiftedWaveforms_BarycenterAlwaysWithinEnvelopeBand()
    {
        const int h = 10;
        var s1 = new double[h]; s1[4] = 0.10;
        var s2 = new double[h]; s2[4] = 0.10;
        var s3 = new double[h]; s3[5] = 0.10;
        var s4 = new double[h]; s4[6] = 0.10;
        var s5 = new double[h]; s5[6] = 0.10;
        var sequences = new List<double[]> { s1, s2, s3, s4, s5 };
        double[] distances = { 0.1, 0.1, 0.1, 0.1, 0.1 };

        var bary = new double[h];
        var lower = new double[h];
        var upper = new double[h];

        bool ok = DtwBarycenterMath.ComputeEnsemble(sequences, distances, h, bary, lower, upper);
        Assert.True(ok);

        for (int i = 0; i < h; i++)
        {
            Assert.True(lower[i] <= bary[i] + 1e-12, $"Expected lower[{i}] ({lower[i]}) <= bary[{i}] ({bary[i]}).");
            Assert.True(bary[i] <= upper[i] + 1e-12, $"Expected bary[{i}] ({bary[i]}) <= upper[{i}] ({upper[i]}).");
        }
    }

    [Fact]
    public void FindTopKMatches_WithCancelledToken_ThrowsOperationCanceledException()
    {
        double[] prices = Enumerable.Range(0, 120).Select(i => 100.0 + Math.Sin(i * 0.15)).ToArray();
        var matches = new List<HistoricalMatch>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            MatrixProfileMath.FindTopKMatches(prices, queryStart: 90, queryLength: 15, horizon: 5, topK: 3, matches, cts.Token));
    }

    [Fact]
    public async Task SearchSimilarPatternsAsync_WithCancelledToken_ThrowsOperationCanceledException()
    {
        var candles = Enumerable.Range(0, 120)
            .Select(i => new CandleData(new DateTime(2024, 1, 1).AddDays(i), 100m + i * 0.1m, 101m + i * 0.1m, 99m + i * 0.1m, 100m + i * 0.1m, 1000L))
            .ToList();
        var service = new MarketStructureService(UnavailablePythonService.Instance);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SearchSimilarPatternsAsync(
                candles,
                queryLength: 20,
                queryStartIndex: 80,
                futureSteps: 5,
                useDbaEnsemble: true,
                topKMatches: 3,
                cancellationToken: cts.Token));
    }

    [Fact]
    public void ComputeEnsemble_LargeSequenceCount_UsesArrayPoolWithoutStackOverflow()
    {
        // 150 sequences -> 4 * 150 = 600 > MathBufferLimits.StackAllocThreshold (512), exercising ArrayPool fallback.
        const int count = 150;
        const int horizon = 4;
        var sequences = Enumerable.Range(0, count)
            .Select(i => new double[] { i * 0.001, (i + 1) * 0.001, (i + 2) * 0.001, (i + 3) * 0.001 })
            .ToList();
        double[] distances = Enumerable.Repeat(0.1, count).ToArray();
        var bary = new double[horizon];
        var lower = new double[horizon];
        var upper = new double[horizon];

        bool ok = DtwBarycenterMath.ComputeEnsemble(sequences, distances, horizon, bary, lower, upper);
        Assert.True(ok);
        for (int h = 0; h < horizon; h++)
        {
            Assert.True(double.IsFinite(bary[h]));
            Assert.True(lower[h] <= bary[h] + 1e-12);
            Assert.True(bary[h] <= upper[h] + 1e-12);
        }
    }
}
