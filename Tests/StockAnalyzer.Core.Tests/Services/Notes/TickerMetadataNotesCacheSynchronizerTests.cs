using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Models.Notes;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Notes;
using StockAnalyzer.Core.Tests.TestHelpers;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services.Notes;

public class TickerMetadataNotesCacheSynchronizerTests
{
    private static string CreateIsolatedTempDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "sa_notes_cachesync_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    // UserStrategyMetadataRepository.Instance is a process-wide singleton with an in-memory cache
    // that outlives any single test, so every test uses a fresh random ticker to avoid cross-test
    // pollution rather than resetting shared state.
    private static string UniqueTicker() => $"NOTE_TEST_{Guid.NewGuid():N}";

    private static async Task<(NoteRepository NoteRepository, TickerMetadataNotesCacheSynchronizer Synchronizer)> CreateAsync(string tempDir)
    {
        var connectionManager = new NoteDatabaseConnectionManager(NullLogger<NoteDatabaseConnectionManager>.Instance, tempDir);
        var schemaInitializer = new NoteSchemaInitializer(connectionManager, NullLogger<NoteSchemaInitializer>.Instance);
        await schemaInitializer.InitializeAsync();

        var noteRepository = new NoteRepository(connectionManager, NullLogger<NoteRepository>.Instance);
        var synchronizer = new TickerMetadataNotesCacheSynchronizer(
            noteRepository,
            UserStrategyMetadataRepository.Instance,
            new FakeNotesSettingsManager(),
            NullLogger<TickerMetadataNotesCacheSynchronizer>.Instance);

        return (noteRepository, synchronizer);
    }

    [Fact]
    public async Task RecalculateNotesCacheAsync_AfterCreate_WritesPreview()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (noteRepository, synchronizer) = await CreateAsync(tempDir);
            var ticker = UniqueTicker();

            var note = new Note(Guid.NewGuid(), "中国市場について考察している。 #EV #中国", DateTime.Now, DateTime.Now)
            {
                RelatedTicker = ticker,
            };
            await noteRepository.CreateAsync(note);

            await synchronizer.RecalculateNotesCacheAsync(ticker);

            var strategy = UserStrategyMetadataRepository.Instance.GetStrategy(ticker);
            Assert.NotNull(strategy);
            Assert.Equal("中国市場について考察している。", strategy!.Notes);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task RecalculateNotesCacheAsync_UsesNewestByCreatedAt_NotByUpdatedAt()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (noteRepository, synchronizer) = await CreateAsync(tempDir);
            var ticker = UniqueTicker();

            var older = new Note(Guid.NewGuid(), "older note body", new DateTime(2026, 8, 1), new DateTime(2026, 8, 1)) { RelatedTicker = ticker };
            var newer = new Note(Guid.NewGuid(), "newer note body", new DateTime(2026, 8, 10), new DateTime(2026, 8, 10)) { RelatedTicker = ticker };
            await noteRepository.CreateAsync(older);
            await noteRepository.CreateAsync(newer);

            // Editing the older Note advances its UpdatedAt past the newer Note's CreatedAt; the
            // cache must still key off CreatedAt and keep pointing at the newer Note (spec 4.4).
            await noteRepository.UpdateAsync(older with { Body = "older note body, lightly edited" });

            await synchronizer.RecalculateNotesCacheAsync(ticker);

            Assert.Equal("newer note body", UserStrategyMetadataRepository.Instance.GetStrategy(ticker)!.Notes);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task RecalculateNotesCacheAsync_AfterBodyEdit_UpdatesPreview()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (noteRepository, synchronizer) = await CreateAsync(tempDir);
            var ticker = UniqueTicker();
            var note = new Note(Guid.NewGuid(), "original body", DateTime.Now, DateTime.Now) { RelatedTicker = ticker };
            await noteRepository.CreateAsync(note);
            await synchronizer.RecalculateNotesCacheAsync(ticker);
            Assert.Equal("original body", UserStrategyMetadataRepository.Instance.GetStrategy(ticker)!.Notes);

            await noteRepository.UpdateAsync(note with { Body = "corrected body" });
            await synchronizer.RecalculateNotesCacheAsync(ticker);

            Assert.Equal("corrected body", UserStrategyMetadataRepository.Instance.GetStrategy(ticker)!.Notes);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task RecalculateNotesCacheAsync_AfterTickerChange_UpdatesBothOldAndNewTicker()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (noteRepository, synchronizer) = await CreateAsync(tempDir);
            var oldTicker = UniqueTicker();
            var newTicker = UniqueTicker();

            var note = new Note(Guid.NewGuid(), "moving between tickers", DateTime.Now, DateTime.Now) { RelatedTicker = oldTicker };
            await noteRepository.CreateAsync(note);
            await synchronizer.RecalculateNotesCacheAsync(oldTicker);
            Assert.Equal("moving between tickers", UserStrategyMetadataRepository.Instance.GetStrategy(oldTicker)!.Notes);

            await noteRepository.UpdateAsync(note with { RelatedTicker = newTicker });
            // Spec 4.4: a RelatedTicker change recalculates both the old and the new ticker.
            await synchronizer.RecalculateNotesCacheAsync(oldTicker);
            await synchronizer.RecalculateNotesCacheAsync(newTicker);

            Assert.Null(UserStrategyMetadataRepository.Instance.GetStrategy(oldTicker)!.Notes);
            Assert.Equal("moving between tickers", UserStrategyMetadataRepository.Instance.GetStrategy(newTicker)!.Notes);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task RecalculateNotesCacheAsync_AfterSoftDelete_FallsBackOrClears()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (noteRepository, synchronizer) = await CreateAsync(tempDir);
            var ticker = UniqueTicker();

            var older = new Note(Guid.NewGuid(), "Note A", new DateTime(2026, 8, 1), new DateTime(2026, 8, 1)) { RelatedTicker = ticker };
            var newer = new Note(Guid.NewGuid(), "Note B", new DateTime(2026, 8, 10), new DateTime(2026, 8, 10)) { RelatedTicker = ticker };
            await noteRepository.CreateAsync(older);
            await noteRepository.CreateAsync(newer);
            await synchronizer.RecalculateNotesCacheAsync(ticker);
            Assert.Equal("Note B", UserStrategyMetadataRepository.Instance.GetStrategy(ticker)!.Notes);

            await noteRepository.SoftDeleteAsync(newer.Id);
            await synchronizer.RecalculateNotesCacheAsync(ticker);
            Assert.Equal("Note A", UserStrategyMetadataRepository.Instance.GetStrategy(ticker)!.Notes);

            await noteRepository.SoftDeleteAsync(older.Id);
            await synchronizer.RecalculateNotesCacheAsync(ticker);
            Assert.Null(UserStrategyMetadataRepository.Instance.GetStrategy(ticker)!.Notes);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task RecalculateNotesCacheAsync_AfterRestore_ShowsPreviewAgain()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (noteRepository, synchronizer) = await CreateAsync(tempDir);
            var ticker = UniqueTicker();
            var note = new Note(Guid.NewGuid(), "restorable note", DateTime.Now, DateTime.Now) { RelatedTicker = ticker };
            await noteRepository.CreateAsync(note);
            await synchronizer.RecalculateNotesCacheAsync(ticker);

            await noteRepository.SoftDeleteAsync(note.Id);
            await synchronizer.RecalculateNotesCacheAsync(ticker);
            Assert.Null(UserStrategyMetadataRepository.Instance.GetStrategy(ticker)!.Notes);

            await noteRepository.RestoreAsync(note.Id);
            await synchronizer.RecalculateNotesCacheAsync(ticker);

            Assert.Equal("restorable note", UserStrategyMetadataRepository.Instance.GetStrategy(ticker)!.Notes);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task RecalculateNotesCacheAsync_PreservesOtherStrategyFields()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (_, synchronizer) = await CreateAsync(tempDir);
            var ticker = UniqueTicker();
            UserStrategyMetadataRepository.Instance.SaveStrategy(ticker, 100m, 120m, 90m, null);

            // No Notes exist for this ticker, so the cache should clear to null while leaving
            // the unrelated strategy fields (Long/ExitLong/StopLossLong) untouched.
            await synchronizer.RecalculateNotesCacheAsync(ticker);

            var strategy = UserStrategyMetadataRepository.Instance.GetStrategy(ticker);
            Assert.NotNull(strategy);
            Assert.Null(strategy!.Notes);
            Assert.Equal(100m, strategy.Long);
            Assert.Equal(120m, strategy.ExitLong);
            Assert.Equal(90m, strategy.StopLossLong);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task RecalculateNotesCacheAsync_TruncatesToReadMoreBoundaryAndStripsHashtags()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (noteRepository, synchronizer) = await CreateAsync(tempDir);
            var ticker = UniqueTicker();
            var longBody = new string('あ', 250) + " #EV #中国";
            var note = new Note(Guid.NewGuid(), longBody, DateTime.Now, DateTime.Now) { RelatedTicker = ticker };
            await noteRepository.CreateAsync(note);

            await synchronizer.RecalculateNotesCacheAsync(ticker);

            // CreateAsync wires a FakeNotesSettingsManager with the production default
            // ReadMoreMaxCharacters (150) - the same "Read more" collapse boundary the Notes tab
            // itself uses, not a fixed 200-character cap.
            var preview = UserStrategyMetadataRepository.Instance.GetStrategy(ticker)!.Notes;
            Assert.NotNull(preview);
            Assert.Equal(StockAnalyzer.Core.Models.Settings.NotesSettingsConstants.DefaultReadMoreMaxCharacters, preview!.Length);
            Assert.DoesNotContain("#", preview);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task RecalculateNotesCacheAsync_WithBlankTicker_IsNoOp()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (_, synchronizer) = await CreateAsync(tempDir);

            // Should not throw for an empty/whitespace ticker; simply does nothing.
            await synchronizer.RecalculateNotesCacheAsync(string.Empty);
            await synchronizer.RecalculateNotesCacheAsync("   ");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }
}
