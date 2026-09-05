using System;
using System.Collections.Generic;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

/// <summary>
/// Regression tests for DeferredComputationRecalculator, the SSoT dispatcher extracted
/// (SAで制約確認 Phase 2) from four previously-duplicated "if (obj is TypeX) { ...;
/// TypeX.Recalculate(candles); }" blocks in ChartInteractionController/ChartViewModel.
/// Proves the consolidated dispatch is behaviorally identical to those original inline
/// blocks: recalculates the three known types, and is a safe no-op for anything else
/// (matching the original if/else-if chains, which simply fell through without action for
/// unmatched object types).
/// </summary>
public class DeferredComputationRecalculatorTests
{
    private static List<CoreCandleData> BuildCandles()
    {
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < 10; i++)
        {
            decimal close = 100m + i * 10m;
            candles.Add(new CoreCandleData(new DateTime(2025, 1, 1).AddDays(i), close, close + 5, close - 5, close, 1000));
        }
        return candles;
    }

    [Fact]
    public void TryRecalculate_RegressionTrendObject_RecalculatesAndReturnsTrue()
    {
        var obj = new RegressionTrendObject(
            new ChartPoint(new DateTime(2025, 1, 2), 0m),
            new ChartPoint(new DateTime(2025, 1, 7), 0m));

        bool handled = DeferredComputationRecalculator.TryRecalculate(obj, BuildCandles());

        Assert.True(handled);
        // Recalculate() syncs Points[] to the fitted price (fix ①); price should no
        // longer be the placeholder 0m used at construction if recalculation actually ran.
        Assert.NotEqual(0m, obj.Points[0].Price);
    }

    [Fact]
    public void TryRecalculate_RangeSplineObject_RecalculatesAndReturnsTrue()
    {
        var obj = new RangeSplineObject(
            new ChartPoint(new DateTime(2025, 1, 2), 0m),
            new ChartPoint(new DateTime(2025, 1, 7), 0m));

        bool handled = DeferredComputationRecalculator.TryRecalculate(obj, BuildCandles());

        Assert.True(handled);
        Assert.True(obj.ExtractedPoints.Count >= 2);
    }

    [Fact]
    public void TryRecalculate_FixedRangeVolumeProfileObject_RecalculatesAndReturnsTrue()
    {
        var obj = new FixedRangeVolumeProfileObject(
            new ChartPoint(new DateTime(2025, 1, 2), 100m),
            new ChartPoint(new DateTime(2025, 1, 7), 100m));

        bool handled = DeferredComputationRecalculator.TryRecalculate(obj, BuildCandles());

        Assert.True(handled);
        Assert.NotEmpty(obj.ProfileData);
    }

    [Fact]
    public void TryRecalculate_FftSpectrumObject_RecalculatesAndReturnsTrue()
    {
        var obj = new FftSpectrumObject(
            new ChartPoint(new DateTime(2025, 1, 2), 100m),
            new ChartPoint(new DateTime(2025, 1, 7), 100m));

        bool handled = DeferredComputationRecalculator.TryRecalculate(obj, BuildCandles());

        Assert.True(handled);
        Assert.NotEmpty(obj.SpectrumBins);
    }

    [Fact]
    public void TryRecalculate_UnrelatedObjectType_IsNoOpAndReturnsFalse()
    {
        var obj = new TrendLineObject(
            new ChartPoint(new DateTime(2025, 1, 2), 100m),
            new ChartPoint(new DateTime(2025, 1, 7), 120m));

        bool handled = DeferredComputationRecalculator.TryRecalculate(obj, BuildCandles());

        Assert.False(handled);
    }

    [Fact]
    public void TryRecalculate_HarmonicPatternObject_DispatchesAndReturnsTrue()
    {
        var obj = new HarmonicPatternObject();
        obj.Points.Add(new ChartPoint(new DateTime(2025, 1, 2), 100m));
        obj.Points.Add(new ChartPoint(new DateTime(2025, 1, 7), 120m));

        bool handled = DeferredComputationRecalculator.TryRecalculate(obj, BuildCandles());

        Assert.True(handled);
        Assert.NotNull(obj.CachedResults);
    }

    [Fact]
    public void TryRecalculate_AutoElliottWaveObject_DispatchesAndReturnsTrue()
    {
        var obj = new AutoElliottWaveObject();
        obj.Points.Add(new ChartPoint(new DateTime(2025, 1, 2), 100m));
        obj.Points.Add(new ChartPoint(new DateTime(2025, 1, 7), 120m));

        bool handled = DeferredComputationRecalculator.TryRecalculate(obj, BuildCandles());

        Assert.True(handled);
        Assert.NotNull(obj.CachedResults);
    }

    [Fact]
    public void TryRecalculate_GeometricPatternObject_DispatchesAndReturnsTrue()
    {
        var obj = new GeometricPatternObject(new ChartPoint(new DateTime(2025, 1, 2), 100m));

        bool handled = DeferredComputationRecalculator.TryRecalculate(obj, BuildCandles());

        Assert.True(handled);
    }
}
