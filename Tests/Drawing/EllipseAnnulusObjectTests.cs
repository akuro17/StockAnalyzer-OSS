using System;
using System.Collections.Generic;
using global::Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Behaviors;
using StockAnalyzer.Core.Models;
using Xunit;
using Point = global::Avalonia.Point;
using Rect = global::Avalonia.Rect;

namespace StockAnalyzer.Tests.Drawing;

public class EllipseAnnulusObjectTests
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

    // --- EllipseArcGeometry additions ---

    [Fact]
    public void ScaleRectAroundCenter_HalvesSize_KeepsCenter()
    {
        var rect = new SKRect(100, 100, 300, 300); // center (200,200)
        var scaled = EllipseArcGeometry.ScaleRectAroundCenter(rect, 0.5f);

        Assert.Equal(new SKRect(150, 150, 250, 250), scaled);
    }

    [Fact]
    public void ComputeRadiusRatio_HalfwayPoint_ReturnsHalf()
    {
        var rect = new SKRect(100, 100, 300, 300); // center (200,200), r=100
        float ratio = EllipseArcGeometry.ComputeRadiusRatio(rect, new SKPoint(250, 200)); // 50px out
        Assert.Equal(0.5f, ratio, 2f);
    }

    [Fact]
    public void ComputeRadiusRatio_ClampsToSafeRange()
    {
        var rect = new SKRect(100, 100, 300, 300);
        Assert.Equal(0.02f, EllipseArcGeometry.ComputeRadiusRatio(rect, new SKPoint(200, 200)), 3f); // at center
        Assert.Equal(0.98f, EllipseArcGeometry.ComputeRadiusRatio(rect, new SKPoint(500, 200)), 3f); // beyond outer
    }

    [Theory]
    [InlineData(300, 200, 0)]    // due east
    [InlineData(200, 300, 90)]   // due south (screen Y grows downward)
    [InlineData(100, 200, 180)]  // due west
    [InlineData(200, 100, 270)]  // due north
    public void ComputeRotationAngle_CardinalDirections_ReturnsExpectedDegrees(float cornerX, float cornerY, float expectedDeg)
    {
        var center = new SKPoint(200, 200);
        float angle = EllipseArcGeometry.ComputeRotationAngle(center, new SKPoint(cornerX, cornerY));
        Assert.Equal(expectedDeg, angle, 1f);
    }

    [Fact]
    public void ComputeRotationAngle_UnlikeAngleFromPoint_IsNotDistortedByRectAspectRatio()
    {
        // A corner sitting at (dx=200, dy=100) from center is NOT at the same true screen angle as one
        // at (dx=100, dy=100) — ComputeRotationAngle must reflect the raw, undistorted direction,
        // unlike AngleFromPoint (which normalizes by a rect's own Rx/Ry and would give both the same
        // 45 deg when Rx=200/Ry=100 is used as the normalizing rect).
        var center = new SKPoint(200, 200);
        float trueAngle = EllipseArcGeometry.ComputeRotationAngle(center, new SKPoint(400, 300)); // dx=200, dy=100
        Assert.NotEqual(45f, trueAngle, 1f);
        Assert.Equal(26.565f, trueAngle, 1f); // atan2(100, 200)
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    [InlineData(90f)]
    [InlineData(123.4f)]
    [InlineData(-60f)]
    public void RotatePoint_ThenInverseRotate_ReturnsOriginalPoint(float angleDeg)
    {
        var center = new SKPoint(200, 200);
        var original = new SKPoint(350, 260);

        var rotated = EllipseArcGeometry.RotatePoint(original, center, angleDeg);
        var roundTripped = EllipseArcGeometry.RotatePoint(rotated, center, -angleDeg);

        Assert.Equal(original.X, roundTripped.X, 2f);
        Assert.Equal(original.Y, roundTripped.Y, 2f);
    }

    [Fact]
    public void RotatePoint_NinetyDegrees_MapsToExpectedScreenPosition()
    {
        // 90 deg in this screen-space convention (Y grows downward) sends +X to +Y (visually clockwise).
        var center = new SKPoint(200, 200);
        var point = new SKPoint(300, 200); // 100px due east of center

        var rotated = EllipseArcGeometry.RotatePoint(point, center, 90f);

        Assert.Equal(200f, rotated.X, 1f);
        Assert.Equal(300f, rotated.Y, 1f);
    }

    // --- EllipseAnnulusObject ---

    [Fact]
    public void Constructor_FivePoints_StoredInOrder()
    {
        var transform = new DummyCoordinateTransform();
        var p0 = transform.ScreenToChart(new Point(100, 100));
        var p1 = transform.ScreenToChart(new Point(300, 300));
        var p2 = transform.ScreenToChart(new Point(300, 200));
        var p3 = transform.ScreenToChart(new Point(200, 300));
        var p4 = transform.ScreenToChart(new Point(250, 200));

        var ring = new EllipseAnnulusObject(p0, p1, p2, p3, p4);

        Assert.Equal(ChartObjectType.EllipseAnnulus, ring.Type);
        Assert.Equal(5, ring.Points.Count);
        Assert.Equal(p4, ring.Points[4]);
    }

    [Fact]
    public void HitTest_FullRing_HitsBetweenInnerAndOuterRadius()
    {
        var transform = new DummyCoordinateTransform();
        var p0 = transform.ScreenToChart(new Point(100, 100));
        var p1 = transform.ScreenToChart(new Point(300, 300));
        // Start and end angle click at the same spot => 360 deg sweep (seamless full ring).
        var p2 = transform.ScreenToChart(new Point(300, 200));
        var p3 = transform.ScreenToChart(new Point(300, 200));
        var p4 = transform.ScreenToChart(new Point(250, 200)); // inner ratio 0.5 => inner radius 50

        var ring = new EllipseAnnulusObject(p0, p1, p2, p3, p4) { IsFilled = true };

        Assert.True(ring.HitTest(new Point(275, 200), transform, tolerance: 3.0));  // radius 75: in the ring band
        Assert.False(ring.HitTest(new Point(230, 200), transform, tolerance: 3.0)); // radius 30: inside the hole
        Assert.False(ring.HitTest(new Point(350, 200), transform, tolerance: 3.0)); // radius 150: outside the outer edge
    }

    [Fact]
    public void HitTest_AnnularSector_RestrictsToSweepRange()
    {
        var transform = new DummyCoordinateTransform();
        var p0 = transform.ScreenToChart(new Point(100, 100));
        var p1 = transform.ScreenToChart(new Point(300, 300));
        var p2 = transform.ScreenToChart(new Point(300, 200)); // start angle 0 deg
        var p3 = transform.ScreenToChart(new Point(200, 300)); // end angle 90 deg
        var p4 = transform.ScreenToChart(new Point(250, 200)); // inner ratio 0.5 => inner radius 50

        var ring = new EllipseAnnulusObject(p0, p1, p2, p3, p4) { IsFilled = true };

        // Inside the wedge, radius 75, 45 deg.
        double inWedge = 75.0 * Math.Cos(Math.PI / 4.0);
        Assert.True(ring.HitTest(new Point(200 + inWedge, 200 + inWedge), transform, tolerance: 3.0));

        // Same radius band (75, between inner 50 and outer 100) but at 180 deg, outside the [0,90] sweep.
        Assert.False(ring.HitTest(new Point(125, 200), transform, tolerance: 3.0));
    }

    [Fact]
    public void HitTest_InsufficientPoints_ReturnsFalse()
    {
        var transform = new DummyCoordinateTransform();
        var p0 = transform.ScreenToChart(new Point(100, 100));
        var ring = new EllipseAnnulusObject(p0, p0, p0, p0, p0);
        ring.Points.RemoveAt(4);

        Assert.False(ring.HitTest(new Point(200, 200), transform));
    }

    [Fact]
    public void Render_DrawsToCanvasWithoutException()
    {
        var transform = new DummyCoordinateTransform();
        var p0 = transform.ScreenToChart(new Point(100, 100));
        var p1 = transform.ScreenToChart(new Point(300, 300));
        var p2 = transform.ScreenToChart(new Point(300, 200));
        var p3 = transform.ScreenToChart(new Point(200, 300));
        var p4 = transform.ScreenToChart(new Point(250, 200));

        using var surface = SKSurface.Create(new SKImageInfo(800, 600));
        var canvas = surface.Canvas;

        var ring = new EllipseAnnulusObject(p0, p1, p2, p3, p4) { IsFilled = true };
        ring.Render(canvas, transform);
    }

    [Fact]
    public void Translate_MovesAllFivePoints()
    {
        var p0 = new ChartPoint(new DateTime(2025, 1, 1), 100m);
        var p1 = new ChartPoint(new DateTime(2025, 1, 5), 150m);
        var p2 = new ChartPoint(new DateTime(2025, 1, 3), 120m);
        var p3 = new ChartPoint(new DateTime(2025, 1, 4), 130m);
        var p4 = new ChartPoint(new DateTime(2025, 1, 2), 110m);

        var ring = new EllipseAnnulusObject(p0, p1, p2, p3, p4);
        ring.Translate(TimeSpan.FromDays(2), 10m);

        Assert.Equal(new DateTime(2025, 1, 4), ring.Points[4].Time);
        Assert.Equal(120m, ring.Points[4].Price);
    }

    // --- EllipseAnnulusObject.InnerRadiusRatio (settings-panel fine-tuning) ---

    [Fact]
    public void InnerRadiusRatio_Get_MatchesPointsFourPositionAlongExistingDirection()
    {
        var p0 = new ChartPoint(new DateTime(2025, 1, 1), 100m);   // outer box corner
        var p1 = new ChartPoint(new DateTime(2025, 1, 11), 300m);  // outer box corner (center: Jan 6, 200)
        var p2 = new ChartPoint(new DateTime(2025, 1, 6), 300m);
        var p3 = new ChartPoint(new DateTime(2025, 1, 6), 300m);
        var p4 = new ChartPoint(new DateTime(2025, 1, 11), 200m);  // +X direction, at outer edge (ratio 1.0 -> clamped 0.98)

        var ring = new EllipseAnnulusObject(p0, p1, p2, p3, p4);

        Assert.Equal(0.98, ring.InnerRadiusRatio, 2);
    }

    [Fact]
    public void InnerRadiusRatio_SetThenGet_RoundTrips()
    {
        var p0 = new ChartPoint(new DateTime(2025, 1, 1), 100m);
        var p1 = new ChartPoint(new DateTime(2025, 1, 11), 300m);
        var p2 = new ChartPoint(new DateTime(2025, 1, 6), 300m);
        var p3 = new ChartPoint(new DateTime(2025, 1, 6), 300m);
        var p4 = new ChartPoint(new DateTime(2025, 1, 11), 200m); // initial direction: +X

        var ring = new EllipseAnnulusObject(p0, p1, p2, p3, p4);

        ring.InnerRadiusRatio = 0.5;

        Assert.Equal(0.5, ring.InnerRadiusRatio, 2);
    }

    [Fact]
    public void InnerRadiusRatio_Set_PreservesExistingDirectionOfPointsFour()
    {
        var p0 = new ChartPoint(new DateTime(2025, 1, 1), 100m);
        var p1 = new ChartPoint(new DateTime(2025, 1, 11), 300m); // center: Jan 6, 200; half extents: 5 days, 100
        var p2 = new ChartPoint(new DateTime(2025, 1, 6), 300m);
        var p3 = new ChartPoint(new DateTime(2025, 1, 6), 300m);
        var p4 = new ChartPoint(new DateTime(2025, 1, 6), 300m);  // initial direction: +Y (toward higher price)

        var ring = new EllipseAnnulusObject(p0, p1, p2, p3, p4);

        ring.InnerRadiusRatio = 0.5;

        // Direction preserved (+Y only): Time stays at the outer-box center's day, Price moves to center + 0.5*halfHeight.
        Assert.Equal(new DateTime(2025, 1, 6), ring.Points[4].Time);
        Assert.Equal(250m, ring.Points[4].Price);
    }

    [Theory]
    [InlineData(0.0, 0.02)]
    [InlineData(-1.0, 0.02)]
    [InlineData(1.0, 0.98)]
    [InlineData(5.0, 0.98)]
    public void InnerRadiusRatio_Set_ClampsToSafeRange(double input, double expected)
    {
        var p0 = new ChartPoint(new DateTime(2025, 1, 1), 100m);
        var p1 = new ChartPoint(new DateTime(2025, 1, 11), 300m);
        var p2 = new ChartPoint(new DateTime(2025, 1, 6), 300m);
        var p3 = new ChartPoint(new DateTime(2025, 1, 6), 300m);
        var p4 = new ChartPoint(new DateTime(2025, 1, 11), 200m);

        var ring = new EllipseAnnulusObject(p0, p1, p2, p3, p4);

        ring.InnerRadiusRatio = input;

        Assert.Equal(expected, ring.InnerRadiusRatio, 2);
    }

    [Fact]
    public void InnerRadiusRatio_Get_DefaultsToHalf_WhenPointsIncomplete()
    {
        var p0 = new ChartPoint(new DateTime(2025, 1, 1), 100m);
        var ring = new EllipseAnnulusObject(p0, p0, p0, p0, p0);
        ring.Points.RemoveAt(4);

        Assert.Equal(0.5, ring.InnerRadiusRatio);
    }

    // Note: EllipseAnnulusSettingsPanelDefinition.Populate/Commit wiring (NumericUpDown lookup via
    // FindControl, which requires a real NameScope from XAML) is covered by an [AvaloniaFact] test in
    // Tests/StockAnalyzer.Avalonia.Tests/Views/DrawingSettingsDialogTests.cs, matching the existing
    // NurbsWeightedCurvePanel test's pattern rather than this plain (non-Avalonia-headless) project.

    // --- EllipseAnnulusBehavior ---

    [Fact]
    public void Behavior_RequiredSteps_IsFive()
    {
        var behavior = new EllipseAnnulusBehavior();
        Assert.Equal(5, behavior.RequiredSteps);
        Assert.False(behavior.FinishesOnRelease);
    }

    [Fact]
    public void Behavior_CreateObject_AllFivePointsStartAtClickPosition()
    {
        var behavior = new EllipseAnnulusBehavior();
        var p0 = new ChartPoint(new DateTime(2025, 1, 1), 100m);

        var obj = behavior.CreateObject(p0);

        Assert.IsType<EllipseAnnulusObject>(obj);
        Assert.Equal(5, obj.Points.Count);
        Assert.All(obj.Points, p => Assert.Equal(p0, p));
    }

    [Fact]
    public void Behavior_UpdatePoint_EachStepUpdatesOnlyItsOwnPoint()
    {
        var behavior = new EllipseAnnulusBehavior();
        var p0 = new ChartPoint(new DateTime(2025, 1, 1), 100m);
        var p1 = new ChartPoint(new DateTime(2025, 1, 2), 110m);
        var p2 = new ChartPoint(new DateTime(2025, 1, 3), 120m);
        var p3 = new ChartPoint(new DateTime(2025, 1, 4), 130m);
        var p4 = new ChartPoint(new DateTime(2025, 1, 5), 140m);

        var obj = behavior.CreateObject(p0);

        behavior.UpdatePoint(obj, 1, p1);
        Assert.Equal(p1, obj.Points[1]);

        behavior.UpdatePoint(obj, 2, p2);
        Assert.Equal(p2, obj.Points[2]);
        Assert.Equal(p0, obj.Points[3]); // untouched until its own step

        behavior.UpdatePoint(obj, 3, p3);
        Assert.Equal(p3, obj.Points[3]);
        Assert.Equal(p0, obj.Points[4]); // untouched until its own step

        behavior.UpdatePoint(obj, 4, p4);
        Assert.Equal(p4, obj.Points[4]);
    }

    // --- Registry ---

    [Fact]
    public void BehaviorRegistry_HasEllipseAnnulusBehavior()
    {
        var behavior = DrawingToolBehaviorRegistry.GetBehavior(DrawingTool.EllipseAnnulus);
        Assert.NotNull(behavior);
        Assert.Equal(5, behavior.RequiredSteps);
        Assert.False(behavior.FinishesOnRelease);
    }

    [Fact]
    public void GetCategories_ShapesCategory_NoLongerContainsEllipseAnnulus()
    {
        // Retired from the drawing-tool menu: its capability (a ring/annular-sector shape) was ported
        // into EllipseObject.InnerRadiusRatio, so users can no longer place a NEW EllipseAnnulusObject.
        // The class itself, its serialization, and its rendering remain intact for backward
        // compatibility with previously saved workspaces -- see
        // ChartObjectPersistenceTests.EllipseAnnulusObject_RoundTrips_DespiteBeingRetiredFromTheDrawingToolMenu.
        var categories = StockAnalyzer.Avalonia.Services.DrawingToolCategoryService.GetCategories();
        var shapesCategory = System.Linq.Enumerable.FirstOrDefault(categories, c => c.NameKey == "DrawCat_Shapes");

        Assert.NotNull(shapesCategory);
        Assert.DoesNotContain(shapesCategory.Tools, t => t.Tool == DrawingTool.EllipseAnnulus);
    }
}
