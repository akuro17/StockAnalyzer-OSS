using System;
using System.Collections.Generic;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models.Drawing;

namespace StockAnalyzer.Avalonia.Services.Drawing;

/// <summary>
/// Service managing independent drawing layers across all chart panels per S06.
/// Enforces layer hierarchy invariants (1 object in 1 layer, 1 layer in 1 panel, edit lock, visibility).
/// </summary>
public interface IDrawingLayerService
{
    /// <summary>
    /// Fired whenever any layer is added, removed, reordered, or its properties are mutated.
    /// </summary>
    event Action? LayerStateChanged;

    /// <summary>
    /// Monotonically increasing revision counter incremented on every layer mutation.
    /// Used by rendering caches to detect changes.
    /// </summary>
    long Revision { get; }

    /// <summary>
    /// Creates a new layer in the specified panel and makes it the active layer for that panel.
    /// </summary>
    DrawingCommandResult CreateLayer(PanelKey panel, string name);

    /// <summary>
    /// Deletes the specified layer. Rejects deletion if it is the sole remaining layer for the panel,
    /// or if it contains locked objects, or if the layer itself is edit-locked.
    /// Returns <see cref="DrawingCommandStatus.ConfirmationRequired"/> when confirmed is false and the layer is non-empty.
    /// </summary>
    DrawingCommandResult DeleteLayer(Guid layerId, bool confirmed = false, Func<Guid, bool>? isObjectLocked = null);

    /// <summary>
    /// Renames the specified layer.
    /// </summary>
    DrawingCommandResult RenameLayer(Guid layerId, string name);

    /// <summary>
    /// Toggles the visibility of the specified layer without overwriting child object visibility flags.
    /// </summary>
    DrawingCommandResult SetLayerVisibility(Guid layerId, bool visible);

    /// <summary>
    /// Toggles the edit-lock state of the specified layer. Unlocking is always permitted even if currently locked.
    /// </summary>
    DrawingCommandResult SetLayerEditLock(Guid layerId, bool locked);

    /// <summary>
    /// Moves a layer up (+1) or down (-1) within its owning panel. Cross-panel layer moves are prohibited.
    /// </summary>
    DrawingCommandResult MoveLayer(Guid layerId, int delta);

    /// <summary>
    /// Moves an object to a target layer within the same panel. Cross-panel moves are rejected.
    /// </summary>
    DrawingCommandResult MoveObjectToLayer(Guid objectId, Guid targetLayerId);

    /// <summary>
    /// Moves an object forward (+1) or backward (-1) within its containing layer.
    /// </summary>
    DrawingCommandResult MoveObjectWithinLayer(Guid objectId, int delta);

    /// <summary>
    /// Adds an object to the specified layer (or active layer of the panel if layerId is omitted).
    /// </summary>
    DrawingCommandResult AddObjectToLayer(Guid objectId, PanelKey panel, Guid? targetLayerId = null);

    /// <summary>
    /// Removes an object from its containing layer.
    /// </summary>
    DrawingCommandResult RemoveObjectFromLayer(Guid objectId);

    /// <summary>
    /// Checks whether the specified object ID is registered in any layer without allocating a record.
    /// </summary>
    bool IsObjectRegistered(Guid objectId);

    /// <summary>
    /// Gets the layer containing the specified object ID, or null if not registered.
    /// </summary>
    DrawingLayerRecord? GetLayerForObject(Guid objectId);

    /// <summary>
    /// Gets layer metadata by layer ID, or null if not found.
    /// </summary>
    DrawingLayerRecord? GetLayer(Guid layerId);

    /// <summary>
    /// Gets all layers belonging to the specified panel in ascending model order (index 0 = bottom-most).
    /// </summary>
    IReadOnlyList<DrawingLayerRecord> GetLayersForPanel(PanelKey panel);

    /// <summary>
    /// Gets all layers across all panels.
    /// </summary>
    IReadOnlyList<DrawingLayerRecord> GetAllLayers();

    /// <summary>
    /// Gets the active layer ID for the specified panel, or null if none is selected.
    /// </summary>
    Guid? GetActiveLayerId(PanelKey panel);

    /// <summary>
    /// Sets the active layer for the specified panel.
    /// </summary>
    void SetActiveLayerId(PanelKey panel, Guid layerId);

    /// <summary>
    /// Ensures that the specified panel has at least one default layer.
    /// </summary>
    DrawingLayerRecord EnsureDefaultLayer(PanelKey panel);

    /// <summary>
    /// Initializes layer state from persistent records (e.g. loaded from document).
    /// </summary>
    void InitializeLayers(IReadOnlyList<DrawingLayerRecord>? layers);
}
