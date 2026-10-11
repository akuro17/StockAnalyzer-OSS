using System;
using System.Collections.Generic;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

public class FreehandObjectTests
{
    private static GenericCoordinateTransform CreateTestTransform()
    {
        var transform = new GenericCoordinateTransform(ChartAxisMode.Time, 800, 600);
        transform.SetTimeRange(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc));
        transform.SetPriceRange(100m, 200m);
        return transform;
    }

    [Fact]
    public void Translate_ShiftsAllPointsByExactDeltaAndInvalidatesBounds()
    {
        var t0 = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var t1 = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc);
        var points = new[]
        {
            new ChartPoint(t0, 120m),
            new ChartPoint(t1, 150m)
        };

        var freehand = new FreehandObject(points);
        freehand.GetDataBounds(out var minTBefore, out var maxTBefore, out var minPBefore, out var maxPBefore);
        Assert.Equal(t0, minTBefore);
        Assert.Equal(t1, maxTBefore);
        Assert.Equal(120m, minPBefore);
        Assert.Equal(150m, maxPBefore);

        var timeDelta = TimeSpan.FromDays(2);
        var priceDelta = 25m;
        freehand.Translate(timeDelta, priceDelta);

        Assert.Equal(t0.Add(timeDelta), freehand.Points[0].Time);
        Assert.Equal(145m, freehand.Points[0].Price);
        Assert.Equal(t1.Add(timeDelta), freehand.Points[1].Time);
        Assert.Equal(175m, freehand.Points[1].Price);

        freehand.GetDataBounds(out var minTAfter, out var maxTAfter, out var minPAfter, out var maxPAfter);
        Assert.Equal(t0.Add(timeDelta), minTAfter);
        Assert.Equal(t1.Add(timeDelta), maxTAfter);
        Assert.Equal(145m, minPAfter);
        Assert.Equal(175m, maxPAfter);
    }

    [Fact]
    public void Translate_UpdatesHitTestCorrectly()
    {
        var transform = CreateTestTransform();
        var t0 = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var t1 = new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc);
        var freehand = new FreehandObject(new[]
        {
            new ChartPoint(t0, 120m),
            new ChartPoint(t1, 160m)
        });

        var ptInitial = transform.ChartToScreen(new ChartPoint(new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc), 140m));
        Assert.True(freehand.HitTest(ptInitial, transform));

        // Shift by 2 days and 20 price
        var timeDelta = TimeSpan.FromDays(2);
        var priceDelta = 20m;
        freehand.Translate(timeDelta, priceDelta);

        // Old position no longer hits
        Assert.False(freehand.HitTest(ptInitial, transform));

        // Shifted point hits
        var ptShifted = transform.ChartToScreen(new ChartPoint(new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc), 160m));
        Assert.True(freehand.HitTest(ptShifted, transform));
    }

    [Fact]
    public void TransformAabb_DeformsPointsCorrectly_PreservingTopology()
    {
        var t0 = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var t1 = new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc);
        var tMid = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc);

        var freehand = new FreehandObject(new[]
        {
            new ChartPoint(t0, 100m),
            new ChartPoint(tMid, 150m),
            new ChartPoint(t1, 200m)
        });

        // Deform to double width and halved price range
        var newMinT = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newMaxT = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
        var newMinP = 120m;
        var newMaxP = 170m;

        freehand.TransformAabb(newMinT, newMaxT, newMinP, newMaxP);

        // First point mapped to (newMinT, newMinP)
        Assert.Equal(newMinT, freehand.Points[0].Time);
        Assert.Equal(newMinP, freehand.Points[0].Price);

        // Mid point maintains exact 50% relative ratio
        var expectedMidT = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expectedMidT, freehand.Points[1].Time);
        Assert.Equal(145m, freehand.Points[1].Price);

        // End point mapped to (newMaxT, newMaxP)
        Assert.Equal(newMaxT, freehand.Points[2].Time);
        Assert.Equal(newMaxP, freehand.Points[2].Price);
    }

    [Fact]
    public void TransformAabb_HandlesZeroSpanGracefully()
    {
        var t0 = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var singlePoint = new FreehandObject(new[] { new ChartPoint(t0, 150m) });

        var targetT = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
        singlePoint.TransformAabb(targetT, targetT, 180m, 180m);

        Assert.Equal(targetT, singlePoint.Points[0].Time);
        Assert.Equal(180m, singlePoint.Points[0].Price);
    }

    [Fact]
    public void GetAabbHandlePositions_ReturnsEightAccurateHandles()
    {
        var transform = CreateTestTransform();
        var t0 = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var t1 = new DateTime(2026, 1, 6, 0, 0, 0, DateTimeKind.Utc);
        var freehand = new FreehandObject(new[]
        {
            new ChartPoint(t0, 120m),
            new ChartPoint(t1, 180m)
        });

        var handles = freehand.GetAabbHandlePositions(transform);
        Assert.Equal(8, handles.Length);

        var pTopLeft = handles[0];
        var pBottomRight = handles[4];

        Assert.True(pTopLeft.X < pBottomRight.X);
        Assert.True(pTopLeft.Y < pBottomRight.Y);

        // TopCenter X should be midpoint of Left and Right
        Assert.Equal((pTopLeft.X + pBottomRight.X) * 0.5, handles[1].X, 3);
        // RightCenter Y should be midpoint of Top and Bottom
        Assert.Equal((pTopLeft.Y + pBottomRight.Y) * 0.5, handles[3].Y, 3);
    }
}
