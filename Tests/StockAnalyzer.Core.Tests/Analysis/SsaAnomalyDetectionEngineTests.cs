using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

public class SsaAnomalyDetectionEngineTests
{
    private static (List<double> samples, List<DateTime> timestamps) GenerateSeries(int n, Func<int, double> generator)
    {
        var samples = new List<double>(n);
        var timestamps = new List<DateTime>(n);
        var baseDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (int i = 0; i < n; i++)
        {
            samples.Add(generator(i));
            timestamps.Add(baseDate.AddMinutes(i));
        }

        return (samples, timestamps);
    }

    [Fact]
    public void Test_Engine_EmptyAndBoundaryGuards()
    {
        // 1. Null inputs
        var res1 = SsaAnomalyDetectionEngine.CalculateAnomaly(null!, null!);
        Assert.True(res1.IsEmpty);

        // 2. Too short (< 4)
        var (s2, t2) = GenerateSeries(3, i => 100.0);
        var res2 = SsaAnomalyDetectionEngine.CalculateAnomaly(s2, t2);
        Assert.True(res2.IsEmpty);

        // 3. Mismatched lengths
        var (s3, t3) = GenerateSeries(10, i => 100.0);
        var res3 = SsaAnomalyDetectionEngine.CalculateAnomaly(s3, t3.Take(5).ToList());
        Assert.True(res3.IsEmpty);

        // 4. All NaNs
        var (s4, t4) = GenerateSeries(30, i => double.NaN);
        var res4 = SsaAnomalyDetectionEngine.CalculateAnomaly(s4, t4);
        Assert.True(res4.IsEmpty);
    }

    [Fact]
    public void Test_Engine_NormalSeries_NoAnomaliesDetected()
    {
        // Smooth sine wave + minimal deterministic variation
        int n = 80;
        var (samples, timestamps) = GenerateSeries(n, i => 100.0 + 10.0 * Math.Sin(2.0 * Math.PI * i / 20.0));

        var result = SsaAnomalyDetectionEngine.CalculateAnomaly(
            samples, timestamps,
            embeddingDimension: 15,
            numComponents: 2,
            autoRank: true,
            enterThreshold: 2.5,
            exitThreshold: 1.0);

        Assert.False(result.IsEmpty);
        Assert.Empty(result.Intervals);
        Assert.Equal(n, result.ReconstructedPoints.Count);
        Assert.Equal(n, result.UpperBandPoints.Count);
        Assert.Equal(n, result.LowerBandPoints.Count);
        Assert.Equal(n, result.ZScores.Count);
    }

    [Fact]
    public void Test_Engine_SuddenDrop_DetectsBearishAnomaly()
    {
        int n = 80;
        var (samples, timestamps) = GenerateSeries(n, i =>
        {
            double basePrice = 100.0 + 0.1 * i + 10.0 * Math.Sin(2.0 * Math.PI * i / 20.0);
            if (i >= 40 && i <= 43)
            {
                return basePrice - 20.0; // Sharp sudden drop
            }
            return basePrice;
        });

        var result = SsaAnomalyDetectionEngine.CalculateAnomaly(
            samples, timestamps,
            embeddingDimension: 15,
            numComponents: 2,
            autoRank: false,
            enterThreshold: 2.0,
            exitThreshold: 1.0,
            coolDownPeriod: 2,
            minDuration: 2);

        Assert.False(result.IsEmpty);
        Assert.NotEmpty(result.Intervals);

        var bearishInterval = result.Intervals.FirstOrDefault(x => x.Direction == SsaAnomalyDirection.Bearish);
        Assert.NotNull(bearishInterval);
        Assert.True(bearishInterval.StartIndex <= 41);
        Assert.True(bearishInterval.EndIndex >= 42);
        Assert.True(bearishInterval.PeakZ < -2.0);
        Assert.True(bearishInterval.MaxPriceDeviation < 0.0);
        Assert.True(bearishInterval.PercentDeviation < 0.0);
    }

    [Fact]
    public void Test_Engine_SuddenSpike_DetectsBullishAnomaly()
    {
        int n = 80;
        var (samples, timestamps) = GenerateSeries(n, i =>
        {
            double basePrice = 100.0 + 0.1 * i + 10.0 * Math.Sin(2.0 * Math.PI * i / 20.0);
            if (i >= 35 && i <= 38)
            {
                return basePrice + 20.0; // Sharp spike
            }
            return basePrice;
        });

        var result = SsaAnomalyDetectionEngine.CalculateAnomaly(
            samples, timestamps,
            embeddingDimension: 15,
            numComponents: 2,
            autoRank: false,
            enterThreshold: 2.0,
            exitThreshold: 1.0,
            coolDownPeriod: 2,
            minDuration: 2);

        Assert.False(result.IsEmpty);
        Assert.NotEmpty(result.Intervals);

        var bullishInterval = result.Intervals.FirstOrDefault(x => x.Direction == SsaAnomalyDirection.Bullish);
        Assert.NotNull(bullishInterval);
        Assert.True(bullishInterval.StartIndex <= 36);
        Assert.True(bullishInterval.EndIndex >= 37);
        Assert.True(bullishInterval.PeakZ > 2.0);
        Assert.True(bullishInterval.MaxPriceDeviation > 0.0);
        Assert.True(bullishInterval.PercentDeviation > 0.0);
    }

    [Fact]
    public void Test_Engine_DirectReversal_SplitsIntervals()
    {
        // Sequence with high spike at t=30..32 directly reversing to sharp drop at t=33..35
        int n = 80;
        var (samples, timestamps) = GenerateSeries(n, i =>
        {
            double basePrice = 100.0 + 0.1 * i + 10.0 * Math.Sin(2.0 * Math.PI * i / 20.0);
            if (i >= 30 && i <= 32) return basePrice + 25.0; // Bullish spike
            if (i >= 33 && i <= 35) return basePrice - 25.0; // Direct Bearish crash
            return basePrice;
        });

        var result = SsaAnomalyDetectionEngine.CalculateAnomaly(
            samples, timestamps,
            embeddingDimension: 15,
            numComponents: 2,
            autoRank: false,
            enterThreshold: 2.0,
            exitThreshold: 1.0,
            coolDownPeriod: 2,
            minDuration: 2);

        Assert.False(result.IsEmpty);
        Assert.True(result.Intervals.Count >= 2);

        var bullish = result.Intervals.FirstOrDefault(x => x.Direction == SsaAnomalyDirection.Bullish);
        var bearish = result.Intervals.FirstOrDefault(x => x.Direction == SsaAnomalyDirection.Bearish);

        Assert.NotNull(bullish);
        Assert.NotNull(bearish);
        Assert.True(bullish.EndIndex < bearish.StartIndex || bullish.EndIndex == bearish.StartIndex - 1);
    }

    [Fact]
    public void Test_Engine_AnomalyAtSeriesEnd_ClosedSafely()
    {
        // Anomaly occurring right at the end of the series
        int n = 60;
        var (samples, timestamps) = GenerateSeries(n, i =>
        {
            double basePrice = 100.0 + 0.1 * i + 10.0 * Math.Sin(2.0 * Math.PI * i / 20.0);
            if (i >= 56) return basePrice + 25.0; // Stays elevated till the very end
            return basePrice;
        });

        var result = SsaAnomalyDetectionEngine.CalculateAnomaly(
            samples, timestamps,
            embeddingDimension: 15,
            numComponents: 2,
            autoRank: false,
            enterThreshold: 2.0,
            exitThreshold: 1.0,
            minDuration: 2);

        Assert.False(result.IsEmpty);
        var lastInterval = result.Intervals.LastOrDefault();
        Assert.NotNull(lastInterval);
        Assert.Equal(SsaAnomalyDirection.Bullish, lastInterval.Direction);
        Assert.Equal(n - 1, lastInterval.EndIndex);
    }

    [Fact]
    public void Test_Engine_ScaleInvariance_HighAndLowPrice()
    {
        int n = 80;
        var (baseSamples, timestamps) = GenerateSeries(n, i =>
        {
            double p = 100.0 + 5.0 * Math.Sin(2.0 * Math.PI * i / 15.0);
            if (i >= 40 && i <= 43) p += 20.0;
            return p;
        });

        // Scale by 10^6 and 10^-6
        var highSamples = baseSamples.Select(x => x * 1e6).ToList();
        var lowSamples = baseSamples.Select(x => x * 1e-6).ToList();

        var resBase = SsaAnomalyDetectionEngine.CalculateAnomaly(baseSamples, timestamps, 15, 2, false, SsaDetrendMode.LeastSquaresLinear, 2.0, 1.0, 2, 2);
        var resHigh = SsaAnomalyDetectionEngine.CalculateAnomaly(highSamples, timestamps, 15, 2, false, SsaDetrendMode.LeastSquaresLinear, 2.0, 1.0, 2, 2);
        var resLow = SsaAnomalyDetectionEngine.CalculateAnomaly(lowSamples, timestamps, 15, 2, false, SsaDetrendMode.LeastSquaresLinear, 2.0, 1.0, 2, 2);

        Assert.Equal(resBase.Intervals.Count, resHigh.Intervals.Count);
        Assert.Equal(resBase.Intervals.Count, resLow.Intervals.Count);

        for (int i = 0; i < resBase.Intervals.Count; i++)
        {
            Assert.Equal(resBase.Intervals[i].StartIndex, resHigh.Intervals[i].StartIndex);
            Assert.Equal(resBase.Intervals[i].EndIndex, resHigh.Intervals[i].EndIndex);
            Assert.Equal(resBase.Intervals[i].Direction, resHigh.Intervals[i].Direction);
            Assert.InRange(Math.Abs(resBase.Intervals[i].PeakZ - resHigh.Intervals[i].PeakZ), 0.0, 1e-4);

            Assert.Equal(resBase.Intervals[i].StartIndex, resLow.Intervals[i].StartIndex);
            Assert.Equal(resBase.Intervals[i].EndIndex, resLow.Intervals[i].EndIndex);
            Assert.Equal(resBase.Intervals[i].Direction, resLow.Intervals[i].Direction);
            Assert.InRange(Math.Abs(resBase.Intervals[i].PeakZ - resLow.Intervals[i].PeakZ), 0.0, 1e-4);
        }
    }

    [Fact]
    public void Test_Engine_RawPeakZScore_PreservesUnclampedPeak()
    {
        int n = 80;
        var (samples, timestamps) = GenerateSeries(n, i =>
        {
            double basePrice = 100.0;
            if (i >= 40 && i <= 43) return 5000.0;
            return basePrice;
        });

        var result = SsaAnomalyDetectionEngine.CalculateAnomaly(
            samples, timestamps,
            embeddingDimension: 15,
            numComponents: 2,
            autoRank: false,
            enterThreshold: 2.0,
            exitThreshold: 1.0,
            coolDownPeriod: 2,
            minDuration: 2);

        Assert.False(result.IsEmpty);
        Assert.NotEmpty(result.Intervals);
        var interval = result.Intervals[0];
        Assert.True(interval.RawPeakZScore > 0);
        Assert.True(interval.PeakZ <= 100.0);
        Assert.True(interval.RawPeakZScore >= interval.PeakZ);
    }
}
