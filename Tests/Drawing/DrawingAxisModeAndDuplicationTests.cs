using System;
using System.Collections.Generic;
using AvPoint = global::Avalonia.Point;
using AvRect = global::Avalonia.Rect;
using AvColors = Avalonia.Media.Colors;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

public class DrawingAxisModeAndDuplicationTests
{
    [Fact]
    public void DuplicateObject_ValidTrendLine_CreatesNewInstanceWithSamePropertiesAndDistinctId()
    {
        var manager = new ChartObjectManager();
        var p1 = new ChartPoint(new DateTime(2026, 1, 1), 100m);
        var p2 = new ChartPoint(new DateTime(2026, 1, 10), 150m);
        var original = new TrendLineObject(p1, p2)
        {
            Color = AvColors.Red,
            Thickness = 3.5,
            ShowProjection = true,
            ProjectionColumns = 15
        };

        manager.AddObject(original);
        Assert.Single(manager.Objects);

        var clone = manager.DuplicateObject(original.Id);

        Assert.NotNull(clone);
        Assert.NotEqual(original.Id, clone.Id);
        Assert.Equal(2, manager.Objects.Count);
        Assert.True(clone.IsSelected);
        Assert.False(original.IsSelected);
        Assert.Equal(original.Type, clone.Type);
        Assert.Equal(original.Points.Count, clone.Points.Count);
        Assert.Equal(original.Points[0].Time, clone.Points[0].Time);
        Assert.Equal(original.Points[0].Price, clone.Points[0].Price);
        Assert.Equal(original.Points[1].Time, clone.Points[1].Time);
        Assert.Equal(original.Points[1].Price, clone.Points[1].Price);
        Assert.Equal(original.Color, clone.Color);
        Assert.Equal(original.Thickness, clone.Thickness);

        if (clone is TrendLineObject tlClone)
        {
            Assert.Equal(original.ShowProjection, tlClone.ShowProjection);
            Assert.Equal(original.ProjectionColumns, tlClone.ProjectionColumns);
        }
    }

    [Fact]
    public void DuplicateObject_WithOffset_AppliesTranslation()
    {
        var manager = new ChartObjectManager();
        var p1 = new ChartPoint(new DateTime(2026, 1, 1), 100m);
        var p2 = new ChartPoint(new DateTime(2026, 1, 5), 120m);
        var original = new TrendLineObject(p1, p2);
        manager.AddObject(original);

        var timeOffset = TimeSpan.FromDays(2);
        var priceOffset = 15m;

        var clone = manager.DuplicateObject(original.Id, timeOffset, priceOffset);

        Assert.NotNull(clone);
        Assert.Equal(p1.Time.Add(timeOffset), clone.Points[0].Time);
        Assert.Equal(p1.Price + priceOffset, clone.Points[0].Price);
        Assert.Equal(p2.Time.Add(timeOffset), clone.Points[1].Time);
        Assert.Equal(p2.Price + priceOffset, clone.Points[1].Price);
    }

    [Fact]
    public void DuplicateObject_NonExistentId_ReturnsNull()
    {
        var manager = new ChartObjectManager();
        var result = manager.DuplicateObject(Guid.NewGuid());
        Assert.Null(result);
    }

    [Fact]
    public void ChartInteractionController_MoveAxisMode_X_OnlyTranslatesTime()
    {
        var controller = new ChartInteractionController();
        controller.MoveAxisMode = DrawingMoveAxisMode.X;

        var p1 = new ChartPoint(new DateTime(2026, 1, 1), 100m);
        var p2 = new ChartPoint(new DateTime(2026, 1, 5), 120m);
        var trendLine = new TrendLineObject(p1, p2);

        controller.DraggedObject = trendLine;
        controller.DraggedHandleIndex = -1; // Whole object drag
        controller.LastDragPoint = new AvPoint(100, 100);

        var transform = new GenericCoordinateTransform(ChartAxisMode.GaplessTime, 1000, 500);
        transform.UpdateRange(new DateTime(2026, 1, 1), new DateTime(2026, 1, 30), 50m, 200m);

        var snapshot = new ChartDataSnapshot(new List<CoreCandleData>());

        // Simulate moving mouse diagonally (both X and Y screen change)
        var newMousePos = new AvPoint(200, 200);

        var prevChart = transform.ScreenToChart(controller.LastDragPoint);
        var currChart = transform.ScreenToChart(newMousePos);
        var expectedTimeDelta = currChart.Time - prevChart.Time;

        var initialP1Price = trendLine.Points[0].Price;
        var initialP2Price = trendLine.Points[1].Price;

        var chartVm = new ChartViewModel { MoveAxisMode = DrawingMoveAxisMode.X };
        var handled = controller.HandlePointerMoved(
            newMousePos,
            newMousePos,
            chartVm,
            transform,
            snapshot,
            new AvRect(0, 0, 1000, 500),
            0,
            0,
            out bool needsUpdate);

        Assert.True(handled);
        Assert.True(needsUpdate);
        // Price must remain unchanged in X-axis mode
        Assert.Equal(initialP1Price, trendLine.Points[0].Price);
        Assert.Equal(initialP2Price, trendLine.Points[1].Price);
        // Time must have moved by expectedTimeDelta
        Assert.Equal(p1.Time.Add(expectedTimeDelta), trendLine.Points[0].Time);
        Assert.Equal(p2.Time.Add(expectedTimeDelta), trendLine.Points[1].Time);
    }

    [Fact]
    public void ChartInteractionController_MoveAxisMode_Y_OnlyTranslatesPrice()
    {
        var controller = new ChartInteractionController();
        controller.MoveAxisMode = DrawingMoveAxisMode.Y;

        var p1 = new ChartPoint(new DateTime(2026, 1, 1), 100m);
        var p2 = new ChartPoint(new DateTime(2026, 1, 5), 120m);
        var trendLine = new TrendLineObject(p1, p2);

        controller.DraggedObject = trendLine;
        controller.DraggedHandleIndex = -1; // Whole object drag
        controller.LastDragPoint = new AvPoint(100, 100);

        var transform = new GenericCoordinateTransform(ChartAxisMode.GaplessTime, 1000, 500);
        transform.UpdateRange(new DateTime(2026, 1, 1), new DateTime(2026, 1, 30), 50m, 200m);

        var snapshot = new ChartDataSnapshot(new List<CoreCandleData>());

        // Simulate moving mouse diagonally (both X and Y screen change)
        var newMousePos = new AvPoint(200, 200);

        var prevChart = transform.ScreenToChart(controller.LastDragPoint);
        var currChart = transform.ScreenToChart(newMousePos);
        var expectedPriceDelta = currChart.Price - prevChart.Price;

        var initialP1Time = trendLine.Points[0].Time;
        var initialP2Time = trendLine.Points[1].Time;

        var chartVm = new ChartViewModel { MoveAxisMode = DrawingMoveAxisMode.Y };
        var handled = controller.HandlePointerMoved(
            newMousePos,
            newMousePos,
            chartVm,
            transform,
            snapshot,
            new AvRect(0, 0, 1000, 500),
            0,
            0,
            out bool needsUpdate);

        Assert.True(handled);
        Assert.True(needsUpdate);
        // Time must remain unchanged in Y-axis mode
        Assert.Equal(initialP1Time, trendLine.Points[0].Time);
        Assert.Equal(initialP2Time, trendLine.Points[1].Time);
        // Price must have moved by expectedPriceDelta
        Assert.Equal(p1.Price + expectedPriceDelta, trendLine.Points[0].Price);
        Assert.Equal(p2.Price + expectedPriceDelta, trendLine.Points[1].Price);
    }
}
