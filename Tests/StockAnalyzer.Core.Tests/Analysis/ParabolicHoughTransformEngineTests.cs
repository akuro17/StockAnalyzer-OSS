using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

public class ParabolicHoughTransformEngineTests
{
    [Fact]
    public void FitQuadraticOLS_SyntheticPoints_MatchesExactCoefficients()
    {
        // y = 0.02 * x^2 - 0.4 * x + 100
        var points = new List<(int Index, double Price)>();
        for (int x = 0; x <= 20; x += 2)
        {
            double y = 0.02 * x * x - 0.4 * x + 100.0;
            points.Add((x, y));
        }

        var (a, b, c, r2) = ParabolicHoughTransformEngine.FitQuadraticOLS(points);

        Assert.True(Math.Abs(a - 0.02) < 1e-4, $"Expected A=0.02, got {a}");
        Assert.True(Math.Abs(b - (-0.4)) < 1e-4, $"Expected B=-0.4, got {b}");
        Assert.True(Math.Abs(c - 100.0) < 1e-4, $"Expected C=100, got {c}");
        Assert.True(r2 > 0.999, $"Expected R2 near 1.0, got {r2}");
    }

    [Fact]
    public void DetectParabolasFromPoints_ConvexPoints_DetectsConvexParabola()
    {
        var points = new List<(int Index, decimal Price)>
        {
            (0, 120m),
            (10, 105m),
            (20, 100m),
            (30, 105m),
            (40, 120m)
        };

        var result = ParabolicHoughTransformEngine.DetectParabolasFromPoints(
            points,
            totalBars: 45,
            startIndex: 0,
            atr: 2.0,
            voteThreshold: 3,
            maxCurves: 1,
            curvatureSign: ParabolicHoughCurvatureSign.Convex);

        Assert.False(result.IsEmpty);
        Assert.Single(result.Parabolas);
        var p = result.Parabolas[0];
        Assert.Equal(ParabolicHoughCurvatureSign.Convex, p.CurvatureSign);
        Assert.True(p.CurvaturePrice > 0);
        Assert.True(p.RSquared > 0.95);
        Assert.True(p.Votes >= 3);
        Assert.True(p.GetPriceAt(20) < p.GetPriceAt(0));
    }

    [Fact]
    public void DetectParabolasFromCandles_InsufficientData_ReturnsEmpty()
    {
        var result = ParabolicHoughTransformEngine.DetectParabolasFromCandles(null!, lookback: 20);
        Assert.True(result.IsEmpty);

        var shortList = new List<CandleData>
        {
            new(DateTime.UtcNow, 100, 101, 99, 100, 1000)
        };
        var result2 = ParabolicHoughTransformEngine.DetectParabolasFromCandles(shortList, lookback: 20);
        Assert.True(result2.IsEmpty);
    }

    [Fact]
    public void DetectParabolasFromCandles_ParabolicCandles_DetectsCurvature()
    {
        // 50 bars forming a parabolic base: y = 0.05 * (x - 25)^2 + 100
        var candles = new List<CandleData>();
        var baseDate = new DateTime(2025, 1, 1);

        for (int i = 0; i < 50; i++)
        {
            decimal trend = (decimal)(0.05 * Math.Pow(i - 25, 2) + 100.0);
            decimal wiggle = (i % 4 == 0) ? 2.0m : (i % 4 == 2 ? -2.0m : 0.0m);
            decimal centerPrice = trend + wiggle;
            candles.Add(new CandleData(
                baseDate.AddDays(i),
                centerPrice,
                centerPrice + 1.5m,
                centerPrice - 1.5m,
                centerPrice,
                1000
            ));
        }

        var result = ParabolicHoughTransformEngine.DetectParabolasFromCandles(
            candles,
            lookback: 50,
            pivotWindow: 1,
            voteThreshold: 3,
            maxCurves: 1,
            curvatureSign: ParabolicHoughCurvatureSign.Both);

        Assert.False(result.IsEmpty);
        Assert.True(result.Parabolas.Count >= 1);
        var parabola = result.Parabolas[0];
        Assert.True(parabola.RSquared > 0.80);
    }
}
