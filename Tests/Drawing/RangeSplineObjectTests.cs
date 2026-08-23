using System;
using System.Collections.Generic;
using System.Linq;
using global::Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using Xunit;
using Point = global::Avalonia.Point;
using Rect = global::Avalonia.Rect;

namespace StockAnalyzer.Tests.Drawing;

public class RangeSplineObjectTests
{
    private class DummyCoordinateTransform : ICoordinateTransform
    {
        public double CanvasWidth => 800;
        public double CanvasHeight => 600;
        public Rect ScreenRect => new Rect(0, 0, 800, 600);
        public double ViewportX => 0;
        public double ViewportWidth => 800;
        public double ScaleX => 1.0;
        public PriceScaleType PriceScale => PriceScaleType.Linear;
        public TransformMetadata Metadata => new TransformMetadata(false, true, ChartType.Line);
        public IReadOnlyList<DateTime>? TimeMap => null;

        public Point ChartToScreen(ChartPoint chartPoint)
        {
            double x = (chartPoint.Time - new DateTime(2025, 1, 1)).TotalDays * 10.0;
            double y = 600.0 - (double)chartPoint.Price;
            return new Point(x, y);
        }

        public ChartPoint ScreenToChart(Point screenPoint)
        {
            var time = new DateTime(2025, 1, 1).AddDays(screenPoint.X / 10.0);
            var price = (decimal)(600.0 - screenPoint.Y);
            return new ChartPoint(time, price);
        }

        public Point NumericToScreen(double x, double y) => new Point(x, y);
        public (double x, double y) ScreenToNumeric(Point screenPoint) => (screenPoint.X, screenPoint.Y);
        public void UpdateRange(DateTime minTime, DateTime maxTime, decimal minPrice, decimal maxPrice, double? newCanvasWidth = null, double? newCanvasHeight = null) { }
        public void SetTimeMap(IReadOnlyList<DateTime> timeMap) { }
        public double GetXFromIndex(double index) => index;
        public double GetYFromPrice(decimal price) => 600.0 - (double)price;
    }

    [Fact]
    public void Constructor_DefaultProperties_InitializedCorrectly()
    {
        var spline = new RangeSplineObject();

        Assert.Equal(ChartObjectType.RangeSpline, spline.Type);
        Assert.Equal(Colors.Green, spline.Color);
        Assert.Equal(PriceField.Close, spline.PriceField);
        Assert.Equal(BezierSplineMath.DefaultTension, spline.Tension);
        Assert.Empty(spline.Points);
        Assert.Empty(spline.ExtractedPoints);
    }

    [Fact]
    public void PriceField_ExtractPrice_CalculatesAllSevenFieldsPrecisely()
    {
        var candle = new CoreCandleData(
            Timestamp: new DateTime(2025, 1, 1),
            Open: 100m,
            High: 120m,
            Low: 90m,
            Close: 110m,
            Volume: 1000
        );

        Assert.Equal(110m, RangeSplineObject.ExtractPrice(candle, PriceField.Close));
        Assert.Equal(120m, RangeSplineObject.ExtractPrice(candle, PriceField.High));
        Assert.Equal(90m, RangeSplineObject.ExtractPrice(candle, PriceField.Low));
        Assert.Equal(100m, RangeSplineObject.ExtractPrice(candle, PriceField.Open));
        Assert.Equal(105m, RangeSplineObject.ExtractPrice(candle, PriceField.MedianHL));
        Assert.Equal((120m + 90m + 110m) / 3m, RangeSplineObject.ExtractPrice(candle, PriceField.TypicalHLC));
        Assert.Equal((120m + 90m + 2m * 110m) / 4m, RangeSplineObject.ExtractPrice(candle, PriceField.WeightedHLCC));
    }

    [Fact]
    public void Recalculate_BinarySearch_ExtractsCorrectRangeAndOrder()
    {
        var candles = new List<CoreCandleData>();
        for (int i = 1; i <= 10; i++)
        {
            candles.Add(new CoreCandleData(
                Timestamp: new DateTime(2025, 1, i),
                Open: 100m + i,
                High: 110m + i,
                Low: 90m + i,
                Close: 105m + i,
                Volume: 1000 * i
            ));
        }

        // Test normal order: Day 3 to Day 7
        var p0 = new ChartPoint(new DateTime(2025, 1, 3), 0m);
        var p1 = new ChartPoint(new DateTime(2025, 1, 7), 0m);
        var spline = new RangeSplineObject(p0, p1);

        spline.Recalculate(candles);

        Assert.Equal(5, spline.ExtractedPoints.Count);
        Assert.Equal(new DateTime(2025, 1, 3), spline.ExtractedPoints[0].Time);
        Assert.Equal(108m, spline.ExtractedPoints[0].Price); // 105 + 3
        Assert.Equal(new DateTime(2025, 1, 7), spline.ExtractedPoints[4].Time);
        Assert.Equal(112m, spline.ExtractedPoints[4].Price); // 105 + 7

        // Test reversed order: Day 7 to Day 3
        var splineReversed = new RangeSplineObject(p1, p0);
        splineReversed.Recalculate(candles);

        Assert.Equal(5, splineReversed.ExtractedPoints.Count);
        Assert.Equal(new DateTime(2025, 1, 3), splineReversed.ExtractedPoints[0].Time);
        Assert.Equal(new DateTime(2025, 1, 7), splineReversed.ExtractedPoints[4].Time);
    }

    [Fact]
    public void Recalculate_BoundaryConditions_HandlesN0_N1_N2_N3Plus()
    {
        var candles = new List<CoreCandleData>
        {
            new(new DateTime(2025, 1, 10), 100m, 110m, 90m, 105m, 1000),
            new(new DateTime(2025, 1, 20), 105m, 115m, 95m, 110m, 1000),
            new(new DateTime(2025, 1, 30), 110m, 120m, 100m, 115m, 1000)
        };

        var spline = new RangeSplineObject();

        // Null candles
        spline.Recalculate(null);
        Assert.Empty(spline.ExtractedPoints);

        // N = 0 (Outside range)
        spline.Points.Add(new ChartPoint(new DateTime(2025, 1, 1), 0m));
        spline.Points.Add(new ChartPoint(new DateTime(2025, 1, 5), 0m));
        spline.Recalculate(candles);
        Assert.Empty(spline.ExtractedPoints);

        // N = 1 (Exact match single candle)
        spline.Points[0] = new ChartPoint(new DateTime(2025, 1, 10), 0m);
        spline.Points[1] = new ChartPoint(new DateTime(2025, 1, 10), 0m);
        spline.Recalculate(candles);
        Assert.Single(spline.ExtractedPoints);

        // N = 2 (Two candles)
        spline.Points[0] = new ChartPoint(new DateTime(2025, 1, 10), 0m);
        spline.Points[1] = new ChartPoint(new DateTime(2025, 1, 20), 0m);
        spline.Recalculate(candles);
        Assert.Equal(2, spline.ExtractedPoints.Count);

        // N = 3 (All candles)
        spline.Points[0] = new ChartPoint(new DateTime(2025, 1, 5), 0m);
        spline.Points[1] = new ChartPoint(new DateTime(2025, 2, 1), 0m);
        spline.Recalculate(candles);
        Assert.Equal(3, spline.ExtractedPoints.Count);
    }

    [Fact]
    public void Recalculate_NonIReadOnlyListFallback_YieldsSameResult()
    {
        var candles = new List<CoreCandleData>();
        for (int i = 1; i <= 10; i++)
        {
            candles.Add(new CoreCandleData(new DateTime(2025, 1, i), 100m, 110m, 90m, 100m + i, 1000));
        }

        var p0 = new ChartPoint(new DateTime(2025, 1, 2), 0m);
        var p1 = new ChartPoint(new DateTime(2025, 1, 6), 0m);
        var spline = new RangeSplineObject(p0, p1);

        // Pass as plain IEnumerable (using Enumerable.Where)
        IEnumerable<CoreCandleData> enumerable = candles.Where(_ => true);
        spline.Recalculate(enumerable);

        Assert.Equal(5, spline.ExtractedPoints.Count);
        Assert.Equal(new DateTime(2025, 1, 2), spline.ExtractedPoints[0].Time);
        Assert.Equal(new DateTime(2025, 1, 6), spline.ExtractedPoints[4].Time);
    }

    [Fact]
    public void Render_AllStates_ExecutesZeroAllocationWithoutErrors()
    {
        var transform = new DummyCoordinateTransform();
        using var surface = SKSurface.Create(new SKImageInfo(800, 600));
        var canvas = surface.Canvas;

        var spline = new RangeSplineObject();

        // 1. N = 0
        spline.Render(canvas, transform);

        // 2. N = 1
        var candles = new List<CoreCandleData>
        {
            new(new DateTime(2025, 1, 1), 100m, 110m, 90m, 100m, 1000),
            new(new DateTime(2025, 1, 2), 100m, 110m, 90m, 110m, 1000),
            new(new DateTime(2025, 1, 3), 100m, 110m, 90m, 105m, 1000),
            new(new DateTime(2025, 1, 4), 100m, 110m, 90m, 115m, 1000)
        };

        spline.Points.Add(new ChartPoint(new DateTime(2025, 1, 1), 0m));
        spline.Points.Add(new ChartPoint(new DateTime(2025, 1, 1), 0m));
        spline.Recalculate(candles);
        spline.Render(canvas, transform);

        // 3. N = 2
        spline.Points[1] = new ChartPoint(new DateTime(2025, 1, 2), 0m);
        spline.Recalculate(candles);
        spline.Render(canvas, transform);

        // 4. N >= 3
        spline.Points[1] = new ChartPoint(new DateTime(2025, 1, 4), 0m);
        spline.Recalculate(candles);
        spline.IsSelected = true;
        spline.Render(canvas, transform);

        // 5. Large point count (ArrayPool path)
        var largeCandles = new List<CoreCandleData>();
        for (int i = 0; i < 200; i++)
        {
            largeCandles.Add(new CoreCandleData(new DateTime(2025, 1, 1).AddHours(i), 100m, 110m, 90m, 100m + (i % 20), 1000));
        }
        var splineLarge = new RangeSplineObject(
            new ChartPoint(new DateTime(2025, 1, 1), 0m),
            new ChartPoint(new DateTime(2025, 1, 1).AddHours(199), 0m));
        splineLarge.Recalculate(largeCandles);
        splineLarge.Render(canvas, transform);
    }

    [Fact]
    public void HitTest_SplineSegments_DetectsHitAndMissPrecisely()
    {
        var transform = new DummyCoordinateTransform();

        var candles = new List<CoreCandleData>
        {
            new(new DateTime(2025, 1, 1), 100m, 110m, 90m, 100m, 1000), // Screen: (0, 500)
            new(new DateTime(2025, 1, 6), 100m, 110m, 90m, 200m, 1000), // Screen: (50, 400)
            new(new DateTime(2025, 1, 11), 100m, 110m, 90m, 100m, 1000) // Screen: (100, 500)
        };

        var spline = new RangeSplineObject(
            new ChartPoint(new DateTime(2025, 1, 1), 0m),
            new ChartPoint(new DateTime(2025, 1, 11), 0m));
        spline.Recalculate(candles);

        // Points directly on nodes should hit
        Assert.True(spline.HitTest(new Point(0, 500), transform, tolerance: 3.0));
        Assert.True(spline.HitTest(new Point(50, 400), transform, tolerance: 3.0));
        Assert.True(spline.HitTest(new Point(100, 500), transform, tolerance: 3.0));

        // Far point should miss
        Assert.False(spline.HitTest(new Point(50, 200), transform, tolerance: 5.0));

        // Bounding box outer point should miss immediately
        Assert.False(spline.HitTest(new Point(200, 500), transform, tolerance: 3.0));
    }

    [Fact]
    public void Translate_ShiftsPointsAndExtractedPoints()
    {
        var candles = new List<CoreCandleData>
        {
            new(new DateTime(2025, 1, 1), 100m, 110m, 90m, 100m, 1000),
            new(new DateTime(2025, 1, 5), 100m, 110m, 90m, 120m, 1000)
        };

        var spline = new RangeSplineObject(
            new ChartPoint(new DateTime(2025, 1, 1), 100m),
            new ChartPoint(new DateTime(2025, 1, 5), 120m));
        spline.Recalculate(candles);

        spline.Translate(TimeSpan.FromDays(2), 10m);

        Assert.Equal(new DateTime(2025, 1, 3), spline.Points[0].Time);
        Assert.Equal(110m, spline.Points[0].Price);
        Assert.Equal(new DateTime(2025, 1, 7), spline.Points[1].Time);
        Assert.Equal(130m, spline.Points[1].Price);

        Assert.Equal(new DateTime(2025, 1, 3), spline.ExtractedPoints[0].Time);
        Assert.Equal(110m, spline.ExtractedPoints[0].Price);
        Assert.Equal(new DateTime(2025, 1, 7), spline.ExtractedPoints[1].Time);
        Assert.Equal(130m, spline.ExtractedPoints[1].Price);
    }

    [Fact]
    public void Render_LargeDataset_Over128Points_UsesArrayPoolCorrectlyWithoutTrailingOriginPoints()
    {
        var transform = new DummyCoordinateTransform();
        var candles = new List<CoreCandleData>();
        var baseDate = new DateTime(2025, 1, 1);

        for (int i = 0; i < 200; i++)
        {
            candles.Add(new CoreCandleData(baseDate.AddDays(i), 100m, 110m, 90m, 100m + (i % 10), 1000));
        }

        var spline = new RangeSplineObject(
            new ChartPoint(baseDate, 0m),
            new ChartPoint(baseDate.AddDays(199), 0m));
        spline.Recalculate(candles);

        Assert.Equal(200, spline.ExtractedPoints.Count);

        using var surface = SKSurface.Create(new SKImageInfo(1000, 1000));
        var canvas = surface.Canvas;

        // Render should succeed without throwing and without connecting to (0, 0)
        spline.Render(canvas, transform);

        // HitTest on valid candle position
        var midPt = transform.ChartToScreen(spline.ExtractedPoints[100]);
        Assert.True(spline.HitTest(midPt, transform, tolerance: 5.0));

        // (0, 0) is far away from the spline line and should NOT hit
        Assert.False(spline.HitTest(new Point(0, 0), transform, tolerance: 3.0));
    }

    [Fact]
    public void TC_61_8_09_GetAxisProjections_WithHorizontalLevels_YieldsProjectedLabels()
    {
        var transform = new DummyCoordinateTransform();
        var candles = new List<CoreCandleData>
        {
            new(new DateTime(2025, 1, 1), 100m, 100m, 100m, 100m, 1000),
            new(new DateTime(2025, 1, 2), 100m, 200m, 100m, 200m, 1000), // Peak
            new(new DateTime(2025, 1, 3), 100m, 50m, 50m, 50m, 1000),   // Trough
            new(new DateTime(2025, 1, 4), 100m, 100m, 100m, 100m, 1000)
        };

        var spline = new RangeSplineObject(
            new ChartPoint(new DateTime(2025, 1, 1), 0m),
            new ChartPoint(new DateTime(2025, 1, 4), 0m));
        spline.Recalculate(candles);

        // When ShowHorizontalLevels = false, no projections
        spline.ShowHorizontalLevels = false;
        spline.UpdateExtremaCache(transform);
        var projWhenFalse = spline.GetAxisProjections(null!, null!).ToList();
        Assert.Empty(projWhenFalse);

        // When ShowHorizontalLevels = true, projections are emitted
        spline.ShowHorizontalLevels = true;
        spline.UpdateExtremaCache(transform);
        var projWhenTrue = spline.GetAxisProjections(null!, null!).ToList();

        Assert.NotEmpty(projWhenTrue);
        foreach (var req in projWhenTrue)
        {
            Assert.True(req.Value > 0);
            Assert.Equal(AxisLabelStyle.Default, req.Style);
            Assert.Equal(req.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture), req.Label);
            Assert.True(req.Color == new SKColor(239, 83, 80) || req.Color == new SKColor(38, 166, 154));
        }
    }

    [Fact]
    public void ExtremaLevels_CacheInvalidation_WorksOnRecalculateAndTranslate()
    {
        var transform = new DummyCoordinateTransform();
        var candles = new List<CoreCandleData>
        {
            new(new DateTime(2025, 1, 1), 100m, 100m, 100m, 100m, 1000),
            new(new DateTime(2025, 1, 2), 100m, 200m, 100m, 200m, 1000),
            new(new DateTime(2025, 1, 3), 100m, 50m, 50m, 50m, 1000),
            new(new DateTime(2025, 1, 4), 100m, 100m, 100m, 100m, 1000)
        };

        var spline = new RangeSplineObject(
            new ChartPoint(new DateTime(2025, 1, 1), 0m),
            new ChartPoint(new DateTime(2025, 1, 4), 0m))
        {
            ShowHorizontalLevels = true
        };

        spline.Recalculate(candles);
        spline.UpdateExtremaCache(transform);
        Assert.NotEmpty(spline.ExtremaLevels);

        // Manual InvalidateExtrema
        spline.InvalidateExtrema();
        Assert.Empty(spline.ExtremaLevels);

        // Re-cache
        spline.UpdateExtremaCache(transform);
        Assert.NotEmpty(spline.ExtremaLevels);

        // Invalidate via Translate
        spline.Translate(TimeSpan.FromDays(1), 10m);
        Assert.Empty(spline.ExtremaLevels);

        // Invalidate via Recalculate
        spline.UpdateExtremaCache(transform);
        Assert.NotEmpty(spline.ExtremaLevels);
        spline.Recalculate(candles);
        Assert.Empty(spline.ExtremaLevels);
    }

    [Fact]
    public void Render_WithShowHorizontalLevels_RendersWithoutErrors()
    {
        var transform = new DummyCoordinateTransform();
        using var surface = SKSurface.Create(new SKImageInfo(800, 600));
        var canvas = surface.Canvas;

        var candles = new List<CoreCandleData>
        {
            new(new DateTime(2025, 1, 1), 100m, 100m, 100m, 100m, 1000),
            new(new DateTime(2025, 1, 2), 100m, 200m, 100m, 200m, 1000),
            new(new DateTime(2025, 1, 3), 100m, 50m, 50m, 50m, 1000),
            new(new DateTime(2025, 1, 4), 100m, 100m, 100m, 100m, 1000)
        };

        var spline = new RangeSplineObject(
            new ChartPoint(new DateTime(2025, 1, 1), 0m),
            new ChartPoint(new DateTime(2025, 1, 4), 0m))
        {
            ShowHorizontalLevels = true
        };

        spline.Recalculate(candles);

        // Rendering should calculate extrema cache and draw lines
        spline.Render(canvas, transform);

        Assert.NotEmpty(spline.ExtremaLevels);
    }

    [Fact]
    public void SeparateResistanceAndSupport_FiltersProjectionsCorrectly()
    {
        var transform = new DummyCoordinateTransform();
        var candles = new List<CoreCandleData>
        {
            new(new DateTime(2025, 1, 1), 100m, 100m, 100m, 100m, 1000),
            new(new DateTime(2025, 1, 2), 100m, 200m, 100m, 200m, 1000), // Peak (High)
            new(new DateTime(2025, 1, 3), 100m, 50m, 50m, 50m, 1000),   // Trough (Low)
            new(new DateTime(2025, 1, 4), 100m, 100m, 100m, 100m, 1000)
        };

        var spline = new RangeSplineObject(
            new ChartPoint(new DateTime(2025, 1, 1), 0m),
            new ChartPoint(new DateTime(2025, 1, 4), 0m));
        spline.Recalculate(candles);
        spline.UpdateExtremaCache(transform);

        // 1. Only Resistance
        spline.ShowResistanceLevels = true;
        spline.ShowSupportLevels = false;
        var resOnly = spline.GetAxisProjections(null!, null!).ToList();
        Assert.NotEmpty(resOnly);
        Assert.All(resOnly, r => Assert.Equal(new SKColor(239, 83, 80), r.Color));

        // 2. Only Support
        spline.ShowResistanceLevels = false;
        spline.ShowSupportLevels = true;
        var supOnly = spline.GetAxisProjections(null!, null!).ToList();
        Assert.NotEmpty(supOnly);
        Assert.All(supOnly, r => Assert.Equal(new SKColor(38, 166, 154), r.Color));

        // 3. Both false
        spline.ShowResistanceLevels = false;
        spline.ShowSupportLevels = false;
        Assert.Empty(spline.GetAxisProjections(null!, null!));

        // 4. Combined property helper
        spline.ShowHorizontalLevels = true;
        Assert.True(spline.ShowResistanceLevels);
        Assert.True(spline.ShowSupportLevels);
        spline.ShowHorizontalLevels = false;
        Assert.False(spline.ShowResistanceLevels);
        Assert.False(spline.ShowSupportLevels);
    }

    [Fact]
    public void PriceField_Recalculate_UpdatesPricesAndExtremaDifferently()
    {
        var candles = new List<CoreCandleData>
        {
            new(new DateTime(2025, 1, 1), 100m, 150m, 80m, 110m, 1000),
            new(new DateTime(2025, 1, 2), 110m, 250m, 90m, 180m, 1000),
            new(new DateTime(2025, 1, 3), 180m, 190m, 40m, 60m, 1000),
            new(new DateTime(2025, 1, 4), 60m, 120m, 50m, 100m, 1000)
        };

        var spline = new RangeSplineObject(
            new ChartPoint(new DateTime(2025, 1, 1), 0m),
            new ChartPoint(new DateTime(2025, 1, 4), 0m))
        {
            PriceField = PriceField.Close
        };
        spline.Recalculate(candles);
        decimal closePriceAtDay2 = spline.ExtractedPoints[1].Price;
        Assert.Equal(180m, closePriceAtDay2);

        // Change to High and Recalculate
        spline.PriceField = PriceField.High;
        spline.Recalculate(candles);
        decimal highPriceAtDay2 = spline.ExtractedPoints[1].Price;
        Assert.Equal(250m, highPriceAtDay2);

        // Change to Low and Recalculate
        spline.PriceField = PriceField.Low;
        spline.Recalculate(candles);
        decimal lowPriceAtDay2 = spline.ExtractedPoints[1].Price;
        Assert.Equal(90m, lowPriceAtDay2);
    }

    [Fact]
    public void UpdateExtremaCache_WithMinSwingAndMaxLevels_FiltersAndLimitsProperly()
    {
        var transform = new DummyCoordinateTransform();
        var candles = new List<CoreCandleData>
        {
            new(new DateTime(2025, 1, 1), 100m, 100m, 100m, 100m, 1000),
            new(new DateTime(2025, 1, 2), 200m, 200m, 200m, 200m, 1000),
            new(new DateTime(2025, 1, 3), 50m, 50m, 50m, 50m, 1000),
            new(new DateTime(2025, 1, 4), 180m, 180m, 180m, 180m, 1000),
            new(new DateTime(2025, 1, 5), 80m, 80m, 80m, 80m, 1000),
            new(new DateTime(2025, 1, 6), 190m, 190m, 190m, 190m, 1000),
            new(new DateTime(2025, 1, 7), 100m, 100m, 100m, 100m, 1000)
        };

        var spline = new RangeSplineObject(
            new ChartPoint(new DateTime(2025, 1, 1), 0m),
            new ChartPoint(new DateTime(2025, 1, 7), 0m))
        {
            ShowHorizontalLevels = true,
            MinSwingPercent = 1.0,
            MaxLevels = 1, // Only 1 High and 1 Low allowed
            ClusterTolerancePx = 2.0
        };

        spline.Recalculate(candles);
        spline.UpdateExtremaCache(transform);

        var highs = spline.ExtremaLevels.Where(x => x.Type == ExtremaType.High).ToList();
        var lows = spline.ExtremaLevels.Where(x => x.Type == ExtremaType.Low).ToList();

        Assert.Single(highs);
        Assert.Single(lows);
    }
}
