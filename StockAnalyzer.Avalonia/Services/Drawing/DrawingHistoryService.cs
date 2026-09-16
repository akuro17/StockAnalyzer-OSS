using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Serialization;
using StockAnalyzer.Core.Models.Drawing;

namespace StockAnalyzer.Avalonia.Services.Drawing;

/// <summary>
/// In-process history engine providing Begin/Commit/Cancel and Undo/Redo transaction semantics
/// for chart drawing operations per DrawingDocumentKey context.
/// Strictly enforces domain boundaries:
/// - Immutable value-only Before/After storage (FrozenContextState) decoupled from runtime IChartObject.
/// - Capacity thresholds and byte budget caps with deterministic oldest-first eviction (G3 specification).
/// - Semantic equality checks avoiding redundant history entries.
/// - Concurrency safety ensuring one active mutation per document key.
/// </summary>
public sealed class DrawingHistoryService : IDisposable
{
    private readonly object _lock = new();
    private readonly int _maxEntryCount;
    private readonly long _maxByteBudget;

    private readonly LinkedList<DrawingHistoryEntry> _undoStack = new();
    private readonly Stack<DrawingHistoryEntry> _redoStack = new();

    private DrawingEditToken? _activeToken;
    private DrawingOperationKind? _activeKind;
    private FrozenContextState? _beforeState;
    private long _currentByteTotal;
    private bool _isDisposed;

    public DrawingDocumentKey Key { get; }

    public int UndoCount
    {
        get { lock (_lock) return _undoStack.Count; }
    }

    public int RedoCount
    {
        get { lock (_lock) return _redoStack.Count; }
    }

    public bool CanUndo
    {
        get { lock (_lock) return _undoStack.Count > 0 && !_activeToken.HasValue; }
    }

    public bool CanRedo
    {
        get { lock (_lock) return _redoStack.Count > 0 && !_activeToken.HasValue; }
    }

    public long CurrentByteTotal
    {
        get { lock (_lock) return _currentByteTotal; }
    }

    public bool IsEditing
    {
        get { lock (_lock) return _activeToken.HasValue; }
    }

    /// <summary>
    /// Gets the UTC creation timestamp of the oldest preserved undo entry, or null if undo stack is empty.
    /// Used by multi-session global budget pruning to identify the globally oldest candidate for eviction.
    /// </summary>
    public DateTime? OldestEntryTimestampUtc
    {
        get
        {
            lock (_lock)
            {
                return _undoStack.Count > 0 ? _undoStack.First!.Value.TimestampUtc : null;
            }
        }
    }

    /// <summary>
    /// Evicts the oldest entry on the undo stack, reducing current byte totals.
    /// Returns true if an entry was evicted with the number of freed bytes, or false if the stack is empty.
    /// </summary>
    public bool TryEvictOldestEntry(out long freedBytes)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            if (_undoStack.Count > 0)
            {
                var oldest = _undoStack.First!.Value;
                _undoStack.RemoveFirst();
                _currentByteTotal -= oldest.ByteSize;
                freedBytes = oldest.ByteSize;
                NotifyStateChanged();
                return true;
            }
            freedBytes = 0;
            return false;
        }
    }

    public event EventHandler? HistoryChanged;

    public DrawingHistoryService(
        DrawingDocumentKey key,
        int maxEntryCount = DrawingInteractionLimits.MaxHistoryEntriesPerContext,
        long maxByteBudget = DrawingInteractionLimits.MaxHistoryPayloadBytesPerContext)
    {
        if (maxEntryCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntryCount), maxEntryCount, "Max entry count must be strictly positive.");
        }
        if (maxByteBudget <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxByteBudget), maxByteBudget, "Max byte budget must be strictly positive.");
        }

        Key = key;
        _maxEntryCount = maxEntryCount;
        _maxByteBudget = maxByteBudget;
    }

    /// <summary>
    /// Begins an atomic drawing mutation transaction.
    /// Captures the initial Before state and issues an exclusive correlation token.
    /// Returns Busy if another edit transaction is currently active for this document key.
    /// </summary>
    public DrawingBeginResult BeginEdit(DrawingOperationKind kind, FrozenContextState before)
    {
        lock (_lock)
        {
            ThrowIfDisposed();

            if (_activeToken.HasValue)
            {
                return DrawingBeginResult.Busy();
            }

            var token = DrawingEditToken.NewToken();
            _activeToken = token;
            _activeKind = kind;
            _beforeState = before;

            NotifyStateChanged();
            return DrawingBeginResult.Succeeded(token);
        }
    }

    /// <summary>
    /// Commits an active drawing mutation transaction.
    /// Evaluates semantic equality: if Before and After are identical, returns NoChange and records nothing.
    /// Enforces byte budgets and entry caps, discarding oldest history entries on overflow.
    /// Discards all existing Redo entries upon a newly committed change.
    /// </summary>
    public DrawingCommandResult Commit(DrawingEditToken token, FrozenContextState after)
    {
        lock (_lock)
        {
            ThrowIfDisposed();

            if (token.IsEmpty || !_activeToken.HasValue || _activeToken.Value != token)
            {
                return DrawingCommandResult.Fail(DrawingCommandStatus.InvalidArgument, "Mismatched or empty edit token.");
            }

            var kind = _activeKind!.Value;
            var before = _beforeState!.Value;

            _activeToken = null;
            _activeKind = null;
            _beforeState = null;

            // Semantic comparison: identical meaning produces no history entry
            if (AreSemanticallyEqual(before, after))
            {
                NotifyStateChanged();
                return DrawingCommandResult.NoChange();
            }

            long byteSize = ComputePayloadBytes(before, after);

            // G3 single-entry boundary rejection: an operation larger than total budget cannot be recorded
            if (byteSize > _maxByteBudget)
            {
                NotifyStateChanged();
                return DrawingCommandResult.Fail(
                    DrawingCommandStatus.CapacityExceeded,
                    $"Operation payload {byteSize} bytes exceeds maximum history byte budget {_maxByteBudget} bytes.",
                    restoredState: before);
            }

            int operationPoints = CalculateOperationPoints(before, after);
            if (operationPoints > DrawingInteractionLimits.MaxPointsPerOperation)
            {
                NotifyStateChanged();
                return DrawingCommandResult.Fail(
                    DrawingCommandStatus.CapacityExceeded,
                    $"Operation point count {operationPoints} exceeds maximum allowed points {DrawingInteractionLimits.MaxPointsPerOperation}.",
                    restoredState: before);
            }

            // A newly committed operation clears any existing redo stack
            ClearRedoStack();

            // Evict oldest undo entries until count fits within cap and total payload fits within budget
            while (_undoStack.Count > 0 && (_undoStack.Count >= _maxEntryCount || _currentByteTotal + byteSize > _maxByteBudget))
            {
                var oldest = _undoStack.First!.Value;
                _undoStack.RemoveFirst();
                _currentByteTotal -= oldest.ByteSize;
            }

            var entry = new DrawingHistoryEntry(kind, before, after, byteSize, DateTime.UtcNow);
            _undoStack.AddLast(entry);
            _currentByteTotal += byteSize;

            NotifyStateChanged();
            return DrawingCommandResult.Success();
        }
    }

    /// <summary>
    /// Cancels an active drawing mutation transaction without recording any history.
    /// Releases the active correlation token and returns the Before state for rollback.
    /// </summary>
    public DrawingCommandResult Cancel(DrawingEditToken token)
    {
        lock (_lock)
        {
            ThrowIfDisposed();

            if (token.IsEmpty || !_activeToken.HasValue || _activeToken.Value != token)
            {
                return DrawingCommandResult.Fail(DrawingCommandStatus.InvalidArgument, "Mismatched or empty edit token.");
            }

            var before = _beforeState;
            _activeToken = null;
            _activeKind = null;
            _beforeState = null;

            NotifyStateChanged();
            return DrawingCommandResult.Success(before ?? default);
        }
    }

    /// <summary>
    /// Reverses the most recently committed operation, returning its Before state.
    /// Moves the entry to the redo stack. Returns NoChange if the undo stack is empty.
    /// </summary>
    public DrawingCommandResult Undo()
    {
        lock (_lock)
        {
            ThrowIfDisposed();

            if (_activeToken.HasValue)
            {
                return DrawingCommandResult.Busy();
            }

            if (_undoStack.Count == 0)
            {
                return DrawingCommandResult.NoChange();
            }

            var entry = _undoStack.Last!.Value;
            _undoStack.RemoveLast();
            _redoStack.Push(entry);

            NotifyStateChanged();
            return DrawingCommandResult.Success(entry.Before);
        }
    }

    /// <summary>
    /// Re-applies the most recently undone operation, returning its After state.
    /// Moves the entry back to the undo stack. Returns NoChange if the redo stack is empty.
    /// </summary>
    public DrawingCommandResult Redo()
    {
        lock (_lock)
        {
            ThrowIfDisposed();

            if (_activeToken.HasValue)
            {
                return DrawingCommandResult.Busy();
            }

            if (_redoStack.Count == 0)
            {
                return DrawingCommandResult.NoChange();
            }

            var entry = _redoStack.Pop();
            _undoStack.AddLast(entry);

            NotifyStateChanged();
            return DrawingCommandResult.Success(entry.After);
        }
    }

    /// <summary>
    /// Clears all undo and redo history for this context and resets tracked byte totals to zero.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _undoStack.Clear();
            ClearRedoStack();
            _currentByteTotal = 0;
            _activeToken = null;
            _activeKind = null;
            _beforeState = null;

            NotifyStateChanged();
        }
    }

    private void ClearRedoStack()
    {
        while (_redoStack.Count > 0)
        {
            var entry = _redoStack.Pop();
            _currentByteTotal -= entry.ByteSize;
        }
    }

    private void NotifyStateChanged()
    {
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(DrawingHistoryService));
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_isDisposed) return;
            _isDisposed = true;

            _undoStack.Clear();
            _redoStack.Clear();
            _currentByteTotal = 0;
            _activeToken = null;
            _activeKind = null;
            _beforeState = null;
        }
    }

    private static readonly JsonSerializerOptions CanonicalSerializerOptions = new()
    {
        WriteIndented = false,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters =
        {
            new StockAnalyzer.Avalonia.Drawing.Serialization.AvaloniaColorJsonConverter(),
            new System.Text.Json.Serialization.JsonStringEnumConverter()
        }
    };

    /// <summary>
    /// Computes canonical UTF-8 payload byte length for a Before and After state pair (G3 specification).
    /// Uses compact, canonical UTF-8 serialization without indentation for deterministic cross-platform measurement.
    /// </summary>
    public static long ComputePayloadBytes(FrozenContextState before, FrozenContextState after)
    {
        byte[] beforeBytes = JsonSerializer.SerializeToUtf8Bytes(before, CanonicalSerializerOptions);
        byte[] afterBytes = JsonSerializer.SerializeToUtf8Bytes(after, CanonicalSerializerOptions);
        return beforeBytes.Length + afterBytes.Length;
    }

    /// <summary>
    /// Evaluates semantic equality between two frozen context states.
    /// Compares layers, objects, coordinates, and style parameters without reliance on reference identity.
    /// </summary>
    public static bool AreSemanticallyEqual(FrozenContextState a, FrozenContextState b)
    {
        if (!string.Equals(a.ContextName, b.ContextName, StringComparison.Ordinal))
        {
            return false;
        }

        // Compare layers
        if (a.Layers.Count != b.Layers.Count) return false;
        for (int i = 0; i < a.Layers.Count; i++)
        {
            var la = a.Layers[i];
            var lb = b.Layers[i];

            if (la.LayerId != lb.LayerId) return false;
            if (!string.Equals(la.Name, lb.Name, StringComparison.Ordinal)) return false;
            if (la.Panel != lb.Panel) return false;
            if (la.IsVisible != lb.IsVisible) return false;
            if (la.IsEditLocked != lb.IsEditLocked) return false;
            if (!la.ObjectIds.SequenceEqual(lb.ObjectIds)) return false;
        }

        // Compare objects
        if (a.Objects.Count != b.Objects.Count) return false;
        for (int i = 0; i < a.Objects.Count; i++)
        {
            var oa = a.Objects[i];
            var ob = b.Objects[i];

            if (oa.ObjectId != ob.ObjectId) return false;
            if (!string.Equals(oa.TypeName, ob.TypeName, StringComparison.Ordinal)) return false;
            if (oa.Panel != ob.Panel) return false;
            if (oa.CoordinateKind != ob.CoordinateKind) return false;

            // Compare points
            if (oa.Points.Count != ob.Points.Count) return false;
            for (int p = 0; p < oa.Points.Count; p++)
            {
                var pa = oa.Points[p];
                var pb = ob.Points[p];

                if (pa.Kind != pb.Kind) return false;
                if (pa.UtcTime != pb.UtcTime) return false;
                if (pa.XValue != pb.XValue) return false;
                if (pa.YValue != pb.YValue) return false;
            }

            // Compare style and parameters
            if (!AreParametersEqual(oa.StyleAndParameters, ob.StyleAndParameters))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AreParametersEqual(
        IReadOnlyDictionary<string, object?> a,
        IReadOnlyDictionary<string, object?> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;

        foreach (var (key, valA) in a)
        {
            if (!b.TryGetValue(key, out var valB)) return false;
            if (!AreValuesEqual(valA, valB)) return false;
        }

        return true;
    }

    private static bool AreValuesEqual(object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;

        if (a is JsonElement jeA && b is JsonElement jeB)
        {
            return AreJsonElementsEqual(jeA, jeB);
        }

        if (a is JsonElement je && !(b is JsonElement))
        {
            var serializedB = JsonSerializer.SerializeToElement(b, DrawingDocumentCodec.SerializerOptions);
            return AreJsonElementsEqual(je, serializedB);
        }

        if (!(a is JsonElement) && b is JsonElement je2)
        {
            var serializedA = JsonSerializer.SerializeToElement(a, DrawingDocumentCodec.SerializerOptions);
            return AreJsonElementsEqual(serializedA, je2);
        }

        if (a is System.Collections.IEnumerable enA && b is System.Collections.IEnumerable enB && !(a is string))
        {
            var listA = enA.Cast<object?>().ToList();
            var listB = enB.Cast<object?>().ToList();
            if (listA.Count != listB.Count) return false;
            for (int i = 0; i < listA.Count; i++)
            {
                if (!AreValuesEqual(listA[i], listB[i])) return false;
            }
            return true;
        }

        return Equals(a, b);
    }

    private static bool AreJsonElementsEqual(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind)
        {
            return false;
        }

        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
                var propsA = a.EnumerateObject().ToList();
                var propsB = b.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                if (propsA.Count != propsB.Count) return false;
                foreach (var pA in propsA)
                {
                    if (!propsB.TryGetValue(pA.Name, out var valB)) return false;
                    if (!AreJsonElementsEqual(pA.Value, valB)) return false;
                }
                return true;

            case JsonValueKind.Array:
                var arrA = a.EnumerateArray().ToList();
                var arrB = b.EnumerateArray().ToList();
                if (arrA.Count != arrB.Count) return false;
                for (int i = 0; i < arrA.Count; i++)
                {
                    if (!AreJsonElementsEqual(arrA[i], arrB[i])) return false;
                }
                return true;

            case JsonValueKind.Number:
                if (a.TryGetDecimal(out var decA) && b.TryGetDecimal(out var decB))
                {
                    return decA == decB;
                }
                return a.GetDouble().Equals(b.GetDouble());

            case JsonValueKind.String:
                return a.GetString() == b.GetString();

            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return true;

            default:
                return a.GetRawText() == b.GetRawText();
        }
    }

    /// <summary>
    /// Calculates the point count specifically belonging to objects mutated, added, or removed in this operation (F09).
    /// Prevents existing large drawing sets from incorrectly blocking small interactive edits.
    /// </summary>
    public static int CalculateOperationPoints(FrozenContextState before, FrozenContextState after)
    {
        var beforeMap = before.Objects?.ToDictionary(o => o.ObjectId) ?? new();
        var afterMap = after.Objects?.ToDictionary(o => o.ObjectId) ?? new();

        int points = 0;
        var allIds = new HashSet<Guid>(beforeMap.Keys);
        allIds.UnionWith(afterMap.Keys);

        foreach (var id in allIds)
        {
            bool inBefore = beforeMap.TryGetValue(id, out var bObj);
            bool inAfter = afterMap.TryGetValue(id, out var aObj);

            if (!inBefore && inAfter)
            {
                points += aObj.Points?.Count ?? 0;
            }
            else if (inBefore && !inAfter)
            {
                points += bObj.Points?.Count ?? 0;
            }
            else if (inBefore && inAfter)
            {
                if (!AreObjectRecordsEqual(bObj, aObj))
                {
                    points += Math.Max(bObj.Points?.Count ?? 0, aObj.Points?.Count ?? 0);
                }
            }
        }

        return points;
    }

    private static bool AreObjectRecordsEqual(DrawingObjectRecord a, DrawingObjectRecord b)
    {
        if (a.Panel != b.Panel || a.CoordinateKind != b.CoordinateKind) return false;
        if (!string.Equals(a.TypeName, b.TypeName, StringComparison.Ordinal)) return false;
        if (a.Points.Count != b.Points.Count) return false;
        for (int i = 0; i < a.Points.Count; i++)
        {
            var pa = a.Points[i];
            var pb = b.Points[i];
            if (pa.Kind != pb.Kind || pa.UtcTime != pb.UtcTime || pa.XValue != pb.XValue || pa.YValue != pb.YValue) return false;
        }
        return AreParametersEqual(a.StyleAndParameters, b.StyleAndParameters);
    }
}
