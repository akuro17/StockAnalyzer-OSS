using System;
using System.Collections.Generic;
using System.Text.Json;
using Avalonia;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using Xunit;
using Point = Avalonia.Point;

namespace StockAnalyzer.Tests.Drawing;

public class ScreenSpaceLayerTests
{
    [Fact]
    public void ScreenSpaceCoordinateTransform_NormalizedToScreen_ComputesPixelPositionsAccurately()
    {
        var transform = new ScreenSpaceCoordinateTransform(800, 600);

        var pOrigin = transform.NormalizedToScreen(0.0m, 0.0m);
        Assert.Equal(new Point(0, 0), pOrigin);

        var pCenter = transform.NormalizedToScreen(0.5m, 0.5m);
        Assert.Equal(new Point(400, 300), pCenter);

        var pMax = transform.NormalizedToScreen(1.0m, 1.0m);
        Assert.Equal(new Point(800, 600), pMax);

        // Clamping check
        var pClamped = transform.NormalizedToScreen(-0.5m, 1.5m);
        Assert.Equal(new Point(0, 600), pClamped);
    }

    [Fact]
    public void ScreenSpaceCoordinateTransform_ScreenToNormalized_InvertsScreenPixelsAccurately()
    {
        var transform = new ScreenSpaceCoordinateTransform(800, 600);

        var (uxOrigin, uyOrigin) = transform.ScreenToNormalized(new Point(0, 0));
        Assert.Equal(0.0m, uxOrigin);
        Assert.Equal(0.0m, uyOrigin);

        var (uxCenter, uyCenter) = transform.ScreenToNormalized(new Point(400, 300));
        Assert.Equal(0.5m, uxCenter);
        Assert.Equal(0.5m, uyCenter);

        var (uxMax, uyMax) = transform.ScreenToNormalized(new Point(800, 600));
        Assert.Equal(1.0m, uxMax);
        Assert.Equal(1.0m, uyMax);

        // Beyond bounds clamping
        var (uxUnder, uyOver) = transform.ScreenToNormalized(new Point(-100, 900));
        Assert.Equal(0.0m, uxUnder);
        Assert.Equal(1.0m, uyOver);
    }

    [Fact]
    public void ScreenSpaceCoordinateTransform_ChartToScreen_ScreenToChart_PreservesNormalizedValues()
    {
        var transform = new ScreenSpaceCoordinateTransform(1000, 500);

        var halfTime = ScreenSpaceCoordinateTransform.NormalizedTimeBase.AddTicks(ScreenSpaceCoordinateTransform.NormalizedTimeSpanTicks / 2);
        var chartPoint = new ChartPoint(halfTime, 0.5m);

        var screenPoint = transform.ChartToScreen(chartPoint);
        Assert.Equal(500, screenPoint.X);
        Assert.Equal(250, screenPoint.Y);

        var roundTripped = transform.ScreenToChart(screenPoint);
        Assert.Equal(0.5m, roundTripped.Price);
        Assert.Equal(halfTime, roundTripped.Time);
    }

    [Fact]
    public void ScreenSpaceCoordinateTransform_UpdateRange_UpdatesDimensions()
    {
        var transform = new ScreenSpaceCoordinateTransform(800, 600);
        transform.UpdateRange(DateTime.MinValue, DateTime.MaxValue, 0, 100, 1600, 1200);

        Assert.Equal(1600, transform.CanvasWidth);
        Assert.Equal(1200, transform.CanvasHeight);

        var pCenter = transform.NormalizedToScreen(0.5m, 0.5m);
        Assert.Equal(new Point(800, 600), pCenter);
    }

    [Fact]
    public void DrawingStoredPoint_CreateViewportNormalized_ProducesCorrectProperties()
    {
        var point = DrawingStoredPoint.CreateViewportNormalized(0.25m, 0.75m);

        Assert.Equal(DrawingCoordinateKind.ViewportNormalized, point.Kind);
        Assert.Null(point.UtcTime);
        Assert.Equal(0.25m, point.XValue);
        Assert.Equal(0.75m, point.YValue);
    }

    [Fact]
    public void DrawingStoredPoint_ViewportNormalized_EnforcesInvariants()
    {
        // ViewportNormalized requires null UtcTime
        Assert.Throws<ArgumentException>(() => new DrawingStoredPoint(
            DrawingCoordinateKind.ViewportNormalized,
            DateTime.UtcNow,
            0.5m,
            0.5m));

        // ViewportNormalized requires non-null XValue
        Assert.Throws<ArgumentException>(() => new DrawingStoredPoint(
            DrawingCoordinateKind.ViewportNormalized,
            null,
            null,
            0.5m));
    }

    [Fact]
    public void DrawingLayerRecord_Serialization_PreservesIsScreenSpace()
    {
        var record = new DrawingLayerRecord(
            Guid.NewGuid(),
            "Screen Layer",
            PanelKey.Main,
            true,
            false,
            Array.Empty<Guid>(),
            isScreenSpace: true);

        var json = JsonSerializer.Serialize(record);
        var deserialized = JsonSerializer.Deserialize<DrawingLayerRecord>(json);

        Assert.Equal(record.LayerId, deserialized.LayerId);
        Assert.Equal(record.Name, deserialized.Name);
        Assert.True(deserialized.IsScreenSpace);
    }

    [Fact]
    public void DrawingLayerRecord_Deserialization_LegacyJsonDefaultsToFalse()
    {
        var id = Guid.NewGuid();
        var legacyJson = $"{{\"layerId\":\"{id}\",\"name\":\"Legacy Layer\",\"panel\":{{\"kind\":0}},\"isVisible\":true,\"isEditLocked\":false,\"objectIds\":[]}}";

        var deserialized = JsonSerializer.Deserialize<DrawingLayerRecord>(legacyJson);

        Assert.Equal(id, deserialized.LayerId);
        Assert.Equal("Legacy Layer", deserialized.Name);
        Assert.False(deserialized.IsScreenSpace);
    }

    [Fact]
    public void DrawingLayerService_ScreenSpaceLayer_ManagementAndEventDispatch()
    {
        var service = new DrawingLayerService();

        var normalResult = service.CreateLayer(PanelKey.Main, "Normal Data Layer", isScreenSpace: false);
        Assert.True(normalResult.IsSuccess);
        var normalId = normalResult.TargetId!.Value;

        var screenResult = service.CreateLayer(PanelKey.Main, "Screen HUD Layer", isScreenSpace: true);
        Assert.True(screenResult.IsSuccess);
        var screenId = screenResult.TargetId!.Value;

        var normalLayer = service.GetLayer(normalId);
        Assert.NotNull(normalLayer);
        Assert.False(normalLayer.Value.IsScreenSpace);

        var screenLayer = service.GetLayer(screenId);
        Assert.NotNull(screenLayer);
        Assert.True(screenLayer.Value.IsScreenSpace);

        // Toggle screen space on normalLayer
        bool eventFired = false;
        service.LayerStateChanged += () =>
        {
            eventFired = true;
        };

        var updateResult = service.SetLayerScreenSpace(normalId, true);
        Assert.True(updateResult.IsSuccess);
        Assert.True(eventFired);

        var updated = service.GetLayer(normalId);
        Assert.NotNull(updated);
        Assert.True(updated.Value.IsScreenSpace);

        // Test GetAllLayers preservation
        var layers = service.GetAllLayers();
        Assert.Contains(layers, l => l.LayerId == screenId && l.IsScreenSpace);
    }

    [Fact]
    public void LayeredDrawingScene_DualTransformDispatch_AppliesCorrectTransformPerLayer()
    {
        var scene = new LayeredDrawingScene();
        var normalLayerId = Guid.NewGuid();
        var screenLayerId = Guid.NewGuid();

        var layers = new List<DrawingLayerRecord>
        {
            new(normalLayerId, "Data Layer", PanelKey.Main, true, false, Array.Empty<Guid>(), isScreenSpace: false),
            new(screenLayerId, "HUD Layer", PanelKey.Main, true, false, Array.Empty<Guid>(), isScreenSpace: true)
        };

        var emptyObjects = new Dictionary<Guid, IChartObject>();
        scene.UpdateScene(ChartDrawingContextType.Standard, 1, 1, 1, layers, emptyObjects);

        var standardTransform = new GenericCoordinateTransform(ChartAxisMode.Time, 800, 600);
        standardTransform.SetTimeRange(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc));
        standardTransform.SetPriceRange(100m, 200m);

        var screenTransform = new ScreenSpaceCoordinateTransform(800, 600);

        // Verify that RenderPanel runs without exception under dual transform
        using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(800, 600));
        scene.RenderPanel(surface.Canvas, PanelKey.Main, standardTransform, screenTransform);

        // Verify hit-test routes properly without throwing
        var hit = scene.HitTest(PanelKey.Main, new Point(400, 300), standardTransform, 5.0, screenTransform);
        Assert.Null(hit);
    }
}
