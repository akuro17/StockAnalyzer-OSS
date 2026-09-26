using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models.Drawing;

namespace StockAnalyzer.Avalonia.Services.Drawing;

/// <summary>
/// Default implementation of <see cref="IDrawingLayerService"/>.
/// Manages independent drawing layers across all panels per S06.
/// Guarantees that 1 object belongs to exactly 1 layer and 1 layer belongs to exactly 1 panel.
/// </summary>
public sealed class DrawingLayerService : IDrawingLayerService
{
    private sealed class LayerEntry
    {
        public Guid LayerId { get; set; }
        public string Name { get; set; } = string.Empty;
        public PanelKey Panel { get; set; }
        public bool IsVisible { get; set; } = true;
        public bool IsEditLocked { get; set; } = false;
        public List<Guid> ObjectIds { get; } = new();

        public DrawingLayerRecord ToRecord() =>
            new(LayerId, Name, Panel, IsVisible, IsEditLocked, ObjectIds);
    }

    private readonly object _syncLock = new();
    private readonly Dictionary<Guid, LayerEntry> _layersById = new();
    private readonly Dictionary<PanelKey, List<Guid>> _panelLayerOrder = new();
    private readonly Dictionary<Guid, Guid> _objectToLayerMap = new();
    private readonly Dictionary<PanelKey, Guid> _activeLayers = new();

    public event Action? LayerStateChanged;

    public long Revision { get; private set; } = 1;

    public DrawingLayerService()
    {
        // Automatically seed default layer for Main panel
        EnsureDefaultLayer(PanelKey.Main);
    }

    public DrawingCommandResult CreateLayer(PanelKey panel, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return DrawingCommandResult.Fail(DrawingCommandStatus.InvalidArgument, "Layer name must not be empty or whitespace.");
        }

        lock (_syncLock)
        {
            var layerId = Guid.NewGuid();
            var entry = new LayerEntry
            {
                LayerId = layerId,
                Name = name.Trim(),
                Panel = panel,
                IsVisible = true,
                IsEditLocked = false
            };

            _layersById[layerId] = entry;

            if (!_panelLayerOrder.TryGetValue(panel, out var orderList))
            {
                orderList = new List<Guid>();
                _panelLayerOrder[panel] = orderList;
            }

            // Append to top of model order (highest index = top-most)
            orderList.Add(layerId);
            _activeLayers[panel] = layerId;

            NotifyChanged();
            return DrawingCommandResult.Success(layerId);
        }
    }

    public DrawingCommandResult DeleteLayer(Guid layerId, bool confirmed = false, Func<Guid, bool>? isObjectLocked = null)
    {
        lock (_syncLock)
        {
            if (!_layersById.TryGetValue(layerId, out var layer))
            {
                return DrawingCommandResult.Fail(DrawingCommandStatus.NotFound, $"Layer {layerId} not found.");
            }

            if (layer.IsEditLocked)
            {
                return DrawingCommandResult.Fail(DrawingCommandStatus.Locked, "Layer is edit-locked.");
            }

            var orderList = _panelLayerOrder[layer.Panel];
            if (layer.Panel.Kind == PanelKind.Main && orderList.Count <= 1)
            {
                return DrawingCommandResult.Fail(DrawingCommandStatus.InvalidState, "Cannot delete the sole remaining layer of the main chart panel.");
            }

            if (layer.ObjectIds.Count > 0)
            {
                if (isObjectLocked != null && layer.ObjectIds.Any(isObjectLocked))
                {
                    return DrawingCommandResult.Fail(DrawingCommandStatus.Locked, "Cannot delete layer containing deletion-protected objects.");
                }

                if (!confirmed)
                {
                    return DrawingCommandResult.Fail(
                        DrawingCommandStatus.ConfirmationRequired,
                        "Deleting a non-empty layer requires user confirmation.",
                        layerId);
                }
            }

            // Perform deletion
            foreach (var objectId in layer.ObjectIds)
            {
                _objectToLayerMap.Remove(objectId);
            }

            orderList.Remove(layerId);
            _layersById.Remove(layerId);

            if (orderList.Count == 0)
            {
                _panelLayerOrder.Remove(layer.Panel);
                _activeLayers.Remove(layer.Panel);
            }
            else if (_activeLayers.TryGetValue(layer.Panel, out var active) && active == layerId)
            {
                _activeLayers[layer.Panel] = orderList[0];
            }

            NotifyChanged();
            return DrawingCommandResult.Success(layerId);
        }
    }

    public DrawingCommandResult RenameLayer(Guid layerId, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return DrawingCommandResult.Fail(DrawingCommandStatus.InvalidArgument, "Layer name must not be empty or whitespace.");
        }

        lock (_syncLock)
        {
            if (!_layersById.TryGetValue(layerId, out var layer))
            {
                return DrawingCommandResult.Fail(DrawingCommandStatus.NotFound, $"Layer {layerId} not found.");
            }

            if (layer.IsEditLocked)
            {
                return DrawingCommandResult.Fail(DrawingCommandStatus.Locked, "Layer is edit-locked.");
            }

            var trimmed = name.Trim();
            if (layer.Name == trimmed)
            {
                return DrawingCommandResult.NoChange();
            }

            layer.Name = trimmed;
            NotifyChanged();
            return DrawingCommandResult.Success(layerId);
        }
    }

    public DrawingCommandResult SetLayerVisibility(Guid layerId, bool visible)
    {
        lock (_syncLock)
        {
            if (!_layersById.TryGetValue(layerId, out var layer))
            {
                return DrawingCommandResult.Fail(DrawingCommandStatus.NotFound, $"Layer {layerId} not found.");
            }

            if (layer.IsEditLocked)
            {
                return DrawingCommandResult.Fail(DrawingCommandStatus.Locked, "Layer is edit-locked.");
            }

            if (layer.IsVisible == visible)
            {
                return DrawingCommandResult.NoChange();
            }

            layer.IsVisible = visible;
            NotifyChanged();
            return DrawingCommandResult.Success(layerId);
        }
    }

    public DrawingCommandResult SetLayerEditLock(Guid layerId, bool locked)
    {
        lock (_syncLock)
        {
            if (!_layersById.TryGetValue(layerId, out var layer))
            {
                return DrawingCommandResult.Fail(DrawingCommandStatus.NotFound, $"Layer {layerId} not found.");
            }

            if (layer.IsEditLocked == locked)
            {
                return DrawingCommandResult.NoChange();
            }

            // Unlocking or locking is always permitted on the layer itself
            layer.IsEditLocked = locked;
            NotifyChanged();
            return DrawingCommandResult.Success(layerId);
        }
    }

    public DrawingCommandResult MoveLayer(Guid layerId, int delta)
    {
        if (delta != -1 && delta != 1)
        {
            return DrawingCommandResult.Fail(DrawingCommandStatus.InvalidArgument, "Delta must be strictly -1 or +1.");
        }

        lock (_syncLock)
        {
            if (!_layersById.TryGetValue(layerId, out var layer))
            {
                return DrawingCommandResult.Fail(DrawingCommandStatus.NotFound, $"Layer {layerId} not found.");
            }

            if (layer.IsEditLocked)
            {
                return DrawingCommandResult.Fail(DrawingCommandStatus.Locked, "Layer is edit-locked.");
            }

            var orderList = _panelLayerOrder[layer.Panel];
            int currentIndex = orderList.IndexOf(layerId);
            if (currentIndex < 0)
            {
                return DrawingCommandResult.Fail(DrawingCommandStatus.NotFound);
            }

            int targetIndex = currentIndex + delta;
            if (targetIndex < 0 || targetIndex >= orderList.Count)
            {
                return DrawingCommandResult.NoChange();
            }

            orderList.RemoveAt(currentIndex);
            orderList.Insert(targetIndex, layerId);

            NotifyChanged();
            return DrawingCommandResult.Success(layerId);
        }
    }

    public DrawingCommandResult MoveObjectToLayer(Guid objectId, Guid targetLayerId)
    {
        lock (_syncLock)
        {
            if (!_objectToLayerMap.TryGetValue(objectId, out var currentLayerId))
            {
                return DrawingCommandResult.Fail(DrawingCommandStatus.NotFound, $"Object {objectId} is not registered in any layer.");
            }

            if (currentLayerId == targetLayerId)
            {
                return DrawingCommandResult.NoChange();
            }

            if (!_layersById.TryGetValue(currentLayerId, out var currentLayer))
            {
                return DrawingCommandResult.Fail(DrawingCommandStatus.NotFound, "Current layer not found.");
            }

            if (!_layersById.TryGetValue(targetLayerId, out var targetLayer))
            {
                return DrawingCommandResult.Fail(DrawingCommandStatus.NotFound, "Target layer not found.");
            }

            if (!currentLayer.Panel.Equals(targetLayer.Panel))
            {
                return DrawingCommandResult.Fail(DrawingCommandStatus.CrossPanel, "Moving objects across different panels is prohibited.");
            }

            if (currentLayer.IsEditLocked || targetLayer.IsEditLocked)
            {
                return DrawingCommandResult.Fail(DrawingCommandStatus.Locked, "Source or target layer is edit-locked.");
            }

            currentLayer.ObjectIds.Remove(objectId);
            targetLayer.ObjectIds.Add(objectId);
            _objectToLayerMap[objectId] = targetLayerId;

            NotifyChanged();
            return DrawingCommandResult.Success(objectId);
        }
    }

    public DrawingCommandResult MoveObjectWithinLayer(Guid objectId, int delta)
    {
        if (delta != -1 && delta != 1)
        {
            return DrawingCommandResult.Fail(DrawingCommandStatus.InvalidArgument, "Delta must be strictly -1 or +1.");
        }

        lock (_syncLock)
        {
            if (!_objectToLayerMap.TryGetValue(objectId, out var layerId) ||
                !_layersById.TryGetValue(layerId, out var layer))
            {
                return DrawingCommandResult.Fail(DrawingCommandStatus.NotFound, $"Object {objectId} is not registered in any layer.");
            }

            if (layer.IsEditLocked)
            {
                return DrawingCommandResult.Fail(DrawingCommandStatus.Locked, "Layer is edit-locked.");
            }

            int currentIndex = layer.ObjectIds.IndexOf(objectId);
            if (currentIndex < 0)
            {
                return DrawingCommandResult.Fail(DrawingCommandStatus.NotFound);
            }

            int targetIndex = currentIndex + delta;
            if (targetIndex < 0 || targetIndex >= layer.ObjectIds.Count)
            {
                return DrawingCommandResult.NoChange();
            }

            layer.ObjectIds.RemoveAt(currentIndex);
            layer.ObjectIds.Insert(targetIndex, objectId);

            NotifyChanged();
            return DrawingCommandResult.Success(objectId);
        }
    }

    public DrawingCommandResult AddObjectToLayer(Guid objectId, PanelKey panel, Guid? targetLayerId = null)
    {
        lock (_syncLock)
        {
            LayerEntry layer;
            if (targetLayerId.HasValue)
            {
                if (!_layersById.TryGetValue(targetLayerId.Value, out var target))
                {
                    return DrawingCommandResult.Fail(DrawingCommandStatus.NotFound, "Target layer not found.");
                }

                if (!target.Panel.Equals(panel))
                {
                    return DrawingCommandResult.Fail(DrawingCommandStatus.CrossPanel, "Target layer belongs to a different panel.");
                }

                layer = target;
            }
            else
            {
                var activeId = GetActiveLayerId(panel) ?? EnsureDefaultLayer(panel).LayerId;
                layer = _layersById[activeId];
            }

            if (layer.IsEditLocked)
            {
                return DrawingCommandResult.Fail(DrawingCommandStatus.Locked, "Active or target layer is edit-locked.");
            }

            // Remove from existing layer if already tracked elsewhere (enforces 1-object-in-1-layer)
            if (_objectToLayerMap.TryGetValue(objectId, out var existingLayerId))
            {
                if (existingLayerId == layer.LayerId)
                {
                    return DrawingCommandResult.NoChange();
                }

                if (_layersById.TryGetValue(existingLayerId, out var existingLayer))
                {
                    if (existingLayer.IsEditLocked)
                    {
                        return DrawingCommandResult.Fail(DrawingCommandStatus.Locked, "Source layer is edit-locked.");
                    }

                    if (!existingLayer.Panel.Equals(layer.Panel))
                    {
                        return DrawingCommandResult.Fail(DrawingCommandStatus.CrossPanel, "Moving objects across different panels is prohibited.");
                    }

                    existingLayer.ObjectIds.Remove(objectId);
                }
            }

            layer.ObjectIds.Add(objectId);
            _objectToLayerMap[objectId] = layer.LayerId;

            NotifyChanged();
            return DrawingCommandResult.Success(objectId);
        }
    }

    public DrawingCommandResult RemoveObjectFromLayer(Guid objectId)
    {
        lock (_syncLock)
        {
            if (_objectToLayerMap.TryGetValue(objectId, out var layerId) &&
                _layersById.TryGetValue(layerId, out var layer))
            {
                layer.ObjectIds.Remove(objectId);
                _objectToLayerMap.Remove(objectId);
                NotifyChanged();
                return DrawingCommandResult.Success(objectId);
            }

            return DrawingCommandResult.NoChange();
        }
    }

    public bool IsObjectRegistered(Guid objectId)
    {
        lock (_syncLock)
        {
            return _objectToLayerMap.ContainsKey(objectId);
        }
    }

    public DrawingLayerRecord? GetLayerForObject(Guid objectId)
    {
        lock (_syncLock)
        {
            if (_objectToLayerMap.TryGetValue(objectId, out var layerId) &&
                _layersById.TryGetValue(layerId, out var layer))
            {
                return layer.ToRecord();
            }
            return null;
        }
    }

    public DrawingLayerRecord? GetLayer(Guid layerId)
    {
        lock (_syncLock)
        {
            return _layersById.TryGetValue(layerId, out var layer) ? layer.ToRecord() : null;
        }
    }

    public IReadOnlyList<DrawingLayerRecord> GetLayersForPanel(PanelKey panel)
    {
        lock (_syncLock)
        {
            if (!_panelLayerOrder.TryGetValue(panel, out var orderList) || orderList.Count == 0)
            {
                return Array.Empty<DrawingLayerRecord>();
            }

            var result = new List<DrawingLayerRecord>(orderList.Count);
            foreach (var id in orderList)
            {
                if (_layersById.TryGetValue(id, out var layer))
                {
                    result.Add(layer.ToRecord());
                }
            }
            return result.AsReadOnly();
        }
    }

    public IReadOnlyList<DrawingLayerRecord> GetAllLayers()
    {
        lock (_syncLock)
        {
            var result = new List<DrawingLayerRecord>(_layersById.Count);
            foreach (var (_, orderList) in _panelLayerOrder)
            {
                foreach (var id in orderList)
                {
                    if (_layersById.TryGetValue(id, out var layer))
                    {
                        result.Add(layer.ToRecord());
                    }
                }
            }
            return result.AsReadOnly();
        }
    }

    public Guid? GetActiveLayerId(PanelKey panel)
    {
        lock (_syncLock)
        {
            if (_activeLayers.TryGetValue(panel, out var id) && _layersById.ContainsKey(id))
            {
                return id;
            }

            if (_panelLayerOrder.TryGetValue(panel, out var orderList) && orderList.Count > 0)
            {
                return orderList[0];
            }

            return null;
        }
    }

    public void SetActiveLayerId(PanelKey panel, Guid layerId)
    {
        lock (_syncLock)
        {
            if (_layersById.TryGetValue(layerId, out var layer) && layer.Panel.Equals(panel))
            {
                _activeLayers[panel] = layerId;
            }
        }
    }

    public DrawingLayerRecord EnsureDefaultLayer(PanelKey panel)
    {
        lock (_syncLock)
        {
            if (_panelLayerOrder.TryGetValue(panel, out var orderList) && orderList.Count > 0)
            {
                return _layersById[orderList[0]].ToRecord();
            }

            var layerId = Guid.NewGuid();
            var defaultLayer = new LayerEntry
            {
                LayerId = layerId,
                Name = "Layer 1",
                Panel = panel,
                IsVisible = true,
                IsEditLocked = false
            };

            _layersById[layerId] = defaultLayer;
            _panelLayerOrder[panel] = new List<Guid> { layerId };
            _activeLayers[panel] = layerId;

            return defaultLayer.ToRecord();
        }
    }

    public void InitializeLayers(IReadOnlyList<DrawingLayerRecord>? layers)
    {
        lock (_syncLock)
        {
            var tempLayersById = new Dictionary<Guid, LayerEntry>();
            var tempPanelOrder = new Dictionary<PanelKey, List<Guid>>();
            var tempObjectMap = new Dictionary<Guid, Guid>();

            if (layers != null && layers.Count > 0)
            {
                foreach (var record in layers)
                {
                    if (record.LayerId == Guid.Empty)
                    {
                        throw new ArgumentException("Layer ID must not be empty.", nameof(layers));
                    }
                    if (tempLayersById.ContainsKey(record.LayerId))
                    {
                        throw new ArgumentException($"Duplicate Layer ID {record.LayerId} detected.", nameof(layers));
                    }

                    var entry = new LayerEntry
                    {
                        LayerId = record.LayerId,
                        Name = record.Name ?? "Layer",
                        Panel = record.Panel,
                        IsVisible = record.IsVisible,
                        IsEditLocked = record.IsEditLocked
                    };

                    if (record.ObjectIds != null)
                    {
                        foreach (var oid in record.ObjectIds)
                        {
                            if (oid == Guid.Empty)
                            {
                                throw new ArgumentException($"Layer {record.LayerId} contains empty object ID.", nameof(layers));
                            }
                            if (tempObjectMap.ContainsKey(oid))
                            {
                                throw new ArgumentException($"Object ID {oid} is assigned to multiple layers.", nameof(layers));
                            }

                            entry.ObjectIds.Add(oid);
                            tempObjectMap[oid] = record.LayerId;
                        }
                    }

                    tempLayersById[record.LayerId] = entry;

                    if (!tempPanelOrder.TryGetValue(record.Panel, out var orderList))
                    {
                        orderList = new List<Guid>();
                        tempPanelOrder[record.Panel] = orderList;
                    }
                    orderList.Add(record.LayerId);
                }
            }

            // Atomic state swap - only reached if all validation succeeds
            _layersById.Clear();
            foreach (var (k, v) in tempLayersById) _layersById[k] = v;

            _panelLayerOrder.Clear();
            foreach (var (k, v) in tempPanelOrder) _panelLayerOrder[k] = v;

            _objectToLayerMap.Clear();
            foreach (var (k, v) in tempObjectMap) _objectToLayerMap[k] = v;

            _activeLayers.Clear();

            // Always ensure Main panel has at least one layer
            EnsureDefaultLayer(PanelKey.Main);

            NotifyChanged();
        }
    }

    private void NotifyChanged()
    {
        unchecked
        {
            Revision++;
        }
        LayerStateChanged?.Invoke();
    }
}
