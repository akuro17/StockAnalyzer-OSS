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

    /// <summary>
    /// Applies a pre-loaded WorkspaceSettings object directly to the UI target asynchronously.
    /// </summary>
    Task ApplyLoadedWorkspaceAsync(StockAnalyzer.Core.Models.Settings.WorkspaceSettings settings);

    /// <summary>
    /// Captures the current active workspace state into a WorkspaceSettings instance on the UI thread asynchronously.
    /// </summary>
    Task<StockAnalyzer.Core.Models.Settings.WorkspaceSettings> CaptureCurrentWorkspaceAsync();

    /// <summary>
    /// True while automatic saving of the default workspace file is blocked because its restore was incomplete.
    /// While true, <see cref="SaveActiveWorkspaceAsync"/> without a path and <see cref="ForceSaveSync"/> write nothing;
    /// a save to an explicit path is not affected.
    /// </summary>
    bool IsAutoSaveLocked { get; }

    /// <summary>
    /// The user's explicit "save the current state" decision: writes the current state to the default workspace file and,
    /// only after that write succeeded, lifts the lock. Returns false (lock unchanged) when nothing was written.
    /// </summary>
    Task<bool> SaveCurrentStateAndUnlockAsync();
}
