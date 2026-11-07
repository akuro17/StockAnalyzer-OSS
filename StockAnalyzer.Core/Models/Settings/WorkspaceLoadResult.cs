using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Models.Settings;

/// <summary>
/// Outcome class of reading the default workspace file (stage 1 of the two-stage restore).
/// The first matching condition in the reader's decision order wins.
/// </summary>
public enum WorkspaceReadStatus
{
    /// <summary>The file does not exist. Defaults apply; nothing to protect.</summary>
    Missing,

    /// <summary>The file has zero length or only whitespace. Defaults apply; nothing to lose.</summary>
    Empty,

    /// <summary>
    /// The file could not be interpreted at all (I/O failure, JSON syntax error, root is not an object, SchemaVersion below 1).
    /// No settings are returned; the original file must be protected from automatic overwrite.
    /// </summary>
    Unreadable,

    /// <summary>
    /// Full deserialisation failed but some sections were rescued. At least one <see cref="WorkspaceWarningSeverity.Lossy"/> warning exists.
    /// </summary>
    Recovered,

    /// <summary>Full deserialisation succeeded.</summary>
    Ok,

    /// <summary>
    /// The file's SchemaVersion is newer than <see cref="WorkspaceSettings.CurrentSchemaVersion"/>.
    /// No settings are returned (no partial restore) because saving would silently downgrade the file.
    /// </summary>
    NewerSchema
}

/// <summary>Whether a warning means information was lost.</summary>
public enum WorkspaceWarningSeverity
{
    /// <summary>Information present in the file could not be represented and was dropped.</summary>
    Lossy,

    /// <summary>The value was normalised without losing information.</summary>
    Cosmetic
}

/// <summary>One rescued-or-dropped item found while reading a workspace file.</summary>
/// <param name="Severity">Whether information was lost.</param>
/// <param name="Path">Location inside the workspace, e.g. <c>PanelCharts[1].Indicators[2]</c>. Empty for the whole file.</param>
/// <param name="Message">Diagnostic text (English, not localised).</param>
public sealed record WorkspaceLoadWarning(WorkspaceWarningSeverity Severity, string Path, string Message);

/// <summary>
/// Result of stage 1 of the workspace restore: what the file contained, without touching any view model.
/// </summary>
/// <param name="Status">Outcome class.</param>
/// <param name="Settings">The settings to apply; non-null only for <see cref="WorkspaceReadStatus.Ok"/> and <see cref="WorkspaceReadStatus.Recovered"/>.</param>
/// <param name="Warnings">Everything that was dropped or normalised; empty when nothing happened.</param>
public sealed record WorkspaceLoadResult(
    WorkspaceReadStatus Status,
    WorkspaceSettings? Settings,
    IReadOnlyList<WorkspaceLoadWarning> Warnings)
{
    /// <summary>
    /// True when automatic overwrite of the source file must be blocked: the file was unreadable, partly rescued,
    /// newer than supported, or any warning reports lost information.
    /// </summary>
    public bool RequiresAutoSaveLock =>
        Status is WorkspaceReadStatus.Unreadable or WorkspaceReadStatus.Recovered or WorkspaceReadStatus.NewerSchema
        || Warnings.Any(w => w.Severity == WorkspaceWarningSeverity.Lossy);
}
