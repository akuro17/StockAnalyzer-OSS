using System;
using System.Collections.Generic;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

public class DrawingAlignHelperTests
{
    private readonly DateTime _baseTime = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private FreehandObject CreateBoxObject(int startSec, int endSec, decimal minPrice, decimal maxPrice)
    {
        return new FreehandObject(new[]
        {
            new ChartPoint(_baseTime.AddSeconds(startSec), minPrice),
            new ChartPoint(_baseTime.AddSeconds(endSec), maxPrice)
        });
    }

    [Fact]
    public void AlignLeft_AlignsObjectsToMinimumTime()
    {
        var obj1 = CreateBoxObject(100, 200, 10m, 20m);
        var obj2 = CreateBoxObject(300, 400, 10m, 20m);
        var obj3 = CreateBoxObject(500, 600, 10m, 20m);

        bool success = DrawingAlignHelper.AlignLeft(new[] { obj1, obj2, obj3 });

        Assert.True(success);
        Assert.True(DrawingAlignHelper.TryGetObjectBounds(obj1, out var t1Min, out _, out _, out _));
        Assert.True(DrawingAlignHelper.TryGetObjectBounds(obj2, out var t2Min, out _, out _, out _));
        Assert.True(DrawingAlignHelper.TryGetObjectBounds(obj3, out var t3Min, out _, out _, out _));

        Assert.Equal(_baseTime.AddSeconds(100), t1Min);
        Assert.Equal(_baseTime.AddSeconds(100), t2Min);
        Assert.Equal(_baseTime.AddSeconds(100), t3Min);
    }

    [Fact]
    public void AlignRight_AlignsObjectsToMaximumTime()
    {
        var obj1 = CreateBoxObject(100, 200, 10m, 20m);
        var obj2 = CreateBoxObject(300, 400, 10m, 20m);
        var obj3 = CreateBoxObject(500, 600, 10m, 20m);

        bool success = DrawingAlignHelper.AlignRight(new[] { obj1, obj2, obj3 });

        Assert.True(success);
        Assert.True(DrawingAlignHelper.TryGetObjectBounds(obj1, out _, out var t1Max, out _, out _));
        Assert.True(DrawingAlignHelper.TryGetObjectBounds(obj2, out _, out var t2Max, out _, out _));
        Assert.True(DrawingAlignHelper.TryGetObjectBounds(obj3, out _, out var t3Max, out _, out _));

        Assert.Equal(_baseTime.AddSeconds(600), t1Max);
        Assert.Equal(_baseTime.AddSeconds(600), t2Max);
        Assert.Equal(_baseTime.AddSeconds(600), t3Max);
    }

    [Fact]
    public void AlignCenterHorizontal_AlignsObjectsToSharedCenter()
    {
        var obj1 = CreateBoxObject(100, 200, 10m, 20m); // Width 100s
        var obj2 = CreateBoxObject(300, 500, 10m, 20m); // Width 200s, max 500s

        // Global min = 100s, max = 500s -> target center = 300s
        bool success = DrawingAlignHelper.AlignCenterHorizontal(new[] { obj1, obj2 });

        Assert.True(success);
        Assert.True(DrawingAlignHelper.TryGetObjectBounds(obj1, out var t1Min, out var t1Max, out _, out _));
        Assert.True(DrawingAlignHelper.TryGetObjectBounds(obj2, out var t2Min, out var t2Max, out _, out _));

        var c1 = t1Min + TimeSpan.FromTicks((t1Max - t1Min).Ticks / 2);
        var c2 = t2Min + TimeSpan.FromTicks((t2Max - t2Min).Ticks / 2);

        Assert.Equal(_baseTime.AddSeconds(300), c1);
        Assert.Equal(_baseTime.AddSeconds(300), c2);
    }

    [Fact]
    public void AlignTop_AlignsObjectsToMaximumPrice()
    {
        var obj1 = CreateBoxObject(100, 200, 10m, 50m);
        var obj2 = CreateBoxObject(100, 200, 20m, 80m);
        var obj3 = CreateBoxObject(100, 200, 15m, 60m);

        bool success = DrawingAlignHelper.AlignTop(new[] { obj1, obj2, obj3 });

        Assert.True(success);
        Assert.True(DrawingAlignHelper.TryGetObjectBounds(obj1, out _, out _, out _, out var p1Max));
        Assert.True(DrawingAlignHelper.TryGetObjectBounds(obj2, out _, out _, out _, out var p2Max));
        Assert.True(DrawingAlignHelper.TryGetObjectBounds(obj3, out _, out _, out _, out var p3Max));

        Assert.Equal(80m, p1Max);
        Assert.Equal(80m, p2Max);
        Assert.Equal(80m, p3Max);
    }

    [Fact]
    public void AlignBottom_AlignsObjectsToMinimumPrice()
    {
        var obj1 = CreateBoxObject(100, 200, 10m, 50m);
        var obj2 = CreateBoxObject(100, 200, 20m, 80m);
        var obj3 = CreateBoxObject(100, 200, 15m, 60m);

        bool success = DrawingAlignHelper.AlignBottom(new[] { obj1, obj2, obj3 });

        Assert.True(success);
        Assert.True(DrawingAlignHelper.TryGetObjectBounds(obj1, out _, out _, out var p1Min, out _));
        Assert.True(DrawingAlignHelper.TryGetObjectBounds(obj2, out _, out _, out var p2Min, out _));
        Assert.True(DrawingAlignHelper.TryGetObjectBounds(obj3, out _, out _, out var p3Min, out _));

        Assert.Equal(10m, p1Min);
        Assert.Equal(10m, p2Min);
        Assert.Equal(10m, p3Min);
    }

    [Fact]
    public void DistributeHorizontal_SpacesObjectsUniformly()
    {
        // 3 objects of width 50s each
        var obj1 = CreateBoxObject(0, 50, 10m, 20m);
        var obj2 = CreateBoxObject(80, 130, 10m, 20m);
        var obj3 = CreateBoxObject(300, 350, 10m, 20m);

        // Total span: 350s. Total widths: 150s. Available gap: 200s.
        // Gap per segment (2 gaps): 100s.
        // Target obj2: min = 50 + 100 = 150s, max = 200s.
        bool success = DrawingAlignHelper.DistributeHorizontal(new[] { obj1, obj2, obj3 });

        Assert.True(success);
        Assert.True(DrawingAlignHelper.TryGetObjectBounds(obj2, out var t2Min, out var t2Max, out _, out _));

        Assert.Equal(_baseTime.AddSeconds(150), t2Min);
        Assert.Equal(_baseTime.AddSeconds(200), t2Max);
    }

    [Fact]
    public void DistributeVertical_SpacesObjectsUniformlyWithExactDecimal()
    {
        // 3 objects of height 20m each
        var obj1 = CreateBoxObject(100, 200, 0m, 20m);
        var obj2 = CreateBoxObject(100, 200, 30m, 50m);
        var obj3 = CreateBoxObject(100, 200, 100m, 120m);

        // Total span: 120m. Total height: 60m. Available gap: 60m.
        // Gap per segment (2 gaps): 30m.
        // Target obj2: min = 20 + 30 = 50m, max = 70m.
        bool success = DrawingAlignHelper.DistributeVertical(new[] { obj1, obj2, obj3 });

        Assert.True(success);
        Assert.True(DrawingAlignHelper.TryGetObjectBounds(obj2, out _, out _, out var p2Min, out var p2Max));

        Assert.Equal(50m, p2Min);
        Assert.Equal(70m, p2Max);
    }

    [Fact]
    public void GroupAndUngroupSelected_ManagesLinkGroups()
    {
        var manager = new ChartObjectManager();
        var obj1 = CreateBoxObject(100, 200, 10m, 20m);
        var obj2 = CreateBoxObject(300, 400, 10m, 20m);
        var obj3 = CreateBoxObject(500, 600, 10m, 20m);

        manager.AddObject(obj1);
        manager.AddObject(obj2);
        manager.AddObject(obj3);

        // Select all 3
        manager.SelectObjects(new[] { obj1.Id, obj2.Id, obj3.Id });

        // Group
        bool grouped = DrawingAlignHelper.GroupSelected(manager);
        Assert.True(grouped);

        // Check members
        Assert.True(manager.TryGetLinkedMembers(obj1.Id, out var members));
        Assert.Equal(3, members.Count);
        Assert.Contains(obj1.Id, members);
        Assert.Contains(obj2.Id, members);
        Assert.Contains(obj3.Id, members);

        // Ungroup
        bool ungrouped = DrawingAlignHelper.UngroupSelected(manager);
        Assert.True(ungrouped);

        Assert.False(manager.TryGetLinkedMembers(obj1.Id, out _));
    }
}
