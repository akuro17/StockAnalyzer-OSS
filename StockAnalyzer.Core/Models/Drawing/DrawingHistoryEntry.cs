using System;

namespace StockAnalyzer.Core.Models.Drawing;

/// <summary>
/// An opaque, unique correlation token returned by BeginEdit and required for Commit/Cancel.
/// Ensures that each drawing mutation lifecycle is atomically paired and prevents accidental
/// commits from mismatched callers or overlapping interactions.
/// </summary>
public readonly record struct DrawingEditToken(Guid Value)
{
    public static readonly DrawingEditToken Empty = new(Guid.Empty);

    public bool IsEmpty => Value == Guid.Empty;

    public static DrawingEditToken NewToken() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}

/// <summary>
/// Classifies the semantic mutation category for an undo/redo history entry.
/// Encompasses single object actions, bulk actions, and layer-level modifications (G4 specification).
/// </summary>
public enum DrawingOperationKind
{
    // Object mutations
    Add,
    Delete,
    Move,
    PointEdit,
    StyleChange,
    VisibilityToggle,
    LockToggle,
    ZOrderChange,
    Duplicate,
    Rename,
    BulkDelete,
    BulkVisibility,
    BulkLock,
    SettingsApply,

    // Layer mutations (G4 / S07 specification)
    LayerAdd,
    LayerDelete,
    LayerVisibilityToggle,
    LayerLockToggle,
    LayerReorder,
    LayerRename,
    ObjectMoveToLayer,

    // Link group mutations (appended last; existing members must never be reordered)
    LinkObjects,
    UnlinkObjects
}

/// <summary>
/// An immutable history record encapsulating the Before and After states of a drawing context.
/// Holds zero live UI or drawing object references, ensuring past states are completely decoupled
/// from in-memory mutations (P3-04 requirement).
/// </summary>
public readonly record struct DrawingHistoryEntry(
    DrawingOperationKind Kind,
    FrozenContextState Before,
    FrozenContextState After,
    long ByteSize,
    DateTime TimestampUtc
);
