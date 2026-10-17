using System;
using System.Collections.Generic;
using StockAnalyzer.Core;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.GeometricPattern;
using StockAnalyzer.Core.Models.MarketStructure;
using StockAnalyzer.Core.Tests.Backtest.Verification;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.Core.Tests;

public class GeometricRobustFitAndPromotionTests
{
    private readonly ITestOutputHelper _output;

    public GeometricRobustFitAndPromotionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static CandleData C(int index, decimal open, decimal high, decimal low, decimal close) =>
        new(new DateTime(2025, 1, 1).AddDays(index), open, high, low, close, 1000);

    #region Flat-Line Fit Tests (F26, D8a)

    [Fact]
    public void CalculateTheilSen_FlatHorizontalLine_YieldsZeroRSquared()
    {
        // Probe: on a perfectly flat line of pivots, SS_tot == 0, causing legacy Theil-Sen R2 to collapse to 0.0
        var pivots = new List<PivotPoint>
        {
            new(10, DateTime.Today.AddDays(10), 100m, isHigh: true),
            new(20, DateTime.Today.AddDays(20), 100m, isHigh: true),
            new(30, DateTime.Today.AddDays(30), 100m, isHigh: true),
        };

        var (_, _, legacyRSquared) = GeometricPatternDetector.CalculateTheilSen(pivots);
        Assert.Equal(0.0, legacyRSquared);
    }

    [Fact]
    public void CalculateRobustFit_FlatHorizontalLine_YieldsPerfectRSquared()
    {
        var pivots = new List<PivotPoint>
        {
            new(10, DateTime.Today.AddDays(10), 100m, isHigh: true),
            new(20, DateTime.Today.AddDays(20), 100m, isHigh: true),
            new(30, DateTime.Today.AddDays(30), 100m, isHigh: true),
        };

        var candles = new List<CandleData>();
        for (int i = 0; i <= 40; i++)
        {
            candles.Add(C(i, 95m, 101m, 94m, 96m));
        }

        var (slope, intercept, robustRSquared) = GeometricPatternDetector.CalculateRobustFit(pivots, candles, 10, 30);

        Assert.Equal(0.0, slope);
        Assert.Equal(100.0, intercept);
        Assert.Equal(1.0, robustRSquared);
    }

    [Fact]
    public void AscendingTriangle_WithFlatResistance_DetectedOnlyWithRobustFit()
    {
        // Specification requirement:
        // Horizontal top with rising bottom: (10,100),(20,100),(30,100) and (15,90),(25,94),(35,98)
        // yields AscendingTriangle confidence >= 0.95 under robust fit, rejected under legacy.
        var highs = new List<PivotPoint>
        {
            new(10, DateTime.Today.AddDays(10), 100m, isHigh: true),
            new(20, DateTime.Today.AddDays(20), 100m, isHigh: true),
            new(30, DateTime.Today.AddDays(30), 100m, isHigh: true),
        };

        var lows = new List<PivotPoint>
        {
            new(15, DateTime.Today.AddDays(15), 90m, isHigh: false),
            new(25, DateTime.Today.AddDays(25), 94m, isHigh: false),
            new(35, DateTime.Today.AddDays(35), 98m, isHigh: false),
        };

        var candles = new List<CandleData>();
        for (int i = 0; i <= 50; i++)
        {
            // Tight candles with ATR ~ 1.0 to ensure normLowerSlope (0.4 / 1.0 = 0.4) > flatThreshold (0.10)
            decimal mid = (100m + 90m + 0.4m * (i - 15)) / 2m;
            candles.Add(C(i, mid - 0.4m, mid + 0.5m, mid - 0.5m, mid + 0.4m));
        }

        // Legacy fit: rejected because top R2 == 0, avg R2 < 0.75
        var legacyFormation = GeometricPatternDetector.AnalyzeFormation(highs, lows, candles, useRobustFit: false);
        Assert.Null(legacyFormation);

        // Robust fit: accepted with high confidence
        var robustFormation = GeometricPatternDetector.AnalyzeFormation(highs, lows, candles, useRobustFit: true);
        Assert.NotNull(robustFormation);
        Assert.Equal(GeometricFormationType.AscendingTriangle, robustFormation.Type);
        Assert.True(robustFormation.ConfidenceScore >= 0.95,
            $"Expected confidence >= 0.95, but was {robustFormation.ConfidenceScore}");
    }

    #endregion

    #region Flag and Pennant Promotion Tests (F27, D8b)

    [Fact]
    public void DetectPoleDirection_DetectsUpwardAndDownwardPoles()
    {
        // Upward pole: 10 bars (0..10) rising from 100 to 120 (20% move, lookback=10, 100% directional consistency)
        var candles = new List<CandleData>();
        for (int i = 0; i <= 10; i++)
        {
            decimal p = 100m + i * 2m;
            candles.Add(C(i, p - 0.8m, p + 0.2m, p - 1m, p));
        }

        int poleDirUp = GeometricPatternDetector.DetectPoleDirection(candles, 10);
        Assert.Equal(1, poleDirUp);
        Assert.True(GeometricPatternDetector.DetectPole(candles, 10));

        // Downward pole: 10 bars falling from 120 to 100
        var bearCandles = new List<CandleData>();
        for (int i = 0; i <= 10; i++)
        {
            decimal p = 120m - i * 2m;
            bearCandles.Add(C(i, p + 0.8m, p + 1m, p - 0.2m, p));
        }

        int poleDirDown = GeometricPatternDetector.DetectPoleDirection(bearCandles, 10);
        Assert.Equal(-1, poleDirDown);
        Assert.True(GeometricPatternDetector.DetectPole(bearCandles, 10));
    }

    [Fact]
    public void PromoteWithPole_TableVerification()
    {
        // Bullish pole (+1)
        Assert.Equal(GeometricFormationType.BullishFlag,
            GeometricPatternDetector.PromoteWithPole(GeometricFormationType.DescendingChannel, 1));
        Assert.Equal(GeometricFormationType.BullishFlag,
            GeometricPatternDetector.PromoteWithPole(GeometricFormationType.HorizontalBox, 1));
        Assert.Equal(GeometricFormationType.Pennant,
            GeometricPatternDetector.PromoteWithPole(GeometricFormationType.SymmetricalTriangle, 1));
        Assert.Equal(GeometricFormationType.Pennant,
            GeometricPatternDetector.PromoteWithPole(GeometricFormationType.AscendingTriangle, 1));

        // Bearish pole (-1)
        Assert.Equal(GeometricFormationType.BearishFlag,
            GeometricPatternDetector.PromoteWithPole(GeometricFormationType.AscendingChannel, -1));
        Assert.Equal(GeometricFormationType.BearishFlag,
            GeometricPatternDetector.PromoteWithPole(GeometricFormationType.HorizontalBox, -1));
        Assert.Equal(GeometricFormationType.Pennant,
            GeometricPatternDetector.PromoteWithPole(GeometricFormationType.DescendingTriangle, -1));

        // No pole (0) -> preserves baseType
        Assert.Equal(GeometricFormationType.DescendingChannel,
            GeometricPatternDetector.PromoteWithPole(GeometricFormationType.DescendingChannel, 0));
        Assert.Equal(GeometricFormationType.SymmetricalTriangle,
            GeometricPatternDetector.PromoteWithPole(GeometricFormationType.SymmetricalTriangle, 0));
    }

    [Fact]
    public void PoleFollowedByDescendingChannel_PromotesToBullishFlag_WhenEnabled()
    {
        // Construct 15% pole (100 -> 115) followed by a parallel descending channel
        var candles = new List<CandleData>();
        for (int i = 0; i <= 10; i++)
        {
            decimal p = 100m + i * 1.5m; // 100 -> 115 at bar 10 (15% pole)
            candles.Add(C(i, p - 0.5m, p + 0.5m, p - 1m, p));
        }

        // Descending channel from bar 10 to bar 40 with ATR ~ 1.0 (range 1.0) and slope -0.3 (normSlope = -0.3 < -0.10)
        for (int i = 11; i <= 40; i++)
        {
            decimal center = 112.5m - 0.3m * (i - 10);
            candles.Add(C(i, center - 0.4m, center + 0.5m, center - 0.5m, center - 0.4m));
        }

        var highs = new List<PivotPoint>
        {
            new(10, DateTime.Today.AddDays(10), 115m, isHigh: true),
            new(20, DateTime.Today.AddDays(20), 112m, isHigh: true),
            new(30, DateTime.Today.AddDays(30), 109m, isHigh: true),
        };

        var lows = new List<PivotPoint>
        {
            new(15, DateTime.Today.AddDays(15), 108.5m, isHigh: false),
            new(25, DateTime.Today.AddDays(25), 105.5m, isHigh: false),
            new(35, DateTime.Today.AddDays(35), 102.5m, isHigh: false),
        };

        // Without promotion: returns DescendingChannel (preserving legacy behavior)
        var defaultFormation = GeometricPatternDetector.AnalyzeFormation(
            highs, lows, candles, useRobustFit: true, promoteFlagsAndPennants: false);
        Assert.NotNull(defaultFormation);
        Assert.Equal(GeometricFormationType.DescendingChannel, defaultFormation.Type);

        // With promotion: returns BullishFlag
        var promotedFormation = GeometricPatternDetector.AnalyzeFormation(
            highs, lows, candles, useRobustFit: true, promoteFlagsAndPennants: true);
        Assert.NotNull(promotedFormation);
        Assert.Equal(GeometricFormationType.BullishFlag, promotedFormation.Type);
    }

    #endregion

    #region Staged Comparison Measurement (D8 Review Table)

    [Fact]
    public void PhaseD8_MeasureGeometricDetections_LegacyVsNew()
    {
        // Measure detections across seeds 1..20 (400 bars each)
        var countsLegacy = new Dictionary<GeometricFormationType, int>();
        var countsNew = new Dictionary<GeometricFormationType, int>();

        foreach (GeometricFormationType type in Enum.GetValues(typeof(GeometricFormationType)))
        {
            countsLegacy[type] = 0;
            countsNew[type] = 0;
        }

        for (int seed = 1; seed <= 20; seed++)
        {
            var bars = SyntheticBars.SeededRandomWalk(seed, 400, 100m);

            var legacyFormations = GeometricPatternDetector.Detect(bars, useRobustFit: false, promoteFlagsAndPennants: false);
            foreach (var f in legacyFormations) countsLegacy[f.Type]++;

            var newFormations = GeometricPatternDetector.Detect(bars, useRobustFit: true, promoteFlagsAndPennants: true);
            foreach (var f in newFormations) countsNew[f.Type]++;
        }

        _output.WriteLine("=== Phase D8 Comparison Measurement (Seeds 1..20, 400 bars) ===");
        _output.WriteLine($"{"FormationType",-24} | {"Legacy",-10} | {"RobustFit+Promotion",-20}");
        _output.WriteLine(new string('-', 60));

        int totalLegacy = 0, totalNew = 0;
        foreach (GeometricFormationType type in Enum.GetValues(typeof(GeometricFormationType)))
        {
            _output.WriteLine($"{type,-24} | {countsLegacy[type],-10} | {countsNew[type],-20}");
            totalLegacy += countsLegacy[type];
            totalNew += countsNew[type];
        }
        _output.WriteLine(new string('-', 60));
        _output.WriteLine($"{"Total",-24} | {totalLegacy,-10} | {totalNew,-20}");

        Assert.True(totalNew >= 0);
    }

    [Fact]
    public void CalculateRobustFit_NullOrEmptyOrSinglePivot_ReturnsSafelyWithoutException()
    {
        var candles = new List<CandleData> { C(0, 100m, 105m, 95m, 100m) };

        var (slope1, intercept1, r2_1) = GeometricPatternDetector.CalculateRobustFit(null!, candles, 0, 0);
        Assert.Equal(0.0, slope1);
        Assert.Equal(0.0, intercept1);
        Assert.Equal(0.0, r2_1);

        var (slope2, intercept2, r2_2) = GeometricPatternDetector.CalculateRobustFit(Array.Empty<PivotPoint>(), candles, 0, 0);
        Assert.Equal(0.0, slope2);
        Assert.Equal(0.0, intercept2);
        Assert.Equal(0.0, r2_2);

        var singlePivot = new List<PivotPoint> { new(0, DateTime.Today, 100m, true) };
        var (slope3, intercept3, r2_3) = GeometricPatternDetector.CalculateRobustFit(singlePivot, candles, 0, 0);
        Assert.Equal(0.0, slope3);
        Assert.Equal(100.0, intercept3);
        Assert.Equal(0.0, r2_3);
    }

    [Fact]
    public void DetectPole_NonPositiveStartPrice_ReturnsSafelyWithoutZeroDivision()
    {
        var zeroCandles = new List<CandleData>();
        for (int i = 0; i < 20; i++)
        {
            zeroCandles.Add(C(i, 0m, 0m, 0m, 0m));
        }

        bool result = GeometricPatternDetector.DetectPole(zeroCandles, 15);
        Assert.False(result);
    }

    #endregion
}
