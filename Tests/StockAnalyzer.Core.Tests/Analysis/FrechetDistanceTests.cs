using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

public class FrechetDistanceTests
{
    [Fact]
    public void CalculateDiscreteFrechetDistance_IdenticalSeries_ReturnsZero()
    {
        double[] p = { 10.0, 15.0, 20.0, 18.0, 25.0, 30.0 };
        double[] q = { 10.0, 15.0, 20.0, 18.0, 25.0, 30.0 };

        double distance = FrechetMath.CalculateDiscreteFrechetDistance(p, q);

        Assert.Equal(0.0, distance, 6);
    }

    [Fact]
    public void CalculateDiscreteFrechetDistance_ConstantShift_ReturnsExactShift()
    {
        double[] p = { 10.0, 15.0, 20.0, 18.0, 25.0, 30.0 };
        double shift = 5.25;
        double[] q = new double[p.Length];
        for (int i = 0; i < p.Length; i++)
        {
            q[i] = p[i] + shift;
        }

        double distance = FrechetMath.CalculateDiscreteFrechetDistance(p, q);

        Assert.Equal(shift, distance, 6);
    }

    [Fact]
    public void CalculateDiscreteFrechetDistance_EmptyOrSingle_HandlesBoundaries()
    {
        // Empty inputs
        Assert.Throws<ArgumentException>(() =>
            FrechetMath.CalculateDiscreteFrechetDistance(ReadOnlySpan<double>.Empty, new double[] { 1.0, 2.0 }));
        Assert.Throws<ArgumentException>(() =>
            FrechetMath.CalculateDiscreteFrechetDistance(new double[] { 1.0, 2.0 }, ReadOnlySpan<double>.Empty));

        // Single elements
        double dSingle = FrechetMath.CalculateDiscreteFrechetDistance(new double[] { 10.0 }, new double[] { 14.5 });
        Assert.Equal(4.5, dSingle, 6);

        // Different lengths
        double[] p = { 1.0, 2.0, 3.0, 4.0, 5.0 };
        double[] q = { 1.0, 3.0, 5.0 };
        double dDiff = FrechetMath.CalculateDiscreteFrechetDistance(p, q);
        Assert.True(dDiff >= 0.0);
    }

    [Fact]
    public void CalculateDiscreteFrechetDistance_WithNaN_ReturnsNaN()
    {
        double[] p = { 1.0, double.NaN, 3.0 };
        double[] q = { 1.0, 2.0, 3.0 };

        double d1 = FrechetMath.CalculateDiscreteFrechetDistance(p, q);
        double d2 = FrechetMath.CalculateDiscreteFrechetDistance(q, p);

        Assert.True(double.IsNaN(d1));
        Assert.True(double.IsNaN(d2));
    }

    [Fact]
    public void CalculateDiscreteFrechetDistance2D_IdenticalAndShifted_CalculatesEuclideanDistances()
    {
        var p = new (double T, double P)[]
        {
            (0.0, 10.0),
            (1.0, 15.0),
            (2.0, 20.0),
            (3.0, 25.0)
        };
        var q = new (double T, double P)[]
        {
            (0.0, 10.0),
            (1.0, 15.0),
            (2.0, 20.0),
            (3.0, 25.0)
        };

        double dIdentical = FrechetMath.CalculateDiscreteFrechetDistance2D(p, q);
        Assert.Equal(0.0, dIdentical, 6);

        // Constant Y shift of 3.0
        var qShifted = new (double T, double P)[]
        {
            (0.0, 13.0),
            (1.0, 18.0),
            (2.0, 23.0),
            (3.0, 28.0)
        };

        double dShifted = FrechetMath.CalculateDiscreteFrechetDistance2D(p, qShifted);
        Assert.Equal(3.0, dShifted, 6);
    }

    [Fact]
    public void CalculateDiscreteFrechetDistance_ZeroAllocation_AllocatesZeroBytes()
    {
        // Pre-warm method and JIT
        double[] p = new double[200];
        double[] q = new double[200];
        for (int i = 0; i < 200; i++)
        {
            p[i] = Math.Sin(i * 0.1);
            q[i] = Math.Sin(i * 0.1 + 0.2);
        }
        _ = FrechetMath.CalculateDiscreteFrechetDistance(p, q);

        // Measure GC allocation during span calculation
        long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
        double dist = FrechetMath.CalculateDiscreteFrechetDistance(p.AsSpan(), q.AsSpan());
        long afterAlloc = GC.GetAllocatedBytesForCurrentThread();

        Assert.True(dist > 0.0);
        Assert.Equal(0, afterAlloc - beforeAlloc);
    }

    [Fact]
    public void CalculateProjection_NoLookaheadLeakage_SatisfiesPrecedingCondition()
    {
        int totalBars = 120;
        var candles = new List<CoreCandleData>(totalBars);
        var baseDate = new DateTime(2024, 1, 1);

        for (int i = 0; i < totalBars; i++)
        {
            decimal p = 100m + i * 0.1m;
            candles.Add(new CoreCandleData(baseDate.AddDays(i), p, p + 1m, p - 1m, p, 1000));
        }

        // Inject distinct "V-shape" pattern in past: index 20 to 30 (11 bars)
        for (int i = 0; i <= 10; i++)
        {
            decimal shapeVal = i <= 5 ? -i * 2.0m : -(10 - i) * 2.0m;
            decimal p = 100m + shapeVal;
            candles[20 + i] = new CoreCandleData(baseDate.AddDays(20 + i), p, p + 1m, p - 1m, p, 1000);
        }
        // Rally after past V-shape
        for (int k = 1; k <= 15; k++)
        {
            decimal p = candles[30].Close + k * 1.5m;
            candles[30 + k] = new CoreCandleData(baseDate.AddDays(30 + k), p, p + 1m, p - 1m, p, 1000);
        }

        // Replicate same V-shape pattern at current query: index 80 to 90 (11 bars)
        for (int i = 0; i <= 10; i++)
        {
            decimal shapeVal = i <= 5 ? -i * 2.0m : -(10 - i) * 2.0m;
            decimal p = 150m + shapeVal * 1.5m;
            candles[80 + i] = new CoreCandleData(baseDate.AddDays(80 + i), p, p + 1m, p - 1m, p, 1000);
        }

        int queryStart = 80;
        int queryEnd = 90;
        int horizon = 10;

        var result = FrechetDistanceAnalysis.CalculateProjection(
            candles,
            queryStartIndex: queryStart,
            queryEndIndex: queryEnd,
            horizon: horizon,
            priceType: PriceType.Close);

        Assert.NotNull(result);
        Assert.Equal(20, result.MatchedStartIndex);
        Assert.Equal(30, result.MatchedEndIndex);
        Assert.Equal(horizon, result.Projections.Count);

        // Strict non-lookahead check: matched pattern's future horizon must strictly precede queryStart
        Assert.True(result.MatchedEndIndex + horizon < queryStart);

        // Check that projections contain valid positive prices and bounds
        foreach (var proj in result.Projections)
        {
            Assert.True(proj.PredictedPrice > 0m);
            Assert.True(proj.UpperBand >= proj.PredictedPrice);
            Assert.True(proj.LowerBand <= proj.PredictedPrice);
            Assert.True(proj.LowerBand >= 0m);
        }
    }

    [Fact]
    public void CalculateProjection_InvalidInputs_ReturnsNull()
    {
        var candles = new List<CoreCandleData>();
        var result1 = FrechetDistanceAnalysis.CalculateProjection(candles, 0, 10, 5);
        Assert.Null(result1);

        // Insufficient data for horizon
        candles.Add(new CoreCandleData(DateTime.Now, 10m, 11m, 9m, 10m, 100));
        candles.Add(new CoreCandleData(DateTime.Now.AddDays(1), 10m, 11m, 9m, 10m, 100));
        var result2 = FrechetDistanceAnalysis.CalculateProjection(candles, 0, 1, 5);
        Assert.Null(result2);
    }

    [Fact]
    public void CoreFrechetOscillatorIndicator_WarmupPeriod_ReturnsNulls()
    {
        var indicator = new CoreFrechetOscillatorIndicator
        {
            Period = 10,
            Lag = 5
        };

        var candles = new List<CoreCandleData>();
        for (int i = 0; i < 30; i++)
        {
            decimal p = 100m + (decimal)Math.Sin(i * 0.2) * 5m;
            candles.Add(new CoreCandleData(DateTime.Now.AddDays(i), p, p + 1m, p - 1m, p, 1000));
        }

        var result = indicator.Calculate(candles);
        Assert.True(result.IsSuccessful);
        Assert.Equal(30, indicator.Values.Count);

        // Warmup: first Period + Lag - 2 indices (0 to 13) must be null
        for (int i = 0; i < 10 + 5 - 1; i++)
        {
            Assert.Null(indicator.Values[i]);
        }

        // Indices from 14 onwards must have calculated values >= 0
        for (int i = 14; i < 30; i++)
        {
            Assert.NotNull(indicator.Values[i]);
            Assert.True(indicator.Values[i] >= 0m);
        }
    }

    [Fact]
    public void CoreFrechetOscillatorIndicator_Factory_IsRegistered()
    {
        var factory = IndicatorFactory.Default;
        Assert.True(factory.IsRegistered(IndicatorType.FrechetOscillator));

        var indicator = factory.Create(IndicatorType.FrechetOscillator, new CoreFrechetOscillatorParameter
        {
            Period = 15,
            Lag = 8
        });

        Assert.NotNull(indicator);
        Assert.IsType<CoreFrechetOscillatorIndicator>(indicator);
        var frechetInd = (CoreFrechetOscillatorIndicator)indicator;
        Assert.Equal(15, frechetInd.Period);
        Assert.Equal(8, frechetInd.Lag);
    }

    [Fact]
    public void CalculateProjection_WithFlatQuery_ReturnsNull()
    {
        var candles = new List<CoreCandleData>();
        var baseDate = new DateTime(2024, 1, 1);

        for (int i = 0; i < 50; i++)
        {
            decimal p = 100m; // completely flat
            candles.Add(new CoreCandleData(baseDate.AddDays(i), p, p, p, p, 1000));
        }

        var result = FrechetDistanceAnalysis.CalculateProjection(
            candles,
            queryStartIndex: 30,
            queryEndIndex: 40,
            horizon: 5);

        Assert.Null(result);
    }

    [Fact]
    public void CalculateProjection_WithPennyStockZeroNear_DoesNotThrowOrProduceNaN()
    {
        var candles = new List<CoreCandleData>();
        var baseDate = new DateTime(2024, 1, 1);

        for (int i = 0; i < 60; i++)
        {
            decimal p = 0.0001m + (decimal)Math.Sin(i * 0.5) * 0.00005m;
            candles.Add(new CoreCandleData(baseDate.AddDays(i), p, p + 0.00001m, p - 0.00001m, p, 100));
        }

        var result = FrechetDistanceAnalysis.CalculateProjection(
            candles,
            queryStartIndex: 40,
            queryEndIndex: 50,
            horizon: 5);

        Assert.NotNull(result);
        foreach (var proj in result.Projections)
        {
            Assert.False(double.IsNaN((double)proj.PredictedPrice));
            Assert.False(double.IsInfinity((double)proj.PredictedPrice));
            Assert.True(proj.PredictedPrice >= 0m);
            Assert.True(proj.UpperBand >= proj.PredictedPrice);
            Assert.True(proj.LowerBand >= 0m);
        }
    }

    [Fact]
    public void CoreFrechetOscillatorIndicator_WithFlatSeries_ProducesDeterministicZero()
    {
        var indicator = new CoreFrechetOscillatorIndicator
        {
            Period = 5,
            Lag = 5
        };

        var candles = new List<CoreCandleData>();
        for (int i = 0; i < 20; i++)
        {
            decimal p = 50m;
            candles.Add(new CoreCandleData(DateTime.Now.AddDays(i), p, p, p, p, 1000));
        }

        var result = indicator.Calculate(candles);
        Assert.True(result.IsSuccessful);

        // Indices from 9 onwards are 0.0m (both windows flat)
        for (int i = 9; i < 20; i++)
        {
            Assert.NotNull(indicator.Values[i]);
            Assert.Equal(0.0m, indicator.Values[i]!.Value);
        }
    }
}
