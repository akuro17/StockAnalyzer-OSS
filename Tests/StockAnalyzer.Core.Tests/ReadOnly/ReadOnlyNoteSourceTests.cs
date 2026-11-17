using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Models.Notes;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services.Notes;
using Xunit;

namespace StockAnalyzer.Core.Tests.ReadOnly;

/// <summary>
/// <see cref="SqliteReadOnlyNoteSource"/> on a database the application's own classes wrote: the same rows, read without creating, migrating or changing anything.
/// </summary>
public sealed class ReadOnlyNoteSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_ro_notes_" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_root, NoteDatabaseConnectionManager.DatabaseFileName);

    public ReadOnlyNoteSourceTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    private static DateTime Local(int day, int hour = 9) => new(2024, 3, day, hour, 0, 0, DateTimeKind.Local);

    private static Note NewNote(string body, DateTime createdAt) => new(Guid.NewGuid(), body, createdAt, createdAt);

    private async Task<NoteRepository> CreateRepositoryAsync()
    {
        var manager = new NoteDatabaseConnectionManager(NullLogger<NoteDatabaseConnectionManager>.Instance, _root);
        await new NoteSchemaInitializer(manager, NullLogger<NoteSchemaInitializer>.Instance).InitializeAsync();
        return new NoteRepository(manager, NullLogger<NoteRepository>.Instance);
    }

    /// <summary>The bytes of a file the application's writer connection still holds open.</summary>
    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private Dictionary<string, long> Files()
        => Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).ToDictionary(path => path, path => new FileInfo(path).Length);

    [Fact]
    public async Task NoDatabase_IsNoNotesAndNothingIsCreated()
    {
        var source = new SqliteReadOnlyNoteSource(DatabasePath);

        Assert.Empty(await source.GetActiveNotesAsync());
        Assert.Empty(await source.GetCatalystNotesAsync());
        Assert.Empty(await source.GetByIdsAsync(new[] { Guid.NewGuid() }));
        Assert.False(File.Exists(DatabasePath));
    }

    [Fact]
    public async Task ActiveNotes_AreTheNotesNotInTheTrash_NewestFirst_AsTheApplicationMapsThem()
    {
        NoteRepository repository = await CreateRepositoryAsync();
        Note old = NewNote("old", Local(1)) with { };
        Note recent = NewNote("recent #tag", Local(5)) with { RelatedTicker = "7203-T", Hashtags = ImmutableArray.Create("tag"), IsPinned = true, LinkUrls = ImmutableArray.Create("https://example.com") };
        Note trashed = NewNote("trashed", Local(3));
        await repository.CreateAsync(old);
        await repository.CreateAsync(recent);
        await repository.CreateAsync(trashed);
        await repository.SoftDeleteAsync(trashed.Id);
        var source = new SqliteReadOnlyNoteSource(DatabasePath);

        IReadOnlyList<Note> notes = await source.GetActiveNotesAsync();

        Assert.Equal(new[] { recent.Id, old.Id }, notes.Select(n => n.Id));
        Note read = notes[0];
        Assert.Equal("recent #tag", read.Body);
        Assert.Equal("7203-T", read.RelatedTicker);
        Assert.Equal(new[] { "tag" }, read.Hashtags);
        Assert.True(read.IsPinned);
        Assert.Equal(new[] { "https://example.com" }, read.LinkUrls);
        Assert.Equal(recent.CreatedAt, read.CreatedAt);
    }

    [Fact]
    public async Task CatalystNotes_AreOrderedByTheirDate_AndTheTrashAndNotesWithoutOneAreLeftOut()
    {
        NoteRepository repository = await CreateRepositoryAsync();
        Note later = NewNote("later", Local(1)) with { CatalystDate = new DateTime(2024, 6, 1) };
        Note sooner = NewNote("sooner", Local(2)) with { CatalystDate = new DateTime(2024, 4, 1) };
        Note none = NewNote("none", Local(3));
        Note trashed = NewNote("trashed", Local(4)) with { CatalystDate = new DateTime(2024, 5, 1) };
        foreach (Note note in new[] { later, sooner, none, trashed })
        {
            await repository.CreateAsync(note);
        }

        await repository.SoftDeleteAsync(trashed.Id);

        IReadOnlyList<Note> notes = await new SqliteReadOnlyNoteSource(DatabasePath).GetCatalystNotesAsync();

        Assert.Equal(new[] { sooner.Id, later.Id }, notes.Select(n => n.Id));
        Assert.Equal(new DateTime(2024, 4, 1), notes[0].CatalystDate);
    }

    [Fact]
    public async Task ByIds_IncludesTrashedNotes_AndLeavesOutIdsThatDoNotExist()
    {
        NoteRepository repository = await CreateRepositoryAsync();
        Note kept = NewNote("kept", Local(1));
        Note trashed = NewNote("trashed", Local(2));
        await repository.CreateAsync(kept);
        await repository.CreateAsync(trashed);
        await repository.SoftDeleteAsync(trashed.Id);

        IReadOnlyDictionary<Guid, Note> found = await new SqliteReadOnlyNoteSource(DatabasePath).GetByIdsAsync(new[] { kept.Id, trashed.Id, Guid.NewGuid() });

        Assert.Equal(2, found.Count);
        Assert.True(found[trashed.Id].IsDeleted);
        Assert.False(found[kept.Id].IsDeleted);
    }

    [Fact]
    public async Task Reading_ChangesNoFileAndAddsNoFile()
    {
        NoteRepository repository = await CreateRepositoryAsync();
        await repository.CreateAsync(NewNote("a", Local(1)));
        var source = new SqliteReadOnlyNoteSource(DatabasePath);
        Dictionary<string, long> before = Files();
        byte[] databaseBefore = ReadShared(DatabasePath);

        await source.GetActiveNotesAsync();
        await source.GetCatalystNotesAsync();

        Assert.Equal(before, Files());
        Assert.Equal(databaseBefore, ReadShared(DatabasePath));
    }

    [Fact]
    public async Task ADamagedDatabase_IsDataUnavailableNotNoNotes()
    {
        File.WriteAllBytes(DatabasePath, Enumerable.Repeat((byte)0x41, 4096).ToArray());
        var source = new SqliteReadOnlyNoteSource(DatabasePath);

        await Assert.ThrowsAsync<DataUnavailableException>(() => source.GetActiveNotesAsync());
    }
}
