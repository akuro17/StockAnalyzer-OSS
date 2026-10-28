#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.ReinforcementLearning;

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
    string? Error,
    RlArchiveImportFailure Reason = RlArchiveImportFailure.None,
    string? ReasonArgument = null);

/// <summary>
/// Why an import was refused, for the caller that shows it in its own language. <see cref="RlArchiveImportResult.Error"/> keeps the technical English
/// text for logs and tool output; <see cref="RlArchiveImportResult.ReasonArgument"/> carries the entry key or the bound the reason names.
/// </summary>
public enum RlArchiveImportFailure
{
    /// <summary>The import succeeded (or the result predates reasons).</summary>
    None,
    /// <summary>Anything not listed below; only <see cref="RlArchiveImportResult.Error"/> describes it.</summary>
    Other,
    NotFound,
    InvalidManifestId,
    PathEscape,
    MissingFileHashes,
    EntryNotListed,
    ManifestEntryMissing,
    EntryCollision,
    HashMismatch,
    TooManyEntries,
    EntryTooLarge,
}

/// <summary>
/// Safety bounds of one archive extraction (decompression-bomb guard). They are code constants, not settings: a bound that protects the disk must not be
/// editable. No total bound is needed, it follows from the two: at most <see cref="MaxEntries"/> files of at most <see cref="MaxEntryBytes"/> each.
/// </summary>
/// <param name="MaxEntryBytes">Largest uncompressed size of one entry.</param>
/// <param name="MaxEntries">Largest number of payload entries (the archive manifest is not counted).</param>
public sealed record RlArchiveLimits(long MaxEntryBytes, int MaxEntries)
{
    /// <summary>One entry is at most the largest file an RL run captures (<see cref="RlContract.BufferByteLimit"/>); an archive holds at most every file a generation can have plus its manifest.json.</summary>
    public static RlArchiveLimits Default { get; } = new(RlContract.BufferByteLimit, ModelGenerationRegistry.MaxRlGenerationFileCount + 1);

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxEntryBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxEntries);
    }
}

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
    public const string CheckpointKind = "Checkpoint";
    public const string GenerationKind = "Generation";
    public const string GenericKind = "Generic";
    private const string StagingDirectoryPrefix = ".import-";
    private const string BackupSuffix = ".bak";
    private const int CopyBufferBytes = 81_920;
    private readonly RlArchiveLimits _limits;

    /// <param name="limits">The extraction bounds; null uses <see cref="RlArchiveLimits.Default"/>.</param>
    public RlModelArchiveService(RlArchiveLimits? limits = null)
    {
        _limits = limits ?? RlArchiveLimits.Default;
        _limits.Validate();
    }

    public async Task<string> ExportCheckpointArchiveAsync(string checkpointDirectory, string destinationZipPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationZipPath);

        if (!Directory.Exists(checkpointDirectory))
            throw new DirectoryNotFoundException($"Checkpoint directory '{checkpointDirectory}' does not exist.");

        string id = Path.GetFileName(checkpointDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Type"] = CheckpointKind,
            ["SourceDirectory"] = id,
        };

        return await PackageDirectoryAsync(checkpointDirectory, destinationZipPath, CheckpointKind, id, metadata, ct).ConfigureAwait(false);
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
            ["Type"] = GenerationKind,
            ["SourceDirectory"] = id,
        };

        return await PackageDirectoryAsync(generationDirectory, destinationZipPath, GenerationKind, id, metadata, ct).ConfigureAwait(false);
    }

    public async Task<RlArchiveImportResult> ImportArchiveAsync(string sourceZipPath, string destinationDirectory, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceZipPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        if (!File.Exists(sourceZipPath))
            return new RlArchiveImportResult(false, "", "", "", 0, $"Archive file '{sourceZipPath}' not found.", RlArchiveImportFailure.NotFound);

        string? createdTargetDir = null;
        bool succeeded = false;
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
                        $"Manifest Id '{manifest.Id}' contains invalid characters or path separators.", RlArchiveImportFailure.InvalidManifestId, manifest.Id);
                }

                string candidateDir = Path.GetFullPath(Path.Combine(fullDest, manifest.Id));
                if (!candidateDir.StartsWith(safeBasePrefix, pathComparison))
                {
                    return new RlArchiveImportResult(false, "", "", destinationDirectory, 0,
                        $"Manifest Id '{manifest.Id}' escapes destination directory.", RlArchiveImportFailure.InvalidManifestId, manifest.Id);
                }

                targetDir = candidateDir;
                if (!Directory.Exists(targetDir)) createdTargetDir = targetDir;
                Directory.CreateDirectory(targetDir);
            }
            string fullTargetDir = Path.GetFullPath(targetDir);
            string safeTargetPrefix = fullTargetDir.EndsWith(Path.DirectorySeparatorChar)
                ? fullTargetDir
                : fullTargetDir + Path.DirectorySeparatorChar;

            var limitFailure = CheckLimits(archive);
            if (limitFailure is not null)
                return Failure(destinationDirectory, limitFailure);

            int fileCount = 0;
            if (manifest is null)
            {
                // No manifest: nothing to verify against, imported as GenericKind.
                foreach (var entry in archive.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
                    if (entry.FullName == ManifestEntryName) continue;

                    string destPath = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));
                    if (!destPath.StartsWith(safeTargetPrefix, pathComparison))
                        throw EscapesTarget(entry.FullName);

                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    ExtractBounded(entry, destPath, entry.FullName.Replace('\\', '/'));
                    fileCount++;
                }
            }
            else
            {
                var verified = ExtractVerified(archive, manifest, targetDir, safeTargetPrefix, pathComparison, ct);
                if (verified.Failure is not null)
                    return Failure(destinationDirectory, verified.Failure);
                fileCount = verified.FileCount;
            }

            string kind = manifest?.ArchiveKind ?? GenericKind;
            string id = manifest?.Id ?? Path.GetFileNameWithoutExtension(sourceZipPath);
            succeeded = true;
            return new RlArchiveImportResult(true, kind, id, targetDir, fileCount, null);
        }
        catch (ArchiveRefusedException ex)
        {
            return Failure(destinationDirectory, ex.Refusal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new RlArchiveImportResult(false, "", "", destinationDirectory, 0, ex.Message, RlArchiveImportFailure.Other);
        }
        finally
        {
            // A rejected import must not leave the Id folder it created behind.
            if (!succeeded && createdTargetDir is not null) TryDeleteEmptyDirectory(createdTargetDir);
        }
    }

    /// <summary>
    /// Extracts every payload entry into a private staging folder, verifies each against
    /// <see cref="RlArchiveManifest.FileHashes"/> and moves the files into place only when all match.
    /// The manifest travels inside the same zip, so this detects corruption, not tampering.
    /// </summary>
    private (int FileCount, Refusal? Failure) ExtractVerified(
        ZipArchive archive,
        RlArchiveManifest manifest,
        string targetDir,
        string safeTargetPrefix,
        StringComparison pathComparison,
        CancellationToken ct)
    {
        string stagingDir = Path.GetFullPath(Path.Combine(targetDir, StagingDirectoryPrefix + Guid.NewGuid().ToString("N")));
        string stagingPrefix = stagingDir + Path.DirectorySeparatorChar;

        var entries = new List<(string Key, ZipArchiveEntry Entry, string StagedPath, string FinalPath)>();
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
            if (entry.FullName == ManifestEntryName) continue;

            string finalPath = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));
            string stagedPath = Path.GetFullPath(Path.Combine(stagingDir, entry.FullName));
            if (!finalPath.StartsWith(safeTargetPrefix, pathComparison) || !stagedPath.StartsWith(stagingPrefix, pathComparison))
                throw EscapesTarget(entry.FullName);

            entries.Add((entry.FullName.Replace('\\', '/'), entry, stagedPath, finalPath));
        }

        var hashes = manifest.FileHashes;
        if (hashes is null) return (0, new Refusal(RlArchiveImportFailure.MissingFileHashes, null, "Archive manifest has no file hashes."));

        foreach (var item in entries)
        {
            if (!hashes.ContainsKey(item.Key))
                return (0, new Refusal(RlArchiveImportFailure.EntryNotListed, item.Key, $"Archive entry '{item.Key}' is not listed in the manifest."));
        }

        var presentKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in entries) presentKeys.Add(item.Key);
        foreach (string listedKey in hashes.Keys)
        {
            if (!presentKeys.Contains(listedKey))
                return (0, new Refusal(RlArchiveImportFailure.ManifestEntryMissing, listedKey, $"Archive manifest lists '{listedKey}' but the archive does not contain it."));
        }

        // Two entries that resolve to one file (the same name twice, or names that differ only by case on a case-insensitive file system)
        // would overwrite each other in staging after the first one was verified.
        var finalPaths = new HashSet<string>(StringComparer.FromComparison(pathComparison));
        foreach (var item in entries)
        {
            if (!finalPaths.Add(item.FinalPath))
                return (0, new Refusal(RlArchiveImportFailure.EntryCollision, item.Key, $"Archive entry '{item.Key}' collides with another entry of the archive."));
        }

        try
        {
            foreach (var item in entries)
            {
                ct.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(item.StagedPath)!);
                ExtractBounded(item.Entry, item.StagedPath, item.Key);

                if (!string.Equals(ContentHash.OfFile(item.StagedPath), hashes[item.Key], StringComparison.OrdinalIgnoreCase))
                    return (0, new Refusal(RlArchiveImportFailure.HashMismatch, item.Key, $"Archive entry '{item.Key}' failed SHA-256 verification."));
            }

            // Publish: an existing file is replaced with a backup kept in the staging folder, so a failure part-way restores every file already published.
            var published = new List<(string Final, string? Backup)>();
            try
            {
                foreach (var item in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    Directory.CreateDirectory(Path.GetDirectoryName(item.FinalPath)!);
                    if (File.Exists(item.FinalPath))
                    {
                        string backup = item.StagedPath + BackupSuffix;
                        File.Replace(item.StagedPath, item.FinalPath, backup);
                        published.Add((item.FinalPath, backup));
                    }
                    else
                    {
                        File.Move(item.StagedPath, item.FinalPath);
                        published.Add((item.FinalPath, null));
                    }
                }
            }
            catch
            {
                RollBack(published);
                throw;
            }

            return (entries.Count, null);
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort staging cleanup
            }
        }
    }

    private Refusal TooLarge(string key)
        => new(RlArchiveImportFailure.EntryTooLarge, key, $"Archive entry '{key}' exceeds the size limit of {_limits.MaxEntryBytes} bytes.");

    private static ArchiveRefusedException EscapesTarget(string entryName)
        => new(new Refusal(RlArchiveImportFailure.PathEscape, entryName, $"Entry '{entryName}' escapes target directory (ZipSlip prevention)."));

    private static RlArchiveImportResult Failure(string destinationDirectory, Refusal refusal)
        => new(false, "", "", destinationDirectory, 0, refusal.Message, refusal.Reason, refusal.Argument);

    /// <summary>A refusal with the reason a caller can translate and the technical text for logs.</summary>
    private sealed record Refusal(RlArchiveImportFailure Reason, string? Argument, string Message);

    /// <summary>Carries a <see cref="Refusal"/> out of the extraction code that cannot return it; <see cref="ImportArchiveAsync"/> turns it into a failed result.</summary>
    private sealed class ArchiveRefusedException(Refusal refusal) : Exception(refusal.Message)
    {
        public Refusal Refusal { get; } = refusal;
    }

    /// <summary>Rejects an archive with too many payload entries or an entry whose declared size is above the bound; nothing has been extracted yet.</summary>
    private Refusal? CheckLimits(ZipArchive archive)
    {
        int count = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
            if (entry.FullName == ManifestEntryName) continue;
            if (++count > _limits.MaxEntries)
                return new Refusal(RlArchiveImportFailure.TooManyEntries, _limits.MaxEntries.ToString(CultureInfo.InvariantCulture), $"Archive holds more than {_limits.MaxEntries} entries.");
            if (entry.Length > _limits.MaxEntryBytes)
                return TooLarge(entry.FullName.Replace('\\', '/'));
        }

        return null;
    }

    /// <summary>
    /// Writes one entry and stops as soon as it holds more than the bound. The declared size in the zip directory can be false, so the bytes actually
    /// produced are counted; a partly written file is deleted.
    /// </summary>
    private void ExtractBounded(ZipArchiveEntry entry, string destination, string key)
    {
        try
        {
            using var input = entry.Open();
            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[CopyBufferBytes];
            long written = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                written += read;
                if (written > _limits.MaxEntryBytes)
                    throw new ArchiveRefusedException(TooLarge(key));
                output.Write(buffer, 0, read);
            }
        }
        catch
        {
            try { File.Delete(destination); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* leave it; the staging or target cleanup removes it */ }
            throw;
        }
    }

    /// <summary>Undoes a part-way publish in reverse order: a replaced file gets its previous content back, a new file is deleted. Best effort.</summary>
    private static void RollBack(List<(string Final, string? Backup)> published)
    {
        for (int i = published.Count - 1; i >= 0; i--)
        {
            var (final, backup) = published[i];
            try
            {
                if (backup is not null) File.Move(backup, final, overwrite: true);
                else File.Delete(final);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nothing more can be done for this file; the others are still restored.
            }
        }
    }

    private static void TryDeleteEmptyDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not empty or in use: leave it.
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
