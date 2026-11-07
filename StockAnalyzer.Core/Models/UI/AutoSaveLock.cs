namespace StockAnalyzer.Core.Models.UI;

/// <summary>
/// Why automatic saving of the default workspace file is blocked. While anything but <see cref="None"/> is set, the
/// default-path workspace save, the layout save scheduler and the shutdown save do not write.
/// Explicit-path saves (exports the user chose) and other stores (settings, drawings, templates) are not affected.
/// </summary>
public enum AutoSaveLock
{
    /// <summary>Automatic saving is allowed.</summary>
    None,

    /// <summary>
    /// The workspace file could not be restored completely (unreadable, partly rescued, newer than supported, or applying it failed),
    /// so saving the current state over it could destroy what was in the file. Lifted by the user's explicit save or by a clean manual load.
    /// </summary>
    RestoreAnomaly,

    /// <summary>
    /// The persistent data folder is not writable (the application runs in read-only mode), so a save could only fail.
    /// Fixed for the life of the process; only a restart after fixing the permissions lifts it. Stronger than <see cref="RestoreAnomaly"/>.
    /// </summary>
    StorageReadOnly
}
