using StockAnalyzer.Core.Models.UI;

namespace StockAnalyzer.Avalonia.ViewModels;

/// <summary>
/// Defines the contract for a container window that can host detached workspace items.
/// </summary>
public interface IDetachedWindowContainer
{
    /// <summary>
    /// Gets the unique identifier for this container.
    /// </summary>
    string ContainerId { get; }

    /// <summary>
    /// Adds a view item to the detached window.
    /// </summary>
    /// <param name="item">The item to add.</param>
    void AddItem(WorkspaceViewItem item);
}
