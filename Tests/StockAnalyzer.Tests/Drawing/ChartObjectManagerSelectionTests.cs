using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Avalonia.Drawing;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

public class ChartObjectManagerSelectionTests
{
    private class TestDrawingObject : RelativeGeometricRenderer
    {
        public override ChartObjectType Type => ChartObjectType.TrendLine;

        public TestDrawingObject(DateTime time, decimal price)
        {
            Points.Add(new ChartPoint(time, price));
        }

        protected override void DrawGeometry(SkiaSharp.SKCanvas canvas, ICoordinateTransform transform) { }

        public override bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = 5)
        {
            return true;
        }
    }

    [Fact]
    public void SelectObjects_MultiSelectsCorrectObjects()
    {
        var manager = new ChartObjectManager();
        var obj1 = new TestDrawingObject(DateTime.UtcNow, 100m);
        var obj2 = new TestDrawingObject(DateTime.UtcNow, 110m);
        var obj3 = new TestDrawingObject(DateTime.UtcNow, 120m);

        manager.AddObject(obj1);
        manager.AddObject(obj2);
        manager.AddObject(obj3);

        Assert.False(manager.HasSelection);

        // Select obj1 and obj2
        bool result = manager.SelectObjects(new[] { obj1.Id, obj2.Id });

        Assert.True(result);
        Assert.True(manager.HasSelection);
        Assert.Equal(2, manager.SelectedIds.Count);
        Assert.Contains(obj1.Id, manager.SelectedIds);
        Assert.Contains(obj2.Id, manager.SelectedIds);
        Assert.DoesNotContain(obj3.Id, manager.SelectedIds);

        Assert.True(obj1.IsSelected);
        Assert.True(obj2.IsSelected);
        Assert.False(obj3.IsSelected);

        var selectedList = manager.SelectedObjects;
        Assert.Equal(2, selectedList.Count);
    }

    [Fact]
    public void ToggleSelectObject_TogglesSelectionStateAccurately()
    {
        var manager = new ChartObjectManager();
        var obj1 = new TestDrawingObject(DateTime.UtcNow, 100m);
        var obj2 = new TestDrawingObject(DateTime.UtcNow, 110m);

        manager.AddObject(obj1);
        manager.AddObject(obj2);

        manager.SelectObject(obj1.Id);
        Assert.Single(manager.SelectedIds);
        Assert.True(obj1.IsSelected);

        // Toggle obj2: now both obj1 and obj2 are selected
        manager.ToggleSelectObject(obj2.Id);
        Assert.Equal(2, manager.SelectedIds.Count);
        Assert.True(obj1.IsSelected);
        Assert.True(obj2.IsSelected);

        // Toggle obj1: obj1 removed, only obj2 selected
        manager.ToggleSelectObject(obj1.Id);
        Assert.Single(manager.SelectedIds);
        Assert.False(obj1.IsSelected);
        Assert.True(obj2.IsSelected);
    }

    [Fact]
    public void SelectObjects_ClearExistingFalse_AppendsSelection()
    {
        var manager = new ChartObjectManager();
        var obj1 = new TestDrawingObject(DateTime.UtcNow, 100m);
        var obj2 = new TestDrawingObject(DateTime.UtcNow, 110m);
        var obj3 = new TestDrawingObject(DateTime.UtcNow, 120m);

        manager.AddObject(obj1);
        manager.AddObject(obj2);
        manager.AddObject(obj3);

        manager.SelectObject(obj1.Id);
        Assert.Single(manager.SelectedIds);

        // Append obj2 without clearing existing
        manager.SelectObjects(new[] { obj2.Id }, clearExisting: false);
        Assert.Equal(2, manager.SelectedIds.Count);
        Assert.Contains(obj1.Id, manager.SelectedIds);
        Assert.Contains(obj2.Id, manager.SelectedIds);
    }

    [Fact]
    public void InvisibleObjects_CannotBeSelected()
    {
        var manager = new ChartObjectManager();
        var obj1 = new TestDrawingObject(DateTime.UtcNow, 100m);
        manager.AddObject(obj1);
        manager.ToggleVisibility(obj1.Id);

        Assert.False(manager.IsVisible(obj1.Id));

        bool result = manager.SelectObjects(new[] { obj1.Id });
        Assert.False(manager.HasSelection);
        Assert.False(obj1.IsSelected);
    }
}
