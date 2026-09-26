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

public class EllipseObjectTests
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

    // --- Center+corner click model (Ellipse mode, default) ---

    [Fact]
    public void Constructor_CenterAndCorner_DefaultsToCircleMode()
    {
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var corner = transform.ScreenToChart(new Point(300, 250));

        var ellipse = new EllipseObject(center, corner);

        Assert.True(ellipse.IsCircular);
        Assert.False(ellipse.IsArcEnabled);
        Assert.False(ellipse.ShowRadiusLines);
        Assert.False(ellipse.ShowChordLine);
        Assert.Equal(0.5, ellipse.AspectRatio);
        Assert.Equal(4, ellipse.Points.Count);
    }

    [Fact]
    public void Constructor_AlwaysCreatesCircumferenceHandles_DefaultingToOppositeSideOfCorner_NotOverlappingIt()
    {
        // Points[2]/[3] (the circumference angle handles) must always exist, regardless of
        // IsArcEnabled — the user can see/drag them even in Ellipse mode. They default to the boundary
        // point diametrically OPPOSITE the corner (not the corner's own direction), so their marker
        // never renders on top of the corner handle.
        var center = new ChartPoint(new DateTime(2025, 1, 1), 200m);
        var corner = new ChartPoint(new DateTime(2025, 1, 11), 300m); // rx=10 days, ry=100 (diagonal)

        var ellipse = new EllipseObject(center, corner);

        Assert.Equal(4, ellipse.Points.Count);
        Assert.Equal(ellipse.Points[2], ellipse.Points[3]); // coincident: default full sweep
        Assert.NotEqual(corner, ellipse.Points[2]);

        // The default must sit on the opposite side of center from the corner: Time before center's,
        // Price below center's (corner is Time after / Price above center's).
        Assert.True(ellipse.Points[2].Time < center.Time);
        Assert.True(ellipse.Points[2].Price < center.Price);
    }

    [Fact]
    public void ControlPointColor_DefaultsToOrange_AndIsSettableIndependentlyOfOtherFlags()
    {
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var corner = transform.ScreenToChart(new Point(300, 250));

        var ellipse = new EllipseObject(center, corner);
        Assert.Equal(global::Avalonia.Media.Colors.Orange, ellipse.ControlPointColor);

        ellipse.ControlPointColor = global::Avalonia.Media.Colors.Magenta;
        Assert.Equal(global::Avalonia.Media.Colors.Magenta, ellipse.ControlPointColor);
    }

    [Fact]
    public void AspectRatio_IsSettableIndependentlyOfOtherFlags()
    {
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var corner = transform.ScreenToChart(new Point(300, 250));

        var ellipse = new EllipseObject(center, corner) { AspectRatio = 0.25 };

        Assert.Equal(0.25, ellipse.AspectRatio);
    }

    [Fact]
    public void DynamicAspectRatioByDistance_DefaultsToFalse_WithNullReferenceCorner()
    {
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var corner = transform.ScreenToChart(new Point(300, 250));

        var ellipse = new EllipseObject(center, corner);

        Assert.False(ellipse.DynamicAspectRatioByDistance);
        Assert.Null(ellipse.DynamicAspectRatioReferenceCorner);
    }

    [Fact]
    public void DynamicAspectRatioByDistance_CornerCloserThanReference_GrowsTaller()
    {
        // center (200,200); reference corner due east at distance 100 (a circle of radius 100).
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var referenceCorner = transform.ScreenToChart(new Point(300, 200)); // distance 100

        // Corner dragged CLOSER to the center (distance 50) must grow Ry beyond the reference radius:
        // Rx=50, Ry=100^2/50=200 -- taller than it is wide. IsFilled=false so only near-boundary points
        // hit, distinguishing the correct Ry=200 from a wrong value (e.g. Ry=50 or Ry=100).
        var closerCorner = transform.ScreenToChart(new Point(250, 200)); // distance 50
        var ellipse = new EllipseObject(center, closerCorner)
        {
            IsFilled = false,
            IsCircular = false,
            DynamicAspectRatioByDistance = true,
            DynamicAspectRatioReferenceCorner = referenceCorner
        };

        Assert.True(ellipse.HitTest(new Point(250, 200), transform, tolerance: 2.0)); // Rx=50 boundary
        Assert.True(ellipse.HitTest(new Point(200, 400), transform, tolerance: 2.0)); // Ry=200 boundary
        Assert.False(ellipse.HitTest(new Point(200, 300), transform, tolerance: 2.0)); // distance 100 != Ry=200
    }

    [Fact]
    public void DynamicAspectRatioByDistance_CornerFartherThanReference_GrowsWider()
    {
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var referenceCorner = transform.ScreenToChart(new Point(300, 200)); // distance 100

        // Corner dragged FARTHER from the center (distance 200) must shrink Ry below the reference
        // radius: Rx=200, Ry=100^2/200=50 -- wider than it is tall.
        var fartherCorner = transform.ScreenToChart(new Point(400, 200)); // distance 200
        var ellipse = new EllipseObject(center, fartherCorner)
        {
            IsCircular = false,
            DynamicAspectRatioByDistance = true,
            DynamicAspectRatioReferenceCorner = referenceCorner
        };

        Assert.True(ellipse.HitTest(new Point(400, 200), transform, tolerance: 2.0)); // Rx=200 boundary
        Assert.True(ellipse.HitTest(new Point(200, 250), transform, tolerance: 2.0)); // Ry=50 boundary
        Assert.False(ellipse.HitTest(new Point(200, 280), transform, tolerance: 2.0)); // well beyond Ry=50
    }

    [Fact]
    public void DynamicAspectRatioByDistance_IgnoredWhileCircular()
    {
        // IsCircular still forces Rx=Ry=distance regardless of DynamicAspectRatioByDistance, matching
        // Converse ($Q \implies P$) in the implementation plan: Circle mode is unaffected either way.
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var corner = transform.ScreenToChart(new Point(300, 200));
        var referenceCorner = transform.ScreenToChart(new Point(220, 200));

        var ellipse = new EllipseObject(center, corner)
        {
            IsCircular = true,
            DynamicAspectRatioByDistance = true,
            DynamicAspectRatioReferenceCorner = referenceCorner
        };

        Assert.True(ellipse.HitTest(new Point(300, 200), transform, tolerance: 2.0)); // Rx=Ry=100
        Assert.True(ellipse.HitTest(new Point(200, 300), transform, tolerance: 2.0));
    }

    [Fact]
    public void DynamicAspectRatioByDistance_EquivalentAreaReference_ShapeUnchangedAtTheMomentOfEnabling()
    {
        // Regression test for the reported bug: turning DynamicAspectRatioByDistance on must not snap
        // an elongated ellipse into a circle. With AspectRatio=0.25 and corner distance=100, Ry=25
        // before enabling; the reference corner must sit at the EQUIVALENT-AREA radius
        // (100*sqrt(0.25)=50, not the raw 100), so that immediately after enabling (before any further
        // drag), Ry = referenceDistance^2/distance = 50^2/100 = 25 -- unchanged.
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var corner = transform.ScreenToChart(new Point(300, 200)); // due east, distance 100

        var before = new EllipseObject(center, corner) { IsFilled = false, IsCircular = false, AspectRatio = 0.25 };
        Assert.True(before.HitTest(new Point(200, 225), transform, tolerance: 2.0)); // Ry=25 boundary
        Assert.False(before.HitTest(new Point(200, 250), transform, tolerance: 2.0)); // not Ry=50

        var equivalentAreaReference = EllipseArcGeometry.ScaleCornerInChartSpace(center, corner, Math.Sqrt(0.25));
        var after = new EllipseObject(center, corner)
        {
            IsFilled = false,
            IsCircular = false,
            AspectRatio = 0.25,
            DynamicAspectRatioByDistance = true,
            DynamicAspectRatioReferenceCorner = equivalentAreaReference
        };

        // Same boundary as before enabling -- Ry=25, not snapped to Rx=100 (a circle).
        Assert.True(after.HitTest(new Point(200, 225), transform, tolerance: 2.0));
        Assert.False(after.HitTest(new Point(200, 250), transform, tolerance: 2.0));
        Assert.False(after.HitTest(new Point(200, 300), transform, tolerance: 2.0)); // nowhere near Ry=100
    }

    [Fact]
    public void EllipticityActivationCorner_DefaultsToNull()
    {
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var corner = transform.ScreenToChart(new Point(300, 250));

        var ellipse = new EllipseObject(center, corner);

        Assert.Null(ellipse.EllipticityActivationCorner);
    }

    [Fact]
    public void EllipticityActivationCorner_MatchesCurrentCorner_StaysCircular_IgnoringAspectRatio()
    {
        // Regression test for the reported bug: turning Ellipse Mode on (IsCircular -> false) must not
        // itself change the shape's appearance. EllipseSettingsPanelDefinition.Commit captures the
        // corner's position at that moment into EllipticityActivationCorner; as long as the corner has
        // not since moved from it, the shape must keep rendering as a circle (Ry=Rx) despite
        // IsCircular now being false and AspectRatio=0.5.
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var corner = transform.ScreenToChart(new Point(300, 200)); // distance 100

        var ellipse = new EllipseObject(center, corner)
        {
            IsFilled = false,
            IsCircular = false,
            AspectRatio = 0.5,
            EllipticityActivationCorner = corner
        };

        Assert.True(ellipse.HitTest(new Point(300, 200), transform, tolerance: 2.0)); // Rx=100 boundary
        Assert.True(ellipse.HitTest(new Point(200, 300), transform, tolerance: 2.0)); // Ry=100 too -- still a circle
        Assert.False(ellipse.HitTest(new Point(200, 250), transform, tolerance: 2.0)); // not Ry=50 (AspectRatio ignored)
    }

    [Fact]
    public void EllipticityActivationCorner_CornerMovedAway_AppliesAspectRatioNormally()
    {
        // Once the corner is actually dragged away from the captured activation position, the shape
        // must switch to the normal AspectRatio-scaled ellipse -- Ellipse Mode "activates" from that
        // drag onward, exactly as the user requested.
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var activationCorner = transform.ScreenToChart(new Point(250, 200)); // where the corner was at toggle-on
        var draggedCorner = transform.ScreenToChart(new Point(300, 200)); // distance 100, since dragged

        var ellipse = new EllipseObject(center, draggedCorner)
        {
            IsFilled = false,
            IsCircular = false,
            AspectRatio = 0.5,
            EllipticityActivationCorner = activationCorner
        };

        Assert.True(ellipse.HitTest(new Point(300, 200), transform, tolerance: 2.0)); // Rx=100 boundary
        Assert.True(ellipse.HitTest(new Point(200, 250), transform, tolerance: 2.0)); // Ry=50 boundary
        Assert.False(ellipse.HitTest(new Point(200, 300), transform, tolerance: 2.0)); // not Ry=100 anymore
    }

    [Fact]
    public void EllipticityActivationCorner_IgnoredWhileCircular()
    {
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var corner = transform.ScreenToChart(new Point(300, 200));

        var ellipse = new EllipseObject(center, corner)
        {
            IsFilled = false,
            IsCircular = true,
            AspectRatio = 0.5,
            EllipticityActivationCorner = corner
        };

        Assert.True(ellipse.HitTest(new Point(200, 300), transform, tolerance: 2.0)); // Rx=Ry=100 (IsCircular wins)
    }

    [Fact]
    public void EllipticityActivationCorner_IgnoredWhileDynamicAspectRatioByDistanceActive()
    {
        // DynamicAspectRatioByDistance's own reference-corner mechanism must take priority over a
        // stale EllipticityActivationCorner from before it was enabled.
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var corner = transform.ScreenToChart(new Point(300, 200)); // distance 100
        var dynamicReference = transform.ScreenToChart(new Point(250, 200)); // distance 50

        var ellipse = new EllipseObject(center, corner)
        {
            IsFilled = false,
            IsCircular = false,
            AspectRatio = 0.5,
            EllipticityActivationCorner = corner,
            DynamicAspectRatioByDistance = true,
            DynamicAspectRatioReferenceCorner = dynamicReference
        };

        // Ry = referenceDistance^2 / distance = 50^2/100 = 25, not Rx=100 (which EllipticityActivationCorner
        // matching the corner would otherwise force).
        Assert.True(ellipse.HitTest(new Point(200, 225), transform, tolerance: 2.0));
        Assert.False(ellipse.HitTest(new Point(200, 300), transform, tolerance: 2.0));
    }

    [Fact]
    public void HitTest_NonCircular_PointAlongMajorAxisHits_SameOffsetAlongMinorAxisMisses()
    {
        var transform = new DummyCoordinateTransform();
        // center (200,200), corner (300,200): due east (no rotation) => rx=100 (distance to corner),
        // ry=rx*AspectRatio=50 (default AspectRatio 0.5).
        var center = transform.ScreenToChart(new Point(200, 200));
        var corner = transform.ScreenToChart(new Point(300, 200));

        var ellipse = new EllipseObject(center, corner) { IsFilled = true, IsCircular = false };

        // 70px along the major (X) axis: inside the ellipse (rx=100).
        Assert.True(ellipse.HitTest(new Point(270, 200), transform, tolerance: 3.0));

        // The same 70px offset along the minor (Y) axis exceeds ry=50, so it must miss.
        Assert.False(ellipse.HitTest(new Point(200, 270), transform, tolerance: 3.0));
    }

    [Fact]
    public void HitTest_Circular_UsesFullDistanceToCornerAsRadius_ExpandingTheMinorAxis()
    {
        // IsCircular forces ry = rx (rx = full distance to the corner) rather than the
        // AspectRatio-scaled ry used otherwise, so a point that misses along the minor axis in
        // ellipse mode can hit once circular (the circle's radius equals the major-axis distance,
        // matching the corner point sitting exactly on the boundary in either mode).
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var corner = transform.ScreenToChart(new Point(300, 200)); // rx=100

        var ellipseNonCircular = new EllipseObject(center, corner) { IsFilled = true, IsCircular = false };
        var ellipseCircular = new EllipseObject(center, corner) { IsFilled = true, IsCircular = true };

        var minorAxisPoint = new Point(200, 270); // 70px along the minor/Y axis
        Assert.False(ellipseNonCircular.HitTest(minorAxisPoint, transform, tolerance: 3.0)); // ry=50, misses
        Assert.True(ellipseCircular.HitTest(minorAxisPoint, transform, tolerance: 3.0));      // ry=rx=100, hits
    }

    [Fact]
    public void HitTest_RotatedEllipse_PointAlongRotatedMajorAxisHits_SameScreenOffsetAlongUnrotatedAxisMisses()
    {
        // center (200,200), corner (300,300): 45 deg direction => rotationAngle=45, rx=~141.4,
        // ry=rx*0.5=~70.7. This proves true rotation is applied: a point along the *rotated* major
        // axis hits, while the same raw screen offset a hypothetical *unrotated* ellipse would have
        // accepted along its major axis now misses, because the ellipse's own axes have rotated away
        // from it.
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var corner = transform.ScreenToChart(new Point(300, 300));

        var ellipse = new EllipseObject(center, corner) { IsFilled = true, IsCircular = false };

        // 100px along the rotated major axis (45 deg direction: cos45*100, sin45*100).
        double along45 = 100.0 * Math.Cos(Math.PI / 4.0);
        Assert.True(ellipse.HitTest(new Point(200 + along45, 200 + along45), transform, tolerance: 3.0));

        // 100px due east (0 deg, screen X axis) — would be "along the major axis" for an unrotated
        // ellipse with the same rx, but this ellipse's major axis actually points at 45 deg, so this
        // point falls outside.
        Assert.False(ellipse.HitTest(new Point(300, 200), transform, tolerance: 3.0));
    }

    [Fact]
    public void HitTest_DraggingCenterHandle_TranslatesRatherThanResizing()
    {
        // Regression guard for the "center is fixed as a pivot" requirement: Translate (the
        // mechanism the center handle drag now uses) must move both center and corner by the same
        // delta, preserving Rx/Ry, unlike a diagonal-corner drag which would resize asymmetrically.
        var center = new ChartPoint(new DateTime(2025, 1, 1), 200m);
        var corner = new ChartPoint(new DateTime(2025, 1, 2), 250m);
        var ellipse = new EllipseObject(center, corner);

        ellipse.Translate(TimeSpan.FromDays(1), 10m);

        Assert.Equal(new DateTime(2025, 1, 2), ellipse.Points[0].Time);
        Assert.Equal(210m, ellipse.Points[0].Price);
        Assert.Equal(new DateTime(2025, 1, 3), ellipse.Points[1].Time);
        Assert.Equal(260m, ellipse.Points[1].Price);
    }

    [Fact]
    public void Render_Circular_DrawsToCanvasWithoutException()
    {
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var corner = transform.ScreenToChart(new Point(300, 250));

        using var surface = SKSurface.Create(new SKImageInfo(800, 600));
        var canvas = surface.Canvas;

        var ellipse = new EllipseObject(center, corner) { IsFilled = true, IsCircular = true };

        ellipse.Render(canvas, transform);
    }

    [Fact]
    public void Render_Selected_NonArcMode_DrawsCircumferenceHandlesWithoutException()
    {
        // Regression guard for always-visible circumference handles: selecting a plain (Arc-disabled)
        // ellipse must draw all 4 handles (center, corner, and the two circumference angle handles)
        // without throwing, even though Points[2]/[3] have no effect on the rendered shape itself.
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var corner = transform.ScreenToChart(new Point(300, 250));

        using var surface = SKSurface.Create(new SKImageInfo(800, 600));
        var canvas = surface.Canvas;

        var ellipse = new EllipseObject(center, corner) { IsSelected = true };

        var exception = Record.Exception(() => ellipse.Render(canvas, transform));
        Assert.Null(exception);
    }

    // --- EllipseBehavior (unchanged 2-click TwoClickBehavior, now interpreted as center+corner) ---

    [Fact]
    public void Behavior_RequiredSteps_IsTwo()
    {
        var behavior = new EllipseBehavior();
        Assert.Equal(2, behavior.RequiredSteps);
        Assert.False(behavior.FinishesOnRelease);
    }

    [Fact]
    public void BehaviorRegistry_HasEllipseBehavior()
    {
        var behavior = DrawingToolBehaviorRegistry.GetBehavior(DrawingTool.Ellipse);
        Assert.NotNull(behavior);
        Assert.Equal(2, behavior.RequiredSteps);
    }

    [Fact]
    public void Behavior_RealisticPlacementDrag_CircumferenceHandlesDefaultOppositeCorner_NotAdjacentToIt()
    {
        // Regression test for the REAL 2-click placement flow (not a directly-constructed EllipseObject
        // with an already-final corner): EllipseObject's own constructor only ever sees the degenerate
        // center==corner state at the very first click (CreateInstance(p, p)), so any boundary-direction
        // default computed there collapses to the center itself, not "opposite the corner" — and since
        // Points[0]/[2]/[3] never move relative to each other for the rest of the placement drag (only
        // Points[1] does, via TwoClickBehavior.UpdatePoint), that degenerate default silently ends up
        // matching the corner's OWN angle once placement finishes (same angle => rendered on/near the
        // corner handle, not opposite it), defeating EllipseObject's opposite-side default entirely.
        // EllipseBehavior.OnPointUpdated must keep re-deriving Points[2]/[3] against the CURRENT corner
        // throughout the live drag so the final result actually reflects the corner's final position.
        var transform = new DummyCoordinateTransform();
        var behavior = new EllipseBehavior();

        var p1 = transform.ScreenToChart(new Point(200, 200));
        var obj = (EllipseObject)behavior.CreateObject(p1);

        var p2 = transform.ScreenToChart(new Point(300, 250)); // diagonal drag, not due-east
        behavior.UpdatePoint(obj, 1, p2);

        var handles = obj.GetSelectionHandleScreenPositions(transform);

        Assert.Equal(4, handles.Length);
        Assert.Equal(new Point(200, 200), handles[0]); // center
        Assert.Equal(new Point(300, 250), handles[1]); // corner

        // Corner is up-and-right of center (+100, +50); the circumference handles must default to the
        // OPPOSITE side (down-and-left of center), not merely "somewhere not exactly on the corner".
        Assert.True(handles[2].X < 200.0 && handles[2].Y < 200.0, $"handle[2] ({handles[2].X},{handles[2].Y}) is not on the opposite side of center from the corner.");
        Assert.True(handles[3].X < 200.0 && handles[3].Y < 200.0, $"handle[3] ({handles[3].X},{handles[3].Y}) is not on the opposite side of center from the corner.");

        // Must also be comfortably far from the corner handle (grabbable as distinct handles), not just
        // technically unequal.
        double distToCorner2 = Math.Sqrt(Math.Pow(handles[2].X - handles[1].X, 2) + Math.Pow(handles[2].Y - handles[1].Y, 2));
        double distToCorner3 = Math.Sqrt(Math.Pow(handles[3].X - handles[1].X, 2) + Math.Pow(handles[3].Y - handles[1].Y, 2));
        Assert.True(distToCorner2 > 100.0);
        Assert.True(distToCorner3 > 100.0);
        Assert.NotEqual(handles[2], handles[3]); // nudged apart from each other too
    }

    [Fact]
    public void Behavior_DraggingOneCircumferenceHandleAfterPlacement_LeavesTheOtherFarFromCorner()
    {
        // Regression test combining the realistic placement flow with the "drag one, check the other"
        // scenario: after a real 2-click placement, dragging one circumference handle away must leave
        // the untouched sibling comfortably far from the corner handle (not stacked on it), matching
        // Behavior_RealisticPlacementDrag_CircumferenceHandlesDefaultOppositeCorner_NotAdjacentToIt's
        // opposite-side default.
        var transform = new DummyCoordinateTransform();
        var behavior = new EllipseBehavior();

        var p1 = transform.ScreenToChart(new Point(200, 200));
        var obj = (EllipseObject)behavior.CreateObject(p1);
        var p2 = transform.ScreenToChart(new Point(300, 250));
        behavior.UpdatePoint(obj, 1, p2);

        obj.IsArcEnabled = true;
        obj.Points[2] = transform.ScreenToChart(new Point(200, 300)); // drag handle 2 to 90 deg (down)

        var handles = obj.GetSelectionHandleScreenPositions(transform);
        var cornerHandle = handles[1];
        var untouchedHandle = handles[3];

        double distance = Math.Sqrt(
            Math.Pow(untouchedHandle.X - cornerHandle.X, 2) + Math.Pow(untouchedHandle.Y - cornerHandle.Y, 2));
        Assert.True(distance > 100.0, $"Untouched circumference handle at ({untouchedHandle.X},{untouchedHandle.Y}) is too close to the corner handle at ({cornerHandle.X},{cornerHandle.Y}).");
    }

    [Fact]
    public void DraggingOneCircumferenceHandle_LeavesTheOtherGrabbable_NotStackedOnCorner()
    {
        // Regression test: dragging one of the two circumference handles (Points[2]) away from its
        // coincident default used to leave the other (Points[3], still untouched) rendered exactly on
        // top of the corner handle — because the old default direction was the same as the corner's own
        // direction. Now that the default sits on the OPPOSITE side of the corner, the untouched handle
        // must stay far away from the corner even after its sibling has been dragged elsewhere.
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var corner = transform.ScreenToChart(new Point(300, 250)); // diagonal, not due-east

        var ellipse = new EllipseObject(center, corner) { IsArcEnabled = true };

        // Simulate dragging Points[2] to a genuinely different angle (90 deg, straight down from
        // center), matching how ChartInteractionController projects a raw drag point onto the boundary.
        ellipse.Points[2] = transform.ScreenToChart(new Point(200, 300));

        var handles = ellipse.GetSelectionHandleScreenPositions(transform);

        Assert.Equal(4, handles.Length);
        var cornerHandle = handles[1];
        var untouchedHandle = handles[3];

        double distance = Math.Sqrt(
            Math.Pow(untouchedHandle.X - cornerHandle.X, 2) + Math.Pow(untouchedHandle.Y - cornerHandle.Y, 2));
        Assert.True(distance > 20.0, $"Untouched circumference handle at ({untouchedHandle.X},{untouchedHandle.Y}) is too close to the corner handle at ({cornerHandle.X},{cornerHandle.Y}); it would be hard/impossible to grab independently.");
    }

    // --- Arc-enabled family (always-drawn circumference + independent Radius Lines/Chord Line
    // toggles), merged from the former EllipseArcObject/ArcShapeMode design. Most tests below use a
    // due-east corner (200,200)+(300,200) with IsCircular=true, giving a rotation-free rx=ry=100
    // circle so the angle/hit assertions stay simple; true rotation is covered separately by
    // HitTest_RotatedEllipse_* (Ellipse mode) and the dedicated Arc-family rotation test further down.

    [Fact]
    public void HitTest_PointOnArcWithinSweep_ReturnsTrue()
    {
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        // Corner due east (no rotation) + Circular => rx=ry=100 (distance to corner).
        var corner = transform.ScreenToChart(new Point(300, 200));
        var startAnglePoint = transform.ScreenToChart(new Point(300, 200)); // 0 deg
        var endAnglePoint = transform.ScreenToChart(new Point(200, 300));   // 90 deg

        var ellipse = new EllipseObject(center, corner) { IsArcEnabled = true, IsCircular = true };
        ellipse.Points[2] = startAnglePoint;
        ellipse.Points[3] = endAnglePoint;

        double angled = 100.0 * Math.Cos(Math.PI / 4.0);
        var midArcPoint = new Point(200 + angled, 200 + angled);

        Assert.True(ellipse.HitTest(midArcPoint, transform, tolerance: 3.0));
    }

    [Fact]
    public void HitTest_PointOnEllipseButOutsideSweep_ReturnsFalse()
    {
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var corner = transform.ScreenToChart(new Point(300, 200)); // due east + Circular => rx=ry=100
        var startAnglePoint = transform.ScreenToChart(new Point(300, 200));
        var endAnglePoint = transform.ScreenToChart(new Point(200, 300));

        var ellipse = new EllipseObject(center, corner) { IsArcEnabled = true, IsCircular = true };
        ellipse.Points[2] = startAnglePoint;
        ellipse.Points[3] = endAnglePoint;

        // (100,200) sits exactly on the ellipse boundary at 180 deg, outside the [0,90] sweep.
        Assert.False(ellipse.HitTest(new Point(100, 200), transform, tolerance: 3.0));
    }

    [Fact]
    public void HitTest_RadiusLines_Filled_CenterPointHits()
    {
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var corner = transform.ScreenToChart(new Point(300, 200)); // due east + Circular => rx=ry=100
        var startAnglePoint = transform.ScreenToChart(new Point(300, 200));
        var endAnglePoint = transform.ScreenToChart(new Point(200, 300));

        var ellipse = new EllipseObject(center, corner) { IsArcEnabled = true, ShowRadiusLines = true, IsFilled = true, IsCircular = true };
        ellipse.Points[2] = startAnglePoint;
        ellipse.Points[3] = endAnglePoint;

        // Radius Lines include the two radii back to center, so the center itself is inside the wedge.
        Assert.True(ellipse.HitTest(new Point(200, 200), transform, tolerance: 3.0));
    }

    [Fact]
    public void HitTest_ChordLine_Filled_ExcludesCenterButIncludesOuterCap()
    {
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var corner = transform.ScreenToChart(new Point(300, 200)); // due east + Circular => rx=ry=100
        var startAnglePoint = transform.ScreenToChart(new Point(300, 200));
        var endAnglePoint = transform.ScreenToChart(new Point(200, 300));

        var ellipse = new EllipseObject(center, corner) { IsArcEnabled = true, ShowChordLine = true, IsFilled = true, IsCircular = true };
        ellipse.Points[2] = startAnglePoint;
        ellipse.Points[3] = endAnglePoint;

        Assert.False(ellipse.HitTest(new Point(200, 200), transform, tolerance: 3.0));

        double offset = 95.0 * Math.Cos(Math.PI / 4.0);
        Assert.True(ellipse.HitTest(new Point(200 + offset, 200 + offset), transform, tolerance: 3.0));
    }

    [Fact]
    public void HitTest_ChordLineOnly_ArcIsAlwaysDrawnUnlikeTheRetiredChordOnlyMode()
    {
        // Behavior intentionally differs from the retired ArcShapeMode.Chord (which drew *only* the
        // straight line, no arc at all): the circumference is now always drawn regardless of which
        // line toggles are on, so both the chord line and the arc curve itself must hit.
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var corner = transform.ScreenToChart(new Point(300, 200)); // due east + Circular => rx=ry=100
        var startAnglePoint = transform.ScreenToChart(new Point(300, 200));
        var endAnglePoint = transform.ScreenToChart(new Point(200, 300));

        var ellipse = new EllipseObject(center, corner) { IsArcEnabled = true, ShowChordLine = true, IsFilled = false, IsCircular = true };
        ellipse.Points[2] = startAnglePoint;
        ellipse.Points[3] = endAnglePoint;

        // On the straight chord line.
        Assert.True(ellipse.HitTest(new Point(250, 250), transform, tolerance: 3.0));

        // On the arc curve itself (stroke), which the retired Chord-only mode never drew.
        double onArc = 100.0 * Math.Cos(Math.PI / 4.0);
        Assert.True(ellipse.HitTest(new Point(200 + onArc, 200 + onArc), transform, tolerance: 3.0));
    }

    [Fact]
    public void HitTest_BothRadiusLinesAndChordLine_UsesRadiusLinesAsFillBoundary()
    {
        // When both toggles are on, the radius-lines closure (the larger wedge) defines the fill
        // boundary; the chord is composed on top purely as a decorative stroke-only diagonal.
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var corner = transform.ScreenToChart(new Point(300, 200)); // due east + Circular => rx=ry=100
        var startAnglePoint = transform.ScreenToChart(new Point(300, 200));
        var endAnglePoint = transform.ScreenToChart(new Point(200, 300));

        var ellipse = new EllipseObject(center, corner)
        {
            IsArcEnabled = true,
            ShowRadiusLines = true,
            ShowChordLine = true,
            IsFilled = true,
            IsCircular = true
        };
        ellipse.Points[2] = startAnglePoint;
        ellipse.Points[3] = endAnglePoint;

        // Center is inside the wedge fill (radius-lines boundary), same as Radius-Lines-only.
        Assert.True(ellipse.HitTest(new Point(200, 200), transform, tolerance: 3.0));

        // The chord line itself (a straight diagonal inside the wedge) also hits, as a stroked segment.
        Assert.True(ellipse.HitTest(new Point(250, 250), transform, tolerance: 3.0));
    }

    [Fact]
    public void HitTest_CoincidentAngleHandles_DefaultToFullSweep()
    {
        // Regression guard for the settings-panel default: both angle handles start out as a copy
        // of the corner point (see EllipseSettingsPanelDefinition.Commit). Only their *direction*
        // from the center matters, so two coincident points collapse to a 360 deg sweep — the user
        // then drags one apart to carve a partial arc/wedge out of the initial full shape.
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var corner = transform.ScreenToChart(new Point(300, 200)); // due east + Circular => rx=ry=100

        var ellipse = new EllipseObject(center, corner) { IsArcEnabled = true, ShowRadiusLines = true, IsFilled = true, IsCircular = true };
        ellipse.Points[2] = corner; // both angle handles set to a copy of the corner
        ellipse.Points[3] = corner;

        // A point on the opposite side (225 deg) is still inside the now-full-circle wedge.
        double opposite = 100.0 * Math.Cos(5.0 * Math.PI / 4.0); // -70.7
        Assert.True(ellipse.HitTest(new Point(200 + opposite, 200 + opposite), transform, tolerance: 3.0));
    }

    [Fact]
    public void ArcMode_DraggingCoincidentHandle_InNaturalNudgeDirection_KeepsMajorityFilled()
    {
        // Regression test: when the two circumference handles are still coincident (default, full
        // 360-degree sweep) and the user grabs one and drags it further in the direction its marker is
        // already visually nudged toward (the most natural first drag), the result must be the MAJORITY
        // of the circle staying filled (a small notch carved out), not the minority collapsing down to
        // just that small sliver.
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var corner = transform.ScreenToChart(new Point(300, 200)); // due east + Circular => rx=ry=100

        var ellipse = new EllipseObject(center, corner) { IsCircular = true, IsArcEnabled = true, ShowRadiusLines = true, IsFilled = true };
        // Constructor default: Points[2]/[3] both at the opposite-side boundary point (local angle 180
        // deg, screen (100,200)). Handle index 2's marker nudges toward local angle 188 deg (+8 deg);
        // simulate continuing to drag it further in that same direction, past the nudge, to 220 deg —
        // matching ChartInteractionController's boundary-snap (Points[3] stays at the untouched default).
        double angle220Rad = 220.0 * Math.PI / 180.0;
        var draggedScreenPoint = new Point(200 + 100 * Math.Cos(angle220Rad), 200 + 100 * Math.Sin(angle220Rad));
        ellipse.Points[2] = transform.ScreenToChart(draggedScreenPoint);

        // Majority (filled) region must include the corner's own direction (due east, local angle 0 deg).
        Assert.True(ellipse.HitTest(new Point(280, 200), transform, tolerance: 3.0));

        // The small excluded sliver (between local angle 180 and 220 deg) must NOT be filled.
        double angle200Rad = 200.0 * Math.PI / 180.0;
        var sliverPoint = new Point(200 + 50 * Math.Cos(angle200Rad), 200 + 50 * Math.Sin(angle200Rad));
        Assert.False(ellipse.HitTest(sliverPoint, transform, tolerance: 3.0));
    }

    [Fact]
    public void HitTest_RotatedArcFamily_PointAlongRotatedMajorAxisHits_SameScreenOffsetAlongUnrotatedAxisMisses()
    {
        // Same rotation proof as HitTest_RotatedEllipse_* (Ellipse mode), but for the Arc-enabled
        // family: center (200,200), corner (300,300) => rotationAngle=45, rx=~141.4, ry=~70.7
        // (AspectRatio default 0.5). Coincident angle handles collapse to a 360 deg sweep so the
        // filled wedge (Radius Lines) covers the whole rotated ellipse, matching the plain-Ellipse
        // rotation test's geometry exactly.
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var corner = transform.ScreenToChart(new Point(300, 300));

        var ellipse = new EllipseObject(center, corner) { IsArcEnabled = true, ShowRadiusLines = true, IsFilled = true, IsCircular = false };
        ellipse.Points[2] = corner;
        ellipse.Points[3] = corner;

        // 100px along the rotated major axis (45 deg direction).
        double along45 = 100.0 * Math.Cos(Math.PI / 4.0);
        Assert.True(ellipse.HitTest(new Point(200 + along45, 200 + along45), transform, tolerance: 3.0));

        // 100px due east — would be "along the major axis" for an unrotated ellipse with the same rx,
        // but this ellipse's major axis actually points at 45 deg, so this point falls outside.
        Assert.False(ellipse.HitTest(new Point(300, 200), transform, tolerance: 3.0));
    }

    [Fact]
    public void Render_Selected_DrawsAngleHandleMarkersAtCurrentBoundary_NotAtStaleRawPosition()
    {
        // Regression test: dragging the corner (Points[1]) changes the rect/boundary every frame, but
        // Points[2]/[3]'s stored chart position stays wherever it was left. The handle *marker* must
        // track the current boundary (matching where the arc curve itself is drawn), not lag behind at
        // the stale raw position until the handle itself is next dragged. Points[2]/[3] are also
        // coincident here (both set to the same stale point), so their markers are nudged symmetrically
        // apart (see TryComputeArc) rather than landing exactly on the boundary position or each other.
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        // Corner due east (no rotation): rx=distance=200, ry=rx*AspectRatio(default 0.5)=100.
        var cornerAfterDrag = transform.ScreenToChart(new Point(400, 200));
        var staleAnglePoint = transform.ScreenToChart(new Point(500, 200)); // direction: 0 deg from center

        var ellipse = new EllipseObject(center, cornerAfterDrag) { IsArcEnabled = true, IsSelected = true, IsCircular = false };
        ellipse.Points[2] = staleAnglePoint;
        ellipse.Points[3] = staleAnglePoint;

        using var surface = SKSurface.Create(new SKImageInfo(600, 600));
        var canvas = surface.Canvas;
        ellipse.Render(canvas, transform);

        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);

        // Stale raw position must NOT show a handle marker.
        Assert.NotEqual(SKColors.Orange, bitmap.GetPixel(500, 200));

        // Boundary position for angle 0 deg on the rx=200/ry=100 ellipse would be (400, 200), but since
        // Points[2]/[3] are coincident, their markers are nudged +/-8 deg apart from it instead.
        // Circumference handles render in ControlPointColor (default Orange), not the shared handle color.
        Assert.Equal(SKColors.Orange, bitmap.GetPixel(398, 186));
        Assert.Equal(SKColors.Orange, bitmap.GetPixel(398, 214));
    }

    [Fact]
    public void GetSelectionHandleScreenPositions_ArcMode_MatchesRenderedBoundaryPosition_NotRawStalePoints()
    {
        // Regression test for the drag-grab bug: ChartInteractionController's handle hit-test must
        // query this same boundary-projected/rotation-corrected position (not the raw, possibly-stale
        // Points[2]/[3] chart coordinates) or clicking a visible handle silently misses and falls
        // through to translating the whole object instead. Same geometry as the Render regression test
        // above: corner due east (rx=200, ry=100, no rotation), stale angle point at 0 deg.
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var cornerAfterDrag = transform.ScreenToChart(new Point(400, 200));
        var staleAnglePoint = transform.ScreenToChart(new Point(500, 200));

        var ellipse = new EllipseObject(center, cornerAfterDrag) { IsArcEnabled = true, IsCircular = false };
        ellipse.Points[2] = staleAnglePoint;
        ellipse.Points[3] = staleAnglePoint;

        var handles = ellipse.GetSelectionHandleScreenPositions(transform);

        Assert.Equal(4, handles.Length);
        Assert.Equal(new Point(200, 200), handles[0]); // center
        Assert.Equal(new Point(400, 200), handles[1]); // corner
        // Angle handles must be near the rendered boundary position at the corner's direction (400,
        // 200), NOT at the raw stale stored point (500, 200). Points[2]/[3] are coincident here (both
        // set to the same stale point), so their markers are also nudged symmetrically apart from that
        // boundary position (see TryComputeArc) rather than landing exactly on top of it or each other.
        Assert.True(Math.Abs(handles[2].X - 500.0) > 10.0);
        Assert.True(Math.Abs(handles[3].X - 500.0) > 10.0);
        Assert.Equal(398.0, handles[2].X, 0);
        Assert.Equal(398.0, handles[3].X, 0);
        Assert.NotEqual(handles[2].Y, handles[3].Y);
    }

    [Fact]
    public void GetSelectionHandleScreenPositions_NonArcMode_StillReturnsCircumferenceHandles()
    {
        // The circumference angle handles must be shown/draggable even when Arc mode is off (IsArcEnabled
        // defaults to false here) — they simply have no effect on the rendered shape until Arc mode is
        // switched on.
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var corner = transform.ScreenToChart(new Point(300, 250));

        var ellipse = new EllipseObject(center, corner);

        var handles = ellipse.GetSelectionHandleScreenPositions(transform);

        Assert.Equal(4, handles.Length);
        Assert.Equal(new Point(200, 200), handles[0]);
        Assert.Equal(new Point(300, 250), handles[1]);
        Assert.True(double.IsFinite(handles[2].X) && double.IsFinite(handles[2].Y));
        Assert.True(double.IsFinite(handles[3].X) && double.IsFinite(handles[3].Y));
    }

    [Fact]
    public void GetSelectionHandleScreenPositions_LegacyTwoPointObject_FallsBackToCenterAndCornerOnly()
    {
        // Objects persisted before circumference control points always existed round-trip through
        // JSON with only 2 points (ChartObjectJsonConverter clears/repopulates Points exactly from
        // saved data, overriding the constructor's 4-point default). Simulate that here by truncating
        // back to 2 points; the handle query must degrade gracefully rather than throw.
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var corner = transform.ScreenToChart(new Point(300, 250));

        var ellipse = new EllipseObject(center, corner);
        ellipse.Points.RemoveRange(2, ellipse.Points.Count - 2);

        var handles = ellipse.GetSelectionHandleScreenPositions(transform);

        Assert.Equal(2, handles.Length);
        Assert.Equal(new Point(200, 200), handles[0]);
        Assert.Equal(new Point(300, 250), handles[1]);
    }

    [Theory]
    [InlineData(false, false, false)] // Ellipse (Arc disabled)
    [InlineData(true, false, false)]  // Arc only
    [InlineData(true, true, false)]   // Arc + Radius Lines
    [InlineData(true, false, true)]   // Arc + Chord Line
    [InlineData(true, true, true)]    // Arc + Radius Lines + Chord Line
    public void Render_AllLineToggleCombinations_DrawWithoutException(bool isArcEnabled, bool showRadiusLines, bool showChordLine)
    {
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var corner = transform.ScreenToChart(new Point(300, 300));

        using var surface = SKSurface.Create(new SKImageInfo(800, 600));
        var canvas = surface.Canvas;

        var ellipse = new EllipseObject(center, corner)
        {
            IsArcEnabled = isArcEnabled,
            ShowRadiusLines = showRadiusLines,
            ShowChordLine = showChordLine,
            IsFilled = true
        };
        ellipse.Points[2] = transform.ScreenToChart(new Point(300, 200));
        ellipse.Points[3] = transform.ScreenToChart(new Point(200, 300));

        ellipse.Render(canvas, transform);
    }

    // --- InnerRadiusRatio (ported from EllipseAnnulusObject) ---

    [Fact]
    public void InnerRadiusRatio_DefaultsToZero()
    {
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var corner = transform.ScreenToChart(new Point(300, 250));

        var ellipse = new EllipseObject(center, corner);

        Assert.Equal(0, ellipse.InnerRadiusRatio);
    }

    [Fact]
    public void InnerRadiusRatio_NonArcMode_CutsFullRing_ExcludingCenterButIncludingMidRingAndOuterBoundary()
    {
        // due east, Circular => rx=ry=100. InnerRadiusRatio=0.5 -> inner boundary at radius 50.
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var corner = transform.ScreenToChart(new Point(300, 200));

        var ellipse = new EllipseObject(center, corner) { IsCircular = true, IsFilled = true, InnerRadiusRatio = 0.5 };

        Assert.False(ellipse.HitTest(new Point(200, 200), transform, tolerance: 3.0)); // center: inside the hole
        Assert.False(ellipse.HitTest(new Point(220, 200), transform, tolerance: 3.0)); // radius 20: still inside the hole
        Assert.True(ellipse.HitTest(new Point(275, 200), transform, tolerance: 3.0));  // radius 75: within the ring
        Assert.True(ellipse.HitTest(new Point(300, 200), transform, tolerance: 3.0));  // radius 100: outer boundary
        Assert.False(ellipse.HitTest(new Point(350, 200), transform, tolerance: 3.0)); // radius 150: outside entirely
    }

    [Fact]
    public void InnerRadiusRatio_Zero_HitTestUnchangedFromPlainEllipse()
    {
        // Regression guard: InnerRadiusRatio == 0 (the default) must behave identically to a plain
        // solid ellipse -- no behavior change for any pre-existing EllipseObject.
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var corner = transform.ScreenToChart(new Point(300, 200));

        var withZeroRatio = new EllipseObject(center, corner) { IsCircular = true, IsFilled = true, InnerRadiusRatio = 0 };
        var withoutRatio = new EllipseObject(center, corner) { IsCircular = true, IsFilled = true };

        foreach (var p in new[] { new Point(200, 200), new Point(250, 200), new Point(300, 200), new Point(350, 200) })
        {
            Assert.Equal(withoutRatio.HitTest(p, transform, tolerance: 3.0), withZeroRatio.HitTest(p, transform, tolerance: 3.0));
        }
    }

    [Fact]
    public void InnerRadiusRatio_ArcMode_CutsAnnularSector_RespectingExistingSweep()
    {
        // due east + Circular => rx=ry=100. Arc sweep 0-90 deg (quarter circle), InnerRadiusRatio=0.5.
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var corner = transform.ScreenToChart(new Point(300, 200));
        var startAnglePoint = transform.ScreenToChart(new Point(300, 200)); // 0 deg
        var endAnglePoint = transform.ScreenToChart(new Point(200, 300));   // 90 deg

        var ellipse = new EllipseObject(center, corner) { IsArcEnabled = true, IsCircular = true, IsFilled = true, InnerRadiusRatio = 0.5 };
        ellipse.Points[2] = startAnglePoint;
        ellipse.Points[3] = endAnglePoint;

        // Within the 0-90 deg sweep, between the inner (50) and outer (100) boundary: hits.
        double angled75 = 75.0 * Math.Cos(Math.PI / 4.0);
        Assert.True(ellipse.HitTest(new Point(200 + angled75, 200 + angled75), transform, tolerance: 3.0));

        // Same sweep, but inside the inner hole (radius 20): misses.
        double angled20 = 20.0 * Math.Cos(Math.PI / 4.0);
        Assert.False(ellipse.HitTest(new Point(200 + angled20, 200 + angled20), transform, tolerance: 3.0));

        // Outside the sweep entirely (180 deg), even within the outer/inner radius band: misses.
        Assert.False(ellipse.HitTest(new Point(125, 200), transform, tolerance: 3.0));
    }

    [Theory]
    [InlineData(false, 0.5)]
    [InlineData(true, 0.5)]
    public void Render_InnerRadiusRatio_DrawsToCanvasWithoutException(bool isArcEnabled, double innerRadiusRatio)
    {
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var corner = transform.ScreenToChart(new Point(300, 250));

        using var surface = SKSurface.Create(new SKImageInfo(800, 600));
        var canvas = surface.Canvas;

        var ellipse = new EllipseObject(center, corner) { IsArcEnabled = isArcEnabled, InnerRadiusRatio = innerRadiusRatio, IsFilled = true };
        ellipse.Render(canvas, transform);
    }

    // --- ShowTangentLines (tangent line at each of Points[2]/[3] individually, distinct from ShowChordLine) ---

    [Fact]
    public void ShowTangentLines_DefaultsToFalse()
    {
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var corner = transform.ScreenToChart(new Point(300, 250));

        var ellipse = new EllipseObject(center, corner);

        Assert.False(ellipse.ShowTangentLines);
    }

    [Theory]
    [InlineData(false)] // plain ellipse/ring mode
    [InlineData(true)]  // Arc mode
    public void ShowTangentLines_VerticalTangentAtRightmostPointOfACircle_DrawsShortSegmentOnly(bool isArcEnabled)
    {
        // Base ShowTangentLines behavior (ExtendTangentLinesToChart off, the default): a short segment,
        // not extended to any edge. center (200,200), corner (300,200): a circle of radius 100.
        // Points[2] at angle 0 (the rightmost point, (300,200)) has a purely VERTICAL tangent direction.
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var corner = transform.ScreenToChart(new Point(300, 200));

        using var bitmap = new SKBitmap(800, 600);
        using var canvas = new SKCanvas(bitmap);
        bitmap.Erase(SKColors.Black);

        var ellipse = new EllipseObject(center, corner)
        {
            IsArcEnabled = isArcEnabled,
            IsFilled = false,
            Thickness = 4.0,
            Color = global::Avalonia.Media.Colors.Red,
            ShowTangentLines = true
        };
        ellipse.Points[2] = transform.ScreenToChart(new Point(300, 200)); // angle 0
        ellipse.Points[3] = transform.ScreenToChart(new Point(200, 300)); // angle 90

        ellipse.Render(canvas, transform);

        // 28px above/below the boundary point, within the short segment's half-length (0.3 *
        // (100+100)/2 = 30px). 28px is deliberately NOT too small: since the tangent line touches the
        // circle at (300,200), it stays extremely close to the circle's own boundary for small offsets
        // too (a tangent hugs its curve to first order) -- at exactly 28px the true circle boundary is
        // at radius sqrt(100^2+28^2)=~103.9, over 1.8px outside the circle's own stroke (radius 98-102
        // with Thickness=4), so this pixel can only be painted by the tangent line itself.
        Assert.True(bitmap.GetPixel(300, 172).Red > 200);
        Assert.True(bitmap.GetPixel(300, 228).Red > 200);

        // Well beyond the short segment's half-length (e.g. at the rect's own top/bottom edges, 100px
        // away) or at the chart's edges (0/800): must NOT be painted -- proving it stayed short rather
        // than reaching the ellipse's bounding box or the chart's own edges.
        Assert.True(bitmap.GetPixel(300, 100).Red < 50);
        Assert.True(bitmap.GetPixel(300, 10).Red < 50);
    }

    [Fact]
    public void ExtendTangentLinesToChart_DefaultsToFalse()
    {
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var corner = transform.ScreenToChart(new Point(300, 250));

        var ellipse = new EllipseObject(center, corner);

        Assert.False(ellipse.ExtendTangentLinesToChart);
    }

    [Theory]
    [InlineData(false)] // plain ellipse/ring mode
    [InlineData(true)]  // Arc mode
    public void ExtendTangentLinesToChart_HorizontalTangent_SpansTheEntireChartWidth(bool isArcEnabled)
    {
        // center (200,200), corner (300,200): a circle of radius 100, with no rotation (the corner sits
        // due east of the center). Points[3] at angle 90 (the bottommost point, (200,300)) has a
        // perfectly horizontal tangent direction, so with ExtendTangentLinesToChart on the line must run
        // along y=300 across the WHOLE 800px-wide chart (DummyCoordinateTransform.CanvasWidth=800), not
        // just the ellipse's own 200px-wide bounding box.
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var corner = transform.ScreenToChart(new Point(300, 200));

        using var bitmap = new SKBitmap(800, 600);
        using var canvas = new SKCanvas(bitmap);
        bitmap.Erase(SKColors.Black);

        var ellipse = new EllipseObject(center, corner)
        {
            IsArcEnabled = isArcEnabled,
            IsFilled = false,
            Thickness = 4.0,
            Color = global::Avalonia.Media.Colors.Red,
            ShowTangentLines = true,
            ExtendTangentLinesToChart = true
        };
        ellipse.Points[2] = transform.ScreenToChart(new Point(200, 100)); // angle 270 (top), out of the way
        ellipse.Points[3] = transform.ScreenToChart(new Point(200, 300)); // angle 90 (bottom)

        ellipse.Render(canvas, transform);

        // Near the chart's own left edge, the middle, and near its right edge -- all along y=300.
        Assert.True(bitmap.GetPixel(10, 300).Red > 200);
        Assert.True(bitmap.GetPixel(400, 300).Red > 200);
        Assert.True(bitmap.GetPixel(790, 300).Red > 200);
    }

    [Theory]
    [InlineData(false)] // plain ellipse/ring mode
    [InlineData(true)]  // Arc mode
    public void ExtendTangentLinesToChart_VerticalTangentAtCirclesOwnVertex_SpansTheEntireChartHeight(bool isArcEnabled)
    {
        // At angle 0 (the rightmost point of a circle), the tangent is exactly VERTICAL -- a
        // left-to-right chart extent is undefined there (the line never reaches a different X), so it
        // must instead span the chart's own top (y=0) to bottom (y=600) screen edges.
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var corner = transform.ScreenToChart(new Point(300, 200));

        using var bitmap = new SKBitmap(800, 600);
        using var canvas = new SKCanvas(bitmap);
        bitmap.Erase(SKColors.Black);

        var ellipse = new EllipseObject(center, corner)
        {
            IsArcEnabled = isArcEnabled,
            IsFilled = false,
            Thickness = 4.0,
            Color = global::Avalonia.Media.Colors.Red,
            ShowTangentLines = true,
            ExtendTangentLinesToChart = true
        };
        ellipse.Points[2] = transform.ScreenToChart(new Point(300, 200)); // angle 0 (right)
        ellipse.Points[3] = transform.ScreenToChart(new Point(200, 300)); // angle 90, out of the way

        ellipse.Render(canvas, transform);

        // Near the chart's own top edge, the middle, and near its bottom edge -- all at x=300.
        Assert.True(bitmap.GetPixel(300, 10).Red > 200);
        Assert.True(bitmap.GetPixel(300, 300).Red > 200);
        Assert.True(bitmap.GetPixel(300, 590).Red > 200);

        // The same offset along the HORIZONTAL direction, at the boundary's own height, must miss --
        // a horizontal or diagonal tangent (a wrong-direction bug) would paint here instead/also.
        Assert.True(bitmap.GetPixel(272, 200).Red < 50);
        Assert.True(bitmap.GetPixel(328, 200).Red < 50);
    }

    [Fact]
    public void ExtendTangentLinesToChart_True_ButShowTangentLinesFalse_DrawsNothing()
    {
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var corner = transform.ScreenToChart(new Point(300, 200));

        using var bitmap = new SKBitmap(800, 600);
        using var canvas = new SKCanvas(bitmap);
        bitmap.Erase(SKColors.Black);

        var ellipse = new EllipseObject(center, corner)
        {
            IsFilled = false,
            Thickness = 4.0,
            Color = global::Avalonia.Media.Colors.Red,
            ShowTangentLines = false,
            ExtendTangentLinesToChart = true
        };
        ellipse.Points[2] = transform.ScreenToChart(new Point(300, 200));
        ellipse.Points[3] = transform.ScreenToChart(new Point(200, 300));

        ellipse.Render(canvas, transform);

        Assert.True(bitmap.GetPixel(10, 200).Red < 50);
        Assert.True(bitmap.GetPixel(300, 10).Red < 50);
    }

    [Fact]
    public void ShowTangentLines_False_DoesNotDrawAnythingAtTheTangentPosition()
    {
        var transform = new DummyCoordinateTransform();
        var center = transform.ScreenToChart(new Point(200, 200));
        var corner = transform.ScreenToChart(new Point(300, 200));

        using var bitmap = new SKBitmap(800, 600);
        using var canvas = new SKCanvas(bitmap);
        bitmap.Erase(SKColors.Black);

        var ellipse = new EllipseObject(center, corner)
        {
            IsFilled = false,
            Thickness = 4.0,
            Color = global::Avalonia.Media.Colors.Red,
            ShowTangentLines = false
        };
        ellipse.Points[2] = transform.ScreenToChart(new Point(300, 200));
        ellipse.Points[3] = transform.ScreenToChart(new Point(200, 300));

        ellipse.Render(canvas, transform);

        // No tangent line drawn at all: the same points used in the "enabled" counterpart tests, which
        // sit outside the plain circle's own boundary stroke, must stay black.
        Assert.True(bitmap.GetPixel(300, 110).Red < 50);
        Assert.True(bitmap.GetPixel(105, 300).Red < 50);
    }

    // --- Category registration (EllipseArc entry retired; EllipseAnnulus retired from the menu too,
    // superseded by EllipseObject.InnerRadiusRatio -- see EllipseAnnulusObjectTests for the matching
    // backward-compatibility guard proving the class itself still deserializes/renders) ---

    [Fact]
    public void GetCategories_ShapesCategory_ContainsEllipseButNotRetiredEllipseArcOrEllipseAnnulus()
    {
        var categories = StockAnalyzer.Avalonia.Services.DrawingToolCategoryService.GetCategories();
        var shapesCategory = System.Linq.Enumerable.FirstOrDefault(categories, c => c.NameKey == "DrawCat_Shapes");

        Assert.NotNull(shapesCategory);
        Assert.Contains(shapesCategory.Tools, t => t.Tool == DrawingTool.Ellipse);
        Assert.DoesNotContain(shapesCategory.Tools, t => t.Tool == DrawingTool.EllipseAnnulus);
    }
}
