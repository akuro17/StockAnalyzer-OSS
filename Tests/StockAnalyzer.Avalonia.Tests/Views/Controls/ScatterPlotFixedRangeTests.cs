#nullable enable
using System.Collections.Generic;
using Avalonia;
using Avalonia.Headless.XUnit;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Views.Controls;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Controls;

/// <summary>An axis with a fixed range is exactly that range: no padding, no outlier suppression, no log scale, and its boundary markers are not cut.</summary>
public sealed class ScatterPlotFixedRangeTests
{
    private static ScatterDataset Dataset(AxisRange? xRange, AxisRange? yRange, params (double X, double Y)[] points)
    {
        var list = new List<ScatterPoint>();
        foreach (var (x, y) in points) list.Add(new ScatterPoint(x, y));
        return new ScatterDataset("t", "x", "y", list, null) { XFixedRange = xRange, YFixedRange = yRange };
    }

    // ===== Scale =====

    [Theory]
    [InlineData(PriceScaleType.Log, true, PriceScaleType.Linear)]
    [InlineData(PriceScaleType.Linear, true, PriceScaleType.Linear)]
    [InlineData(PriceScaleType.Log, false, PriceScaleType.Log)]
    [InlineData(PriceScaleType.Linear, false, PriceScaleType.Linear)]
    public void AFixedRangeAxis_IsAlwaysLinear(PriceScaleType requested, bool hasFixedRange, PriceScaleType expected)
    {
        AxisRange? range = hasFixedRange ? new AxisRange(0, 100) : null;

        Assert.Equal(expected, ScatterPlotControl.ResolveEffectiveScale(requested, range));
    }

    // ===== Bounds =====

    [Fact]
    public void AFixedRange_IsTheExactBoundOfItsAxis_WithoutPadding()
    {
        var dataset = Dataset(new AxisRange(0, 100), new AxisRange(-4, 4), (10, 1), (60, -2), (90, 3));
        var analysis = ScatterAnalysisEngine.Analyze(dataset);

        var (minX, maxX, minY, maxY) = ScatterPlotControl.ResolveViewportDataBounds(dataset, analysis, suppressOutliers: false, PriceScaleType.Linear, PriceScaleType.Linear);

        Assert.Equal((0.0, 100.0), (minX, maxX));
        Assert.Equal((-4.0, 4.0), (minY, maxY));
    }

    [Fact]
    public void AFixedRange_IgnoresOutlierSuppression_AndTheLogScale()
    {
        var dataset = Dataset(new AxisRange(0, 100), null, (10, 1), (60, 2), (90, 3), (5000, 4));
        var analysis = ScatterAnalysisEngine.Analyze(dataset);

        var suppressed = ScatterPlotControl.ResolveViewportDataBounds(dataset, analysis, suppressOutliers: true, PriceScaleType.Linear, PriceScaleType.Linear);
        var logged = ScatterPlotControl.ResolveViewportDataBounds(dataset, analysis, suppressOutliers: false, PriceScaleType.Log, PriceScaleType.Linear);

        Assert.Equal((0.0, 100.0), (suppressed.MinX, suppressed.MaxX));
        Assert.Equal((0.0, 100.0), (logged.MinX, logged.MaxX));
    }

    [Fact]
    public void TheAxisWithoutAFixedRange_KeepsItsPaddedAutoscale()
    {
        var dataset = Dataset(new AxisRange(0, 100), null, (10, 1), (60, 2), (90, 3));
        var analysis = ScatterAnalysisEngine.Analyze(dataset);

        var (_, _, minY, maxY) = ScatterPlotControl.ResolveViewportDataBounds(dataset, analysis, suppressOutliers: false, PriceScaleType.Linear, PriceScaleType.Linear);

        double pad = (3.0 - 1.0) * ScatterPlotControl.DataRangePaddingFraction;
        Assert.Equal(1.0 - pad, minY, precision: 9);
        Assert.Equal(3.0 + pad, maxY, precision: 9);
    }

    [Fact]
    public void WithoutAnyFixedRange_TheBoundsAreAsBefore()
    {
        var dataset = Dataset(null, null, (10, 1), (60, 2), (90, 3));
        var analysis = ScatterAnalysisEngine.Analyze(dataset);

        var (minX, maxX, _, _) = ScatterPlotControl.ResolveViewportDataBounds(dataset, analysis, suppressOutliers: false, PriceScaleType.Linear, PriceScaleType.Linear);

        double pad = (90.0 - 10.0) * ScatterPlotControl.DataRangePaddingFraction;
        Assert.Equal(10.0 - pad, minX, precision: 9);
        Assert.Equal(90.0 + pad, maxX, precision: 9);
    }

    // ===== Boundary markers =====

    [Fact]
    public void TheClipPadding_IsTheReachOfTheOutlierRing_TheWidestDefaultMarker()
    {
        Assert.Equal(5.25f, ScatterPlotControl.FixedAxisClipPadding);
        Assert.True(ScatterPlotControl.FixedAxisClipPadding >= ScatterPlotControl.DefaultMarkerRadius + (ScatterPlotControl.PointOutlineWidth / 2.0f));
    }

    private static SKBitmap Render(ScatterDataset dataset)
    {
        var control = new ScatterPlotControl();
        var bitmap = new SKBitmap(600, 500);
        using var canvas = new SKCanvas(bitmap);
        control.RenderSkia(canvas, new Rect(0, 0, 600, 500), dataset, null, false, false, false, false,
            PriceScaleType.Linear, PriceScaleType.Linear, null, null, null, new ScatterCoordinateTransform());
        return bitmap;
    }

    // The plot rectangle of a 600 x 500 control: left 20, top 20, right 540, bottom 458 (margins 20 / 20 / 60 / 42).
    private const int Left = 20, Top = 20, Right = 540, Bottom = 458, MidX = 280, MidY = 239;

    [AvaloniaFact]
    public void AMarkerOnTheBoundaryOfAFixedAxis_IsDrawnWholeNotCutInHalf()
    {
        var dataset = Dataset(new AxisRange(0, 100), new AxisRange(0, 100), (0, 50), (100, 50), (50, 0), (50, 100));
        using SKBitmap bitmap = Render(dataset);
        SKColor margin = bitmap.GetPixel(5, 5);

        // A pixel just outside the plot rectangle, at the centre of each marker, belongs to the marker's outer half.
        Assert.NotEqual(margin, bitmap.GetPixel(Left - 2, MidY));
        Assert.NotEqual(margin, bitmap.GetPixel(Right + 2, MidY));
        Assert.NotEqual(margin, bitmap.GetPixel(MidX, Top - 2));
        Assert.NotEqual(margin, bitmap.GetPixel(MidX, Bottom + 2));
    }

    [AvaloniaFact]
    public void ThePaddingReachesOnlyAsFarAsTheMarkerNeeds()
    {
        var dataset = Dataset(new AxisRange(0, 100), new AxisRange(0, 100), (0, 50));
        using SKBitmap bitmap = Render(dataset);
        SKColor margin = bitmap.GetPixel(5, 5);

        Assert.Equal(margin, bitmap.GetPixel(Left - 6, MidY));
    }

    [AvaloniaFact]
    public void AnOutlierRingOnTheBoundaryOfAFixedAxis_IsDrawnWholeToo()
    {
        // 30 points on the line y = x and one far off it, in the top-left corner of both fixed ranges: the engine flags it as an outlier,
        // which is drawn with a larger radius and a thicker outline than a regular marker.
        var list = new List<ScatterPoint>();
        for (int i = 0; i < 30; i++) list.Add(new ScatterPoint(20.0 + i * 2.0, 20.0 + i * 2.0));
        list.Add(new ScatterPoint(0, 100));
        var dataset = new ScatterDataset("t", "x", "y", list, null) { XFixedRange = new AxisRange(0, 100), YFixedRange = new AxisRange(0, 100) };
        var analysis = ScatterAnalysisEngine.Analyze(dataset);
        Assert.True(analysis.OutlierMask[^1], "precondition: the corner point is an outlier");

        var control = new ScatterPlotControl();
        using var bitmap = new SKBitmap(600, 500);
        using (var canvas = new SKCanvas(bitmap))
        {
            control.RenderSkia(canvas, new Rect(0, 0, 600, 500), dataset, analysis, false, false, false, false,
                PriceScaleType.Linear, PriceScaleType.Linear, null, null, null, new ScatterCoordinateTransform());
        }

        SKColor margin = bitmap.GetPixel(5, 5);

        // The ring (radius 4.5, 1.5 wide) reaches 5.25 px beyond the corner; the pixel 4.5 px out on the left and on the top is on it.
        Assert.NotEqual(margin, bitmap.GetPixel(Left - 5, Top));
        Assert.NotEqual(margin, bitmap.GetPixel(Left, Top - 5));
    }

    [AvaloniaFact]
    public void AFixedYAxis_WithAnAutoscaledXAxis_KeepsTheWholeMarkerOnTheBottomEdge()
    {
        var dataset = Dataset(null, new AxisRange(0, 100), (10, 0), (20, 50), (30, 100));
        var transform = new ScatterCoordinateTransform();
        var control = new ScatterPlotControl();
        using var bitmap = new SKBitmap(600, 500);
        using (var canvas = new SKCanvas(bitmap))
        {
            control.RenderSkia(canvas, new Rect(0, 0, 600, 500), dataset, null, false, false, false, false,
                PriceScaleType.Linear, PriceScaleType.Linear, null, null, null, transform);
        }

        SKColor margin = bitmap.GetPixel(5, 5);
        int markerX = (int)System.Math.Round(transform.XToScreen(10));

        Assert.True(markerX > Left + 10 && markerX < Right - 10, "the autoscaled X axis keeps its padding, so the marker is inside");
        Assert.NotEqual(margin, bitmap.GetPixel(markerX, Bottom + 2));
    }

    [AvaloniaFact]
    public void AMarkerOutsideTheFixedRange_IsNotDrawn()
    {
        var dataset = Dataset(new AxisRange(0, 100), new AxisRange(0, 100), (50, 50), (150, 50));
        using SKBitmap bitmap = Render(dataset);
        SKColor margin = bitmap.GetPixel(5, 5);

        // x = 150 would be 260 px right of the right edge: the 60 px right margin shows nothing of it.
        Assert.Equal(margin, bitmap.GetPixel(Right + 20, MidY));
        Assert.Equal(margin, bitmap.GetPixel(Right + 40, MidY));
    }
}
