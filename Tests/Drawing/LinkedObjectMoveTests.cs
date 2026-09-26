using System;
using System.Collections.Generic;
using Avalonia.Input;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Models;
using Xunit;
using Point = global::Avalonia.Point;

namespace StockAnalyzer.Tests.Drawing;

/// <summary>
/// Whole-object (Move) drags of a linked object must translate every group member by the identical
/// (time, price) displacement; unlinked objects and handle drags are unaffected.
/// </summary>
public class LinkedObjectMoveTests
{
    private static readonly DateTime Start = new(2025, 1, 1);

    private static List<CoreCandleData> BuildCandles()
    {
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < 40; i++)
        {
            decimal close = 100m + i * 5m;
            candles.Add(new CoreCandleData(Start.AddDays(i), close, close + 5, close - 5, close, 1000));
        }
        return candles;
    }

    private static LinearCoordinateTransform NewTransform()
        => new(Start, Start.AddDays(40), 0m, 400m, 1000, 600);

    private static TrendLineObject Line(int day, decimal price)
        => new(new ChartPoint(Start.AddDays(day), price), new ChartPoint(Start.AddDays(day + 6), price));

    private sealed class Harness
    {
        public readonly ChartInteractionController Controller = new(new MagnetSnapService(), new DialogService(), new SmartGuideService());
        public readonly ChartViewModel ViewModel;
        public readonly LinearCoordinateTransform Transform = NewTransform();
        public readonly List<CoreCandleData> Candles = BuildCandles();

        public Harness()
        {
            ViewModel = new ChartViewModel { CurrentTool = DrawingTool.Pointer, Candles = Candles };
            Controller.SetActiveObjectManager(ViewModel.ObjectManager);
        }

        public ChartObjectManager Manager => ViewModel.ObjectManager;

        /// <summary>Presses on the middle of <paramref name="obj"/> and drags it by the given screen offset, then releases.</summary>
        public void Drag(TrendLineObject obj, double dx, double dy)
        {
            var mid = Transform.ChartToScreen(new ChartPoint(
                obj.Points[0].Time.AddTicks((obj.Points[1].Time - obj.Points[0].Time).Ticks / 2), obj.Points[0].Price));
            Assert.True(Controller.HandlePointerPressed(mid, mid, ViewModel, Transform, KeyModifiers.Alt, 1));
            Assert.Same(obj, Controller.DraggedObject);

            var snapshot = new ChartDataSnapshot(Candles, startIndex: 0, count: Candles.Count);
            Assert.True(Controller.HandleObjectDrag(
                new Point(mid.X + dx, mid.Y + dy), snapshot, Transform, 0, 0,
                new global::Avalonia.Rect(0, 0, 1000, 600), KeyModifiers.Alt, allCandles: Candles));
            Controller.HandlePointerReleased(ViewModel);
        }
    }

    private static (TimeSpan time, decimal price) Delta(ChartPoint before, ChartPoint after)
        => (after.Time - before.Time, after.Price - before.Price);

    [Fact]
    public void DraggingParent_MovesEveryChildByTheIdenticalDisplacement()
    {
        var h = new Harness();
        var parent = Line(2, 100m);
        var child1 = Line(8, 200m);
        var child2 = Line(14, 300m);
        var outsider = Line(20, 50m);
        foreach (var o in new[] { parent, child1, child2, outsider }) h.Manager.AddObject(o);
        h.Manager.LinkObjects(parent.Id, new[] { child1.Id, child2.Id });

        var parentBefore = parent.Points[0];
        var child1Before = child1.Points[0];
        var child2Before = child2.Points[1];
        var outsiderBefore = outsider.Points[0];

        h.Drag(parent, 60, -40);

        var parentDelta = Delta(parentBefore, parent.Points[0]);
        Assert.NotEqual(TimeSpan.Zero, parentDelta.time);
        Assert.NotEqual(0m, parentDelta.price);
        Assert.Equal(parentDelta, Delta(child1Before, child1.Points[0]));
        Assert.Equal(parentDelta, Delta(child2Before, child2.Points[1]));
        Assert.Equal(outsiderBefore, outsider.Points[0]);
    }

    [Fact]
    public void DraggingAChild_MovesTheParentAndSiblingsToo()
    {
        var h = new Harness();
        var parent = Line(2, 100m);
        var child1 = Line(8, 200m);
        var child2 = Line(14, 300m);
        foreach (var o in new[] { parent, child1, child2 }) h.Manager.AddObject(o);
        h.Manager.LinkObjects(parent.Id, new[] { child1.Id, child2.Id });

        var parentBefore = parent.Points[0];
        var child1Before = child1.Points[0];
        var child2Before = child2.Points[0];

        h.Drag(child2, -50, 30);

        var delta = Delta(child2Before, child2.Points[0]);
        Assert.NotEqual(0m, delta.price);
        Assert.Equal(delta, Delta(parentBefore, parent.Points[0]));
        Assert.Equal(delta, Delta(child1Before, child1.Points[0]));
    }

    [Fact]
    public void UnlinkedObjects_DragAloneAsBefore()
    {
        var h = new Harness();
        var a = Line(2, 100m);
        var b = Line(8, 200m);
        h.Manager.AddObject(a);
        h.Manager.AddObject(b);
        var bBefore = b.Points[0];

        h.Drag(a, 40, 20);

        Assert.Equal(bBefore, b.Points[0]);
    }

    [Fact]
    public void AfterUnlink_TheFormerMemberNoLongerFollows()
    {
        var h = new Harness();
        var a = Line(2, 100m);
        var b = Line(8, 200m);
        h.Manager.AddObject(a);
        h.Manager.AddObject(b);
        h.Manager.LinkObjects(a.Id, new[] { b.Id });
        h.Manager.UnlinkObjects(new[] { a.Id });
        var bBefore = b.Points[0];

        h.Drag(a, 40, 20);

        Assert.Equal(bBefore, b.Points[0]);
    }

    [Fact]
    public void AxisMode_OfTheDraggedObjectGovernsTheWholeGroup()
    {
        var h = new Harness();
        var parent = Line(2, 100m);
        var child = Line(8, 200m);
        h.Manager.AddObject(parent);
        h.Manager.AddObject(child);
        h.Manager.LinkObjects(parent.Id, new[] { child.Id });

        // Dragged object is X-only; the child's own Y-only mode must be ignored (group moves rigidly).
        h.Manager.SetMoveAxisMode(parent.Id, DrawingMoveAxisMode.X);
        h.Manager.SetMoveAxisMode(child.Id, DrawingMoveAxisMode.Y);
        var parentBefore = parent.Points[0];
        var childBefore = child.Points[0];

        h.Drag(parent, 60, -40);

        var parentDelta = Delta(parentBefore, parent.Points[0]);
        Assert.NotEqual(TimeSpan.Zero, parentDelta.time);
        Assert.Equal(0m, parentDelta.price);
        Assert.Equal(parentDelta, Delta(childBefore, child.Points[0]));
    }

    [Fact]
    public void WithSmartGuidesEnabled_TheGroupStillMovesRigidly()
    {
        var h = new Harness();
        var parent = Line(2, 100m);
        var child = Line(8, 100m); // same price: would attract snapping if it were treated as a static snap target
        h.Manager.AddObject(parent);
        h.Manager.AddObject(child);
        h.Manager.LinkObjects(parent.Id, new[] { child.Id });
        var parentBefore = parent.Points[0];
        var childBefore = child.Points[0];

        var mid = h.Transform.ChartToScreen(new ChartPoint(Start.AddDays(5), 100m));
        Assert.True(h.Controller.HandlePointerPressed(mid, mid, h.ViewModel, h.Transform, KeyModifiers.None, 1));
        var snapshot = new ChartDataSnapshot(h.Candles, startIndex: 0, count: h.Candles.Count);
        Assert.True(h.Controller.HandleObjectDrag(
            new Point(mid.X + 25, mid.Y - 25), snapshot, h.Transform, 0, 0,
            new global::Avalonia.Rect(0, 0, 1000, 600), KeyModifiers.None, allCandles: h.Candles));
        h.Controller.HandlePointerReleased(h.ViewModel);

        Assert.Equal(Delta(parentBefore, parent.Points[0]), Delta(childBefore, child.Points[0]));
    }

    [Fact]
    public void MultipleDragFrames_ApplyTheTotalDisplacementOnce_NotAccumulated()
    {
        var h = new Harness();
        var parent = Line(2, 100m);
        var child = Line(8, 200m);
        h.Manager.AddObject(parent);
        h.Manager.AddObject(child);
        h.Manager.LinkObjects(parent.Id, new[] { child.Id });
        var parentBefore = parent.Points[0];
        var childBefore = child.Points[0];

        var mid = h.Transform.ChartToScreen(new ChartPoint(Start.AddDays(5), 100m));
        Assert.True(h.Controller.HandlePointerPressed(mid, mid, h.ViewModel, h.Transform, KeyModifiers.Alt, 1));
        var snapshot = new ChartDataSnapshot(h.Candles, startIndex: 0, count: h.Candles.Count);
        foreach (var step in new[] { 10.0, 20.0, 35.0, 50.0 })
        {
            Assert.True(h.Controller.HandleObjectDrag(
                new Point(mid.X + step, mid.Y - step), snapshot, h.Transform, 0, 0,
                new global::Avalonia.Rect(0, 0, 1000, 600), KeyModifiers.Alt, allCandles: h.Candles));
        }
        h.Controller.HandlePointerReleased(h.ViewModel);

        Assert.Equal(Delta(parentBefore, parent.Points[0]), Delta(childBefore, child.Points[0]));
    }

    [Fact]
    public void BarLockedDraggedObject_GivesMembersNoPriceDisplacement_AndMovesByWholeBars()
    {
        var h = new Harness();
        var locked = new FixedRangeVolumeProfileObject(new ChartPoint(Start.AddDays(8), 100m), new ChartPoint(Start.AddDays(20), 200m))
        {
            LockRange = true
        };
        var child = Line(4, 300m);
        h.Manager.AddObject(locked);
        h.Manager.AddObject(child);
        h.Manager.LinkObjects(locked.Id, new[] { child.Id });
        var lockedBefore = locked.Points[0];
        var childBefore = child.Points[0];

        var center = h.Transform.ChartToScreen(new ChartPoint(Start.AddDays(14), 150m));
        Assert.True(h.Controller.HandlePointerPressed(center, center, h.ViewModel, h.Transform, KeyModifiers.Alt, 1));
        Assert.Same(locked, h.Controller.DraggedObject);
        var snapshot = new ChartDataSnapshot(h.Candles, startIndex: 0, count: h.Candles.Count);
        Assert.True(h.Controller.HandleObjectDrag(
            new Point(center.X + 60, center.Y - 40), snapshot, h.Transform, 0, 0,
            new global::Avalonia.Rect(0, 0, 1000, 600), KeyModifiers.Alt, allCandles: h.Candles));
        h.Controller.HandlePointerReleased(h.ViewModel);

        var lockedDelta = Delta(lockedBefore, locked.Points[0]);
        Assert.NotEqual(TimeSpan.Zero, lockedDelta.time);
        Assert.Equal(0L, lockedDelta.time.Ticks % TimeSpan.TicksPerDay); // whole bars (daily candles)
        Assert.Equal(0m, lockedDelta.price);
        var childDelta = Delta(childBefore, child.Points[0]);
        Assert.NotEqual(TimeSpan.Zero, childDelta.time);
        Assert.Equal(0m, childDelta.price); // no price displacement for members of a bar-locked object
    }

    [Fact]
    public void WhileDragging_MembersAreNotSmartGuideSnapTargets()
    {
        var h = new Harness();
        var parent = Line(2, 100m);
        var child = Line(8, 100m); // same price: a static target here would pull the parent back onto its line
        h.Manager.AddObject(parent);
        h.Manager.AddObject(child);
        h.Manager.LinkObjects(parent.Id, new[] { child.Id });
        var parentBefore = parent.Points[0];

        var mid = h.Transform.ChartToScreen(new ChartPoint(Start.AddDays(5), 100m));
        Assert.True(h.Controller.HandlePointerPressed(mid, mid, h.ViewModel, h.Transform, KeyModifiers.None, 1));
        var snapshot = new ChartDataSnapshot(h.Candles, startIndex: 0, count: h.Candles.Count);
        Assert.True(h.Controller.HandleObjectDrag(
            new Point(mid.X, mid.Y - 2), snapshot, h.Transform, 0, 0,
            new global::Avalonia.Rect(0, 0, 1000, 600), KeyModifiers.None, allCandles: h.Candles));

        Assert.NotEqual(0m, parent.Points[0].Price - parentBefore.Price); // not snapped back to the member's line
        h.Controller.HandlePointerReleased(h.ViewModel);
    }
}
