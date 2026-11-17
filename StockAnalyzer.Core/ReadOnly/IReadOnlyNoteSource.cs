using StockAnalyzer.Core.Models.Notes;

namespace StockAnalyzer.Core.ReadOnly;

/// <summary>Read-only access to the user's Ticker Notes (Data/Notes/notes.db). Nothing is created, migrated or written; a missing database is simply no notes.</summary>
public interface IReadOnlyNoteSource
{
    /// <summary>The Notes that are not in the trash, newest <see cref="Note.CreatedAt"/> first.</summary>
    /// <exception cref="DataUnavailableException">The database exists but cannot be read now.</exception>
    Task<IReadOnlyList<Note>> GetActiveNotesAsync(CancellationToken cancellationToken = default);

    /// <summary>The Notes that are not in the trash and carry a <see cref="Note.CatalystDate"/>, earliest catalyst first (then newest <see cref="Note.CreatedAt"/> first).</summary>
    /// <exception cref="DataUnavailableException">The database exists but cannot be read now.</exception>
    Task<IReadOnlyList<Note>> GetCatalystNotesAsync(CancellationToken cancellationToken = default);

    /// <summary>The Notes with these ids, trashed ones included (a thread can lead through a deleted Note); an id that does not exist has no entry.</summary>
    /// <exception cref="DataUnavailableException">The database exists but cannot be read now.</exception>
    Task<IReadOnlyDictionary<Guid, Note>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default);
}
