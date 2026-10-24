#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>
/// Manifest recorded inside every exported checkpoint or generation archive (.zip).
/// </summary>
public sealed record RlArchiveManifest(
    int SchemaVersion,
    string ArchiveKind,
    string Id,
    DateTimeOffset CreatedUtc,
    Dictionary<string, string> Metadata,
    Dictionary<string, string> FileHashes);

/// <summary>
/// Result of importing an external RL archive into the local repository or store.
/// </summary>
public sealed record RlArchiveImportResult(
    bool Success,
    string ArchiveKind,
    string Id,
    string TargetDirectory,
    int FileCount,
    string? Error);

/// <summary>
/// Service interface for archiving, exporting, and importing RL checkpoints and trained model generations.
/// </summary>
public interface IRlModelArchiveService
{
    Task<string> ExportCheckpointArchiveAsync(string checkpointDirectory, string destinationZipPath, CancellationToken ct = default);
    Task<string> ExportGenerationArchiveAsync(string generationDirectory, string destinationZipPath, CancellationToken ct = default);
    Task<RlArchiveImportResult> ImportArchiveAsync(string sourceZipPath, string destinationDirectory, CancellationToken ct = default);
}

/// <summary>
/// Production implementation of RL model and checkpoint archive packaging.
/// </summary>
public sealed class RlModelArchiveService : IRlModelArchiveService
{
    public const int CurrentSchemaVersion = 1;
    public const string ManifestEntryName = "archive_manifest.json";

    public async Task<string> ExportCheckpointArchiveAsync(string checkpointDirectory, string destinationZipPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationZipPath);

        if (!Directory.Exists(checkpointDirectory))
            throw new DirectoryNotFoundException($"Checkpoint directory '{checkpointDirectory}' does not exist.");

        string id = Path.GetFileName(checkpointDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Type"] = "Checkpoint",
            ["SourceDirectory"] = id,
        };

        return await PackageDirectoryAsync(checkpointDirectory, destinationZipPath, "Checkpoint", id, metadata, ct).ConfigureAwait(false);
    }

    public async Task<string> ExportGenerationArchiveAsync(string generationDirectory, string destinationZipPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generationDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationZipPath);

        if (!Directory.Exists(generationDirectory))
            throw new DirectoryNotFoundException($"Generation directory '{generationDirectory}' does not exist.");

        string id = Path.GetFileName(generationDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Type"] = "Generation",
            ["SourceDirectory"] = id,
        };

        return await PackageDirectoryAsync(generationDirectory, destinationZipPath, "Generation", id, metadata, ct).ConfigureAwait(false);
    }

    public async Task<RlArchiveImportResult> ImportArchiveAsync(string sourceZipPath, string destinationDirectory, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceZipPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        if (!File.Exists(sourceZipPath))
            return new RlArchiveImportResult(false, "", "", "", 0, $"Archive file '{sourceZipPath}' not found.");

        try
        {
            Directory.CreateDirectory(destinationDirectory);
            string fullDest = Path.GetFullPath(destinationDirectory);

            using var archive = ZipFile.OpenRead(sourceZipPath);
            ct.ThrowIfCancellationRequested();

            RlArchiveManifest? manifest = null;
            var manifestEntry = archive.GetEntry(ManifestEntryName);
            if (manifestEntry is not null)
            {
                using var stream = manifestEntry.Open();
                manifest = await JsonSerializer.DeserializeAsync<RlArchiveManifest>(stream, cancellationToken: ct).ConfigureAwait(false);
            }

            string safeBasePrefix = fullDest.EndsWith(Path.DirectorySeparatorChar)
                ? fullDest
                : fullDest + Path.DirectorySeparatorChar;

            var pathComparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            string targetDir = fullDest;
            if (manifest is not null && !string.IsNullOrWhiteSpace(manifest.Id))
            {
                if (!PlainFileName.IsValid(manifest.Id))
                {
                    return new RlArchiveImportResult(false, "", "", destinationDirectory, 0,
                        $"Manifest Id '{manifest.Id}' contains invalid characters or path separators.");
                }

                string candidateDir = Path.GetFullPath(Path.Combine(fullDest, manifest.Id));
                if (!candidateDir.StartsWith(safeBasePrefix, pathComparison))
                {
                    return new RlArchiveImportResult(false, "", "", destinationDirectory, 0,
                        $"Manifest Id '{manifest.Id}' escapes destination directory.");
                }

                targetDir = candidateDir;
                Directory.CreateDirectory(targetDir);
            }
            string fullTargetDir = Path.GetFullPath(targetDir);
            string safeTargetPrefix = fullTargetDir.EndsWith(Path.DirectorySeparatorChar)
                ? fullTargetDir
                : fullTargetDir + Path.DirectorySeparatorChar;

            int fileCount = 0;
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
                if (entry.FullName == ManifestEntryName) continue;

                string destPath = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));
                if (!destPath.StartsWith(safeTargetPrefix, pathComparison))
                    throw new InvalidDataException($"Entry '{entry.FullName}' escapes target directory (ZipSlip prevention).");

                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                entry.ExtractToFile(destPath, overwrite: true);
                fileCount++;
            }

            string kind = manifest?.ArchiveKind ?? "Generic";
            string id = manifest?.Id ?? Path.GetFileNameWithoutExtension(sourceZipPath);
            return new RlArchiveImportResult(true, kind, id, targetDir, fileCount, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new RlArchiveImportResult(false, "", "", destinationDirectory, 0, ex.Message);
        }
    }

    private static async Task<string> PackageDirectoryAsync(
        string sourceDirectory,
        string destinationZipPath,
        string kind,
        string id,
        Dictionary<string, string> metadata,
        CancellationToken ct)
    {
        string? destDir = Path.GetDirectoryName(destinationZipPath);
        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

        var fileHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var files = Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories);

        foreach (string file in files)
        {
            string rel = Path.GetRelativePath(sourceDirectory, file).Replace('\\', '/');
            fileHashes[rel] = ContentHash.OfFile(file);
        }

        var manifest = new RlArchiveManifest(
            CurrentSchemaVersion,
            kind,
            id,
            DateTimeOffset.UtcNow,
            metadata,
            fileHashes);

        string tempZipPath = destinationZipPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var zipStream = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                foreach (string file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    string entryName = Path.GetRelativePath(sourceDirectory, file).Replace('\\', '/');
                    archive.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
                }

                var manifestEntry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
                using var entryStream = manifestEntry.Open();
                await JsonSerializer.SerializeAsync(entryStream, manifest, cancellationToken: ct).ConfigureAwait(false);
            }

            File.Move(tempZipPath, destinationZipPath, overwrite: true);
            return destinationZipPath;
        }
        finally
        {
            try
            {
                if (File.Exists(tempZipPath)) File.Delete(tempZipPath);
            }
            catch
            {
                // Best-effort temporary file cleanup
            }
        }
    }
}
