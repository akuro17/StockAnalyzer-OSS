using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Views.Chart;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Defines the context group for drawing objects.
/// Objects are isolated within these groups.
/// </summary>
public enum ChartDrawingContextType
{
    Standard,       // Candle, Bar, HeikinAshi
    Linear,         // Line, Area
    Kagi,
    Renko,
    PointAndFigure,
    ThreeLineBreak,
    ReverseWatch
}

/// <summary>
/// Chart object manager service (manages all drawable objects)
/// capable of switching contexts based on chart type.
/// Guarantees ZeroAllocation rendering/hit testing and strict ZIndex physical alignment (Invariant I-01).
/// </summary>
public class ChartObjectManager
{
    // Context Storage
    private readonly Dictionary<ChartDrawingContextType, List<IChartObject>> _contextStore 
        = new Dictionary<ChartDrawingContextType, List<IChartObject>>();

    // Current Context State
    private List<IChartObject> _currentObjects = new List<IChartObject>();
    private ChartDrawingContextType _currentContext = ChartDrawingContextType.Standard;

    private readonly HashSet<Guid> _selectedIds = new HashSet<Guid>();
    private readonly HashSet<Guid> _lockedIds = new HashSet<Guid>();
    private readonly HashSet<Guid> _hiddenIds = new HashSet<Guid>();
    // Batch notification management
    private int _batchDepth = 0;
    private bool _hasPendingChanged = false;

    public ChartObjectManager()
    {
        // Initialize store
        foreach (ChartDrawingContextType type in Enum.GetValues(typeof(ChartDrawingContextType)))
        {
            _contextStore[type] = new List<IChartObject>();
        }
        _currentObjects = _contextStore[_currentContext];
    }

    /// <summary>All managed objects in current context (read-only)</summary>
    public IReadOnlyList<IChartObject> Objects => _currentObjects.AsReadOnly();

    /// <summary>Currently selected object (first one found in current context)</summary>
    public IChartObject? SelectedObject => _currentObjects.FirstOrDefault(o => o.IsSelected);

    /// <summary>
    /// Whether anything is currently selected. O(1) (backed by _selectedIds, which SwitchContext
    /// clears on every context change), unlike SelectedObject's LINQ scan.
    /// </summary>
    public bool HasSelection => _selectedIds.Count > 0;

    /// <summary>active context type</summary>
    public ChartDrawingContextType CurrentContext => _currentContext;

    /// <summary>
    /// Raised whenever a user-driven mutation changes the current context's
    /// object list or object states (requires disk persistence).
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Raised whenever the object collection is modified, reloaded from persistence, or cleared.
    /// Used by UI consumers (DrawingObjectsViewModel, ChartBaseControl) to sync UI without re-persisting.
    /// </summary>
    public event Action? Synced;

    /// <summary>
    /// Starts a batch update scope, suppressing Changed events until the scope is disposed.
    /// </summary>
    public IDisposable BeginBatch()
    {
        _batchDepth++;
        return new BatchScope(this);
    }

    private void EndBatch()
    {
        _batchDepth--;
        if (_batchDepth <= 0)
        {
            _batchDepth = 0;
            if (_hasPendingChanged)
            {
                _hasPendingChanged = false;
                Changed?.Invoke();
                Synced?.Invoke();
            }
        }
    }

    private void NotifyChanged()
    {
        if (_batchDepth > 0)
        {
            _hasPendingChanged = true;
        }
        else
        {
            Changed?.Invoke();
            Synced?.Invoke();
        }
    }

    private void RecalculateZIndices()
    {
        for (int i = 0; i < _currentObjects.Count; i++)
        {
            _currentObjects[i].ZIndex = i;
        }
    }

    /// <summary>
    /// Switches the active drawing context.
    /// Persists objects of the previous context and loads objects of the new context.
    /// </summary>
    public void SwitchContext(ChartDrawingContextType newContext)
    {
        if (_currentContext == newContext) return;

        // Deselect all before switching to avoid phantom selections
        DeselectAll();

        _currentContext = newContext;
        
        if (!_contextStore.TryGetValue(newContext, out var objects))
        {
            objects = new List<IChartObject>();
            _contextStore[newContext] = objects;
        }
        
        _currentObjects = objects;
        RecalculateZIndices();
        _lockedIds.Clear();
        _hiddenIds.Clear();
        foreach (var obj in _currentObjects)
        {
            if (obj.IsLocked) _lockedIds.Add(obj.Id);
            if (!obj.IsVisible) _hiddenIds.Add(obj.Id);
        }
    }

    /// <summary>Add object to manager (Current Context)</summary>
    public bool AddObject(IChartObject chartObject)
    {
        if (chartObject == null || chartObject.Id == Guid.Empty) return false;
        if (_currentObjects.Any(o => o.Id == chartObject.Id)) return false;

        chartObject.ZIndex = _currentObjects.Count;
        _currentObjects.Add(chartObject);
        NotifyChanged();
        return true;
    }

    /// <summary>Remove object by ID (Current Context). Locked objects cannot be deleted.</summary>
    public bool RemoveObject(Guid id)
    {
        if (id == Guid.Empty) return false;
        int index = _currentObjects.FindIndex(o => o.Id == id);
        if (index < 0) return false;

        if (IsLocked(id)) return false; // ロック中オブジェクトは削除拒否

        _currentObjects.RemoveAt(index);
        _selectedIds.Remove(id);
        _lockedIds.Remove(id);
        _hiddenIds.Remove(id);
        RecalculateZIndices();
        NotifyChanged();
        return true;
    }

    /// <summary>Get object by ID (Current Context)</summary>
    public IChartObject? GetObject(Guid id)
    {
        return _currentObjects.FirstOrDefault(o => o.Id == id);
    }

    /// <summary>
    /// Duplicates the specified drawing object into the current context.
    /// The duplicated object receives a new unique ID, is unlocked, made visible, and selected.
    /// </summary>
    /// <param name="id">The ID of the object to duplicate.</param>
    /// <param name="timeOffset">Optional time offset to shift the duplicated object.</param>
    /// <param name="priceOffset">Optional price offset to shift the duplicated object.</param>
    /// <returns>The newly created clone if successful; otherwise null.</returns>
    public IChartObject? DuplicateObject(Guid id, TimeSpan? timeOffset = null, decimal? priceOffset = null)
    {
        if (id == Guid.Empty) return null;
        var source = GetObject(id);
        if (source == null) return null;

        var concreteType = source.GetType();
        var clone = Serialization.ChartObjectInstanceFactory.Create(concreteType);

        // Copy points
        clone.Points.Clear();
        clone.Points.AddRange(source.Points);

        // Copy properties (except Id, IsSelected, ZIndex, Points)
        foreach (var prop in concreteType.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (prop.Name is "Id" or "IsSelected" or "ZIndex" or "Points") continue;
            if (prop.GetIndexParameters().Length > 0) continue;
            if (!prop.CanRead) continue;
            var setter = prop.GetSetMethod(nonPublic: true);
            if (setter == null) continue;

            try
            {
                var val = prop.GetValue(source);
                setter.Invoke(clone, new[] { val });
            }
            catch
            {
                // Best-effort property copy
            }
        }

        clone.IsVisible = true;
        clone.IsLocked = false;

        // Apply optional offset if specified
        if (timeOffset.HasValue || priceOffset.HasValue)
        {
            clone.Translate(timeOffset ?? TimeSpan.Zero, priceOffset ?? 0m);
        }

        if (AddObject(clone))
        {
            var srcMode = GetMoveAxisMode(id);
            SetMoveAxisMode(clone.Id, srcMode);
            SelectObject(clone.Id);
            return clone;
        }

        return null;
    }

    /// <summary>Gets the 0-based physical Z-Index of the object in the current context.</summary>
    public int GetZIndex(Guid id) => _currentObjects.FindIndex(o => o.Id == id);

    /// <summary>Checks whether the object can be moved one step forward (towards top-most / higher ZIndex).</summary>
    public bool CanBringForward(Guid id)
    {
        int index = _currentObjects.FindIndex(o => o.Id == id);
        return index >= 0 && index < _currentObjects.Count - 1;
    }

    /// <summary>Checks whether the object can be moved one step backward (towards bottom-most / lower ZIndex).</summary>
    public bool CanSendBackward(Guid id)
    {
        int index = _currentObjects.FindIndex(o => o.Id == id);
        return index > 0;
    }

    /// <summary>
    /// Checks whether the object has an explicit per-object movement axis constraint, as opposed to
    /// following the chart-wide default (<see cref="Views.Chart.ChartInteractionController.MoveAxisMode"/>).
    /// Reads directly from <see cref="IChartObject.IsMoveAxisModeExplicit"/> (a real persisted
    /// per-object property, set by <see cref="SetMoveAxisMode"/>), so an explicit choice of XY itself
    /// still counts as explicit and is not confused with "never touched", and this survives a reload
    /// like any other persisted field.
    /// </summary>
    public bool HasExplicitMoveAxisMode(Guid id)
    {
        var obj = _currentObjects.FirstOrDefault(o => o.Id == id);
        return obj?.IsMoveAxisModeExplicit ?? false;
    }

    /// <summary>
    /// Gets the movement axis constraint mode for the specified object. Reads directly from
    /// <see cref="IChartObject.MoveAxisMode"/> (a real per-object property, like <see cref="IChartObject.IsLocked"/>),
    /// so this reflects whatever was last persisted -- no separate runtime cache to fall out of sync.
    /// </summary>
    public DrawingMoveAxisMode GetMoveAxisMode(Guid id)
    {
        var obj = _currentObjects.FirstOrDefault(o => o.Id == id);
        return obj?.MoveAxisMode ?? DrawingMoveAxisMode.XY;
    }

    /// <summary>
    /// Sets the movement axis constraint mode for the specified object.
    /// </summary>
    public void SetMoveAxisMode(Guid id, DrawingMoveAxisMode mode)
    {
        var obj = _currentObjects.FirstOrDefault(o => o.Id == id);
        if (obj == null) return;
        obj.MoveAxisMode = mode;
        obj.IsMoveAxisModeExplicit = true;
        NotifyChanged();
    }

    /// <summary>Checks whether the object is locked in the current context.</summary>
    public bool IsLocked(Guid id)
    {
        var obj = _currentObjects.FirstOrDefault(o => o.Id == id);
        return obj != null && (_lockedIds.Contains(id) || obj.IsLocked);
    }

    /// <summary>Checks whether the object is visible in the current context.</summary>
    public bool IsVisible(Guid id)
    {
        var obj = _currentObjects.FirstOrDefault(o => o.Id == id);
        if (obj == null) return false;
        if (_hiddenIds.Contains(id)) return false;
        return obj.IsVisible;
    }

    /// <summary>
    /// Hit test at screen coordinate (Top-most to Bottom-most, ZeroAllocation backward loop).
    /// Invisible objects are ignored. Locked objects are included so they can be selected or inspected.
    /// </summary>
    public IChartObject? GetObjectAt(global::Avalonia.Point screenPoint, ICoordinateTransform transform)
    {
        var list = _currentObjects;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var obj = list[i];
            if (!IsVisible(obj.Id)) continue;

            if (obj.HitTest(screenPoint, transform))
            {
                return obj;
            }
        }
        return null;
    }

    /// <summary>
    /// Select object by ID.
    /// Invisible objects cannot be selected.
    /// </summary>
    public bool SelectObject(Guid id)
    {
        var obj = _currentObjects.FirstOrDefault(o => o.Id == id);
        if (obj == null || !IsVisible(id))
        {
            return false;
        }

        _selectedIds.Clear();
        _selectedIds.Add(id);
        UpdateSelectionState();
        NotifyChanged();
        return true;
    }
    
    /// <summary>Deselect all objects</summary>
    public void DeselectAll()
    {
        _selectedIds.Clear();
        UpdateSelectionState();
        NotifyChanged();
    }

    private void UpdateSelectionState()
    {
        for (int i = 0; i < _currentObjects.Count; i++)
        {
            var obj = _currentObjects[i];
            obj.IsSelected = _selectedIds.Contains(obj.Id);
        }
    }

    /// <summary>
    /// Render all visible objects in current context sorted by ZIndex (ZeroAllocation forward loop).
    /// </summary>
    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        var list = _currentObjects;
        int count = list.Count;
        for (int i = 0; i < count; i++)
        {
            var obj = list[i];
            if (IsVisible(obj.Id))
            {
                obj.Render(canvas, transform);
            }
        }
    }

    /// <summary>ZIndex modification: bring one step forward (towards top-most)</summary>
    public bool BringForward(Guid id)
    {
        int index = _currentObjects.FindIndex(o => o.Id == id);
        if (index < 0 || index >= _currentObjects.Count - 1) return false;

        (_currentObjects[index], _currentObjects[index + 1]) = (_currentObjects[index + 1], _currentObjects[index]);
        RecalculateZIndices();
        NotifyChanged();
        return true;
    }

    /// <summary>ZIndex modification: send one step backward (towards bottom-most)</summary>
    public bool SendBackward(Guid id)
    {
        int index = _currentObjects.FindIndex(o => o.Id == id);
        if (index <= 0) return false;

        (_currentObjects[index], _currentObjects[index - 1]) = (_currentObjects[index - 1], _currentObjects[index]);
        RecalculateZIndices();
        NotifyChanged();
        return true;
    }

    /// <summary>ZIndex modification: bring directly to top-most</summary>
    public bool BringToFront(Guid id)
    {
        int index = _currentObjects.FindIndex(o => o.Id == id);
        if (index < 0 || index == _currentObjects.Count - 1) return false;

        var obj = _currentObjects[index];
        _currentObjects.RemoveAt(index);
        _currentObjects.Add(obj);
        RecalculateZIndices();
        NotifyChanged();
        return true;
    }

    /// <summary>ZIndex modification: send directly to bottom-most</summary>
    public bool SendToBack(Guid id)
    {
        int index = _currentObjects.FindIndex(o => o.Id == id);
        if (index <= 0) return false;

        var obj = _currentObjects[index];
        _currentObjects.RemoveAt(index);
        _currentObjects.Insert(0, obj);
        RecalculateZIndices();
        NotifyChanged();
        return true;
    }

    /// <summary>
    /// Toggles the lock state of an object.
    /// If locked while selected, automatically clears selection to preserve Invariant I-03.
    /// </summary>
    public bool ToggleLock(Guid id)
    {
        var obj = _currentObjects.FirstOrDefault(o => o.Id == id);
        if (obj == null) return false;

        if (IsLocked(id))
        {
            _lockedIds.Remove(id);
            obj.IsLocked = false;
        }
        else
        {
            _lockedIds.Add(id);
            obj.IsLocked = true;
        }
        NotifyChanged();
        return true;
    }

    /// <summary>
    /// Sets the object's user-assigned display name (<see cref="IChartObject.CustomName"/>).
    /// Pass null or empty to clear the custom name and fall back to the localized type name.
    /// </summary>
    public bool RenameObject(Guid id, string? name)
    {
        var obj = _currentObjects.FirstOrDefault(o => o.Id == id);
        if (obj == null) return false;

        obj.CustomName = string.IsNullOrWhiteSpace(name) ? null : name;
        NotifyChanged();
        return true;
    }

    /// <summary>
    /// Notifies subscribers (chart canvas redraw + disk persistence) after a caller has already
    /// mutated an object's properties directly rather than through one of this manager's own
    /// mutator methods above -- e.g. <see cref="Views.Dialogs.DrawingSettingsDialog"/> writes
    /// Color/Thickness/tool-specific settings straight onto the model. Without this call, neither
    /// <see cref="Changed"/> (persistence) nor <see cref="Synced"/> (canvas <c>InvalidateVisual</c>)
    /// fires, so the edit is silently invisible until some unrelated interaction forces a repaint.
    /// </summary>
    public bool NotifyObjectChanged(Guid id)
    {
        if (id == Guid.Empty || !_currentObjects.Any(o => o.Id == id)) return false;
        NotifyChanged();
        return true;
    }

    /// <summary>
    /// Moves the object's <see cref="IChartObject.AnchorPointIndex"/> one step clockwise (as the
    /// point would appear on screen) to the next control point, per <see cref="AnchorPointOrderHelper"/>.
    /// Wraps back to the first point after the last one. No-op (returns false) for objects with
    /// fewer than 2 points, since no orientation/cycling is defined for those.
    /// </summary>
    public bool AdvanceAnchorPoint(Guid id)
    {
        var obj = _currentObjects.FirstOrDefault(o => o.Id == id);
        if (obj == null || obj.Points.Count < 2) return false;

        var candidatePoints = GetAnchorCandidatePoints(obj);
        var order = AnchorPointOrderHelper.GetClockwiseCycleOrder(candidatePoints);
        int currentPos = Array.IndexOf(order, obj.AnchorPointIndex);
        if (currentPos < 0) currentPos = -1; // unknown/out-of-range -> advance to order[0]

        obj.AnchorPointIndex = order[(currentPos + 1) % order.Length];
        NotifyChanged();
        return true;
    }

    /// <summary>
    /// Chart-space points <see cref="AnchorPointIndex"/> cycles through for an object. Most objects'
    /// real stored <see cref="IChartObject.Points"/> map 1:1 to their displayed selection handles.
    /// <see cref="RectangleObject"/> is rendered/dragged with 4 corner handles synthesized from only
    /// 2 stored points (see <see cref="RectangleObject.Render"/>), so its 2 virtual corners are
    /// synthesized here too, matching that same corner math.
    /// </summary>
    private static IReadOnlyList<ChartPoint> GetAnchorCandidatePoints(IChartObject obj)
    {
        if (obj is RectangleObject && obj.Points.Count >= 2)
        {
            var p1 = obj.Points[0];
            var p2 = obj.Points[1];
            return new[]
            {
                p1,
                new ChartPoint(p2.Time, p1.Price),
                p2,
                new ChartPoint(p1.Time, p2.Price)
            };
        }
        return obj.Points;
    }

    /// <summary>
    /// Toggles visibility of an object.
    /// If hidden while selected, automatically clears selection to preserve Invariant I-03.
    /// </summary>
    public bool ToggleVisibility(Guid id)
    {
        var obj = _currentObjects.FirstOrDefault(o => o.Id == id);
        if (obj == null) return false;

        bool currentlyVisible = IsVisible(id);
        if (currentlyVisible)
        {
            _hiddenIds.Add(id);
            obj.IsVisible = false;
            if (obj.IsSelected)
            {
                obj.IsSelected = false;
                _selectedIds.Remove(id);
            }
        }
        else
        {
            _hiddenIds.Remove(id);
            obj.IsVisible = true;
        }
        NotifyChanged();
        return true;
    }

    /// <summary>Locks all objects in current context</summary>
    public void LockAll()
    {
        using (BeginBatch())
        {
            for (int i = 0; i < _currentObjects.Count; i++)
            {
                var obj = _currentObjects[i];
                _lockedIds.Add(obj.Id);
                obj.IsLocked = true;
            }
            NotifyChanged();
        }
    }

    /// <summary>Unlocks all objects in current context</summary>
    public void UnlockAll()
    {
        using (BeginBatch())
        {
            _lockedIds.Clear();
            for (int i = 0; i < _currentObjects.Count; i++)
            {
                _currentObjects[i].IsLocked = false;
            }
            NotifyChanged();
        }
    }

    /// <summary>Shows all objects in current context</summary>
    public void ShowAll()
    {
        using (BeginBatch())
        {
            _hiddenIds.Clear();
            for (int i = 0; i < _currentObjects.Count; i++)
            {
                _currentObjects[i].IsVisible = true;
            }
            NotifyChanged();
        }
    }

    /// <summary>Hides all objects in current context</summary>
    public void HideAll()
    {
        using (BeginBatch())
        {
            for (int i = 0; i < _currentObjects.Count; i++)
            {
                var obj = _currentObjects[i];
                _hiddenIds.Add(obj.Id);
                obj.IsVisible = false;
                if (obj.IsSelected)
                {
                    obj.IsSelected = false;
                    _selectedIds.Remove(obj.Id);
                }
            }
            NotifyChanged();
        }
    }

    /// <summary>Deletes all UNLOCKED objects in current context</summary>
    public void DeleteAll()
    {
        using (BeginBatch())
        {
            for (int i = _currentObjects.Count - 1; i >= 0; i--)
            {
                var obj = _currentObjects[i];
                if (!IsLocked(obj.Id))
                {
                    _selectedIds.Remove(obj.Id);
                    _lockedIds.Remove(obj.Id);
                    _hiddenIds.Remove(obj.Id);
                    _currentObjects.RemoveAt(i);
                }
            }
            RecalculateZIndices();
            NotifyChanged();
        }
    }

    /// <summary>Clear all objects in ALL contexts (e.g. on new data load). Does not fire Changed (fires Synced).</summary>
    public void Clear()
    {
        foreach (var contextList in _contextStore.Values)
        {
            contextList.Clear();
        }
        _currentObjects.Clear();
        _selectedIds.Clear();
        _lockedIds.Clear();
        _hiddenIds.Clear();
        Synced?.Invoke();
    }

    /// <summary>Get objects by type (Current Context)</summary>
    public IEnumerable<IChartObject> GetObjectsByType(ChartObjectType type)
    {
        return _currentObjects.Where(o => o.Type == type);
    }

    /// <summary>Get object count (Current Context)</summary>
    public int Count => _currentObjects.Count;

    /// <summary>
    /// Captures a shallow copy of every context's object list, for persistence.
    /// The lists are copied (so later Add/Remove calls don't mutate the snapshot),
    /// but the IChartObject instances themselves are shared references.
    /// </summary>
    public Dictionary<ChartDrawingContextType, List<IChartObject>> GetSnapshot()
    {
        var snapshot = new Dictionary<ChartDrawingContextType, List<IChartObject>>();
        foreach (var kv in _contextStore)
        {
            snapshot[kv.Key] = new List<IChartObject>(kv.Value);
        }
        return snapshot;
    }

    /// <summary>
    /// Restores object lists for the given contexts (e.g. loaded from persistence),
    /// replacing the contents of each existing context's list in place so that
    /// _currentObjects (a reference into _contextStore) stays valid.
    /// Does not fire Changed (fires Synced).
    /// </summary>
    public void LoadSnapshot(Dictionary<ChartDrawingContextType, List<IChartObject>> data)
    {
        foreach (var kv in data)
        {
            if (_contextStore.TryGetValue(kv.Key, out var list))
            {
                list.Clear();
                list.AddRange(kv.Value);
            }
            else
            {
                _contextStore[kv.Key] = new List<IChartObject>(kv.Value);
            }

            // Recalculate Z-indices for every loaded context
            for (int i = 0; i < _contextStore[kv.Key].Count; i++)
            {
                _contextStore[kv.Key][i].ZIndex = i;
            }
        }
        _selectedIds.Clear();
        _lockedIds.Clear();
        _hiddenIds.Clear();
        foreach (var obj in _currentObjects)
        {
            if (obj.IsLocked) _lockedIds.Add(obj.Id);
            if (!obj.IsVisible) _hiddenIds.Add(obj.Id);
        }
        Synced?.Invoke();
    }

    private sealed class BatchScope : IDisposable
    {
        private ChartObjectManager? _manager;

        public BatchScope(ChartObjectManager manager)
        {
            _manager = manager;
        }

        public void Dispose()
        {
            _manager?.EndBatch();
            _manager = null;
        }
    }
}
