using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.ElliottWave;
using StockAnalyzer.Core.Models.MarketStructure;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class ElliottWaveCorrectiveWaveCTest
{
    private static PivotPoint[] CreatePivots(decimal p0, decimal p1, decimal p2, decimal p3)
    {
        DateTime baseDate = new(2025, 1, 1);
        return new[]
        {
            new PivotPoint(0, baseDate.AddDays(0), p0, isHigh: false),
            new PivotPoint(10, baseDate.AddDays(10), p1, isHigh: true),
            new PivotPoint(20, baseDate.AddDays(20), p2, isHigh: false),
            new PivotPoint(30, baseDate.AddDays(30), p3, isHigh: true),
        };
    }

    private static List<CandleData> CreateCandles(int count)
    {
        DateTime baseDate = new(2025, 1, 1);
        var candles = new List<CandleData>(count);
        for (int i = 0; i < count; i++)
        {
            candles.Add(new CandleData(baseDate.AddDays(i), 150m, 210m, 90m, 150m, 1000));
        }
        return candles;
    }

    [Fact]
    public void ScoreCorrectiveWave_WhenWaveCFailsFibonacciRange_ReturnsZero()
    {
        // wA = 200 - 100 = 100
        // wBRetrace = 200 - 150 = 50 (bRatio = 0.50, valid)
        // wC = 155 - 150 = 5 (cRatio = 0.05, far below 0.618 - 0.20 = 0.418, s2 = 0)
        var pivots = CreatePivots(100m, 200m, 150m, 155m);

        double score = ElliottWaveDetector.ScoreCorrectiveWave(pivots, isBullish: true);

        // Before fix: returns ~0.32 because s1 > 0 and s1 == 0 && s2 == 0 was false.
        // After fix: returns 0.0 because s2 == 0 causes immediate rejection.
        Assert.Equal(0.0, score);
    }

    [Fact]
    public void FindCorrectiveWaves_WhenWaveCFailsFibonacciRange_ReturnsEmpty()
    {
        var pivots = CreatePivots(100m, 200m, 150m, 155m);
        var candles = CreateCandles(31);

        var results = ElliottWaveDetector.FindCorrectiveWaves(pivots, candles);

        // Before fix: 1 result returned with confidence score ~0.32 >= 0.30.
        // After fix: 0 results returned.
        Assert.Empty(results);
    }

    [Theory]
    [InlineData(0.40, false)] // Below lower bound (0.618 - 0.20 = 0.418) -> rejected
    [InlineData(0.43, true)]  // Inside lower tolerance -> accepted
    [InlineData(1.85, false)] // Above upper bound (1.618 + 0.20 = 1.818) -> rejected
    public void ScoreCorrectiveWave_BoundaryValidation(double cRatioTarget, bool expectedAccepted)
    {
        // wA = 100, wB = 50 (bRatio = 0.50, valid s1 > 0)
        // wC = cRatioTarget * 100 => p3 = 150 + wC
        decimal p3 = 150m + (decimal)cRatioTarget * 100m;
        var pivots = CreatePivots(100m, 200m, 150m, p3);

        double score = ElliottWaveDetector.ScoreCorrectiveWave(pivots, isBullish: true);

        if (expectedAccepted)
        {
            Assert.True(score > 0, $"Expected cRatio {cRatioTarget} to produce score > 0, but got {score}");
        }
        else
        {
            Assert.Equal(0.0, score);
        }
    }
}
