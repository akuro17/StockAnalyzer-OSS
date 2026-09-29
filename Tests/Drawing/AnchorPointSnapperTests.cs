using System;
using System.Collections.Generic;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

/// <summary>
/// Link-time anchor point snap: children are translated (never reshaped) so that their AP equals the parent's AP,
/// the parent never moves, and the snap is part of the link's single Undo step.
/// </summary>
public class AnchorPointSnapperTests
{
    private static readonly DateTime Day0 = new(2025, 1, 1);

    private static TrendLineObject Line(int day0, decimal price0, int day1, decimal price1, int anchorIndex = 0)
        => new(new ChartPoint(Day0.AddDays(day0), price0), new ChartPoint(Day0.AddDays(day1), price1)) { AnchorPointIndex = anchorIndex };

    [Fact]
    public void TryGetAnchorPoint_UsesAnchorPointIndex_AndFallsBackToFirstPointWhenOutOfRange()
    {
        var line = Line(2, 100m, 8, 140m, anchorIndex: 1);
        Assert.True(AnchorPointSnapper.TryGetAnchorPoint(line, out var second));
        Assert.Equal(line.Points[1], second);

        line.AnchorPointIndex = 9;
        Assert.True(AnchorPointSnapper.TryGetAnchorPoint(line, out var fallback));
        Assert.Equal(line.Points[0], fallback);
    }

    [Fact]
    public void TryGetAnchorPoint_Rectangle_UsesSynthesizedCorner()
    {
        var rect = new RectangleObject(new ChartPoint(Day0.AddDays(2), 100m), new ChartPoint(Day0.AddDays(6), 140m)) { AnchorPointIndex = 1 };

        Assert.True(AnchorPointSnapper.TryGetAnchorPoint(rect, out var corner));

        Assert.Equal(new ChartPoint(Day0.AddDays(6), 100m), corner); // (p2.Time, p1.Price)
    }

    [Fact]
    public void SnapChildrenToParent_TranslatesEachChildSoItsApEqualsTheParentAp_KeepingShape()
    {
        var parent = Line(10, 200m, 16, 230m, anchorIndex: 1);      // AP = (day16, 230)
        var childA = Line(2, 100m, 8, 140m, anchorIndex: 0);          // AP = (day2, 100)
        var childB = Line(4, 50m, 9, 90m, anchorIndex: 1);            // AP = (day9, 90)
        var parentBefore = (parent.Points[0], parent.Points[1]);
        var shapeA = (childA.Points[1].Time - childA.Points[0].Time, childA.Points[1].Price - childA.Points[0].Price);

        AnchorPointSnapper.SnapChildrenToParent(parent, new IChartObject[] { childA, childB });

        Assert.Equal(new ChartPoint(Day0.AddDays(16), 230m), childA.Points[0]);
        Assert.Equal(new ChartPoint(Day0.AddDays(16), 230m), childB.Points[1]);
        Assert.Equal(shapeA, (childA.Points[1].Time - childA.Points[0].Time, childA.Points[1].Price - childA.Points[0].Price));
        Assert.Equal(parentBefore, (parent.Points[0], parent.Points[1]));
    }

    [Fact]
    public void SnapChildrenToParent_ParentWithoutAp_MovesNothing()
    {
        var parent = new TrendLineObject(new ChartPoint(Day0, 1m), new ChartPoint(Day0.AddDays(1), 2m));
        parent.Points.Clear();
        var child = Line(2, 100m, 8, 140m);
        var before = child.Points[0];

        AnchorPointSnapper.SnapChildrenToParent(parent, new IChartObject[] { child });
        Assert.Equal(before, child.Points[0]);
    }

    [Fact]
    public void SnapChildrenToParent_BarLockedChild_MovesByWholeBarsOnly()
    {
        var history = new List<CoreCandleData>();
        for (int i = 0; i < 40; i++)
        {
            history.Add(new CoreCandleData(Day0.AddDays(i), 100m + i, 105m + i, 95m + i, 100m + i, 1000));
        }
        var parent = Line(15, 500m, 20, 520m, anchorIndex: 0);       // AP at bar 15
        var locked = new FixedRangeVolumeProfileObject(new ChartPoint(Day0.AddDays(10), 100m), new ChartPoint(Day0.AddDays(20), 200m))
        {
            LockRange = true
        };                                                            // AP at bar 10 -> +5 bars

        AnchorPointSnapper.SnapChildrenToParent(parent, new IChartObject[] { locked }, () => history);

        Assert.Equal(Day0.AddDays(15), locked.Points[0].Time);
        Assert.Equal(Day0.AddDays(25), locked.Points[1].Time);
        Assert.Equal(100m, locked.Points[0].Price); // bar-locked objects never move vertically
    }

    [Fact]
    public void LinkAndSnap_InOneEdit_AreUndoneTogetherAndRedoneTogether()
    {
        var key = new DrawingDocumentKey("7203", TimeframeType.Daily);
        var manager = new ChartObjectManager();
        var session = new DrawingDocumentSession(key);
        using var history = new DrawingHistoryService(key);
        using var coordinator = new DrawingEditCoordinator(manager, history, session);

        var parent = Line(10, 200m, 16, 230m);
        var child = Line(2, 100m, 8, 140m);
        coordinator.ExecuteEdit(DrawingOperationKind.Add, () =>
        {
            manager.AddObject(parent);
            manager.AddObject(child);
        });

        coordinator.ExecuteEdit(DrawingOperationKind.LinkObjects, () =>
        {
            manager.LinkObjects(parent.Id, new[] { child.Id });
            AnchorPointSnapper.SnapChildrenToParent(parent, new IChartObject[] { child });
        });
        Assert.Equal(2, coordinator.UndoCount);
        Assert.Equal(parent.Points[0], child.Points[0]);

        Assert.True(coordinator.Undo().IsSuccess);
        Assert.Equal(LinkRole.None, manager.GetLinkRole(manager.Objects[1].Id));
        Assert.Equal(new ChartPoint(Day0.AddDays(2), 100m), manager.Objects[1].Points[0]);

        Assert.True(coordinator.Redo().IsSuccess);
        Assert.Equal(LinkRole.Parent, manager.GetLinkRole(manager.Objects[0].Id));
        Assert.Equal(manager.Objects[0].Points[0], manager.Objects[1].Points[0]);
    }
}
