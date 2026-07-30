using System.Threading.Tasks;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>
/// Handles the initialization, saving, and lifecycle state management of the workspace.
/// </summary>
public interface IWorkspaceCoordinator
{
    /// <summary>
    /// Binds the main view model target to this coordinator. This must be called exactly once.
    /// Subsequent calls will throw an <see cref="System.InvalidOperationException"/>.
    /// </summary>
    void Bind(IWorkspaceLayoutTarget target);

    /// <summary>
    /// Asynchronously initializes and restores the workspace layout and settings.
    /// Supports an optional custom file path to load a specific workspace profile.
    /// </summary>
    Task InitializeWorkspaceAsync(string? customPath = null);

    /// <summary>
    /// Asynchronously saves the active workspace layout and settings.
    /// Supports an optional custom file path to save to a specific workspace profile.
    /// </summary>
    Task SaveActiveWorkspaceAsync(string? customPath = null);

    /// <summary>
    /// Synchronously and atomically saves the workspace settings during application shutdown.
    /// This method is designed to avoid any async task scheduling or UI thread blocking.
    /// </summary>
    /// <param name="settings">The WorkspaceSettings instance to save. Cannot be null.</param>
    /// <exception cref="System.ArgumentNullException">When settings is null.</exception>
    void ForceSaveSync(StockAnalyzer.Core.Models.Settings.WorkspaceSettings settings);
}
