using System;
using System.Collections.Generic;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using Xunit;
using Point = Avalonia.Point;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public class LayeredDrawingSceneTests
{
    private class TestOrderTrackingChartObject : IChartObject
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public ChartObjectType Type => ChartObjectType.TrendLine;
        public string? CustomName { get; set; }
        public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
        public bool IsMoveAxisModeExplicit { get; set; }
        public List<ChartPoint> Points { get; set; } = new();
        public Color Color { get; set; } = Colors.Black;
        public double Thickness { get; set; } = 1.0;
        public bool IsSelected { get; set; }
        public bool IsVisible { get; set; } = true;
        public bool IsLocked { get; set; } = false;
        public int ZIndex { get; set; } = 0;
        public int PanelIndex { get; set; } = -1;
        public SKColor SkiaColor => SKColors.Black;

        public static readonly List<Guid> GlobalRenderExecutionLog = new();
        public bool ShouldHit { get; set; } = true;

        public void Render(SKCanvas canvas, ICoordinateTransform transform)
        {
            GlobalRenderExecutionLog.Add(Id);
        }

        public bool HitTest(Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
        {
            return ShouldHit;
        }

        public void Translate(TimeSpan timeDelta, decimal priceDelta) { }
    }

    private static ICoordinateTransform CreateTransform() =>
        new GenericCoordinateTransform(ChartAxisMode.Time, 1000, 500);

    [Fact]
    public void RenderAndHitTest_MaintainsExactOrderParity()
    {
        TestOrderTrackingChartObject.GlobalRenderExecutionLog.Clear();

        var scene = new LayeredDrawingScene();
        var mainKey = PanelKey.Main;
        var transform = CreateTransform();

        // 2 layers, 2 objects each (4 objects total)
        var obj0 = new TestOrderTrackingChartObject();
        var obj1 = new TestOrderTrackingChartObject();
        var obj2 = new TestOrderTrackingChartObject();
        var obj3 = new TestOrderTrackingChartObject();

        var pid0 = Guid.NewGuid();
        var pid1 = Guid.NewGuid();
        var pid2 = Guid.NewGuid();
        var pid3 = Guid.NewGuid();

        var dict = new Dictionary<Guid, IChartObject>
        {
            [pid0] = obj0,
            [pid1] = obj1,
            [pid2] = obj2,
            [pid3] = obj3
        };

        var layer1 = new DrawingLayerRecord(Guid.NewGuid(), "Layer Bottom", mainKey, isVisible: true, isEditLocked: false, new[] { pid0, pid1 });
        var layer2 = new DrawingLayerRecord(Guid.NewGuid(), "Layer Top", mainKey, isVisible: true, isEditLocked: false, new[] { pid2, pid3 });

        scene.UpdateScene(ChartDrawingContextType.Standard, 1, 1, new[] { layer1, layer2 }, dict);

        // 1. Verify Render order: forward scan 0, 1, 2, 3
        using var bitmap = new SKBitmap(100, 100);
        using var canvas = new SKCanvas(bitmap);

        scene.RenderPanel(canvas, mainKey, transform);

        Assert.Equal(4, TestOrderTrackingChartObject.GlobalRenderExecutionLog.Count);
        Assert.Equal(obj0.Id, TestOrderTrackingChartObject.GlobalRenderExecutionLog[0]);
        Assert.Equal(obj1.Id, TestOrderTrackingChartObject.GlobalRenderExecutionLog[1]);
        Assert.Equal(obj2.Id, TestOrderTrackingChartObject.GlobalRenderExecutionLog[2]);
        Assert.Equal(obj3.Id, TestOrderTrackingChartObject.GlobalRenderExecutionLog[3]);

        // 2. Verify HitTest order: backward scan picks top-most (pid3)
        var hitResult = scene.HitTest(mainKey, new Point(10, 10), transform);
        Assert.Equal(pid3, hitResult);
    }

    [Fact]
    public void HitTest_WhenTopLayerHidden_PicksObjectFromUnderlyingLayer()
    {
        var scene = new LayeredDrawingScene();
        var mainKey = PanelKey.Main;
        var transform = CreateTransform();

        var obj0 = new TestOrderTrackingChartObject();
        var obj1 = new TestOrderTrackingChartObject();
        var pid0 = Guid.NewGuid();
        var pid1 = Guid.NewGuid();

        var dict = new Dictionary<Guid, IChartObject>
        {
            [pid0] = obj0,
            [pid1] = obj1
        };

        // Layer 1 is visible, Layer 2 is hidden
        var layer1 = new DrawingLayerRecord(Guid.NewGuid(), "Layer Bottom", mainKey, isVisible: true, isEditLocked: false, new[] { pid0 });
        var layer2 = new DrawingLayerRecord(Guid.NewGuid(), "Layer Top", mainKey, isVisible: false, isEditLocked: false, new[] { pid1 });

        scene.UpdateScene(ChartDrawingContextType.Standard, 1, 1, new[] { layer1, layer2 }, dict);

        var hitResult = scene.HitTest(mainKey, new Point(10, 10), transform);
        Assert.Equal(pid0, hitResult); // Must pick pid0 since pid1 is in hidden layer
    }

    [Fact]
    public void HandleEraserAt_WhenTopObjectIsLocked_BlocksWithoutPenetration()
    {
        var scene = new LayeredDrawingScene();
        var mainKey = PanelKey.Main;
        var transform = CreateTransform();

        var backgroundObj = new TestOrderTrackingChartObject { IsLocked = false };
        var topLockedObj = new TestOrderTrackingChartObject { IsLocked = true }; // Deletion protection enabled

        var bgPid = Guid.NewGuid();
        var topPid = Guid.NewGuid();

        var dict = new Dictionary<Guid, IChartObject>
        {
            [bgPid] = backgroundObj,
            [topPid] = topLockedObj
        };

        var layer = new DrawingLayerRecord(Guid.NewGuid(), "Layer", mainKey, isVisible: true, isEditLocked: false, new[] { bgPid, topPid });
        scene.UpdateScene(ChartDrawingContextType.Standard, 1, 1, new[] { layer }, dict);

        bool deleted = scene.HandleEraserAt(
            mainKey,
            new Point(10, 10),
            transform,
            out var deletedPid,
            out bool isProtectedBlocked);

        Assert.False(deleted);
        Assert.Null(deletedPid);
        Assert.True(isProtectedBlocked); // Must stop at top locked object and not delete background object
    }

    [Fact]
    public void HandleEraserAt_WhenLayerIsEditLocked_BlocksWithoutPenetration()
    {
        var scene = new LayeredDrawingScene();
        var mainKey = PanelKey.Main;
        var transform = CreateTransform();

        var backgroundObj = new TestOrderTrackingChartObject { IsLocked = false };
        var topObj = new TestOrderTrackingChartObject { IsLocked = false };

        var bgPid = Guid.NewGuid();
        var topPid = Guid.NewGuid();

        var dict = new Dictionary<Guid, IChartObject>
        {
            [bgPid] = backgroundObj,
            [topPid] = topObj
        };

        var layer = new DrawingLayerRecord(Guid.NewGuid(), "Locked Layer", mainKey, isVisible: true, isEditLocked: true, new[] { bgPid, topPid });
        scene.UpdateScene(ChartDrawingContextType.Standard, 1, 1, new[] { layer }, dict);

        bool deleted = scene.HandleEraserAt(
            mainKey,
            new Point(10, 10),
            transform,
            out var deletedPid,
            out bool isProtectedBlocked);

        Assert.False(deleted);
        Assert.Null(deletedPid);
        Assert.True(isProtectedBlocked);
    }

    [Fact]
    public void HandleEraserAt_WhenUnprotected_ReturnsTopMostObject()
    {
        var scene = new LayeredDrawingScene();
        var mainKey = PanelKey.Main;
        var transform = CreateTransform();

        var backgroundObj = new TestOrderTrackingChartObject { IsLocked = false };
        var topObj = new TestOrderTrackingChartObject { IsLocked = false };

        var bgPid = Guid.NewGuid();
        var topPid = Guid.NewGuid();

        var dict = new Dictionary<Guid, IChartObject>
        {
            [bgPid] = backgroundObj,
            [topPid] = topObj
        };

        var layer = new DrawingLayerRecord(Guid.NewGuid(), "Normal Layer", mainKey, isVisible: true, isEditLocked: false, new[] { bgPid, topPid });
        scene.UpdateScene(ChartDrawingContextType.Standard, 1, 1, new[] { layer }, dict);

        bool deleted = scene.HandleEraserAt(
            mainKey,
            new Point(10, 10),
            transform,
            out var deletedPid,
            out bool isProtectedBlocked);

        Assert.True(deleted);
        Assert.Equal(topPid, deletedPid);
        Assert.False(isProtectedBlocked);
    }
}
