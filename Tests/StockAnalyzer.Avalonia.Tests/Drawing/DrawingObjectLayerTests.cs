using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
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

        public bool HitTest(Point screenPoint, ICoordinateTransform transform, double tolerance) => true;

        public void Translate(TimeSpan timeDelta, decimal priceDelta) { }

        public static void ResetGlobalCount() => _globalRenderCount = 0;
    }

    [Fact]
    public void ChartObjectManager_ShouldFilterInvisibleObjects()
    {
        // Arrange
        var manager = new ChartObjectManager();
        var visibleObj = new MockChartObject { IsVisible = true };
        var invisibleObj = new MockChartObject { IsVisible = false };
        manager.AddObject(visibleObj);
        manager.AddObject(invisibleObj);

        // Act
        manager.Render(null!, null!);

        // Assert
        Assert.True(visibleObj.RenderCalled);
        Assert.False(invisibleObj.RenderCalled);
    }

    [Fact]
    public void ChartObjectManager_ShouldRenderInZOrder()
    {
        // Arrange
        MockChartObject.ResetGlobalCount();
        var manager = new ChartObjectManager();
        var backObj = new MockChartObject { ZIndex = -10 };
        var middleObj = new MockChartObject { ZIndex = 0 };
        var frontObj = new MockChartObject { ZIndex = 10 };
        
        // Add in random order
        manager.AddObject(middleObj);
        manager.AddObject(frontObj);
        manager.AddObject(backObj);

        // Act
        manager.Render(null!, null!);

        // Assert
        Assert.True(backObj.RenderOrder < middleObj.RenderOrder);
        Assert.True(middleObj.RenderOrder < frontObj.RenderOrder);
    }

    [Fact]
    public void ChartObjectManager_GetObjectAt_ShouldRespectVisibility()
    {
        // Arrange
        var manager = new ChartObjectManager();
        var invisibleObj = new MockChartObject { IsVisible = false };
        manager.AddObject(invisibleObj);

        // Act
        var result = manager.GetObjectAt(new Point(0, 0), null!);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ChartObjectManager_GetObjectAt_ShouldRespectZOrder()
    {
        // Arrange
        var manager = new ChartObjectManager();
        var backObj = new MockChartObject { ZIndex = 0, Id = Guid.NewGuid() };
        var frontObj = new MockChartObject { ZIndex = 10, Id = Guid.NewGuid() };
        
        manager.AddObject(backObj);
        manager.AddObject(frontObj);

        // Act
        var result = manager.GetObjectAt(new Point(0, 0), null!);

        // Assert
        Assert.Equal(frontObj.Id, result?.Id);
    }
}
