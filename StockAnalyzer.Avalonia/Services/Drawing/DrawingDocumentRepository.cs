using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Avalonia.Drawing.Serialization;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;

namespace StockAnalyzer.Avalonia.Services.Drawing;

/// <summary>
/// File-backed IDrawingDocumentRepository implementation for V2 drawing documents.
/// Provides atomic file persistence with pre-replacement backups, sequential revision safety,
/// and protection of legacy formats against unintended overwrites.
/// </summary>
public class DrawingDocumentRepository : IDrawingDocumentRepository
{
    private readonly ChartDrawingRepository _legacyRepository;
    private readonly ILogger<DrawingDocumentRepository> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();
    private readonly ConcurrentDictionary<DrawingDocumentKey, long> _latestCommittedRevisions = new();

    public DrawingDocumentRepository(
        ChartDrawingRepository? legacyRepository = null,
        ILogger<DrawingDocumentRepository>? logger = null)
    {
        _legacyRepository = legacyRepository ?? new ChartDrawingRepository();
        _logger = logger ?? NullLogger<DrawingDocumentRepository>.Instance;
    }

    private SemaphoreSlim GetLock(string path) => _fileLocks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));

    public async Task<DrawingLoadResult> LoadDocumentAsync(DrawingDocumentKey key, CancellationToken ct = default)
    {
        try
        {
            if (ct.IsCancellationRequested)
            {
                return DrawingLoadResult.CancelledResult();
            }

            if (!_legacyRepository.TryResolveDocumentPath(key.Ticker, key.Timeframe, out var path))
            {
                return DrawingLoadResult.InvalidDataResult($"Cannot resolve storage path for ticker '{key.Ticker}' and timeframe '{key.Timeframe}'.");
            }

            var fileLock = GetLock(path);
            try
            {
                await fileLock.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return DrawingLoadResult.CancelledResult();
            }

            try
            {
                if (!File.Exists(path))
                {
                    return DrawingLoadResult.NotFoundResult();
                }

                string json;
                try
                {
                    json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return DrawingLoadResult.CancelledResult();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to read drawing document file at {Path}", path);
                    return DrawingLoadResult.IoFailureResult(ex.Message);
                }

                if (string.IsNullOrWhiteSpace(json))
                {
                    return DrawingLoadResult.InvalidDataResult("Drawing document file is empty.");
                }

                // Inspect schema version
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (!root.TryGetProperty("version", out var versionEl))
                    {
                        // No "version" property: legacy format
                        return DrawingLoadResult.MigrationRequiredResult("Legacy format detected. Migration to V2 required.");
                    }

                    int version = versionEl.GetInt32();
                    if (version != DrawingDocumentState.CurrentSchemaVersion)
                    {
                        return DrawingLoadResult.UnsupportedVersionResult($"Document schema version {version} is not supported. Current version is {DrawingDocumentState.CurrentSchemaVersion}.");
                    }

                    var document = DrawingDocumentCodec.DeserializeFromJson(json);

                    // Verify key match
                    if (document.Key != key)
                    {
                        return DrawingLoadResult.InvalidDataResult($"Document key mismatch: expected '{key}', found '{document.Key}'.");
                    }

                    _latestCommittedRevisions.AddOrUpdate(key, document.Revision, (_, existing) => Math.Max(existing, document.Revision));
                    return DrawingLoadResult.Succeeded(document);
                }
                catch (JsonException jEx)
                {
                    _logger.LogWarning(jEx, "Corrupted drawing document at {Path}, creating backup", path);
                    try
                    {
                        var corruptedPath = path + ".corrupted";
                        File.Copy(path, corruptedPath, overwrite: true);
                    }
                    catch
                    {
                        // Best-effort corruption backup
                    }
                    return DrawingLoadResult.InvalidDataResult($"Corrupted JSON: {jEx.Message}");
                }
            }
            finally
            {
                fileLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return DrawingLoadResult.CancelledResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error loading drawing document for key {Key}", key);
            return DrawingLoadResult.IoFailureResult(ex.Message);
        }
    }

    public async Task<DrawingSaveResult> SaveDocumentAsync(DrawingDocumentState document, CancellationToken ct = default)
    {
        try
        {
            if (ct.IsCancellationRequested)
            {
                return DrawingSaveResult.CancelledResult();
            }

            var key = document.Key;
            if (!_legacyRepository.TryResolveDocumentPath(key.Ticker, key.Timeframe, out var path))
            {
                return DrawingSaveResult.IoFailureResult($"Cannot resolve storage path for ticker '{key.Ticker}' and timeframe '{key.Timeframe}'.");
            }

            var fileLock = GetLock(path);
            try
            {
                await fileLock.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return DrawingSaveResult.CancelledResult();
            }

            try
            {
                // Re-verify revision INSIDE file lock (R03 Double-Checked Locking resolution)
                if (_latestCommittedRevisions.TryGetValue(key, out var committedRev) && document.Revision < committedRev)
                {
                    return DrawingSaveResult.IoFailureResult($"Stale revision {document.Revision} cannot overwrite committed revision {committedRev}.");
                }

                // Do not create a new file if the document has no objects or custom layers, and file does not exist (V02)
                bool isEmpty = document.Contexts.All(c =>
                    c.Objects.Count == 0 &&
                    (c.Layers.Count == 0 || (c.Layers.Count == 1 && c.Layers[0].Name == "Default Layer" && c.Layers[0].IsVisible && !c.Layers[0].IsEditLocked)));
                if (isEmpty && !File.Exists(path))
                {
                    _latestCommittedRevisions[key] = document.Revision;
                    return DrawingSaveResult.Succeeded(document.Revision);
                }

                return await SaveDrawingDocumentAtomicAsync(path, document, ct).ConfigureAwait(false);
            }
            finally
            {
                fileLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return DrawingSaveResult.CancelledResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error saving drawing document for key {Key}", document.Key);
            return DrawingSaveResult.IoFailureResult(ex.Message);
        }
    }

    private async Task<DrawingSaveResult> SaveDrawingDocumentAtomicAsync(
        string path,
        DrawingDocumentState document,
        CancellationToken ct)
    {
        // Cancellation accepted strictly before atomic replacement begins
        if (ct.IsCancellationRequested)
        {
            return DrawingSaveResult.CancelledResult();
        }

        // Verify that existing file is not a legacy format (R08: legacy protection)
        if (File.Exists(path))
        {
            try
            {
                var existingHeader = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(existingHeader))
                {
                    using var existingDoc = JsonDocument.Parse(existingHeader);
                    if (!existingDoc.RootElement.TryGetProperty("version", out _))
                    {
                        return DrawingSaveResult.IoFailureResult("Cannot overwrite legacy format drawing file without explicit migration.");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return DrawingSaveResult.CancelledResult();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to verify existing drawing document format at {Path}", path);
                return DrawingSaveResult.IoFailureResult($"Failed to verify existing format: {ex.Message}");
            }
        }

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }
        catch (Exception ex)
        {
            return DrawingSaveResult.IoFailureResult($"Failed to create directory: {ex.Message}");
        }

        string tempPath = path + ".tmp";
        string json;
        try
        {
            json = DrawingDocumentCodec.SerializeToJson(document);
        }
        catch (Exception ex)
        {
            return DrawingSaveResult.IoFailureResult($"Serialization failed: {ex.Message}");
        }

        try
        {
            await File.WriteAllTextAsync(tempPath, json, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryCleanupFile(tempPath);
            return DrawingSaveResult.CancelledResult();
        }
        catch (Exception ex)
        {
            TryCleanupFile(tempPath);
            _logger.LogError(ex, "Failed to write temporary drawing file for {Path}", path);
            return DrawingSaveResult.IoFailureResult(ex.Message);
        }

        // Final cancellation check before committing atomic replacement
        if (ct.IsCancellationRequested)
        {
            TryCleanupFile(tempPath);
            return DrawingSaveResult.CancelledResult();
        }

        // Pre-replacement backup: MUST succeed before proceeding to replacement (R07)
        if (File.Exists(path))
        {
            string backupPath = path + ".bak";
            if (File.Exists(backupPath))
            {
                // Suffix with revision and random token to preserve all generation history
                backupPath = $"{path}.bak.rev{document.Revision}_{Guid.NewGuid():N}";
            }

            try
            {
                File.Copy(path, backupPath, overwrite: false);
            }
            catch (Exception ex)
            {
                TryCleanupFile(tempPath);
                _logger.LogError(ex, "Backup creation failed for {Path}. Aborting save to protect original data.", path);
                return DrawingSaveResult.IoFailureResult($"Pre-replacement backup failed: {ex.Message}");
            }
        }

        // Atomic file replacement: Once started, do not report Cancelled on completion
        try
        {
            if (File.Exists(path))
            {
                try
                {
                    File.Replace(tempPath, path, null);
                }
                catch (IOException)
                {
                    // Fallback for virtual filesystems / volume boundaries where File.Replace fails
                    File.Move(tempPath, path, overwrite: true);
                }
            }
            else
            {
                File.Move(tempPath, path, overwrite: true);
            }

            _latestCommittedRevisions[document.Key] = document.Revision;
            return DrawingSaveResult.Succeeded(document.Revision);
        }
        catch (Exception ex)
        {
            TryCleanupFile(tempPath);
            _logger.LogError(ex, "Failed atomic replace for drawing document {Path}", path);
            return DrawingSaveResult.IoFailureResult(ex.Message);
        }
    }

    private static void TryCleanupFile(string path)
    {
        if (File.Exists(path))
        {
            try { File.Delete(path); } catch { /* Ignore cleanup errors */ }
        }
    }
}
