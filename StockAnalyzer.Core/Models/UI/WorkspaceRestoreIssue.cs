using System.Collections.Generic;
using StockAnalyzer.Core.Models.Settings;

namespace StockAnalyzer.Core.Models.UI;

/// <summary>What went wrong while restoring the default workspace file. Decides the wording of the banner.</summary>
public enum WorkspaceRestoreIssueKind
{
    /// <summary>The file was unreadable or only partly restorable; defaults and/or the rescued part were applied.</summary>
    PartialRestore,

    /// <summary>The file has a newer SchemaVersion than this version supports; nothing was loaded and nothing was copied.</summary>
    NewerSchema,

    /// <summary>The file was read but applying it to the screen threw.</summary>
    RestoreFailed
}

/// <summary>
/// The facts behind an <see cref="AutoSaveLock"/>, kept by <see cref="LayoutStateStore"/> so the banner can describe them.
/// </summary>
/// <param name="Kind">Selects the banner wording.</param>
/// <param name="PreservedCopyPath">Path of the preserved copy of the original file, or null when no copy exists (never made, or copying failed).</param>
/// <param name="Warnings">What was dropped or normalised, for the banner's details view.</param>
public sealed record WorkspaceRestoreIssue(
    WorkspaceRestoreIssueKind Kind,
    string? PreservedCopyPath,
    IReadOnlyList<WorkspaceLoadWarning> Warnings);
