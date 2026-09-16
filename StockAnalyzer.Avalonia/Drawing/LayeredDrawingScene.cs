using System;
using System.Collections.Generic;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Models.Drawing;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Maintains an allocation-free cached flat scene array per panel, synchronized with layer order.
/// Enforces identical object evaluation sequence between forward rendering (0..N-1) and
/// backward hit-testing (N-1..0) per S06 and G4.
/// </summary>
public sealed class LayeredDrawingScene
{
    public readonly struct PanelSceneEntry
    {
        public readonly IChartObject Object;
        public readonly Guid PersistentObjectId;
        public readonly Guid LayerId;
        public readonly bool IsLayerVisible;
        public readonly bool IsLayerEditLocked;

        public PanelSceneEntry(
            IChartObject obj,
            Guid persistentObjectId,
            Guid layerId,
            bool isLayerVisible,
            bool isLayerEditLocked)
        {
            Object = obj;
            PersistentObjectId = persistentObjectId;
            LayerId = layerId;
            IsLayerVisible = isLayerVisible;
            IsLayerEditLocked = isLayerEditLocked;
        }
    }

    private readonly object _cacheLock = new();
    private readonly Dictionary<PanelKey, PanelSceneEntry[]> _panelCache = new();
    private ChartDrawingContextType _cachedContext;
    private long _cachedLayerRevision = -1;
    private long _cachedObjectRevision = -1;
    private long _cachedDocumentRevision = -1;
    private int _cachedPanelMapRevision = -1;

    public long CachedLayerRevision
    {
        get
        {
            lock (_cacheLock) return _cachedLayerRevision;
        }
    }

    public long CachedObjectRevision
    {
        get
        {
            lock (_cacheLock) return _cachedObjectRevision;
        }
    }

    public long CachedDocumentRevision
    {
        get
        {
            lock (_cacheLock) return _cachedDocumentRevision;
        }
    }

    public int CachedPanelMapRevision
    {
        get
        {
            lock (_cacheLock) return _cachedPanelMapRevision;
        }
    }

    /// <summary>
    /// Explicitly invalidates the cached scene arrays to force rebuilding on the next update.
    /// </summary>
    public void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _cachedLayerRevision = -1;
            _cachedObjectRevision = -1;
            _cachedDocumentRevision = -1;
            _cachedPanelMapRevision = -1;
            _panelCache.Clear();
        }
    }

    /// <summary>
    /// Rebuilds the cached flat scene array per panel if any of the revision identifiers have changed.
    /// Preserves strict ascending order (layers bottom-to-top, objects bottom-to-top).
    /// Backward-compatible overload delegating to the separate layer and object revision version.
    /// </summary>
    public void UpdateScene(
        ChartDrawingContextType contextType,
        long documentRevision,
        int panelMapRevision,
        IReadOnlyList<DrawingLayerRecord> layers,
        IReadOnlyDictionary<Guid, IChartObject> objectsByPersistentId,
        Func<PanelKey, bool>? isPanelPresent = null)
        => UpdateScene(contextType, documentRevision, documentRevision, panelMapRevision, layers, objectsByPersistentId, isPanelPresent);

    /// <summary>
    /// Rebuilds the cached flat scene array per panel if any of the revision identifiers have changed.
    /// Preserves strict ascending order (layers bottom-to-top, objects bottom-to-top).
    /// </summary>
    public void UpdateScene(
        ChartDrawingContextType contextType,
        long layerRevision,
        long objectRevision,
        int panelMapRevision,
        IReadOnlyList<DrawingLayerRecord> layers,
        IReadOnlyDictionary<Guid, IChartObject> objectsByPersistentId,
        Func<PanelKey, bool>? isPanelPresent = null)
    {
        if (layers == null) throw new ArgumentNullException(nameof(layers));
        if (objectsByPersistentId == null) throw new ArgumentNullException(nameof(objectsByPersistentId));

        lock (_cacheLock)
        {
            if (_cachedContext == contextType &&
                _cachedLayerRevision == layerRevision &&
                _cachedObjectRevision == objectRevision &&
                _cachedPanelMapRevision == panelMapRevision)
            {
                return;
            }

            _panelCache.Clear();

            // Group layers by panel in ascending model order
            var layersByPanel = new Dictionary<PanelKey, List<DrawingLayerRecord>>();
            for (int i = 0; i < layers.Count; i++)
            {
                var layer = layers[i];
                if (!layersByPanel.TryGetValue(layer.Panel, out var list))
                {
                    list = new List<DrawingLayerRecord>();
                    layersByPanel[layer.Panel] = list;
                }
                list.Add(layer);
            }

            foreach (var (panel, panelLayers) in layersByPanel)
            {
                if (isPanelPresent != null && !isPanelPresent(panel))
                {
                    continue;
                }

                int totalCount = 0;
                for (int i = 0; i < panelLayers.Count; i++)
                {
                    totalCount += panelLayers[i].ObjectIds.Count;
                }

                var entries = new List<PanelSceneEntry>(totalCount);
                for (int i = 0; i < panelLayers.Count; i++)
                {
                    var layer = panelLayers[i];
                    for (int j = 0; j < layer.ObjectIds.Count; j++)
                    {
                        var persistentId = layer.ObjectIds[j];
                        if (objectsByPersistentId.TryGetValue(persistentId, out var obj))
                        {
                            entries.Add(new PanelSceneEntry(
                                obj,
                                persistentId,
                                layer.LayerId,
                                layer.IsVisible,
                                layer.IsEditLocked));
                        }
                    }
                }

                _panelCache[panel] = entries.ToArray();
            }

            _cachedContext = contextType;
            _cachedLayerRevision = layerRevision;
            _cachedObjectRevision = objectRevision;
            _cachedDocumentRevision = layerRevision ^ (objectRevision << 16);
            _cachedPanelMapRevision = panelMapRevision;
        }
    }

    /// <summary>
    /// Renders all visible objects belonging to the specified panel in ascending order (forward scan).
    /// Zero-allocation on warm paths.
    /// </summary>
    public void RenderPanel(SKCanvas canvas, PanelKey panel, ICoordinateTransform transform)
    {
        if (canvas == null || transform == null) return;

        PanelSceneEntry[]? entries;
        lock (_cacheLock)
        {
            if (!_panelCache.TryGetValue(panel, out entries)) return;
        }

        for (int i = 0; i < entries.Length; i++)
        {
            ref readonly var entry = ref entries[i];
            if (entry.IsLayerVisible && entry.Object.IsVisible)
            {
                entry.Object.Render(canvas, transform);
            }
        }
    }

    /// <summary>
    /// Evaluates hit-testing in descending order (top-to-bottom backward scan) using the exact same
    /// sequence as <see cref="RenderPanel"/> to guarantee hit-selection matches the visual front-most object.
    /// </summary>
    public Guid? HitTest(
        PanelKey panel,
        global::Avalonia.Point localPoint,
        ICoordinateTransform transform,
        double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (transform == null) return null;

        PanelSceneEntry[]? entries;
        lock (_cacheLock)
        {
            if (!_panelCache.TryGetValue(panel, out entries)) return null;
        }

        for (int i = entries.Length - 1; i >= 0; i--)
        {
            ref readonly var entry = ref entries[i];
            if (entry.IsLayerVisible && entry.Object.IsVisible)
            {
                if (entry.Object.HitTest(localPoint, transform, tolerance))
                {
                    return entry.PersistentObjectId;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Evaluates the top-most hit object for Eraser operation per U11 and G4.
    /// If the hit object is deletion-protected (<see cref="IChartObject.IsLocked"/>) or its layer is edit-locked,
    /// stops immediately without deleting and without penetrating to underlying objects.
    /// </summary>
    public bool HandleEraserAt(
        PanelKey panel,
        global::Avalonia.Point localPoint,
        ICoordinateTransform transform,
        out Guid? targetPersistentObjectId,
        out bool isProtectedBlocked,
        double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        targetPersistentObjectId = null;
        isProtectedBlocked = false;

        if (transform == null) return false;

        PanelSceneEntry[]? entries;
        lock (_cacheLock)
        {
            if (!_panelCache.TryGetValue(panel, out entries)) return false;
        }

        for (int i = entries.Length - 1; i >= 0; i--)
        {
            ref readonly var entry = ref entries[i];
            if (entry.IsLayerVisible && entry.Object.IsVisible)
            {
                if (entry.Object.HitTest(localPoint, transform, tolerance))
                {
                    // Top-most hit object reached
                    if (entry.Object.IsLocked || entry.IsLayerEditLocked)
                    {
                        // Blocked by deletion protection or layer edit-lock: stop without penetration
                        isProtectedBlocked = true;
                        return false;
                    }

                    targetPersistentObjectId = entry.PersistentObjectId;
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the count of cached objects for the specified panel.
    /// </summary>
    public int GetCachedObjectCount(PanelKey panel)
    {
        lock (_cacheLock)
        {
            return _panelCache.TryGetValue(panel, out var entries) ? entries.Length : 0;
        }
    }
}
