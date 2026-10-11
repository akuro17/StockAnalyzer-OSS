using System;
using System.Collections.Generic;
using Avalonia;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using Xunit;
using Point = Avalonia.Point;

namespace StockAnalyzer.Tests.Drawing;

public class StrokeCutterEngineTests
{
    private readonly LinearCoordinateTransform _transform;
    private readonly DateTime _baseTime = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public StrokeCutterEngineTests()
    {
        // 1000px width, 500px height
        // Time: 0 to 1000 seconds -> 1px per second
        // Price: 0m to 500m -> 1px per 1.0m (inverted: y=0 is 500m, y=500 is 0m)
        _transform = new LinearCoordinateTransform(
            _baseTime,
            _baseTime.AddSeconds(1000),
            0m,
            500m,
            1000,
            500);
    }

    [Fact]
    public void CutStroke_WhenStraightStrokeCutInMiddle_SplitsIntoTwoDistinctStrokes()
    {
        // Line from (100s, 250m) to (500s, 250m) to (900s, 250m)
        var stroke = new FreehandObject(new[]
        {
            new ChartPoint(_baseTime.AddSeconds(100), 250m),
            new ChartPoint(_baseTime.AddSeconds(500), 250m),
            new ChartPoint(_baseTime.AddSeconds(900), 250m),
        });

        var centerScreen = _transform.ChartToScreen(new ChartPoint(_baseTime.AddSeconds(500), 250m));
        double eraserRadius = 50.0;

        bool cut = StrokeCutterEngine.CutStroke(stroke, centerScreen, eraserRadius, _transform, out var results);

        Assert.True(cut);
        Assert.Equal(2, results.Count);

        // Both resulting strokes must have at least 2 points
        Assert.True(results[0].Points.Count >= 2);
        Assert.True(results[1].Points.Count >= 2);

        // First stroke ends near 500 - 50 = 450s
        var p1End = results[0].Points[^1];
        Assert.True(p1End.Time < _baseTime.AddSeconds(460));
        Assert.True(p1End.Time > _baseTime.AddSeconds(440));

        // Second stroke starts near 500 + 50 = 550s
        var p2Start = results[1].Points[0];
        Assert.True(p2Start.Time > _baseTime.AddSeconds(540));
        Assert.True(p2Start.Time < _baseTime.AddSeconds(560));
    }

    [Fact]
    public void CutStroke_WhenEraserCoversEntireStroke_ReturnsEmptyResult()
    {
        var stroke = new FreehandObject(new[]
        {
            new ChartPoint(_baseTime.AddSeconds(500), 250m),
            new ChartPoint(_baseTime.AddSeconds(510), 250m)
        });

        var centerScreen = _transform.ChartToScreen(new ChartPoint(_baseTime.AddSeconds(505), 250m));
        double eraserRadius = 100.0; // Covers both points

        bool cut = StrokeCutterEngine.CutStroke(stroke, centerScreen, eraserRadius, _transform, out var results);

        Assert.True(cut);
        Assert.Empty(results);
    }

    [Fact]
    public void CutStroke_WhenEraserDoesNotIntersect_ReturnsFalseAndOriginalStroke()
    {
        var stroke = new FreehandObject(new[]
        {
            new ChartPoint(_baseTime.AddSeconds(100), 250m),
            new ChartPoint(_baseTime.AddSeconds(200), 250m)
        });

        var centerScreen = _transform.ChartToScreen(new ChartPoint(_baseTime.AddSeconds(800), 250m));
        double eraserRadius = 20.0;

        bool cut = StrokeCutterEngine.CutStroke(stroke, centerScreen, eraserRadius, _transform, out var results);

        Assert.False(cut);
        Assert.Single(results);
        Assert.Same(stroke, results[0]);
    }

    [Fact]
    public void ExecuteCut_WhenObjectIsLocked_DoesNotCut()
    {
        var manager = new ChartObjectManager();
        var stroke = new FreehandObject(new[]
        {
            new ChartPoint(_baseTime.AddSeconds(100), 250m),
            new ChartPoint(_baseTime.AddSeconds(500), 250m),
            new ChartPoint(_baseTime.AddSeconds(900), 250m),
        })
        {
            IsLocked = true
        };
        manager.AddObject(stroke);

        var centerScreen = _transform.ChartToScreen(new ChartPoint(_baseTime.AddSeconds(500), 250m));

        bool executed = StrokeCutterEngine.ExecuteCut(
            stroke,
            centerScreen,
            50.0,
            _transform,
            manager,
            null,
            null);

        Assert.False(executed);
        Assert.NotNull(manager.GetObject(stroke.Id));
    }

    [Fact]
    public void ExecuteCut_WhenCutSucceeds_ReplacesOriginalWithSubStrokes()
    {
        var manager = new ChartObjectManager();
        var stroke = new FreehandObject(new[]
        {
            new ChartPoint(_baseTime.AddSeconds(100), 250m),
            new ChartPoint(_baseTime.AddSeconds(500), 250m),
            new ChartPoint(_baseTime.AddSeconds(900), 250m),
        });
        manager.AddObject(stroke);

        var centerScreen = _transform.ChartToScreen(new ChartPoint(_baseTime.AddSeconds(500), 250m));

        bool executed = StrokeCutterEngine.ExecuteCut(
            stroke,
            centerScreen,
            50.0,
            _transform,
            manager,
            null,
            null);

        Assert.True(executed);
        // Original stroke was removed
        Assert.Null(manager.GetObject(stroke.Id));
        // Exactly 2 new strokes added
        Assert.Equal(2, manager.Objects.Count);
    }
}
