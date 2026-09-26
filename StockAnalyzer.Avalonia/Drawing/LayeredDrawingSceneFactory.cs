using System;
using System.Collections.Generic;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Builds standalone <see cref="LayeredDrawingScene"/> snapshots for off-screen consumers (e.g. image export)
/// without mutating the live layer document.
/// </summary>
public static class LayeredDrawingSceneFactory
{
    /// <summary>
    /// Builds a fresh scene from the current layer document and drawing objects.
    /// Returns false (with <paramref name="scene"/> = null) when any object is not registered in a layer,
    /// because registering would mutate <paramref name="layerService"/>; callers must fall back to the legacy path.
    /// </summary>
    public static bool TryBuildSnapshotScene(
        ChartObjectManager objectManager,
        IDrawingLayerService layerService,
        IReadOnlyList<CoreIndicatorSettings>? indicatorSettings,
        bool isSubWindowVisible,
        out LayeredDrawingScene? scene,
        out int unregisteredObjectCount)
    {
        if (objectManager == null) throw new ArgumentNullException(nameof(objectManager));
        if (layerService == null) throw new ArgumentNullException(nameof(layerService));

        scene = null;
        unregisteredObjectCount = 0;

        var objects = objectManager.Objects;
        for (int i = 0; i < objects.Count; i++)
        {
            if (!layerService.IsObjectRegistered(objects[i].Id))
            {
                unregisteredObjectCount++;
            }
        }

        if (unregisteredObjectCount > 0)
        {
            return false;
        }

        var objectsById = new Dictionary<Guid, IChartObject>(objects.Count);
        for (int i = 0; i < objects.Count; i++)
        {
            objectsById[objects[i].Id] = objects[i];
        }

        var built = new LayeredDrawingScene();
        built.UpdateScene(
            objectManager.CurrentContext,
            layerService.Revision,
            objectManager.Revision,
            indicatorSettings?.Count ?? 0,
            layerService.GetAllLayers(),
            objectsById,
            panel => DrawingPanelResolver.IsPanelPresent(panel, indicatorSettings, isSubWindowVisible));

        scene = built;
        return true;
    }
}
