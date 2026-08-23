using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using StockAnalyzer.Core.Models.Notes;

namespace StockAnalyzer.Core.Services.Notes;

/// <summary>Outcome of a single <see cref="AttachmentRepository.SaveAsync"/> call.</summary>
public sealed record AttachmentSaveResult(bool Success, Attachment? Attachment, string? FailureReason)
{
    public static AttachmentSaveResult Ok(Attachment attachment) => new(true, attachment, null);
    public static AttachmentSaveResult Failed(string reason) => new(false, null, reason);
}

/// <summary>
/// Validates, stores, and tracks image attachments for Notes (spec section 8). Files live on disk
/// under Data\Notes\Attachments\{uuid}.{ext}; the Attachments table (created by
/// <see cref="NoteSchemaInitializer"/>) holds only their metadata.
/// </summary>
public sealed class AttachmentRepository
{
    private const int MaxWidthPixels = 4000;
    private const int MaxHeightPixels = 3000;
    private const long MaxFileSizeBytes = 20L * 1024 * 1024;

    private static readonly IReadOnlyDictionary<string, (string MimeType, SKEncodedImageFormat Format)> AllowedExtensions =
        new Dictionary<string, (string, SKEncodedImageFormat)>(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = ("image/jpeg", SKEncodedImageFormat.Jpeg),
            [".jpeg"] = ("image/jpeg", SKEncodedImageFormat.Jpeg),
            [".png"] = ("image/png", SKEncodedImageFormat.Png),
            [".webp"] = ("image/webp", SKEncodedImageFormat.Webp),
        };

    private readonly NoteDatabaseConnectionManager _connectionManager;
    private readonly ILogger<AttachmentRepository>? _logger;
    private readonly string _attachmentsDirectory;

    public AttachmentRepository(NoteDatabaseConnectionManager connectionManager, ILogger<AttachmentRepository>? logger = null)
    {
        _connectionManager = connectionManager;
        _logger = logger ?? NullLogger<AttachmentRepository>.Instance;
        _attachmentsDirectory = Path.Combine(Path.GetDirectoryName(connectionManager.DatabasePath)!, "Attachments");
        Directory.CreateDirectory(_attachmentsDirectory);
    }

    /// <summary>
    /// Validates an image (extension -> declared header size -> full decode -> re-validation,
    /// per spec section 8), strips EXIF by re-encoding the decoded pixels, and saves it under a
    /// UUID file name. Never throws for expected validation failures - callers processing
    /// multiple attachments should skip a failed one and keep saving the rest of the Note.
    /// </summary>
    public async Task<AttachmentSaveResult> SaveAsync(byte[] imageBytes, string originalFileName, CancellationToken ct = default)
    {
        try
        {
            var extension = Path.GetExtension(originalFileName);
            if (string.IsNullOrEmpty(extension) || !AllowedExtensions.TryGetValue(extension, out var formatInfo))
            {
                return AttachmentSaveResult.Failed($"Unsupported file extension '{extension}'. Only JPEG/PNG/WebP are allowed.");
            }

            if (imageBytes.Length == 0 || imageBytes.LongLength > MaxFileSizeBytes)
            {
                return AttachmentSaveResult.Failed($"Image file size exceeds the {MaxFileSizeBytes / (1024 * 1024)}MB limit.");
            }

            // Header-only peek before a full decode guards against decompression bombs (spec section 8).
            var headerInfo = SKBitmap.DecodeBounds(imageBytes);
            if (headerInfo.Width <= 0 || headerInfo.Height <= 0)
            {
                return AttachmentSaveResult.Failed("Could not read image header; the file may be corrupted.");
            }
            if (headerInfo.Width > MaxWidthPixels || headerInfo.Height > MaxHeightPixels)
            {
                return AttachmentSaveResult.Failed($"Image resolution {headerInfo.Width}x{headerInfo.Height} exceeds the {MaxWidthPixels}x{MaxHeightPixels} limit.");
            }

            using var bitmap = SKBitmap.Decode(imageBytes);
            if (bitmap is null)
            {
                return AttachmentSaveResult.Failed("Failed to decode image; the file may be corrupted.");
            }
            if (bitmap.Width > MaxWidthPixels || bitmap.Height > MaxHeightPixels)
            {
                return AttachmentSaveResult.Failed($"Decoded image resolution {bitmap.Width}x{bitmap.Height} exceeds the {MaxWidthPixels}x{MaxHeightPixels} limit.");
            }

            // Re-encoding from decoded pixels (rather than copying the original bytes) strips EXIF
            // metadata by construction, satisfying spec section 8 without a separate EXIF library.
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(formatInfo.Format, 90);
            if (encoded is null)
            {
                return AttachmentSaveResult.Failed("Failed to encode image for storage.");
            }

            var attachmentId = Guid.NewGuid();
            var storedFileName = $"{attachmentId}{extension.ToLowerInvariant()}";
            var filePath = Path.Combine(_attachmentsDirectory, storedFileName);
            await File.WriteAllBytesAsync(filePath, encoded.ToArray(), ct).ConfigureAwait(false);

            var attachment = new Attachment(
                attachmentId,
                storedFileName,
                originalFileName,
                formatInfo.MimeType,
                bitmap.Width,
                bitmap.Height,
                encoded.Size,
                DateTime.Now);

            await InsertAttachmentRowAsync(attachment, ct).ConfigureAwait(false);

            _logger?.LogDebug("Saved attachment {AttachmentId} ({StoredFileName}).", attachmentId, storedFileName);
            return AttachmentSaveResult.Ok(attachment);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to save attachment '{OriginalFileName}'; skipping.", originalFileName);
            return AttachmentSaveResult.Failed($"Unexpected error while saving image: {ex.Message}");
        }
    }

    /// <summary>Looks up a single attachment's metadata by Id, or null if no such row exists (e.g.
    /// a stale/corrupted AttachmentId). Used by NoteCardView's thumbnail loading (spec section 5.2)
    /// to resolve an AttachmentId into a file to decode.</summary>
    public async Task<Attachment?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        using var connection = await _connectionManager.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, StoredFileName, OriginalFileName, MimeType, Width, Height, FileSizeBytes, CreatedAt
            FROM Attachments WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString());

        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new Attachment(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt64(6),
            DateTime.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    /// <summary>Resolves an attachment's metadata to its full path on disk under Data\Notes\Attachments\.</summary>
    public string GetFilePath(Attachment attachment) => Path.Combine(_attachmentsDirectory, attachment.StoredFileName);

    /// <summary>
    /// Compares files on disk under Data\Notes\Attachments\ against every AttachmentId referenced
    /// by any Note - including logically-deleted ones, which still count as a valid reference per
    /// spec section 4.5 - and returns the file names referenced by none. Detection only; this
    /// method never deletes files.
    /// </summary>
    public async Task<IReadOnlyList<string>> FindOrphanedFilesAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_attachmentsDirectory))
        {
            return Array.Empty<string>();
        }

        var referencedFileNames = await GetReferencedStoredFileNamesAsync(ct).ConfigureAwait(false);

        var orphaned = new List<string>();
        foreach (var filePath in Directory.EnumerateFiles(_attachmentsDirectory))
        {
            var fileName = Path.GetFileName(filePath);
            if (!referencedFileNames.Contains(fileName))
            {
                orphaned.Add(fileName);
            }
        }

        return orphaned;
    }

    private async Task InsertAttachmentRowAsync(Attachment attachment, CancellationToken ct)
    {
        using var connection = await _connectionManager.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Attachments (Id, StoredFileName, OriginalFileName, MimeType, Width, Height, FileSizeBytes, CreatedAt)
            VALUES ($id, $storedFileName, $originalFileName, $mimeType, $width, $height, $fileSizeBytes, $createdAt);
            """;
        command.Parameters.AddWithValue("$id", attachment.Id.ToString());
        command.Parameters.AddWithValue("$storedFileName", attachment.StoredFileName);
        command.Parameters.AddWithValue("$originalFileName", attachment.OriginalFileName);
        command.Parameters.AddWithValue("$mimeType", attachment.MimeType);
        command.Parameters.AddWithValue("$width", attachment.Width);
        command.Parameters.AddWithValue("$height", attachment.Height);
        command.Parameters.AddWithValue("$fileSizeBytes", attachment.FileSizeBytes);
        command.Parameters.AddWithValue("$createdAt", attachment.CreatedAt.ToString("o", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<HashSet<string>> GetReferencedStoredFileNamesAsync(CancellationToken ct)
    {
        var referencedAttachmentIds = await GetAllAttachmentIdsReferencedByNotesAsync(ct).ConfigureAwait(false);
        var storedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (referencedAttachmentIds.Count == 0)
        {
            return storedFileNames;
        }

        using var connection = await _connectionManager.OpenConnectionAsync(ct).ConfigureAwait(false);
        var idsList = referencedAttachmentIds.ToList();
        var placeholders = string.Join(",", Enumerable.Range(0, idsList.Count).Select(i => $"$id{i}"));
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT StoredFileName FROM Attachments WHERE Id IN ({placeholders});";
        for (var i = 0; i < idsList.Count; i++)
        {
            command.Parameters.AddWithValue($"$id{i}", idsList[i].ToString());
        }

        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            storedFileNames.Add(reader.GetString(0));
        }

        return storedFileNames;
    }

    private async Task<HashSet<Guid>> GetAllAttachmentIdsReferencedByNotesAsync(CancellationToken ct)
    {
        using var connection = await _connectionManager.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        // Intentionally includes IsDeleted=1 rows: spec section 4.5 treats logically-deleted
        // Notes' attachments as still referenced until the Note is permanently deleted.
        command.CommandText = "SELECT AttachmentIdsJson FROM Notes;";

        var ids = new HashSet<Guid>();
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0))
            {
                continue;
            }

            var json = reader.GetString(0);
            if (string.IsNullOrEmpty(json))
            {
                continue;
            }

            var idStrings = JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
            foreach (var idString in idStrings)
            {
                if (Guid.TryParse(idString, out var id))
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
    }
}
