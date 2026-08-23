using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.ViewModels;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public class DrawingObjectLayerTests
{
    private class MockChartObject : IChartObject
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public ChartObjectType Type { get; set; } = ChartObjectType.TrendLine;
        public List<ChartPoint> Points { get; set; } = new List<ChartPoint>();
        public Color Color { get; set; } = Colors.Black;
        public double Thickness { get; set; } = 1.0;
        public bool IsSelected { get; set; }
        public bool IsVisible { get; set; } = true;
        public bool IsLocked { get; set; } = false;
        public int ZIndex { get; set; } = 0;

        public SKColor SkiaColor => SKColors.Black;

        public bool RenderCalled { get; private set; }
        public int RenderOrder { get; set; }
        private static int _globalRenderCount = 0;

        public void Render(SKCanvas canvas, ICoordinateTransform transform)
        {
            RenderCalled = true;
            RenderOrder = ++_globalRenderCount;
        }

        public bool HitTest(Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance) => true;

        public void Translate(TimeSpan timeDelta, decimal priceDelta) { }

        public static void ResetGlobalCount() => _globalRenderCount = 0;
    }

    [Fact]
    public void Invariant_I01_ZIndexPhysicalEquivalence()
    {
        var manager = new ChartObjectManager();
        var o1 = new MockChartObject();
        var o2 = new MockChartObject();
        var o3 = new MockChartObject();

        manager.AddObject(o1);
        manager.AddObject(o2);
        manager.AddObject(o3);

        Assert.Equal(0, o1.ZIndex);
        Assert.Equal(1, o2.ZIndex);
        Assert.Equal(2, o3.ZIndex);

        // Swap o1 and o2
        manager.BringForward(o1.Id);
        Assert.Equal(0, o2.ZIndex);
        Assert.Equal(1, o1.ZIndex);
        Assert.Equal(2, o3.ZIndex);

        Assert.Same(manager.Objects[0], o2);
        Assert.Same(manager.Objects[1], o1);
        Assert.Same(manager.Objects[2], o3);
    }

    [Fact]
    public void ChartObjectManager_ShouldFilterInvisibleObjects()
    {
        var manager = new ChartObjectManager();
        var visibleObj = new MockChartObject { IsVisible = true };
        var invisibleObj = new MockChartObject { IsVisible = false };
        manager.AddObject(visibleObj);
        manager.AddObject(invisibleObj);

        manager.Render(null!, null!);

        Assert.True(visibleObj.RenderCalled);
        Assert.False(invisibleObj.RenderCalled);
    }

    [Fact]
    public void ChartObjectManager_ShouldRenderInZOrder()
    {
        MockChartObject.ResetGlobalCount();
        var manager = new ChartObjectManager();
        var bottomObj = new MockChartObject();
        var middleObj = new MockChartObject();
        var topObj = new MockChartObject();

        manager.AddObject(bottomObj);
        manager.AddObject(middleObj);
        manager.AddObject(topObj);

        manager.Render(null!, null!);

        Assert.True(bottomObj.RenderOrder < middleObj.RenderOrder);
        Assert.True(middleObj.RenderOrder < topObj.RenderOrder);
    }

    [Fact]
    public void ChartObjectManager_GetObjectAt_ShouldRespectVisibility_AndAllowLocked()
    {
        var manager = new ChartObjectManager();
        var invisibleObj = new MockChartObject { IsVisible = false };
        var lockedObj = new MockChartObject { IsLocked = true };

        manager.AddObject(invisibleObj);
        manager.AddObject(lockedObj);

        var result = manager.GetObjectAt(new Point(0, 0), null!);
        Assert.NotNull(result);
        Assert.Equal(lockedObj.Id, result.Id);
    }

    [Fact]
    public void ChartObjectManager_GetObjectAt_ShouldReturnTopMostObject()
    {
        var manager = new ChartObjectManager();
        var bottomObj = new MockChartObject();
        var topObj = new MockChartObject();

        manager.AddObject(bottomObj);
        manager.AddObject(topObj);

        var result = manager.GetObjectAt(new Point(0, 0), null!);
        Assert.Equal(topObj.Id, result?.Id);
    }

    [Fact]
    public void SelectionAndLockIntegrity_Invariant_I03()
    {
        var manager = new ChartObjectManager();
        var obj = new MockChartObject();
        manager.AddObject(obj);

        // Select successfully
        bool selected = manager.SelectObject(obj.Id);
        Assert.True(selected);
        Assert.True(obj.IsSelected);
        Assert.True(manager.HasSelection);

        // Locking retains selection state so user can inspect locked object
        manager.ToggleLock(obj.Id);
        Assert.True(obj.IsLocked);
        Assert.True(obj.IsSelected);
        Assert.True(manager.HasSelection);

        // Can select locked object
        Assert.True(manager.SelectObject(obj.Id));

        // Unlock, select, then hide
        manager.ToggleLock(obj.Id);
        Assert.True(manager.SelectObject(obj.Id));
        manager.ToggleVisibility(obj.Id);
        Assert.False(obj.IsVisible);
        Assert.False(obj.IsSelected);
        Assert.False(manager.HasSelection);

        // Cannot select hidden object
        Assert.False(manager.SelectObject(obj.Id));
    }

    [Fact]
    public void Reordering_Operations_And_BoundaryGuards()
    {
        var manager = new ChartObjectManager();
        var o1 = new MockChartObject();
        var o2 = new MockChartObject();
        var o3 = new MockChartObject();

        manager.AddObject(o1);
        manager.AddObject(o2);
        manager.AddObject(o3);

        // Boundary guard: top element cannot bring forward
        Assert.False(manager.BringForward(o3.Id));
        Assert.False(manager.BringToFront(o3.Id));

        // Boundary guard: bottom element cannot send backward
        Assert.False(manager.SendBackward(o1.Id));
        Assert.False(manager.SendToBack(o1.Id));

        // BringToFront
        Assert.True(manager.BringToFront(o1.Id));
        Assert.Same(manager.Objects[0], o2);
        Assert.Same(manager.Objects[1], o3);
        Assert.Same(manager.Objects[2], o1);

        // SendToBack
        Assert.True(manager.SendToBack(o1.Id));
        Assert.Same(manager.Objects[0], o1);
        Assert.Same(manager.Objects[1], o2);
        Assert.Same(manager.Objects[2], o3);
    }

    [Fact]
    public void LockProtection_RemoveObject_And_DeleteAll()
    {
        var manager = new ChartObjectManager();
        var unlocked1 = new MockChartObject();
        var locked = new MockChartObject();
        var unlocked2 = new MockChartObject();

        manager.AddObject(unlocked1);
        manager.AddObject(locked);
        manager.AddObject(unlocked2);

        manager.ToggleLock(locked.Id);
        Assert.True(locked.IsLocked);

        // RemoveObject on locked object fails
        Assert.False(manager.RemoveObject(locked.Id));
        Assert.Equal(3, manager.Count);

        // DeleteAll removes only unlocked objects
        manager.DeleteAll();
        Assert.Equal(1, manager.Count);
        Assert.Same(locked, manager.Objects[0]);
        Assert.Equal(0, locked.ZIndex);
    }

    [Fact]
    public void BatchMetaOperations_WorkCorrectly()
    {
        var manager = new ChartObjectManager();
        var o1 = new MockChartObject();
        var o2 = new MockChartObject();
        manager.AddObject(o1);
        manager.AddObject(o2);

        manager.LockAll();
        Assert.True(o1.IsLocked && o2.IsLocked);

        manager.UnlockAll();
        Assert.False(o1.IsLocked || o2.IsLocked);

        manager.HideAll();
        Assert.False(o1.IsVisible || o2.IsVisible);

        manager.ShowAll();
        Assert.True(o1.IsVisible && o2.IsVisible);
    }

    [Fact]
    public void BatchScope_CoalescesChangedEvents()
    {
        var manager = new ChartObjectManager();
        int changedCount = 0;
        manager.Changed += () => changedCount++;

        using (manager.BeginBatch())
        {
            manager.AddObject(new MockChartObject());
            manager.AddObject(new MockChartObject());
            manager.AddObject(new MockChartObject());
        }

        Assert.Equal(1, changedCount);
    }

    private class DimChartObject : IChartObject
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public ChartObjectType Type { get; set; } = ChartObjectType.Rectangle;
        public List<ChartPoint> Points { get; set; } = new List<ChartPoint>();
        public Color Color { get; set; } = Colors.Red;
        public double Thickness { get; set; } = 1.0;
        public bool IsSelected { get; set; }
        public bool IsVisible { get; set; } = true;
        public bool IsLocked { get; set; } = false;
        public int ZIndex { get; set; } = 0;
        public SKColor SkiaColor => SKColors.Red;
        public void Render(SKCanvas canvas, ICoordinateTransform transform) { }
        public bool HitTest(Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance) => true;
        public void Translate(TimeSpan timeDelta, decimal priceDelta) { }
    }

    [Fact]
    public void DimObjects_LayerManager_CanBringForward_CanSendBackward_Accurate()
    {
        var manager = new ChartObjectManager();
        var o1 = new DimChartObject();
        var o2 = new DimChartObject();
        var o3 = new DimChartObject();

        manager.AddObject(o1);
        manager.AddObject(o2);
        manager.AddObject(o3);

        // o1 is at bottom (index 0), o2 is middle (index 1), o3 is top (index 2)
        Assert.True(manager.CanBringForward(o1.Id));
        Assert.False(manager.CanSendBackward(o1.Id));

        Assert.True(manager.CanBringForward(o2.Id));
        Assert.True(manager.CanSendBackward(o2.Id));

        Assert.False(manager.CanBringForward(o3.Id));
        Assert.True(manager.CanSendBackward(o3.Id));

        // Toggle Lock
        Assert.False(manager.IsLocked(o1.Id));
        manager.ToggleLock(o1.Id);
        Assert.True(manager.IsLocked(o1.Id));
        Assert.False(manager.RemoveObject(o1.Id)); // cannot remove locked object

        // Toggle Visibility
        Assert.True(manager.IsVisible(o2.Id));
        manager.ToggleVisibility(o2.Id);
        Assert.False(manager.IsVisible(o2.Id));
    }

    [Fact]
    public void DrawingObjectsViewModel_WithDimObjects_UpdatesCanExecuteAccurately()
    {
        var dispatcher = new SynchronousDispatcherService();
        var manager = new ChartObjectManager();
        var o1 = new DimChartObject();
        var o2 = new DimChartObject();
        var o3 = new DimChartObject();

        manager.AddObject(o1);
        manager.AddObject(o2);
        manager.AddObject(o3);

        using var vm = new DrawingObjectsViewModel(manager, dispatcher);

        // In UI: Items[0] is top (o3), Items[1] is middle (o2), Items[2] is bottom (o1)
        Assert.Equal(o3.Id, vm.Items[0].Id);
        Assert.Equal(o2.Id, vm.Items[1].Id);
        Assert.Equal(o1.Id, vm.Items[2].Id);

        // Top item (o3) cannot move up (forward in 3D), can move down (backward in 3D)
        Assert.False(vm.Items[0].CanBringForward);
        Assert.True(vm.Items[0].CanSendBackward);

        // Middle item (o2) can move up and down
        Assert.True(vm.Items[1].CanBringForward);
        Assert.True(vm.Items[1].CanSendBackward);

        // Bottom item (o1) can move up, cannot move down
        Assert.True(vm.Items[2].CanBringForward);
        Assert.False(vm.Items[2].CanSendBackward);

        // Move middle item (o2) down (SendBackward in 3D)
        vm.Items[1].SendBackwardCommand.Execute(null);

        // Now o2 is at bottom, o1 is middle, o3 is top
        Assert.Equal(o3.Id, vm.Items[0].Id);
        Assert.Equal(o1.Id, vm.Items[1].Id);
        Assert.Equal(o2.Id, vm.Items[2].Id);

        // o2 is now bottom
        Assert.True(vm.Items[2].CanBringForward);
        Assert.False(vm.Items[2].CanSendBackward);

        // o1 is now middle
        Assert.True(vm.Items[1].CanBringForward);
        Assert.True(vm.Items[1].CanSendBackward);
    }

    [Fact]
    public void ConcreteDrawingObjects_LockProtection_PreventsRemoveObject_AndHitTest()
    {
        var manager = new ChartObjectManager();
        var hLine = new HorizontalLineObject(new ChartPoint(DateTime.UtcNow, 150m));
        var text = new TextObject(new ChartPoint(DateTime.UtcNow, 200m));
        var callout = new CalloutObject(new ChartPoint(DateTime.UtcNow, 250m), new ChartPoint(DateTime.UtcNow.AddDays(1), 260m));
        var triangle = new TriangleObject(
            new ChartPoint(DateTime.UtcNow, 100m),
            new ChartPoint(DateTime.UtcNow.AddDays(1), 120m),
            new ChartPoint(DateTime.UtcNow.AddDays(2), 110m));
        var trend = new TrendLineObject(
            new ChartPoint(DateTime.UtcNow, 100m),
            new ChartPoint(DateTime.UtcNow.AddDays(5), 200m));

        manager.AddObject(hLine);
        manager.AddObject(text);
        manager.AddObject(callout);
        manager.AddObject(triangle);
        manager.AddObject(trend);

        Assert.Equal(5, manager.Count);

        // Lock all
        manager.ToggleLock(hLine.Id);
        manager.ToggleLock(text.Id);
        manager.ToggleLock(callout.Id);
        manager.ToggleLock(triangle.Id);
        manager.ToggleLock(trend.Id);

        Assert.True(manager.IsLocked(hLine.Id));
        Assert.True(manager.IsLocked(text.Id));
        Assert.True(manager.IsLocked(callout.Id));
        Assert.True(manager.IsLocked(triangle.Id));
        Assert.True(manager.IsLocked(trend.Id));

        // Direct removal must fail for all locked objects
        Assert.False(manager.RemoveObject(hLine.Id));
        Assert.False(manager.RemoveObject(text.Id));
        Assert.False(manager.RemoveObject(callout.Id));
        Assert.False(manager.RemoveObject(triangle.Id));
        Assert.False(manager.RemoveObject(trend.Id));
        Assert.Equal(5, manager.Count);

        // Locked objects can still be selected
        Assert.True(manager.SelectObject(hLine.Id));
        Assert.Equal(hLine.Id, manager.SelectedObject?.Id);
    }

    [Fact]
    public void ConcreteDrawingObjects_LockState_PersistedAcrossSnapshotRestore()
    {
        var manager1 = new ChartObjectManager();
        var hLine = new HorizontalLineObject(new ChartPoint(DateTime.UtcNow, 150m));
        var text = new TextObject(new ChartPoint(DateTime.UtcNow, 200m));
        var triangle = new TriangleObject(
            new ChartPoint(DateTime.UtcNow, 100m),
            new ChartPoint(DateTime.UtcNow.AddDays(1), 120m),
            new ChartPoint(DateTime.UtcNow.AddDays(2), 110m));
        var trend = new TrendLineObject(
            new ChartPoint(DateTime.UtcNow, 100m),
            new ChartPoint(DateTime.UtcNow.AddDays(5), 200m));

        manager1.AddObject(hLine);
        manager1.AddObject(text);
        manager1.AddObject(triangle);
        manager1.AddObject(trend);

        manager1.ToggleLock(hLine.Id);
        manager1.ToggleLock(text.Id);
        manager1.ToggleLock(triangle.Id);
        manager1.ToggleLock(trend.Id);

        var snapshot = manager1.GetSnapshot();

        var manager2 = new ChartObjectManager();
        manager2.LoadSnapshot(snapshot);

        Assert.Equal(4, manager2.Count);
        Assert.True(manager2.IsLocked(hLine.Id));
        Assert.True(manager2.IsLocked(text.Id));
        Assert.True(manager2.IsLocked(triangle.Id));
        Assert.True(manager2.IsLocked(trend.Id));

        Assert.False(manager2.RemoveObject(hLine.Id));
        Assert.False(manager2.RemoveObject(text.Id));
        Assert.False(manager2.RemoveObject(triangle.Id));
        Assert.False(manager2.RemoveObject(trend.Id));
        Assert.Equal(4, manager2.Count);
    }

    [Fact]
    public void ConcreteDrawingObjects_LockState_PreservedAcrossContextSwitch()
    {
        var manager = new ChartObjectManager();
        var hLine = new HorizontalLineObject(new ChartPoint(DateTime.UtcNow, 150m));
        var trend = new TrendLineObject(
            new ChartPoint(DateTime.UtcNow, 100m),
            new ChartPoint(DateTime.UtcNow.AddDays(5), 200m));

        manager.AddObject(hLine);
        manager.AddObject(trend);

        manager.ToggleLock(hLine.Id);
        manager.ToggleLock(trend.Id);

        Assert.True(manager.IsLocked(hLine.Id));
        Assert.True(manager.IsLocked(trend.Id));

        // Switch to Linear context
        manager.SwitchContext(ChartDrawingContextType.Linear);
        Assert.Equal(0, manager.Count);

        // Switch back to Standard context
        manager.SwitchContext(ChartDrawingContextType.Standard);
        Assert.Equal(2, manager.Count);
        Assert.True(manager.IsLocked(hLine.Id));
        Assert.True(manager.IsLocked(trend.Id));
        Assert.False(manager.RemoveObject(hLine.Id));
        Assert.False(manager.RemoveObject(trend.Id));
    }
}
