using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Notes;

namespace StockAnalyzer.Core.Services.Notes;

/// <summary>
/// Persists and retrieves <see cref="Note"/> records against the notes.db SQLite database.
/// Callers must ensure <see cref="NoteSchemaInitializer.InitializeAsync"/> has run before
/// using this repository.
/// </summary>
public sealed class NoteRepository
{
    private readonly NoteDatabaseConnectionManager _connectionManager;
    private readonly ILogger<NoteRepository>? _logger;

    public NoteRepository(NoteDatabaseConnectionManager connectionManager, ILogger<NoteRepository>? logger = null)
    {
        _connectionManager = connectionManager;
        _logger = logger ?? NullLogger<NoteRepository>.Instance;
    }

    public async Task CreateAsync(Note note, CancellationToken ct = default)
    {
        using var connection = await _connectionManager.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Notes (Id, Body, CreatedAt, UpdatedAt, ChartAnchorDate, ChartTimeframe, RelatedTicker, HashtagsJson, AttachmentIdsJson, LinkUrlsJson, IsPinned, IsDeleted, QuotedNoteId, ParentNoteId)
            VALUES ($id, $body, $createdAt, $updatedAt, $chartAnchorDate, $chartTimeframe, $relatedTicker, $hashtagsJson, $attachmentIdsJson, $linkUrlsJson, $isPinned, $isDeleted, $quotedNoteId, $parentNoteId);
            """;
        BindNoteParameters(command, note);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _logger?.LogDebug("Created Note {NoteId} for ticker {RelatedTicker}.", note.Id, note.RelatedTicker ?? "(none)");
    }

    public async Task<Note?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        using var connection = await _connectionManager.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Notes WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());

        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return MapNote(reader);
    }

    /// <summary>Batched form of <see cref="GetByIdAsync"/> - fetches every Note whose Id is in
    /// <paramref name="ids"/> with a single query instead of one round trip per id (each
    /// <see cref="GetByIdAsync"/> call opens its own connection, so looping it once per id is an N+1
    /// pattern). Duplicate ids are queried once; an id with no matching row is simply absent from the
    /// result rather than causing an error, mirroring <see cref="GetByIdAsync"/>'s null-for-missing
    /// contract.</summary>
    public async Task<IReadOnlyList<Note>> GetByIdsAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var distinctIds = ids.Distinct().ToList();
        if (distinctIds.Count == 0)
        {
            return Array.Empty<Note>();
        }

        using var connection = await _connectionManager.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();

        var parameterNames = new string[distinctIds.Count];
        for (var i = 0; i < distinctIds.Count; i++)
        {
            var parameterName = $"$id{i}";
            parameterNames[i] = parameterName;
            command.Parameters.AddWithValue(parameterName, distinctIds[i].ToString());
        }

        command.CommandText = $"SELECT * FROM Notes WHERE Id IN ({string.Join(", ", parameterNames)});";

        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var results = new List<Note>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(MapNote(reader));
        }

        return results;
    }

    /// <summary>Returns all non-deleted Notes, newest first by CreatedAt.</summary>
    public Task<IReadOnlyList<Note>> GetAllActiveAsync(CancellationToken ct = default) =>
        QueryNotesAsync("SELECT * FROM Notes WHERE IsDeleted = 0 ORDER BY CreatedAt DESC;", ct);

    /// <summary>Returns all logically-deleted Notes (the trash view, spec section 9.3), newest first by CreatedAt.</summary>
    public Task<IReadOnlyList<Note>> GetAllDeletedAsync(CancellationToken ct = default) =>
        QueryNotesAsync("SELECT * FROM Notes WHERE IsDeleted = 1 ORDER BY CreatedAt DESC;", ct);

    /// <summary>Returns just the count of logically-deleted Notes, for toolbar badge display (spec
    /// section 5.2) - a plain COUNT(*) rather than <see cref="GetAllDeletedAsync"/>'s full row fetch
    /// (Body/HashtagsJson/AttachmentIdsJson/LinkUrlsJson deserialization), since callers refreshing
    /// this badge on every filter-bar change only need the number, not the rows.</summary>
    public async Task<int> CountDeletedAsync(CancellationToken ct = default)
    {
        using var connection = await _connectionManager.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Notes WHERE IsDeleted = 1;";

        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(result);
    }

    /// <summary>Returns the direct, non-deleted replies to <paramref name="parentId"/> (spec section 6: Reply Chain), oldest first so a thread reads top-to-bottom in posting order.</summary>
    public async Task<IReadOnlyList<Note>> GetRepliesAsync(Guid parentId, CancellationToken ct = default)
    {
        using var connection = await _connectionManager.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Notes WHERE ParentNoteId = $parentId AND IsDeleted = 0 ORDER BY CreatedAt ASC;";
        command.Parameters.AddWithValue("$parentId", parentId.ToString());

        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var results = new List<Note>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(MapNote(reader));
        }

        return results;
    }

    /// <summary>Same as <see cref="GetRepliesAsync"/> but does not exclude soft-deleted replies -
    /// for callers that need to keep walking a reply chain past a soft-deleted note (deletion
    /// tombstone display) instead of losing everything below it.</summary>
    public async Task<IReadOnlyList<Note>> GetRepliesIncludingDeletedAsync(Guid parentId, CancellationToken ct = default)
    {
        using var connection = await _connectionManager.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Notes WHERE ParentNoteId = $parentId ORDER BY CreatedAt ASC;";
        command.Parameters.AddWithValue("$parentId", parentId.ToString());

        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var results = new List<Note>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(MapNote(reader));
        }

        return results;
    }

    /// <summary>Batched form of the recursive walk callers previously performed by invoking
    /// <see cref="GetRepliesIncludingDeletedAsync"/> once per node (each call opens its own
    /// connection, so walking a thread with D descendants that way is an N+1 connection-open
    /// pattern) - fetches the entire descendant subtree of <paramref name="rootId"/>, including
    /// soft-deleted Notes, with a single recursive query over one connection. Returned order is
    /// unspecified; callers needing parent-grouped or per-level ordering (e.g.
    /// <see cref="NoteThreadOrganizer.CollectPrunedSubtree"/> via its caller's own
    /// GroupBy/OrderBy(CreatedAt)) already re-sort themselves.</summary>
    public async Task<IReadOnlyList<Note>> GetDescendantRepliesIncludingDeletedAsync(Guid rootId, CancellationToken ct = default)
    {
        using var connection = await _connectionManager.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE Descendants AS (
                SELECT * FROM Notes WHERE ParentNoteId = $rootId
                UNION ALL
                SELECT n.* FROM Notes n INNER JOIN Descendants d ON n.ParentNoteId = d.Id
            )
            SELECT * FROM Descendants;
            """;
        command.Parameters.AddWithValue("$rootId", rootId.ToString());

        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var results = new List<Note>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(MapNote(reader));
        }

        return results;
    }

    /// <summary>Returns soft-deleted Notes that are still referenced as a <see cref="Note.ParentNoteId"/>
    /// by some other Note (deleted or not) - i.e. deleted Notes that structurally sit inside a still-
    /// connected thread and so need a deletion tombstone rather than simply being dropped. A deleted
    /// Note nobody points to (an ordinary trashed post with no replies) is intentionally excluded,
    /// since it has no thread-continuity to preserve.</summary>
    public Task<IReadOnlyList<Note>> GetDeletedNotesReferencedAsParentAsync(CancellationToken ct = default) =>
        QueryNotesAsync(
            "SELECT * FROM Notes WHERE IsDeleted = 1 AND Id IN (SELECT DISTINCT ParentNoteId FROM Notes WHERE ParentNoteId IS NOT NULL);",
            ct);

    /// <summary>
    /// Walks <see cref="Note.ParentNoteId"/> from <paramref name="noteId"/> up to the thread root,
    /// returning the ancestors ordered oldest-first (root first, immediate parent last) so a caller
    /// can render them above the note itself (spec section 6: Reply Chain). The starting note itself
    /// is not included. Stops early (without throwing) if a referenced parent no longer exists
    /// (permanently deleted) or a cycle is detected, returning whatever ancestors were resolved so far.
    /// </summary>
    public async Task<IReadOnlyList<Note>> GetAncestorChainAsync(Guid noteId, CancellationToken ct = default)
    {
        var ancestors = new List<Note>();
        var visited = new HashSet<Guid> { noteId };

        var current = await GetByIdAsync(noteId, ct).ConfigureAwait(false);
        while (current?.ParentNoteId is { } parentId && visited.Add(parentId))
        {
            var parent = await GetByIdAsync(parentId, ct).ConfigureAwait(false);
            if (parent is null)
            {
                break;
            }

            ancestors.Add(parent);
            current = parent;
        }

        ancestors.Reverse();
        return ancestors;
    }

    private async Task<IReadOnlyList<Note>> QueryNotesAsync(string sql, CancellationToken ct)
    {
        using var connection = await _connectionManager.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = sql;

        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var results = new List<Note>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(MapNote(reader));
        }

        return results;
    }

    /// <summary>
    /// Persists edits to an existing Note. <see cref="Note.CreatedAt"/> is preserved from the
    /// stored row (immutable per spec invariant 9.1.2) and <see cref="Note.UpdatedAt"/> is always
    /// set to the current time, regardless of what <paramref name="note"/> carries for either field.
    /// </summary>
    public async Task UpdateAsync(Note note, CancellationToken ct = default)
    {
        using var connection = await _connectionManager.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Notes SET
                Body = $body,
                UpdatedAt = $updatedAt,
                ChartAnchorDate = $chartAnchorDate,
                ChartTimeframe = $chartTimeframe,
                RelatedTicker = $relatedTicker,
                HashtagsJson = $hashtagsJson,
                AttachmentIdsJson = $attachmentIdsJson,
                LinkUrlsJson = $linkUrlsJson,
                IsPinned = $isPinned,
                IsDeleted = $isDeleted
            WHERE Id = $id;
            """;

        command.Parameters.AddWithValue("$id", note.Id.ToString());
        command.Parameters.AddWithValue("$body", note.Body);
        command.Parameters.AddWithValue("$updatedAt", SerializeDateTime(DateTime.Now));
        command.Parameters.AddWithValue("$chartAnchorDate", (object?)(note.ChartAnchorDate is { } anchor ? SerializeDateTime(anchor) : null) ?? DBNull.Value);
        command.Parameters.AddWithValue("$chartTimeframe", (object?)(note.ChartTimeframe.HasValue ? (int)note.ChartTimeframe.Value : null) ?? DBNull.Value);
        command.Parameters.AddWithValue("$relatedTicker", (object?)note.RelatedTicker ?? DBNull.Value);
        command.Parameters.AddWithValue("$hashtagsJson", JsonSerializer.Serialize(note.Hashtags));
        command.Parameters.AddWithValue("$attachmentIdsJson", JsonSerializer.Serialize(note.AttachmentIds.Select(a => a.ToString())));
        command.Parameters.AddWithValue("$linkUrlsJson", JsonSerializer.Serialize(note.LinkUrls));
        command.Parameters.AddWithValue("$isPinned", note.IsPinned ? 1 : 0);
        command.Parameters.AddWithValue("$isDeleted", note.IsDeleted ? 1 : 0);

        var rowsAffected = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (rowsAffected == 0)
        {
            throw new InvalidOperationException($"Note {note.Id} was not found and could not be updated.");
        }

        _logger?.LogDebug("Updated Note {NoteId}.", note.Id);
    }

    /// <summary>Marks a Note as logically deleted (spec section 9.3); it is excluded from <see cref="GetAllActiveAsync"/> until restored.</summary>
    public Task SoftDeleteAsync(Guid id, CancellationToken ct = default) => SetIsDeletedAsync(id, isDeleted: true, ct);

    /// <summary>Reverses a logical delete, making the Note visible in <see cref="GetAllActiveAsync"/> again.</summary>
    public Task RestoreAsync(Guid id, CancellationToken ct = default) => SetIsDeletedAsync(id, isDeleted: false, ct);

    private async Task SetIsDeletedAsync(Guid id, bool isDeleted, CancellationToken ct)
    {
        using var connection = await _connectionManager.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Notes SET IsDeleted = $isDeleted WHERE Id = $id;";
        command.Parameters.AddWithValue("$isDeleted", isDeleted ? 1 : 0);
        command.Parameters.AddWithValue("$id", id.ToString());

        var rowsAffected = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (rowsAffected == 0)
        {
            throw new InvalidOperationException($"Note {id} was not found.");
        }

        _logger?.LogDebug("Set Note {NoteId} IsDeleted={IsDeleted}.", id, isDeleted);
    }

    /// <summary>
    /// Physically removes a single Note row and its linked attachment rows/files, then runs
    /// VACUUM to reclaim disk space (spec section 4.6). Works regardless of the Note's current
    /// <see cref="Note.IsDeleted"/> state; callers (the future trash UI) are expected to only
    /// offer this for already-trashed Notes.
    /// </summary>
    public async Task PermanentlyDeleteAsync(Guid id, CancellationToken ct = default)
    {
        using var connection = await _connectionManager.OpenConnectionAsync(ct).ConfigureAwait(false);
        int rowsAffected;

        using (var transaction = connection.BeginTransaction())
        {
            try
            {
                rowsAffected = await DeleteNoteAndAttachmentsAsync(connection, transaction, id, ct).ConfigureAwait(false);
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        if (rowsAffected == 0)
        {
            throw new InvalidOperationException($"Note {id} was not found and could not be permanently deleted.");
        }

        await ExecuteVacuumAsync(connection, ct).ConfigureAwait(false);
        _logger?.LogInformation("Permanently deleted Note {NoteId}.", id);
    }

    /// <summary>
    /// Physically removes every logically-deleted (<c>IsDeleted = 1</c>) Note, along with their
    /// linked attachment rows/files, then runs VACUUM once for the whole batch (spec section 4.6).
    /// No-op when the trash is already empty.
    /// </summary>
    public async Task EmptyTrashAsync(CancellationToken ct = default)
    {
        using var connection = await _connectionManager.OpenConnectionAsync(ct).ConfigureAwait(false);

        var deletedIds = new List<Guid>();
        using (var selectCommand = connection.CreateCommand())
        {
            selectCommand.CommandText = "SELECT Id FROM Notes WHERE IsDeleted = 1;";
            using var reader = await selectCommand.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                deletedIds.Add(Guid.Parse(reader.GetString(0)));
            }
        }

        if (deletedIds.Count == 0)
        {
            _logger?.LogDebug("EmptyTrashAsync called with nothing in the trash; skipping.");
            return;
        }

        using (var transaction = connection.BeginTransaction())
        {
            try
            {
                foreach (var noteId in deletedIds)
                {
                    await DeleteNoteAndAttachmentsAsync(connection, transaction, noteId, ct).ConfigureAwait(false);
                }
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        await ExecuteVacuumAsync(connection, ct).ConfigureAwait(false);
        _logger?.LogInformation("Emptied trash: permanently deleted {Count} Note(s).", deletedIds.Count);
    }

    private async Task<int> DeleteNoteAndAttachmentsAsync(SqliteConnection connection, SqliteTransaction transaction, Guid noteId, CancellationToken ct)
    {
        var attachmentIds = await GetAttachmentIdsForNoteAsync(connection, transaction, noteId, ct).ConfigureAwait(false);

        if (attachmentIds.Count > 0)
        {
            var storedFileNames = await GetStoredFileNamesAsync(connection, transaction, attachmentIds, ct).ConfigureAwait(false);
            DeleteAttachmentFilesFromDisk(storedFileNames);
            await DeleteAttachmentRowsAsync(connection, transaction, attachmentIds, ct).ConfigureAwait(false);
        }

        using var deleteNoteCommand = connection.CreateCommand();
        deleteNoteCommand.Transaction = transaction;
        deleteNoteCommand.CommandText = "DELETE FROM Notes WHERE Id = $id;";
        deleteNoteCommand.Parameters.AddWithValue("$id", noteId.ToString());
        return await deleteNoteCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<List<Guid>> GetAttachmentIdsForNoteAsync(SqliteConnection connection, SqliteTransaction transaction, Guid noteId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT AttachmentIdsJson FROM Notes WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", noteId.ToString());

        var json = await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        if (string.IsNullOrEmpty(json))
        {
            return new List<Guid>();
        }

        var idStrings = JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
        return idStrings.Select(Guid.Parse).ToList();
    }

    private static async Task<List<string>> GetStoredFileNamesAsync(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<Guid> attachmentIds, CancellationToken ct)
    {
        var placeholders = string.Join(",", Enumerable.Range(0, attachmentIds.Count).Select(i => $"$id{i}"));
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT StoredFileName FROM Attachments WHERE Id IN ({placeholders});";
        for (var i = 0; i < attachmentIds.Count; i++)
        {
            command.Parameters.AddWithValue($"$id{i}", attachmentIds[i].ToString());
        }

        var fileNames = new List<string>();
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            fileNames.Add(reader.GetString(0));
        }

        return fileNames;
    }

    private static async Task DeleteAttachmentRowsAsync(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<Guid> attachmentIds, CancellationToken ct)
    {
        var placeholders = string.Join(",", Enumerable.Range(0, attachmentIds.Count).Select(i => $"$id{i}"));
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"DELETE FROM Attachments WHERE Id IN ({placeholders});";
        for (var i = 0; i < attachmentIds.Count; i++)
        {
            command.Parameters.AddWithValue($"$id{i}", attachmentIds[i].ToString());
        }

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private void DeleteAttachmentFilesFromDisk(IEnumerable<string> storedFileNames)
    {
        var attachmentsDirectory = Path.Combine(Path.GetDirectoryName(_connectionManager.DatabasePath)!, "Attachments");
        foreach (var fileName in storedFileNames)
        {
            var filePath = Path.Combine(attachmentsDirectory, fileName);
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Robustness over strictness (spec section 2): a locked/missing attachment file
                // must not block the rest of the purge from completing.
                _logger?.LogWarning(ex, "Failed to delete attachment file {FilePath} during permanent delete; continuing.", filePath);
            }
        }
    }

    private static async Task ExecuteVacuumAsync(SqliteConnection connection, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "VACUUM;";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static void BindNoteParameters(SqliteCommand command, Note note)
    {
        command.Parameters.AddWithValue("$id", note.Id.ToString());
        command.Parameters.AddWithValue("$body", note.Body);
        command.Parameters.AddWithValue("$createdAt", SerializeDateTime(note.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", SerializeDateTime(note.UpdatedAt));
        command.Parameters.AddWithValue("$chartAnchorDate", (object?)(note.ChartAnchorDate is { } anchor ? SerializeDateTime(anchor) : null) ?? DBNull.Value);
        command.Parameters.AddWithValue("$chartTimeframe", (object?)(note.ChartTimeframe.HasValue ? (int)note.ChartTimeframe.Value : null) ?? DBNull.Value);
        command.Parameters.AddWithValue("$relatedTicker", (object?)note.RelatedTicker ?? DBNull.Value);
        command.Parameters.AddWithValue("$hashtagsJson", JsonSerializer.Serialize(note.Hashtags));
        command.Parameters.AddWithValue("$attachmentIdsJson", JsonSerializer.Serialize(note.AttachmentIds.Select(a => a.ToString())));
        command.Parameters.AddWithValue("$linkUrlsJson", JsonSerializer.Serialize(note.LinkUrls));
        command.Parameters.AddWithValue("$isPinned", note.IsPinned ? 1 : 0);
        command.Parameters.AddWithValue("$isDeleted", note.IsDeleted ? 1 : 0);
        command.Parameters.AddWithValue("$quotedNoteId", (object?)note.QuotedNoteId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$parentNoteId", (object?)note.ParentNoteId?.ToString() ?? DBNull.Value);
    }

    private static Note MapNote(SqliteDataReader reader)
    {
        var id = Guid.Parse(reader.GetString(reader.GetOrdinal("Id")));
        var body = reader.GetString(reader.GetOrdinal("Body"));
        var createdAt = DeserializeDateTime(reader.GetString(reader.GetOrdinal("CreatedAt")));
        var updatedAt = DeserializeDateTime(reader.GetString(reader.GetOrdinal("UpdatedAt")));

        var chartAnchorDateOrdinal = reader.GetOrdinal("ChartAnchorDate");
        DateTime? chartAnchorDate = reader.IsDBNull(chartAnchorDateOrdinal)
            ? null
            : DeserializeDateTime(reader.GetString(chartAnchorDateOrdinal));

        var chartTimeframeOrdinal = reader.GetOrdinal("ChartTimeframe");
        TimeFrame? chartTimeframe = reader.IsDBNull(chartTimeframeOrdinal)
            ? null
            : (TimeFrame)reader.GetInt32(chartTimeframeOrdinal);

        var relatedTickerOrdinal = reader.GetOrdinal("RelatedTicker");
        string? relatedTicker = reader.IsDBNull(relatedTickerOrdinal) ? null : reader.GetString(relatedTickerOrdinal);

        var hashtags = JsonSerializer.Deserialize<string[]>(reader.GetString(reader.GetOrdinal("HashtagsJson"))) ?? Array.Empty<string>();
        var attachmentIdStrings = JsonSerializer.Deserialize<string[]>(reader.GetString(reader.GetOrdinal("AttachmentIdsJson"))) ?? Array.Empty<string>();
        var linkUrls = JsonSerializer.Deserialize<string[]>(reader.GetString(reader.GetOrdinal("LinkUrlsJson"))) ?? Array.Empty<string>();

        var isPinned = reader.GetInt32(reader.GetOrdinal("IsPinned")) != 0;
        var isDeleted = reader.GetInt32(reader.GetOrdinal("IsDeleted")) != 0;

        var quotedNoteIdOrdinal = reader.GetOrdinal("QuotedNoteId");
        Guid? quotedNoteId = reader.IsDBNull(quotedNoteIdOrdinal) ? null : Guid.Parse(reader.GetString(quotedNoteIdOrdinal));

        var parentNoteIdOrdinal = reader.GetOrdinal("ParentNoteId");
        Guid? parentNoteId = reader.IsDBNull(parentNoteIdOrdinal) ? null : Guid.Parse(reader.GetString(parentNoteIdOrdinal));

        return new Note(id, body, createdAt, updatedAt)
        {
            ChartAnchorDate = chartAnchorDate,
            ChartTimeframe = chartTimeframe,
            RelatedTicker = relatedTicker,
            Hashtags = hashtags.ToImmutableArray(),
            AttachmentIds = attachmentIdStrings.Select(Guid.Parse).ToImmutableArray(),
            LinkUrls = linkUrls.ToImmutableArray(),
            IsPinned = isPinned,
            IsDeleted = isDeleted,
            QuotedNoteId = quotedNoteId,
            ParentNoteId = parentNoteId,
        };
    }

    // Round-trip ("o") format preserves DateTimeKind.Local's offset, per spec section 3's
    // requirement that CreatedAt/UpdatedAt/ChartAnchorDate stay in local time (no UTC mixing).
    private static string SerializeDateTime(DateTime value) => value.ToString("o", CultureInfo.InvariantCulture);

    private static DateTime DeserializeDateTime(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
