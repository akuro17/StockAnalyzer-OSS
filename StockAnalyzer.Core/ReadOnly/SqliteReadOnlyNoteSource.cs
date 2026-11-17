using Microsoft.Data.Sqlite;
using StockAnalyzer.Core.Models.Notes;
using StockAnalyzer.Core.Services.Notes;

namespace StockAnalyzer.Core.ReadOnly;

/// <summary>
/// Reads notes.db with <see cref="SqliteOpenMode.ReadOnly"/>: no schema creation, no migration, no journal-mode change, no full-text index, no folder creation (the application's own
/// <see cref="NoteDatabaseConnectionManager"/> does all of that). Rows are mapped by the application's own <c>NoteRepository.MapNote</c>. Connections are not pooled, so no handle outlives a call.
/// A database that does not exist is no notes; one that cannot be opened or read now (locked, replaced, damaged) is <see cref="DataUnavailableException"/>.
/// </summary>
public sealed class SqliteReadOnlyNoteSource : IReadOnlyNoteSource
{
    private const string ActiveNotesSql = "SELECT * FROM Notes WHERE IsDeleted = 0 ORDER BY CreatedAt DESC;";
    private const string CatalystNotesSql = "SELECT * FROM Notes WHERE IsDeleted = 0 AND CatalystDate IS NOT NULL ORDER BY CatalystDate ASC, CreatedAt DESC;";

    private readonly string _databasePath;

    public SqliteReadOnlyNoteSource(string databasePath)
    {
        _databasePath = databasePath ?? throw new ArgumentNullException(nameof(databasePath));
    }

    public Task<IReadOnlyList<Note>> GetActiveNotesAsync(CancellationToken cancellationToken = default)
        => QueryAsync(ActiveNotesSql, null, cancellationToken);

    public Task<IReadOnlyList<Note>> GetCatalystNotesAsync(CancellationToken cancellationToken = default)
        => QueryAsync(CatalystNotesSql, null, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, Note>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, Note>();
        }

        string[] names = ids.Select((_, i) => "$id" + i).ToArray();
        IReadOnlyList<Note> notes = await QueryAsync(
            $"SELECT * FROM Notes WHERE Id IN ({string.Join(", ", names)});",
            command =>
            {
                int index = 0;
                foreach (Guid id in ids)
                {
                    command.Parameters.AddWithValue(names[index++], id.ToString());
                }
            },
            cancellationToken).ConfigureAwait(false);
        return notes.ToDictionary(note => note.Id);
    }

    private async Task<IReadOnlyList<Note>> QueryAsync(string sql, Action<SqliteCommand>? bind, CancellationToken cancellationToken)
    {
        if (!File.Exists(_databasePath))
        {
            return Array.Empty<Note>();
        }

        try
        {
            var connectionString = new SqliteConnectionStringBuilder { DataSource = _databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            bind?.Invoke(command);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var notes = new List<Note>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                notes.Add(NoteRepository.MapNote(reader));
            }

            return notes;
        }
        catch (Exception error) when (error is SqliteException or FormatException or InvalidCastException or System.Text.Json.JsonException)
        {
            throw new DataUnavailableException("The notes database cannot be read.", error);
        }
    }
}
