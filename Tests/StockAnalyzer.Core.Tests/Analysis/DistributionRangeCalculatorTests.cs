using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

public sealed class DistributionRangeCalculatorTests
{
    private static readonly DateTime BaseTime = new(2026, 1, 1);

    private static List<CoreCandleData> MakeCandles(IReadOnlyList<decimal> closes)
    {
        var list = new List<CoreCandleData>(closes.Count);
        for (int i = 0; i < closes.Count; i++)
        {
            decimal c = closes[i];
            list.Add(new CoreCandleData(BaseTime.AddDays(i), c - 0.1m, c + 0.5m, c - 0.5m, c, 1000L + i));
        }
        return list;
    }

    private static List<CoreCandleData> MakeWavyCandles(int count, decimal basePrice = 100m)
    {
        var closes = new decimal[count];
        for (int i = 0; i < count; i++)
        {
            double wave = Math.Sin(i * 0.37) * 4.2 + Math.Cos(i * 0.13) * 2.1;
            closes[i] = basePrice + i * 0.25m + Math.Round((decimal)wave, 4);
        }
        return MakeCandles(closes);
    }

    [Fact]
    public void Calculate_ClosedRangeContract_IgnoresBarImmediatelyBeforeStart()
    {
        var candles = MakeWavyCandles(30);
        // Poison bar just before range start to prove it is never accessed.
        DateTime start = candles[10].Timestamp;
        DateTime end = candles[25].Timestamp;
        var poisoned = new List<CoreCandleData>(candles);
        poisoned[9] = candles[9] with { Close = 999999m };

        DistributionRangeResult fromFull = DistributionRangeCalculator.Calculate(poisoned, start, end, DistributionRangeParameters.Default);
        DistributionRangeResult fromSlice = DistributionRangeCalculator.Calculate(poisoned.GetRange(10, 16), DistributionRangeParameters.Default);

        Assert.Equal(15, fromFull.SampleCount);
        Assert.Equal(fromSlice.SampleCount, fromFull.SampleCount);
        Assert.Equal(fromSlice.Mean, fromFull.Mean);
        Assert.Equal(fromSlice.StdDev, fromFull.StdDev);
        Assert.Equal(fromSlice.Skewness, fromFull.Skewness);
        Assert.Equal(fromSlice.ExcessKurtosis, fromFull.ExcessKurtosis);
        Assert.Equal(fromSlice.Bins.Count, fromFull.Bins.Count);
    }

    [Fact]
    public void Calculate_ReversedEndpoints_ProduceIdenticalResult()
    {
        var candles = MakeWavyCandles(25);
        var forward = DistributionRangeCalculator.Calculate(candles, candles[3].Timestamp, candles[20].Timestamp);
        var reversed = DistributionRangeCalculator.Calculate(candles, candles[20].Timestamp, candles[3].Timestamp);

        Assert.Equal(forward.SampleCount, reversed.SampleCount);
        Assert.Equal(forward.Mean, reversed.Mean);
        Assert.Equal(forward.StdDev, reversed.StdDev);
        Assert.Equal(forward.Skewness, reversed.Skewness);
        Assert.Equal(forward.ExcessKurtosis, reversed.ExcessKurtosis);
    }

    [Theory]
    [InlineData(DistributionDataMode.LogReturn)]
    [InlineData(DistributionDataMode.SimpleReturn)]
    public void Calculate_InvalidReturnPairs_AreSkippedWithoutBridgingAcrossGap(DistributionDataMode mode)
    {
        // Close = [100, 110, 0, 120, 132, 145]
        // Bar index 2 has Close = 0 -> pairs (1,2) and (2,3) for LogReturn or (2,3) for SimpleReturn cannot bridge 110 -> 120.
        var candles = MakeCandles(new[] { 100m, 110m, 0m, 120m, 132m, 145m });
        var p = new DistributionRangeParameters { DataMode = mode };

        DistributionRangeResult result = DistributionRangeCalculator.Calculate(candles, p);

        if (mode == DistributionDataMode.LogReturn)
        {
            // Valid pairs: (100->110), (120->132), (132->145) => 3 returns (never 110->120)
            Assert.Equal(3, result.SampleCount);
            double r0 = Math.Log(110.0 / 100.0);
            double r1 = Math.Log(132.0 / 120.0);
            double r2 = Math.Log(145.0 / 132.0);
            decimal expectedMean = IndicatorCalculationHelper.ToDecimalOrNull((r0 + r1 + r2) / 3.0)!.Value;
            Assert.Equal(expectedMean, result.Mean);
        }
        else
        {
            // SimpleReturn: (100->110) = +0.1, (110->0) = -1.0, skip (0->120), (120->132) = +0.1, (132->145) = 13/132 => 4 returns
            Assert.Equal(4, result.SampleCount);
        }
    }

    [Fact]
    public void Calculate_OneBarAndTwoBarsInReturnMode_AreInsufficientData()
    {
        var candles = MakeCandles(new[] { 100m, 105m });
        var r1 = DistributionRangeCalculator.Calculate(candles.GetRange(0, 1));
        var r2 = DistributionRangeCalculator.Calculate(candles);

        Assert.Equal(0, r1.SampleCount);
        Assert.Equal(ReferenceFitStatus.InsufficientData, r1.ReferenceStatus);
        Assert.Empty(r1.Bins);
        Assert.Empty(r1.KdeCurve);
        Assert.Empty(r1.ReferenceCurve);

        Assert.Equal(1, r2.SampleCount);
        Assert.NotNull(r2.Mean);
        Assert.Null(r2.Skewness);
        Assert.Null(r2.ExcessKurtosis);
        Assert.Equal(ReferenceFitStatus.InsufficientData, r2.ReferenceStatus);
        Assert.Empty(r2.Bins);
        Assert.Empty(r2.KdeCurve);
        Assert.Empty(r2.ReferenceCurve);
    }

    [Fact]
    public void Calculate_TwoAndThreeSamples_RespectMinimumCountsForSkewnessAndKurtosis()
    {
        var candles = MakeCandles(new[] { 100m, 102m, 108m, 105m, 115m });
        var r2 = DistributionRangeCalculator.Calculate(candles.GetRange(0, 3)); // 2 returns
        var r3 = DistributionRangeCalculator.Calculate(candles.GetRange(0, 4)); // 3 returns
        var r4 = DistributionRangeCalculator.Calculate(candles); // 4 returns

        Assert.Equal(2, r2.SampleCount);
        Assert.Null(r2.Skewness);
        Assert.Null(r2.ExcessKurtosis);

        Assert.Equal(3, r3.SampleCount);
        Assert.NotNull(r3.Skewness);
        Assert.Null(r3.ExcessKurtosis);

        Assert.Equal(4, r4.SampleCount);
        Assert.NotNull(r4.Skewness);
        Assert.NotNull(r4.ExcessKurtosis);
    }

    [Fact]
    public void Calculate_FlatSeries_ReturnsFlatSeriesWithoutBinsOrCurves()
    {
        var flatPrices = MakeCandles(Enumerable.Repeat(100m, 20).ToArray());
        var result = DistributionRangeCalculator.Calculate(flatPrices, new DistributionRangeParameters
        {
            DataMode = DistributionDataMode.Price
        });

        Assert.Equal(20, result.SampleCount);
        Assert.Equal(ReferenceFitStatus.FlatSeries, result.ReferenceStatus);
        Assert.Equal(0m, result.StdDev);
        Assert.Empty(result.Bins);
        Assert.Empty(result.KdeCurve);
        Assert.Empty(result.ReferenceCurve);
    }

    [Theory]
    [InlineData(DistributionBinningRule.FreedmanDiaconis)]
    [InlineData(DistributionBinningRule.Sturges)]
    [InlineData(DistributionBinningRule.Scott)]
    [InlineData(DistributionBinningRule.FixedCount)]
    public void Calculate_BinningRules_RespectClampAndDensityIntegral(DistributionBinningRule rule)
    {
        var candles = MakeWavyCandles(60);
        var p = new DistributionRangeParameters
        {
            BinningRule = rule,
            FixedBinCount = 15,
            IncludeKde = true
        };

        var result = DistributionRangeCalculator.Calculate(candles, p);

        Assert.Equal(59, result.SampleCount);
        Assert.False(result.IsLowSampleSize);
        Assert.InRange(result.Bins.Count, DistributionRangeParameters.MinFixedBinCount, DistributionRangeParameters.MaxFixedBinCount);
        if (rule == DistributionBinningRule.FixedCount)
        {
            Assert.Equal(15, result.Bins.Count);
        }

        Assert.Equal(result.SampleCount, result.Bins.Sum(b => b.Count));
        decimal integral = result.Bins.Sum(b => b.Density * (b.UpperBound - b.LowerBound));
        Assert.InRange(integral, 0.99m, 1.01m);

        Assert.Equal(DistributionRangeParameters.CurveSamplePoints, result.KdeCurve.Count);
        Assert.Equal(result.MinValue, result.KdeCurve[0].X);
        Assert.Equal(result.MaxValue, result.KdeCurve[^1].X);
        Assert.All(result.KdeCurve, pt => Assert.True(pt.Density >= 0m));
    }

    [Fact]
    public void Calculate_PositiveOnlyDistributions_ReturnDomainViolationOnNegativeReturns_WhileKeepingHistogramAndKde()
    {
        var candles = MakeWavyCandles(40);
        ReferenceDistributionType[] positiveOnly =
        {
            ReferenceDistributionType.LogNormal,
            ReferenceDistributionType.Gamma,
            ReferenceDistributionType.Weibull,
            ReferenceDistributionType.Exponential,
            ReferenceDistributionType.Pareto
        };

        foreach (var dist in positiveOnly)
        {
            var result = DistributionRangeCalculator.Calculate(candles, new DistributionRangeParameters
            {
                DataMode = DistributionDataMode.LogReturn,
                ReferenceDistribution = dist,
                IncludeKde = true
            });

            Assert.Equal(ReferenceFitStatus.DomainViolation, result.ReferenceStatus);
            Assert.Empty(result.ReferenceCurve);
            Assert.NotEmpty(result.Bins);
            Assert.Equal(DistributionRangeParameters.CurveSamplePoints, result.KdeCurve.Count);
        }
    }

    [Theory]
    [InlineData(ReferenceDistributionType.Normal)]
    [InlineData(ReferenceDistributionType.StudentT)]
    [InlineData(ReferenceDistributionType.Laplace)]
    [InlineData(ReferenceDistributionType.Logistic)]
    [InlineData(ReferenceDistributionType.Cauchy)]
    [InlineData(ReferenceDistributionType.Gev)]
    [InlineData(ReferenceDistributionType.Uniform)]
    [InlineData(ReferenceDistributionType.LogNormal)]
    [InlineData(ReferenceDistributionType.Gamma)]
    [InlineData(ReferenceDistributionType.Weibull)]
    [InlineData(ReferenceDistributionType.Exponential)]
    [InlineData(ReferenceDistributionType.Pareto)]
    public void Calculate_AllSupportedDistributions_FitOnPositivePrices_WithExactMinMaxEndpoints(ReferenceDistributionType dist)
    {
        var candles = MakeWavyCandles(45, basePrice: 120m);
        var result = DistributionRangeCalculator.Calculate(candles, new DistributionRangeParameters
        {
            DataMode = DistributionDataMode.Price,
            ReferenceDistribution = dist
        });

        Assert.Equal(ReferenceFitStatus.Fitted, result.ReferenceStatus);
        Assert.Equal(DistributionRangeParameters.CurveSamplePoints, result.ReferenceCurve.Count);
        Assert.Equal(result.MinValue, result.ReferenceCurve[0].X);
        Assert.Equal(result.MaxValue, result.ReferenceCurve[^1].X);
        Assert.All(result.ReferenceCurve, pt => Assert.True(pt.Density >= 0m));
        Assert.Contains(result.ReferenceCurve, pt => pt.Density > 0m);
    }

    [Theory]
    [InlineData(-0.5)]
    [InlineData(0.0)]
    [InlineData(0.45)]
    public void Calculate_GevShapes_AcrossSupportedRange_FitSafely(double xiDouble)
    {
        var candles = MakeWavyCandles(50, basePrice: 100m);
        var result = DistributionRangeCalculator.Calculate(candles, new DistributionRangeParameters
        {
            DataMode = DistributionDataMode.LogReturn,
            ReferenceDistribution = ReferenceDistributionType.Gev,
            GevShapeParameter = (decimal)xiDouble
        });

        Assert.Equal(ReferenceFitStatus.Fitted, result.ReferenceStatus);
        Assert.Equal(DistributionRangeParameters.CurveSamplePoints, result.ReferenceCurve.Count);
        Assert.All(result.ReferenceCurve, pt => Assert.True(pt.Density >= 0m));
    }

    [Fact]
    public void Calculate_CauchyZeroIqr_UsesMadFallbackSafely()
    {
        // Construct >50% identical values in the middle so IQR == 0, while outer points keep non-zero spread.
        var prices = Enumerable.Repeat(100m, 14).ToList();
        prices[0] = 90m;
        prices[1] = 92m;
        prices[^2] = 112m;
        prices[^1] = 115m;
        var candles = MakeCandles(prices);

        var result = DistributionRangeCalculator.Calculate(candles, new DistributionRangeParameters
        {
            DataMode = DistributionDataMode.Price,
            ReferenceDistribution = ReferenceDistributionType.Cauchy
        });

        Assert.Equal(ReferenceFitStatus.Fitted, result.ReferenceStatus);
        Assert.Equal(DistributionRangeParameters.CurveSamplePoints, result.ReferenceCurve.Count);
    }

    [Fact]
    public void Calculate_ParetoLeftEndpoint_HandlesDoubleToDecimalRoundingPrecision()
    {
        // Odd decimal values whose double round-trip sits near precision limits.
        var candles = MakeCandles(new[] { 100.123456789m, 101.234567891m, 103.5m, 108.9m, 114.25m, 121.75m, 135.4m });
        var result = DistributionRangeCalculator.Calculate(candles, new DistributionRangeParameters
        {
            DataMode = DistributionDataMode.Price,
            ReferenceDistribution = ReferenceDistributionType.Pareto
        });

        Assert.Equal(ReferenceFitStatus.Fitted, result.ReferenceStatus);
        Assert.Equal(result.MinValue, result.ReferenceCurve[0].X);
        Assert.True(result.ReferenceCurve[0].Density > 0m);
    }

    [Fact]
    public void Calculate_StudentTOverride_ClampsWithinSupportedBounds()
    {
        var candles = MakeWavyCandles(40);
        var tooLow = DistributionRangeCalculator.Calculate(candles, new DistributionRangeParameters
        {
            ReferenceDistribution = ReferenceDistributionType.StudentT,
            OverrideStudentTDegreesOfFreedom = true,
            StudentTDegreesOfFreedom = 0.5m
        });
        var clampedMin = DistributionRangeCalculator.Calculate(candles, new DistributionRangeParameters
        {
            ReferenceDistribution = ReferenceDistributionType.StudentT,
            OverrideStudentTDegreesOfFreedom = true,
            StudentTDegreesOfFreedom = DistributionRangeParameters.MinStudentTDegreesOfFreedom
        });

        Assert.Equal(tooLow.ReferenceParameterSummary, clampedMin.ReferenceParameterSummary);
        Assert.Contains("df=2.1", tooLow.ReferenceParameterSummary, StringComparison.Ordinal);
        Assert.Equal(clampedMin.ReferenceCurve, tooLow.ReferenceCurve);
    }

    [Fact]
    public void Calculate_ExceedingStackAllocThreshold_UsesArrayPoolCleanly()
    {
        var candles = MakeWavyCandles(MathBufferLimits.StackAllocThreshold + 150);
        var result = DistributionRangeCalculator.Calculate(candles, DistributionRangeParameters.Default);

        Assert.Equal(MathBufferLimits.StackAllocThreshold + 149, result.SampleCount);
        Assert.False(result.IsLowSampleSize);
        Assert.Equal(ReferenceFitStatus.Fitted, result.ReferenceStatus);
        Assert.NotEmpty(result.Bins);
        Assert.Equal(DistributionRangeParameters.CurveSamplePoints, result.KdeCurve.Count);
        Assert.Equal(DistributionRangeParameters.CurveSamplePoints, result.ReferenceCurve.Count);
    }
}
