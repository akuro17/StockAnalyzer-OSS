using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace StockAnalyzer.Core.Services.Notes;

/// <summary>
/// Ensures the Ticker Note SQLite database (notes.db) has the expected schema, using
/// PRAGMA user_version to track the applied version and skip re-initialization on
/// subsequent application starts.
/// </summary>
public sealed class NoteSchemaInitializer
{
    private const long CurrentSchemaVersion = 2;

    private readonly NoteDatabaseConnectionManager _connectionManager;
    private readonly ILogger<NoteSchemaInitializer>? _logger;

    public NoteSchemaInitializer(NoteDatabaseConnectionManager connectionManager, ILogger<NoteSchemaInitializer>? logger = null)
    {
        _connectionManager = connectionManager;
        _logger = logger ?? NullLogger<NoteSchemaInitializer>.Instance;
    }

    /// <summary>
    /// Creates the Notes/Attachments tables (and supporting indexes) if the database's
    /// user_version is behind <see cref="CurrentSchemaVersion"/>. Safe to call on every
    /// application startup.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        using var connection = await _connectionManager.OpenConnectionAsync(ct).ConfigureAwait(false);

        var currentVersion = await GetUserVersionAsync(connection, ct).ConfigureAwait(false);
        if (currentVersion >= CurrentSchemaVersion)
        {
            _logger?.LogDebug("notes.db schema already at version {Version}; skipping initialization.", currentVersion);
            return;
        }

        using var transaction = connection.BeginTransaction();
        try
        {
            if (currentVersion < 1)
            {
                await ExecuteAsync(connection, transaction, CreateNotesTableSql, ct).ConfigureAwait(false);
                await ExecuteAsync(connection, transaction, CreateAttachmentsTableSql, ct).ConfigureAwait(false);
                await ExecuteAsync(connection, transaction, CreateNotesRelatedTickerIndexSql, ct).ConfigureAwait(false);
                await ExecuteAsync(connection, transaction, CreateNotesCreatedAtIndexSql, ct).ConfigureAwait(false);
            }

            if (currentVersion < 2)
            {
                if (currentVersion >= 1)
                {
                    // Notes table already existed at v1 (without the thread columns); add them in place
                    // so existing rows and their data are preserved (SQLite ALTER TABLE ADD COLUMN is non-destructive).
                    await ExecuteAsync(connection, transaction, AddQuotedNoteIdColumnSql, ct).ConfigureAwait(false);
                    await ExecuteAsync(connection, transaction, AddParentNoteIdColumnSql, ct).ConfigureAwait(false);
                }

                await ExecuteAsync(connection, transaction, CreateNotesQuotedNoteIdIndexSql, ct).ConfigureAwait(false);
                await ExecuteAsync(connection, transaction, CreateNotesParentNoteIdIndexSql, ct).ConfigureAwait(false);
            }

            await SetUserVersionAsync(connection, transaction, CurrentSchemaVersion, ct).ConfigureAwait(false);

            transaction.Commit();
            _logger?.LogInformation("notes.db schema initialized to version {Version}.", CurrentSchemaVersion);
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static async Task<long> GetUserVersionAsync(SqliteConnection connection, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is long version ? version : 0;
    }

    private static async Task SetUserVersionAsync(SqliteConnection connection, SqliteTransaction transaction, long version, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // PRAGMA does not support parameter binding; the value is a compile-time constant, not user input.
        command.CommandText = $"PRAGMA user_version = {version};";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private const string CreateNotesTableSql = """
        CREATE TABLE IF NOT EXISTS Notes (
            Id TEXT PRIMARY KEY NOT NULL,
            Body TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL,
            ChartAnchorDate TEXT NULL,
            ChartTimeframe INTEGER NULL,
            RelatedTicker TEXT NULL,
            HashtagsJson TEXT NOT NULL DEFAULT '[]',
            AttachmentIdsJson TEXT NOT NULL DEFAULT '[]',
            LinkUrlsJson TEXT NOT NULL DEFAULT '[]',
            IsPinned INTEGER NOT NULL DEFAULT 0,
            IsDeleted INTEGER NOT NULL DEFAULT 0,
            QuotedNoteId TEXT NULL,
            ParentNoteId TEXT NULL
        );
        """;

    private const string CreateAttachmentsTableSql = """
        CREATE TABLE IF NOT EXISTS Attachments (
            Id TEXT PRIMARY KEY NOT NULL,
            StoredFileName TEXT NOT NULL,
            OriginalFileName TEXT NOT NULL,
            MimeType TEXT NOT NULL,
            Width INTEGER NOT NULL,
            Height INTEGER NOT NULL,
            FileSizeBytes INTEGER NOT NULL,
            CreatedAt TEXT NOT NULL
        );
        """;

    private const string CreateNotesRelatedTickerIndexSql =
        "CREATE INDEX IF NOT EXISTS IX_Notes_RelatedTicker ON Notes(RelatedTicker);";

    private const string CreateNotesCreatedAtIndexSql =
        "CREATE INDEX IF NOT EXISTS IX_Notes_CreatedAt ON Notes(CreatedAt);";

    private const string AddQuotedNoteIdColumnSql =
        "ALTER TABLE Notes ADD COLUMN QuotedNoteId TEXT NULL;";

    private const string AddParentNoteIdColumnSql =
        "ALTER TABLE Notes ADD COLUMN ParentNoteId TEXT NULL;";

    private const string CreateNotesQuotedNoteIdIndexSql =
        "CREATE INDEX IF NOT EXISTS IX_Notes_QuotedNoteId ON Notes(QuotedNoteId);";

    private const string CreateNotesParentNoteIdIndexSql =
        "CREATE INDEX IF NOT EXISTS IX_Notes_ParentNoteId ON Notes(ParentNoteId);";
}
