using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Settings;

namespace StockAnalyzer.Core.Interfaces;

/// <summary>
/// Service interface responsible for serializing and deserializing the entire workspace settings.
/// </summary>
public interface IWorkspaceSerializationService
{
    /// <summary>
    /// Saves the workspace settings to the specified file path asynchronously.
    /// Uses a temporary file strategy to prevent corruption during writes.
    /// </summary>
    Task SaveAsync(WorkspaceSettings settings, string filePath);

    /// <summary>
    /// Saves the workspace settings to the specified file path synchronously and atomically.
    /// Uses a unique temporary file strategy with disk flushing.
    /// </summary>
    void Save(WorkspaceSettings settings, string filePath);

    /// <summary>
    /// Loads the workspace settings from the specified file path asynchronously.
    /// </summary>
    Task<WorkspaceSettings?> LoadAsync(string filePath);

    /// <summary>
    /// Exports the workspace settings as a portable JSON file to the specified file path asynchronously.
    /// Ensures environment-independent properties and validates schema version.
    /// </summary>
    Task ExportPortableAsync(WorkspaceSettings settings, string filePath, System.Threading.CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports a portable workspace settings file asynchronously.
    /// Validates schema version and performs schema migration if necessary.
    /// </summary>
    Task<WorkspaceSettings?> ImportPortableAsync(string filePath, System.Threading.CancellationToken cancellationToken = default);
}
