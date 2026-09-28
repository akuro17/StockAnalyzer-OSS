using StockAnalyzer.Core.Models.UI;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>
/// Service to handle tearing off tabs into standalone windows.
/// </summary>
public interface ITearOffService
{
    /// <summary>
    /// Tears off a workspace item into a new window.
    /// </summary>
    /// <param name="item">The workspace item to detach.</param>
    void TearOff(WorkspaceViewItem item);

    /// <summary>
    /// Restores a workspace item back to the main window.
    /// </summary>
    /// <param name="item">The workspace item to restore.</param>
    void Restore(WorkspaceViewItem item);

    /// <summary>
    /// Materializes a detached window for an item that is already marked as detached (e.g. on startup).
    /// </summary>
    /// <param name="item">The detached workspace item.</param>
    void RestoreDetached(WorkspaceViewItem item);

    /// <summary>
    /// Moves a tab from a detached window back to the main window.
    /// </summary>
    /// <param name="item">The item to redock.</param>
    void Redock(WorkspaceViewItem item);
    
    /// <summary>
    /// Materializes a detached window for a group of items that belong to the same container.
    /// </summary>
    /// <param name="items">The items to restore together.</param>
    void RestoreDetachedGroup(System.Collections.Generic.IEnumerable<WorkspaceViewItem> items);
}
