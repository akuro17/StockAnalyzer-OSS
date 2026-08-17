using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Input;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Models;
using Xunit;
using Point = global::Avalonia.Point;
using Rect = global::Avalonia.Rect;

namespace StockAnalyzer.Tests.Drawing;

/// <summary>
/// Settings -> Chart -> Drawing (Rule of Zero Interference): holding Alt while dragging a handle
/// of an existing drawing object must bypass magnet (candle/price-time) snapping too, not just
/// Smart Guide (object-to-object) snapping.
/// </summary>
public class MagnetSnapAltBypassTests
{
    [Fact]
    public void HandleDrag_NoAlt_SnapsToNearbyCandle()
    {
        var (controller, dragged, snapshot, transform, mousePoint, targetCandle) = Arrange();

        var handled = controller.HandleObjectDrag(mousePoint, snapshot, transform, 0, 0, new Rect(0, 0, 1000, 500), KeyModifiers.None);

        Assert.True(handled);
        Assert.Equal(targetCandle.Timestamp, dragged.Points[0].Time);
        Assert.Equal(targetCandle.Close, dragged.Points[0].Price);
    }

    [Fact]
    public void HandleDrag_WithAltModifier_BypassesMagnetSnap()
    {
        var (controller, dragged, snapshot, transform, mousePoint, targetCandle) = Arrange();
        var rawChartPoint = transform.ScreenToChart(mousePoint);

        var handled = controller.HandleObjectDrag(mousePoint, snapshot, transform, 0, 0, new Rect(0, 0, 1000, 500), KeyModifiers.Alt);

        Assert.True(handled);
        Assert.NotEqual(targetCandle.Close, dragged.Points[0].Price);
        Assert.Equal(rawChartPoint.Time, dragged.Points[0].Time);
        Assert.Equal(rawChartPoint.Price, dragged.Points[0].Price);
    }

    private static (ChartInteractionController controller, TrendLineObject dragged, ChartDataSnapshot snapshot, GenericCoordinateTransform transform, Point mousePoint, CoreCandleData targetCandle) Arrange()
    {
        var baseTime = new DateTime(2025, 1, 1);
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < 10; i++)
        {
            candles.Add(new CoreCandleData(baseTime.AddDays(i), 100, 110, 90, 105, 1000));
        }

        var transform = new GenericCoordinateTransform(ChartAxisMode.Time, 1000, 500);
        transform.SetTimeRange(candles[0].Timestamp, candles[9].Timestamp);
        transform.SetPriceRange(80, 120);

        var snapshot = new ChartDataSnapshot(candles);

        var targetCandle = candles[5];
        var targetPointScreen = transform.ChartToScreen(new ChartPoint(targetCandle.Timestamp, targetCandle.Close));
        // 2px away from the candle's close: within MagnetSnapService's tolerance (matches MagnetSnapTests).
        var mousePoint = new Point(targetPointScreen.X + 2, targetPointScreen.Y + 2);

        var dragged = new TrendLineObject(
            new ChartPoint(candles[0].Timestamp, 100m),
            new ChartPoint(candles[8].Timestamp, 100m));

        var objectManager = new ChartObjectManager();
        objectManager.AddObject(dragged);
        objectManager.SelectObject(dragged.Id);

        var controller = new ChartInteractionController(new MagnetSnapService(), new DialogService(), new SmartGuideService());
        controller.SetActiveObjectManager(objectManager);
        controller.DraggedObject = dragged;
        controller.DraggedHandleIndex = 0;
        controller.LastDragPoint = new Point(0, 0);

        return (controller, dragged, snapshot, transform, mousePoint, targetCandle);
    }
}
