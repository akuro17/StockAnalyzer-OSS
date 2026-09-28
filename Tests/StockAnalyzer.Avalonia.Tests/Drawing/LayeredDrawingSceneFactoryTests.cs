using System;
using System.Collections.Generic;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using StockAnalyzer.Core.Models.Indicators;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public class LayeredDrawingSceneFactoryTests
{
    private static TrendLineObject CreateTrendLine() => new(
        new ChartPoint(new DateTime(2024, 1, 1), 100m),
        new ChartPoint(new DateTime(2024, 1, 10), 120m));

    [Fact]
    public void TryBuildSnapshotScene_NullArguments_Throw()
    {
        var manager = new ChartObjectManager();
        var layers = new DrawingLayerService();

        Assert.Throws<ArgumentNullException>(() =>
            LayeredDrawingSceneFactory.TryBuildSnapshotScene(null!, layers, null, true, out _, out _));
        Assert.Throws<ArgumentNullException>(() =>
            LayeredDrawingSceneFactory.TryBuildSnapshotScene(manager, null!, null, true, out _, out _));
    }

    [Fact]
    public void TryBuildSnapshotScene_NoObjects_ReturnsTrueWithEmptyScene()
    {
        var manager = new ChartObjectManager();
        var layers = new DrawingLayerService();

        var ok = LayeredDrawingSceneFactory.TryBuildSnapshotScene(manager, layers, null, true, out var scene, out var unregistered);

        Assert.True(ok);
        Assert.NotNull(scene);
        Assert.Equal(0, unregistered);
    }

    [Fact]
    public void TryBuildSnapshotScene_UnregisteredObject_ReturnsFalse_AndDoesNotMutateLayerService()
    {
        var manager = new ChartObjectManager();
        var layers = new DrawingLayerService();
        var line = CreateTrendLine();
        manager.AddObject(line);
        long revisionBefore = layers.Revision;

        var ok = LayeredDrawingSceneFactory.TryBuildSnapshotScene(manager, layers, null, true, out var scene, out var unregistered);

        Assert.False(ok);
        Assert.Null(scene);
        Assert.Equal(1, unregistered);
        Assert.Equal(revisionBefore, layers.Revision);
        Assert.False(layers.IsObjectRegistered(line.Id));
    }

    [Fact]
    public void TryBuildSnapshotScene_AllRegistered_ReturnsTrue_AndSceneContainsMainPanelObject()
    {
        var manager = new ChartObjectManager();
        var layers = new DrawingLayerService();
        var line = CreateTrendLine();
        manager.AddObject(line);
        layers.AddObjectToLayer(line.Id, PanelKey.Main);

        var ok = LayeredDrawingSceneFactory.TryBuildSnapshotScene(manager, layers, null, true, out var scene, out var unregistered);

        Assert.True(ok);
        Assert.NotNull(scene);
        Assert.Equal(0, unregistered);
        Assert.Equal(layers.Revision, scene!.CachedLayerRevision);
    }

    [Fact]
    public void TryBuildSnapshotScene_ObjectInAbsentSubPanel_IsNotDrawn()
    {
        var manager = new ChartObjectManager();
        var layers = new DrawingLayerService();
        var line = CreateTrendLine();
        manager.AddObject(line);
        var absentPanel = PanelKey.Indicator("rsi-absent");
        layers.AddObjectToLayer(line.Id, absentPanel);

        var settings = new List<CoreIndicatorSettings>();
        var ok = LayeredDrawingSceneFactory.TryBuildSnapshotScene(manager, layers, settings, true, out var scene, out _);

        Assert.True(ok);
        Assert.NotNull(scene);
        // Absent panel is excluded from the scene: hit-testing that panel finds nothing.
        var transform = new GenericCoordinateTransform(ChartAxisMode.Time, 800, 600);
        Assert.Null(scene!.HitTest(absentPanel, new global::Avalonia.Point(0, 0), transform));
    }
}
