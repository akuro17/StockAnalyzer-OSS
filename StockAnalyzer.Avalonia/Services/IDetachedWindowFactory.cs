using Avalonia.Controls;
using StockAnalyzer.Core.Models.UI;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>
/// Factory for creating detached (tear-off) windows.
/// </summary>
public interface IDetachedWindowFactory
{
    /// <summary>
    /// Creates a new instance of a detached window.
    /// </summary>
    /// <param name="owner">The owner window (usually MainWindow).</param>
    /// <returns>The created window.</returns>
    object CreateWindow(object owner);

    /// <summary>
    /// Creates a new instance of a detached window with a specific content.
    /// </summary>
    /// <param name="owner">The owner window.</param>
    /// <param name="item">The view item to display.</param>
    /// <returns>The created window.</returns>
    object CreateWindow(object owner, WorkspaceViewItem item);

    /// <summary>
    /// Sets the position of a window.
    /// </summary>
    void SetPosition(object window, double x, double y);

    /// <summary>
    /// Applies saved geometry to a window.
    /// </summary>
    void ApplyGeometry(object window, double x, double y, double width, double height);

    /// <summary>
    /// Shows a window.
    /// </summary>
    void ShowWindow(object window, object? owner = null);

    /// <summary>
    /// Closes a window.
    /// </summary>
    void CloseWindow(object window);
}
