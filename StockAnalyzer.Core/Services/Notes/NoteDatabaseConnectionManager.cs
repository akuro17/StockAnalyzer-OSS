using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Common;

namespace StockAnalyzer.Core.Services.Notes;

/// <summary>
/// Resolves the location of and opens WAL-mode connections to the Ticker Note SQLite database
/// (Data\Notes\notes.db). Each call to <see cref="OpenConnectionAsync"/> returns a new, opened
/// connection; SQLite/Microsoft.Data.Sqlite pools the underlying native connections internally,
/// so callers own the returned connection's lifetime (dispose when done).
/// </summary>
public sealed class NoteDatabaseConnectionManager
{
    private readonly string _connectionString;
    private readonly ILogger<NoteDatabaseConnectionManager>? _logger;
    private readonly SemaphoreSlim _walInitLock = new(1, 1);
    private volatile bool _walModeConfirmed;

    public string DatabasePath { get; }

    /// <param name="logger">Optional logger; falls back to <see cref="NullLogger{T}"/>.</param>
    /// <param name="notesDirectoryOverride">
    /// Optional explicit directory for the database file, bypassing <see cref="PathDiscovery"/>.
    /// Intended for test isolation so tests never touch the production Data\Notes folder.
    /// </param>
    public NoteDatabaseConnectionManager(ILogger<NoteDatabaseConnectionManager>? logger = null, string? notesDirectoryOverride = null)
    {
        _logger = logger ?? NullLogger<NoteDatabaseConnectionManager>.Instance;

        var notesDirectory = notesDirectoryOverride ?? PathDiscovery.ResolveDataPath(null, "Data/Notes");
        Directory.CreateDirectory(notesDirectory);

        DatabasePath = Path.Combine(notesDirectory, "notes.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        _logger.LogDebug("NoteDatabaseConnectionManager initialized with database path: {DatabasePath}", DatabasePath);
    }

    /// <summary>
    /// Opens a new connection to notes.db, ensuring WAL journal mode is set on the database.
    /// The caller is responsible for disposing the returned connection.
    /// </summary>
    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        if (!_walModeConfirmed)
        {
            await EnsureWalModeAsync(connection, ct).ConfigureAwait(false);
        }

        return connection;
    }

    private async Task EnsureWalModeAsync(SqliteConnection connection, CancellationToken ct)
    {
        await _walInitLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_walModeConfirmed)
            {
                return;
            }

            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL;";
            var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);

            if (result is string mode && string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                _walModeConfirmed = true;
                _logger?.LogDebug("notes.db journal_mode confirmed as WAL.");
            }
            else
            {
                _logger?.LogWarning("notes.db journal_mode reported as {Mode} instead of WAL.", result);
            }
        }
        finally
        {
            _walInitLock.Release();
        }
    }
}
