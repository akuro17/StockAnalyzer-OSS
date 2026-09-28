using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Serialization;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Service responsible for serializing and deserializing the entire workspace settings.
/// </summary>
public class WorkspaceSerializationService : IWorkspaceSerializationService, IDisposable
{
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILogger<WorkspaceSerializationService> _logger;
    private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

    public WorkspaceSerializationService()
        : this(null)
    {
    }

    public WorkspaceSerializationService(ILogger<WorkspaceSerializationService>? logger)
    {
        _logger = logger ?? NullLogger<WorkspaceSerializationService>.Instance;
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            TypeInfoResolver = WorkspacePolymorphicResolver.CreateResolver(),
            // Allows reading/writing fields if needed, but we mostly use properties
            IncludeFields = true,
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };
    }

    /// <summary>
    /// Saves the workspace settings to the specified file path asynchronously.
    /// Uses a temporary file strategy to prevent corruption during writes.
    /// </summary>
    public async Task SaveAsync(WorkspaceSettings settings, string filePath)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("File path cannot be empty", nameof(filePath));

        await _semaphore.WaitAsync();
        try
        {
            string? directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string tempPath = filePath + LayoutConstants.TemporaryWorkspaceExtension;

            try
            {
                using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                {
                    await JsonSerializer.SerializeAsync(fileStream, settings, _jsonOptions);
                }

                // Atomically replace the old file with the new one
                AtomicReplaceFile(tempPath, filePath, filePath + LayoutConstants.BackupWorkspaceExtension);
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Failed to atomically save workspace to {FilePath}. Temp file: {TempPath}", filePath, tempPath);
                throw new IOException($"Failed to atomically save workspace to {filePath}. Temp file: {tempPath}", ex);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete temporary file {TempPath}", tempPath); }
                }
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Saves the workspace settings to the specified file path synchronously and atomically.
    /// Uses a unique temporary file strategy with disk flushing.
    /// </summary>
    /// <param name="settings">The WorkspaceSettings instance to save. Cannot be null.</param>
    /// <param name="filePath">The absolute path to save the workspace settings to.</param>
    /// <exception cref="ArgumentNullException">When settings is null.</exception>
    /// <exception cref="ArgumentException">When filePath is null, empty, or relative.</exception>
    /// <exception cref="IOException">When file operations or serialization fail.</exception>
    public void Save(WorkspaceSettings settings, string filePath)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("filePath cannot be null or empty", nameof(filePath));
        if (!Path.IsPathRooted(filePath)) throw new ArgumentException("filePath must be an absolute path", nameof(filePath));

        _semaphore.Wait();
        try
        {
            string? directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string tempPath = filePath + LayoutConstants.TemporaryWorkspaceExtension + "." + Guid.NewGuid().ToString("N");

            try
            {
                using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: false))
                {
                    JsonSerializer.Serialize(fileStream, settings, _jsonOptions);
                    fileStream.Flush(flushToDisk: true);
                }

                // Atomically replace the old file with the new one
                AtomicReplaceFile(tempPath, filePath, null);
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Failed to atomically save workspace to {FilePath} synchronously. Temp file: {TempPath}", filePath, tempPath);
                throw new IOException($"Failed to atomically save workspace to {filePath} synchronously. Temp file: {tempPath}", ex);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete temporary file {TempPath}", tempPath); }
                }
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Loads the workspace settings from the specified file path asynchronously.
    /// </summary>
    public async Task<WorkspaceSettings?> LoadAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("File path cannot be empty", nameof(filePath));

        await _semaphore.WaitAsync();
        try
        {
            if (!File.Exists(filePath))
            {
                _logger.LogDebug("Workspace file not found at {FilePath}", filePath);
                return null;
            }

            using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true))
            {
                try
                {
                    var settings = await JsonSerializer.DeserializeAsync<WorkspaceSettings>(fileStream, _jsonOptions);
                    _logger.LogInformation("Successfully loaded workspace from {FilePath}", filePath);
                    return settings;
                }
                catch (JsonException ex)
                {
                    _logger.LogCritical(ex, "Critical corruption in workspace file at {FilePath}", filePath);
                    throw new InvalidOperationException($"Failed to deserialize workspace settings from '{filePath}'.", ex);
                }
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Exports the workspace settings as a portable JSON file to the specified file path asynchronously.
    /// Ensures environment-independent properties, validates schema version, and uses atomic temporary file writes.
    /// </summary>
    public async Task ExportPortableAsync(WorkspaceSettings settings, string filePath, System.Threading.CancellationToken cancellationToken = default)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("File path cannot be empty", nameof(filePath));

        // Enforce current schema version on export
        settings.SchemaVersion = WorkspaceSettings.CurrentSchemaVersion;

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            string? directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string tempPath = filePath + LayoutConstants.TemporaryWorkspaceExtension + "." + Guid.NewGuid().ToString("N");

            try
            {
                using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                {
                    await JsonSerializer.SerializeAsync(fileStream, settings, _jsonOptions, cancellationToken);
                    await fileStream.FlushAsync(cancellationToken);
                }

                AtomicReplaceFile(tempPath, filePath, null);
                _logger.LogInformation("Successfully exported portable workspace (SchemaVersion={Version}) to {FilePath}", settings.SchemaVersion, filePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to export portable workspace to {FilePath}. Temp file: {TempPath}", filePath, tempPath);
                throw new IOException($"Failed to export portable workspace to '{filePath}'.", ex);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete temporary file {TempPath}", tempPath); }
                }
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Imports a portable workspace settings file asynchronously.
    /// Validates schema version and performs schema migration if necessary.
    /// </summary>
    public async Task<WorkspaceSettings?> ImportPortableAsync(string filePath, System.Threading.CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("File path cannot be empty", nameof(filePath));

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(filePath))
            {
                _logger.LogWarning("Portable workspace file not found at {FilePath}", filePath);
                return null;
            }

            WorkspaceSettings? settings;
            using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true))
            {
                try
                {
                    settings = await JsonSerializer.DeserializeAsync<WorkspaceSettings>(fileStream, _jsonOptions, cancellationToken);
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "Failed to deserialize portable workspace at {FilePath}", filePath);
                    throw new InvalidOperationException($"Failed to deserialize workspace settings from '{filePath}'. Format may be corrupt.", ex);
                }
            }

            if (settings == null)
            {
                return null;
            }

            // Validate schema version
            if (settings.SchemaVersion < 1 || settings.SchemaVersion > WorkspaceSettings.CurrentSchemaVersion)
            {
                _logger.LogError("Incompatible workspace SchemaVersion={Version} in file {FilePath}. Current supported version is {CurrentVersion}",
                    settings.SchemaVersion, filePath, WorkspaceSettings.CurrentSchemaVersion);
                throw new InvalidOperationException(
                    $"Workspace file '{filePath}' has an unsupported schema version ({settings.SchemaVersion}). " +
                    $"Current supported schema version is {WorkspaceSettings.CurrentSchemaVersion}.");
            }

            // Execute schema migration if loading legacy Version 1 file
            if (settings.SchemaVersion == 1)
            {
                MigrateV1ToV2(settings);
            }

            _logger.LogInformation("Successfully imported portable workspace (SchemaVersion={Version}) from {FilePath}", settings.SchemaVersion, filePath);
            return settings;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Migrates a Version 1 workspace schema to Version 2 portable format.
    /// </summary>
    private void MigrateV1ToV2(WorkspaceSettings settings)
    {
        _logger.LogInformation("Migrating workspace '{Name}' from SchemaVersion=1 to SchemaVersion=2", settings.Name);

        // Update schema version flag
        settings.SchemaVersion = 2;

        // Ensure default structures are non-null
        settings.ThemeSettings ??= new();
        settings.Indicators ??= new();
        settings.PanelSelectedTabIndices ??= new();
        settings.PanelTabIds ??= new();
        settings.WatchlistProfiles ??= new();
        settings.DetachedTabs ??= new();
        settings.PanelCharts ??= new();
        settings.TagFilters ??= new();
    }

    /// <summary>
    /// Helper method to replace target file atomically with fallback to File.Move.
    /// </summary>
    private void AtomicReplaceFile(string tempPath, string targetPath, string? backupPath = null)
    {
        if (File.Exists(targetPath))
        {
            try
            {
                File.Replace(tempPath, targetPath, backupPath);
            }
            catch (Exception replaceEx)
            {
                _logger.LogWarning(replaceEx, "File.Replace failed for {FilePath}. Falling back to File.Move with overwrite.", targetPath);
                File.Move(tempPath, targetPath, overwrite: true);
            }
        }
        else
        {
            File.Move(tempPath, targetPath, overwrite: true);
        }
    }

    public void Dispose()
    {
        _semaphore.Dispose();
        GC.SuppressFinalize(this);
    }
}
