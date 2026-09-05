using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Models.Notes;

namespace StockAnalyzer.Core.Services.Notes;

/// <summary>
/// Full-text search over Note Body and Hashtags via a SQLite FTS5 virtual table (spec section
/// 4.3). Space-separated keywords are OR-combined, each matched as a token prefix (FTS5 has no
/// native arbitrary-substring match, so "部分一致" is implemented as prefix matching - a known,
/// deliberate v1 simplification).
/// </summary>
public sealed class NoteSearchService
{
    private readonly NoteDatabaseConnectionManager _connectionManager;
    private readonly NoteRepository _noteRepository;
    private readonly ILogger<NoteSearchService>? _logger;
    private bool _ftsInitialized;

    public NoteSearchService(NoteDatabaseConnectionManager connectionManager, NoteRepository noteRepository, ILogger<NoteSearchService>? logger = null)
    {
        _connectionManager = connectionManager;
        _noteRepository = noteRepository;
        _logger = logger ?? NullLogger<NoteSearchService>.Instance;
    }

    /// <summary>
    /// Returns non-deleted Notes whose Body or Hashtags contain any of the space-separated
    /// keywords in <paramref name="searchText"/> (OR match), newest first by CreatedAt. Returns
    /// empty for a blank search text rather than matching everything.
    /// </summary>
    public async Task<IReadOnlyList<Note>> SearchAsync(string? searchText, CancellationToken ct = default)
    {
        var matchQuery = BuildFts5MatchQuery(searchText);
        if (matchQuery is null)
        {
            return Array.Empty<Note>();
        }

        await EnsureFtsIndexAsync(ct).ConfigureAwait(false);

        var matchedIds = await QueryMatchedNoteIdsAsync(matchQuery, ct).ConfigureAwait(false);
        if (matchedIds.Count == 0)
        {
            return Array.Empty<Note>();
        }

        var results = new List<Note>(matchedIds.Count);
        foreach (var id in matchedIds)
        {
            var note = await _noteRepository.GetByIdAsync(id, ct).ConfigureAwait(false);
            if (note is not null && !note.IsDeleted)
            {
                results.Add(note);
            }
        }

        return results.OrderByDescending(n => n.CreatedAt).ToList();
    }

    /// <summary>
    /// Splits on whitespace, strips FTS5 special characters from each keyword, and joins them as
    /// an OR of prefix queries (e.g. "中国 EV" -&gt; <c>中国* OR EV*</c>). Returns null when there
    /// are no usable keywords.
    /// </summary>
    private static string? BuildFts5MatchQuery(string? searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return null;
        }

        var keywords = searchText
            .Split(new[] { ' ', '　', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(SanitizeKeyword)
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return keywords.Count == 0 ? null : string.Join(" OR ", keywords.Select(k => $"{k}*"));
    }

    private static string SanitizeKeyword(string keyword)
    {
        // FTS5 query syntax treats '"', '*', '(', ')', ':', '-' specially; stripping them keeps
        // keyword construction simple and safe without needing full FTS5 query-string escaping.
        var chars = keyword.Where(c => c is not ('"' or '*' or '(' or ')' or ':' or '-')).ToArray();
        return new string(chars);
    }

    private async Task<List<Guid>> QueryMatchedNoteIdsAsync(string matchQuery, CancellationToken ct)
    {
        using var connection = await _connectionManager.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT NoteId FROM NotesFts WHERE NotesFts MATCH $query;";
        command.Parameters.AddWithValue("$query", matchQuery);

        var ids = new List<Guid>();
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            ids.Add(Guid.Parse(reader.GetString(0)));
        }

        return ids;
    }

    /// <summary>
    /// Idempotently creates the FTS5 virtual table and triggers that keep it in sync with every
    /// insert/update/delete on Notes (regardless of which repository method issued it, since
    /// triggers are database-level), then backfills any Notes that existed before this index was
    /// first created. Cheap no-op after the first call within this instance's lifetime.
    /// </summary>
    private async Task EnsureFtsIndexAsync(CancellationToken ct)
    {
        if (_ftsInitialized)
        {
            return;
        }

        using var connection = await _connectionManager.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        try
        {
            await ExecuteAsync(connection, transaction, CreateFtsTableSql, ct).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, CreateAfterInsertTriggerSql, ct).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, CreateAfterUpdateTriggerSql, ct).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, CreateAfterDeleteTriggerSql, ct).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, BackfillMissingRowsSql, ct).ConfigureAwait(false);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }

        _ftsInitialized = true;
        _logger?.LogDebug("NotesFts index ensured (created if missing, backfilled for pre-existing Notes).");
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private const string CreateFtsTableSql = """
        CREATE VIRTUAL TABLE IF NOT EXISTS NotesFts USING fts5(
            NoteId UNINDEXED,
            Body,
            HashtagsJson
        );
        """;

    private const string CreateAfterInsertTriggerSql = """
        CREATE TRIGGER IF NOT EXISTS Notes_AfterInsert_NotesFts AFTER INSERT ON Notes BEGIN
            INSERT INTO NotesFts(NoteId, Body, HashtagsJson) VALUES (NEW.Id, NEW.Body, NEW.HashtagsJson);
        END;
        """;

    private const string CreateAfterUpdateTriggerSql = """
        CREATE TRIGGER IF NOT EXISTS Notes_AfterUpdate_NotesFts AFTER UPDATE ON Notes BEGIN
            DELETE FROM NotesFts WHERE NoteId = OLD.Id;
            INSERT INTO NotesFts(NoteId, Body, HashtagsJson) VALUES (NEW.Id, NEW.Body, NEW.HashtagsJson);
        END;
        """;

    private const string CreateAfterDeleteTriggerSql = """
        CREATE TRIGGER IF NOT EXISTS Notes_AfterDelete_NotesFts AFTER DELETE ON Notes BEGIN
            DELETE FROM NotesFts WHERE NoteId = OLD.Id;
        END;
        """;

    private const string BackfillMissingRowsSql = """
        INSERT INTO NotesFts(NoteId, Body, HashtagsJson)
        SELECT Id, Body, HashtagsJson FROM Notes
        WHERE Id NOT IN (SELECT NoteId FROM NotesFts);
        """;
}
