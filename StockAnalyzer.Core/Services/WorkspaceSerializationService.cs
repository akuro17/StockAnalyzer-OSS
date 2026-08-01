using System;
using System.IO;
using System.Text.Json;
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
            PropertyNameCaseInsensitive = true
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
                if (File.Exists(filePath))
                {
                    File.Replace(tempPath, filePath, filePath + LayoutConstants.BackupWorkspaceExtension);
                }
                else
                {
                    File.Move(tempPath, filePath);
                }
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
                if (File.Exists(filePath))
                {
                    File.Replace(tempPath, filePath, null);
                }
                else
                {
                    File.Move(tempPath, filePath, overwrite: true);
                }
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

    public void Dispose()
    {
        _semaphore.Dispose();
        GC.SuppressFinalize(this);
    }
}
