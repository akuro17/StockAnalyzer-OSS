using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Analysis;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

public class SsaFeatureAdditionsTests
{
    [Fact]
    public void SsaRankSelector_ShortOrZeroEigenvalues_ReturnsOne()
    {
        double[] empty = Array.Empty<double>();
        Assert.Equal(1, SsaRankSelector.EstimateSignalRank(empty, SsaRankSelectionMethod.KaiserGuttman));

        double[] single = new double[] { 10.0 };
        Assert.Equal(1, SsaRankSelector.EstimateSignalRank(single, SsaRankSelectionMethod.KaiserGuttman));

        double[] allZeros = new double[] { 0.0, 0.0, 0.0, 0.0 };
        Assert.Equal(1, SsaRankSelector.EstimateSignalRank(allZeros, SsaRankSelectionMethod.KaiserGuttman));
        Assert.Equal(1, SsaRankSelector.EstimateSignalRank(allZeros, SsaRankSelectionMethod.ScreeMaxCurvature));
        Assert.Equal(1, SsaRankSelector.EstimateSignalRank(allZeros, SsaRankSelectionMethod.CumulativeEnergy));
    }

    [Fact]
    public void SsaRankSelector_KaiserGuttman_SelectsAboveMean()
    {
        // Eigenvalues: 50, 30, 10, 10 -> Sum = 100, Mean = 25
        // Values >= 25 are 50 and 30 -> Rank should be 2
        double[] ev = new double[] { 50.0, 30.0, 10.0, 10.0 };
        int rank = SsaRankSelector.EstimateSignalRank(ev, SsaRankSelectionMethod.KaiserGuttman);
        Assert.Equal(2, rank);
    }

    [Fact]
    public void SsaRankSelector_ScreeMaxCurvature_FindsElbow()
    {
        // Eigenvalues sharply drop after index 1 (rank 2): 100, 80, 5, 2, 1
        double[] ev = new double[] { 100.0, 80.0, 5.0, 2.0, 1.0 };
        int rank = SsaRankSelector.EstimateSignalRank(ev, SsaRankSelectionMethod.ScreeMaxCurvature);
        Assert.InRange(rank, 1, 3);
    }

    [Fact]
    public void SsaRankSelector_ScreeMaxCurvature_FlatSpectrumReturnsOne()
    {
        // Flat noise spectrum: 10, 10, 10, 10 -> d2 <= 0.05 everywhere -> returns 1
        double[] ev = new double[] { 10.0, 10.0, 10.0, 10.0 };
        int rank = SsaRankSelector.EstimateSignalRank(ev, SsaRankSelectionMethod.ScreeMaxCurvature);
        Assert.Equal(1, rank);
    }

    [Fact]
    public void SsaRankSelector_CumulativeEnergy_SatisfiesTarget()
    {
        // Eigenvalues: 50, 30, 15, 5 -> Total = 100
        // Target 0.80 -> 50+30 = 80 >= 80 -> Rank = 2
        // Target 0.95 -> 50+30+15 = 95 >= 95 -> Rank = 3
        double[] ev = new double[] { 50.0, 30.0, 15.0, 5.0 };
        Assert.Equal(2, SsaRankSelector.EstimateSignalRank(ev, SsaRankSelectionMethod.CumulativeEnergy, targetEnergy: 0.80));
        Assert.Equal(3, SsaRankSelector.EstimateSignalRank(ev, SsaRankSelectionMethod.CumulativeEnergy, targetEnergy: 0.95));
        Assert.Equal(1, SsaRankSelector.EstimateSignalRank(ev, SsaRankSelectionMethod.CumulativeEnergy, targetEnergy: 0.50));
    }

    [Fact]
    public void SsaRankSelector_MaxRankClamp_EnforcesLimit()
    {
        double[] ev = new double[] { 50.0, 30.0, 15.0, 5.0 };
        int rank = SsaRankSelector.EstimateSignalRank(ev, SsaRankSelectionMethod.CumulativeEnergy, targetEnergy: 0.99, maxRank: 2);
        Assert.Equal(2, rank);
    }

    [Fact]
    public void SsaDiagnostics_WCorrelation_OrthogonalSignals_NearZeroCorrelation()
    {
        int w = 64;
        int l = 16;
        int r = 2;

        // Two orthogonal harmonic components: sin(t) and cos(t)
        double[] components = new double[r * w];
        for (int t = 0; t < w; t++)
        {
            components[0 * w + t] = Math.Sin(2.0 * Math.PI * t / 16.0);
            components[1 * w + t] = Math.Cos(2.0 * Math.PI * t / 16.0);
        }

        double[] wCorr = new double[r * r];
        SsaDiagnostics.ComputeWCorrelationMatrix(components, r, w, l, wCorr);

        // Diagonals must be exactly 1.0
        Assert.Equal(1.0, wCorr[0 * r + 0], precision: 6);
        Assert.Equal(1.0, wCorr[1 * r + 1], precision: 6);

        // Off-diagonals should be near zero for orthogonal sine/cosine over complete cycles
        Assert.True(Math.Abs(wCorr[0 * r + 1]) < 0.15);
        Assert.Equal(wCorr[0 * r + 1], wCorr[1 * r + 0]); // Symmetric
    }

    [Fact]
    public void SsaDiagnostics_WCorrelation_IdenticalSignals_CorrelationOne()
    {
        int w = 64;
        int l = 16;
        int r = 2;

        double[] components = new double[r * w];
        for (int t = 0; t < w; t++)
        {
            double val = Math.Sin(2.0 * Math.PI * t / 16.0);
            components[0 * w + t] = val;
            components[1 * w + t] = val;
        }

        double[] wCorr = new double[r * r];
        SsaDiagnostics.ComputeWCorrelationMatrix(components, r, w, l, wCorr);

        Assert.Equal(1.0, wCorr[0 * r + 0], precision: 6);
        Assert.Equal(1.0, wCorr[1 * r + 1], precision: 6);
        Assert.Equal(1.0, wCorr[0 * r + 1], precision: 6);
        Assert.Equal(1.0, wCorr[1 * r + 0], precision: 6);
    }

    [Fact]
    public void SsaDiagnostics_WCorrelation_LargeWindow_ArrayPoolFallback_Succeeds()
    {
        int w = 300; // > 256 triggers ArrayPool path
        int l = 50;
        int r = 2;

        double[] components = new double[r * w];
        for (int t = 0; t < w; t++)
        {
            components[0 * w + t] = Math.Sin(2.0 * Math.PI * t / 25.0);
            components[1 * w + t] = Math.Cos(2.0 * Math.PI * t / 25.0);
        }

        double[] wCorr = new double[r * r];
        SsaDiagnostics.ComputeWCorrelationMatrix(components, r, w, l, wCorr);

        Assert.Equal(1.0, wCorr[0 * r + 0], precision: 6);
        Assert.Equal(1.0, wCorr[1 * r + 1], precision: 6);
        Assert.True(Math.Abs(wCorr[0 * r + 1]) < 0.1);
    }

    [Fact]
    public void SsaDiagnostics_WCorrelation_ZeroComponent_SafeZeroCorrelation()
    {
        int w = 64;
        int l = 16;
        int r = 2;

        double[] components = new double[r * w];
        // Comp 0 is zero, comp 1 is sine
        for (int t = 0; t < w; t++)
        {
            components[0 * w + t] = 0.0;
            components[1 * w + t] = Math.Sin(2.0 * Math.PI * t / 16.0);
        }

        double[] wCorr = new double[r * r];
        SsaDiagnostics.ComputeWCorrelationMatrix(components, r, w, l, wCorr);

        Assert.Equal(1.0, wCorr[0 * r + 0]);
        Assert.Equal(1.0, wCorr[1 * r + 1], precision: 6);
        Assert.Equal(0.0, wCorr[0 * r + 1]);
        Assert.Equal(0.0, wCorr[1 * r + 0]);
    }

    [Fact]
    public void Test_SeparabilityScore_SingleComponent_Returns100()
    {
        double[] dummy = new double[] { 1.0 };
        double score = SsaDiagnostics.ComputeSeparabilityScore(dummy, 1);
        Assert.Equal(100.0, score);
        Assert.Equal("Excellent", SsaDiagnostics.GetSeparabilityGrade(score));
    }

    [Fact]
    public void Test_SeparabilityScore_OrthogonalPairs_Returns100()
    {
        // 2x2 identity correlation matrix (orthogonal)
        double[] wCorr = new double[]
        {
            1.0, 0.0,
            0.0, 1.0
        };
        double score = SsaDiagnostics.ComputeSeparabilityScore(wCorr, 2);
        Assert.Equal(100.0, score);
        Assert.Equal("Excellent", SsaDiagnostics.GetSeparabilityGrade(score));
    }

    [Fact]
    public void Test_SeparabilityScore_IdenticalPairs_ReturnsZero()
    {
        // 2x2 all 1.0 matrix (completely entangled)
        double[] wCorr = new double[]
        {
            1.0, 1.0,
            1.0, 1.0
        };
        double score = SsaDiagnostics.ComputeSeparabilityScore(wCorr, 2);
        Assert.Equal(0.0, score);
        Assert.Equal("Poor", SsaDiagnostics.GetSeparabilityGrade(score));
    }

    [Fact]
    public void Test_SeparabilityScore_InvalidLength_ThrowsArgumentException()
    {
        double[] tooShort = new double[] { 1.0, 0.0, 0.0 }; // needs 4 for r=2
        Assert.Throws<ArgumentException>(() => SsaDiagnostics.ComputeSeparabilityScore(tooShort, 2));
    }

    [Fact]
    public void Test_VectorSsa_SinusoidalInput_SmoothOscillation()
    {
        int n = 60;
        var samples = new List<double>(n);
        var timestamps = new List<DateTime>(n);
        var baseDate = new DateTime(2024, 1, 1);
        double meanPrice = 100.0;
        double amplitude = 5.0;

        for (int i = 0; i < n; i++)
        {
            samples.Add(meanPrice + amplitude * Math.Sin(2.0 * Math.PI * i / 12.0));
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
            confidenceMultiplier: 2.0m,
            forecastMode: SsaForecastMode.Vector);

        Assert.Equal(13, result.ProjectedPoints.Count);
        Assert.True(result.IsStable);

        for (int m = 1; m <= 12; m++)
        {
            double projected = result.ProjectedPoints[m].Y;
            Assert.InRange(projected, meanPrice - amplitude * 2.0, meanPrice + amplitude * 2.0);
            Assert.True(result.UpperBandPoints[m].Y >= projected);
            Assert.True(result.LowerBandPoints[m].Y <= projected);
        }
    }

    [Fact]
    public void Test_SeparabilityScore_HalfOpenIntervalGrades()
    {
        Assert.Equal("Excellent", SsaDiagnostics.GetSeparabilityGrade(100.0));
        Assert.Equal("Excellent", SsaDiagnostics.GetSeparabilityGrade(90.0));
        Assert.Equal("Good", SsaDiagnostics.GetSeparabilityGrade(89.999));
        Assert.Equal("Good", SsaDiagnostics.GetSeparabilityGrade(75.0));
        Assert.Equal("Moderate", SsaDiagnostics.GetSeparabilityGrade(74.999));
        Assert.Equal("Moderate", SsaDiagnostics.GetSeparabilityGrade(60.0));
        Assert.Equal("Poor", SsaDiagnostics.GetSeparabilityGrade(59.999));
        Assert.Equal("Poor", SsaDiagnostics.GetSeparabilityGrade(0.0));
    }

    [Fact]
    public void Test_VectorSsa_ConstantSeries_PreservesConstant()
    {
        int n = 40;
        var samples = new List<double>(n);
        var timestamps = new List<DateTime>(n);
        var baseDate = new DateTime(2024, 1, 1);
        double constantPrice = 125.50;

        for (int i = 0; i < n; i++)
        {
            samples.Add(constantPrice);
            timestamps.Add(baseDate.AddDays(i));
        }

        var result = SsaProjectionAnalysis.CalculateProjection(
            samples,
            timestamps,
            futureSteps: 10,
            embeddingDimension: 10,
            numComponents: 2,
            forecastMode: SsaForecastMode.Vector);

        Assert.Equal(11, result.ProjectedPoints.Count);
        for (int m = 0; m <= 10; m++)
        {
            Assert.InRange(result.ProjectedPoints[m].Y, constantPrice - 0.05, constantPrice + 0.05);
        }
    }

    [Fact]
    public void Test_VectorSsa_LinearTrend_PreservesSlope()
    {
        int n = 50;
        var samples = new List<double>(n);
        var timestamps = new List<DateTime>(n);
        var baseDate = new DateTime(2024, 1, 1);
        double intercept = 50.0;
        double slope = 1.5;

        for (int i = 0; i < n; i++)
        {
            samples.Add(intercept + slope * i);
            timestamps.Add(baseDate.AddDays(i));
        }

        var result = SsaProjectionAnalysis.CalculateProjection(
            samples,
            timestamps,
            futureSteps: 10,
            embeddingDimension: 12,
            numComponents: 2,
            detrendMode: SsaDetrendMode.LeastSquaresLinear,
            forecastMode: SsaForecastMode.Vector);

        Assert.Equal(11, result.ProjectedPoints.Count);
        for (int h = 1; h <= 10; h++)
        {
            double expected = intercept + slope * (n - 1 + h);
            Assert.InRange(result.ProjectedPoints[h].Y, expected - 0.5, expected + 0.5);
        }
    }

    [Fact]
    public void Test_VectorSsa_ExtremeSingularity_ClampedWithoutNaN()
    {
        int n = 40;
        var samples = new List<double>(n);
        var timestamps = new List<DateTime>(n);
        var baseDate = new DateTime(2024, 1, 1);

        for (int i = 0; i < n; i++)
        {
            samples.Add(100.0 + (i % 3 == 0 ? 10.0 : (i % 3 == 1 ? -10.0 : 0.0)));
            timestamps.Add(baseDate.AddDays(i));
        }

        // Embedding dimension close to sample count and high number of components to test singularity
        var result = SsaProjectionAnalysis.CalculateProjection(
            samples,
            timestamps,
            futureSteps: 30,
            embeddingDimension: 18,
            numComponents: 18,
            detrendMode: SsaDetrendMode.LeastSquaresLinear,
            forecastMode: SsaForecastMode.Vector);

        Assert.Equal(31, result.ProjectedPoints.Count);
        foreach (var p in result.ProjectedPoints)
        {
            Assert.False(double.IsNaN(p.Y));
            Assert.False(double.IsInfinity(p.Y));
        }
    }
}

