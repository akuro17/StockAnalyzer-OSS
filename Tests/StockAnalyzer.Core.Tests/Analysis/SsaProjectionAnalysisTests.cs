using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

public class SsaProjectionAnalysisTests
{
    [Fact]
    public void CalculateProjection_WithEmptyOrInsufficientSamples_ReturnsEmpty()
    {
        var result1 = SsaProjectionAnalysis.CalculateProjection(
            Array.Empty<double>(),
            Array.Empty<DateTime>());
        Assert.Equal(0, result1.SampleCount);
        Assert.Empty(result1.ProjectedPoints);

        var result2 = SsaProjectionAnalysis.CalculateProjection(
            new double[] { 10.0, 11.0, 12.0 },
            new DateTime[] { DateTime.Now, DateTime.Now.AddDays(1), DateTime.Now.AddDays(2) });
        Assert.Equal(0, result2.SampleCount);
        Assert.Empty(result2.ProjectedPoints);
    }

    [Fact]
    public void CalculateProjection_PureSineWave_DecomposesAndExtrapolatesComponents()
    {
        // 60 samples of pure sine wave with period = 12 bars (5 complete cycles), amplitude = 5.0, mean = 100.0
        int n = 60;
        var samples = new List<double>(n);
        var timestamps = new List<DateTime>(n);
        var baseDate = new DateTime(2024, 1, 1);

        double period = 12.0;
        double amplitude = 5.0;
        double meanPrice = 100.0;

        for (int i = 0; i < n; i++)
        {
            double val = meanPrice + amplitude * Math.Sin(2.0 * Math.PI * i / period);
            samples.Add(val);
            timestamps.Add(baseDate.AddDays(i));
        }

        var result = SsaProjectionAnalysis.CalculateProjection(
            samples,
            timestamps,
            futureSteps: 12,
            timeframeSpan: TimeSpan.FromDays(1),
            embeddingDimension: 12,
            numComponents: 2,
            detrendMode: SsaDetrendMode.None,
            showConfidenceBand: true,
            confidenceMultiplier: 2.0m);

        Assert.Equal(n, result.SampleCount);
        Assert.NotEmpty(result.Components);
        Assert.Equal(12 + 1, result.ProjectedPoints.Count); // 1 connection point + 12 future steps
        Assert.Equal(n, result.ReconstructedPoints.Count);
        Assert.True(result.CumulativeVarianceRatio > 0.8, $"Expected cumulative variance > 0.8, got {result.CumulativeVarianceRatio}");
        Assert.True(result.IsStable);

        // Verify that in-sample reconstructed points closely track the sine wave
        for (int i = 0; i < n; i++)
        {
            Assert.InRange(result.ReconstructedPoints[i].Y, meanPrice - amplitude * 1.5, meanPrice + amplitude * 1.5);
        }

        // Verify that projected points continue oscillating around meanPrice
        for (int m = 1; m <= 12; m++)
        {
            double actualVal = result.ProjectedPoints[m].Y;
            Assert.True(actualVal >= meanPrice - amplitude * 2.0 && actualVal <= meanPrice + amplitude * 2.0,
                $"Projected value {actualVal} at step {m} out of expected range.");
        }

        // Verify uncertainty band ordering: Upper > Center > Lower
        for (int m = 1; m <= 12; m++)
        {
            Assert.True(result.UpperBandPoints[m].Y >= result.ProjectedPoints[m].Y);
            Assert.True(result.LowerBandPoints[m].Y <= result.ProjectedPoints[m].Y);
        }
    }

    [Fact]
    public void CalculateProjection_OlsDetrending_IsRobustAgainstTailSpikes()
    {
        // 60 samples: linear baseline 50 + 0.5*t, with a large spike at the very last bar (t = 59)
        int n = 60;
        var samples = new List<double>(n);
        var timestamps = new List<DateTime>(n);
        var baseDate = new DateTime(2024, 1, 1);

        for (int i = 0; i < n; i++)
        {
            double val = 50.0 + 0.5 * i;
            if (i == n - 1) val += 30.0; // tail spike
            samples.Add(val);
            timestamps.Add(baseDate.AddDays(i));
        }

        var resultOls = SsaProjectionAnalysis.CalculateProjection(
            samples,
            timestamps,
            futureSteps: 10,
            embeddingDimension: 10,
            numComponents: 2,
            detrendMode: SsaDetrendMode.LeastSquaresLinear);

        var resultEndpoint = SsaProjectionAnalysis.CalculateProjection(
            samples,
            timestamps,
            futureSteps: 10,
            embeddingDimension: 10,
            numComponents: 2,
            detrendMode: SsaDetrendMode.EndpointLinear);

        // OLS slope should stay close to true slope ~0.5 despite the single spike
        Assert.InRange(resultOls.Slope, 0.45, 0.70);

        // Endpoint slope is severely corrupted by the 30.0 spike: (59*0.5 + 30)/59 = ~1.01
        Assert.True(resultEndpoint.Slope > 0.90, $"Endpoint slope should be inflated by tail spike, was {resultEndpoint.Slope}");
        Assert.True(resultOls.Slope < resultEndpoint.Slope, "OLS slope should be much less sensitive to tail spike than endpoint slope");
    }

    [Fact]
    public void CalculateProjection_SingularitySafeguard_DoesNotProduceNaNOrInfinity()
    {
        // Degenerate series designed to test stability safeguard
        int n = 30;
        var samples = new List<double>(n);
        var timestamps = new List<DateTime>(n);
        var baseDate = new DateTime(2024, 1, 1);

        for (int i = 0; i < n; i++)
        {
            samples.Add(100.0 + (i % 2 == 0 ? 1.0 : -1.0));
            timestamps.Add(baseDate.AddDays(i));
        }

        var result = SsaProjectionAnalysis.CalculateProjection(
            samples,
            timestamps,
            futureSteps: 20,
            embeddingDimension: 14,
            numComponents: 13, // High component count near L-1
            detrendMode: SsaDetrendMode.LeastSquaresLinear);

        Assert.NotEmpty(result.ProjectedPoints);
        foreach (var p in result.ProjectedPoints)
        {
            Assert.False(double.IsNaN(p.Y));
            Assert.False(double.IsInfinity(p.Y));
        }
    }

    [Fact]
    public void CalculateProjection_FlatPrice_HandlesZeroVarianceGracefully()
    {
        int n = 30;
        var samples = new List<double>(n);
        var timestamps = new List<DateTime>(n);
        var baseDate = new DateTime(2024, 1, 1);

        for (int i = 0; i < n; i++)
        {
            samples.Add(100.0);
            timestamps.Add(baseDate.AddDays(i));
        }

        var result = SsaProjectionAnalysis.CalculateProjection(
            samples,
            timestamps,
            futureSteps: 10,
            embeddingDimension: 8,
            numComponents: 2,
            detrendMode: SsaDetrendMode.LeastSquaresLinear);

        Assert.NotEmpty(result.ProjectedPoints);
        for (int i = 1; i <= 10; i++)
        {
            Assert.Equal(100.0, result.ProjectedPoints[i].Y, 2);
        }
    }

    [Fact]
    public void CalculateProjection_WithNaNOrInfinitySamples_ReturnsEmpty()
    {
        int n = 20;
        var baseDate = new DateTime(2024, 1, 1);
        var timestamps = Enumerable.Range(0, n).Select(i => baseDate.AddDays(i)).ToList();

        // NaN in samples
        var samplesWithNaN = Enumerable.Repeat(100.0, n).ToList();
        samplesWithNaN[5] = double.NaN;
        var resNaN = SsaProjectionAnalysis.CalculateProjection(samplesWithNaN, timestamps);
        Assert.Same(SsaProjectionResult.Empty, resNaN);

        // PositiveInfinity in samples
        var samplesWithPosInf = Enumerable.Repeat(100.0, n).ToList();
        samplesWithPosInf[3] = double.PositiveInfinity;
        var resPosInf = SsaProjectionAnalysis.CalculateProjection(samplesWithPosInf, timestamps);
        Assert.Same(SsaProjectionResult.Empty, resPosInf);

        // NegativeInfinity in samples
        var samplesWithNegInf = Enumerable.Repeat(100.0, n).ToList();
        samplesWithNegInf[7] = double.NegativeInfinity;
        var resNegInf = SsaProjectionAnalysis.CalculateProjection(samplesWithNegInf, timestamps);
        Assert.Same(SsaProjectionResult.Empty, resNegInf);
    }

    [Fact]
    public void CalculateProjection_WithNearFlatSeries_HandlesZeroVarianceGracefully()
    {
        // 30 samples with sub-epsilon floating-point noise (e.g. 1e-12)
        int n = 30;
        var samples = new List<double>(n);
        var timestamps = new List<DateTime>(n);
        var baseDate = new DateTime(2024, 1, 1);

        for (int i = 0; i < n; i++)
        {
            samples.Add(100.0 + 1e-12 * Math.Sin(i));
            timestamps.Add(baseDate.AddDays(i));
        }

        var result = SsaProjectionAnalysis.CalculateProjection(
            samples,
            timestamps,
            futureSteps: 10,
            embeddingDimension: 8,
            numComponents: 2,
            detrendMode: SsaDetrendMode.LeastSquaresLinear);

        Assert.NotEmpty(result.ProjectedPoints);
        for (int i = 1; i <= 10; i++)
        {
            Assert.Equal(100.0, result.ProjectedPoints[i].Y, 2);
        }
    }

    [Fact]
    public void NamedConstants_MatchSpecification()
    {
        Assert.Equal(50, SsaProjectionAnalysis.MaxJacobiSweeps);
        Assert.Equal(1e-12, SsaProjectionAnalysis.JacobiConvergenceTolerance);
        Assert.Equal(1e-4, SsaProjectionAnalysis.LrrDenominatorFloor);
        Assert.Equal(1e-6, SsaProjectionAnalysis.LrrRidgeRegularization);
        Assert.Equal(0.95, SsaProjectionAnalysis.NuSquaredStabilityThreshold);
        Assert.Equal(1e-10, SsaProjectionAnalysis.DegenerateSeriesEpsilon);
    }
}
