using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels.Notes;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Notes;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Notes;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels.Notes;

public class NoteTrashViewModelTests
{
    private static string CreateIsolatedTempDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "sa_note_trash_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    private static string UniqueTicker() => $"NOTE_TRASH_TEST_{Guid.NewGuid():N}";

    private static async Task<(NoteTrashViewModel Trash, NoteRepository NoteRepository)> CreateTrashAsync(
        string tempDir,
        OrphanedAttachmentScanResultHolder? orphanedAttachmentHolder = null,
        FakeNotesSettingsManager? notesSettingsManager = null)
    {
        var connectionManager = new NoteDatabaseConnectionManager(NullLogger<NoteDatabaseConnectionManager>.Instance, tempDir);
        var schemaInitializer = new NoteSchemaInitializer(connectionManager, NullLogger<NoteSchemaInitializer>.Instance);
        await schemaInitializer.InitializeAsync();

        var noteRepository = new NoteRepository(connectionManager, NullLogger<NoteRepository>.Instance);
        var resolvedNotesSettingsManager = notesSettingsManager ?? new FakeNotesSettingsManager();
        var cacheSynchronizer = new TickerMetadataNotesCacheSynchronizer(
            noteRepository, UserStrategyMetadataRepository.Instance, resolvedNotesSettingsManager, NullLogger<TickerMetadataNotesCacheSynchronizer>.Instance);

        var trash = new NoteTrashViewModel(
            noteRepository,
            cacheSynchronizer,
            orphanedAttachmentHolder ?? new OrphanedAttachmentScanResultHolder(),
            resolvedNotesSettingsManager);
        return (trash, noteRepository);
    }

    private static Note MakeNote(string body, DateTime createdAt, string? ticker = null) =>
        new(Guid.NewGuid(), body, createdAt, createdAt) { RelatedTicker = ticker };

    [Fact]
    public async Task RefreshAsync_ListsOnlyLogicallyDeletedNotes()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (trash, noteRepository) = await CreateTrashAsync(tempDir);
            var active = MakeNote("active", DateTime.Now);
            var deleted = MakeNote("deleted", DateTime.Now);
            await noteRepository.CreateAsync(active);
            await noteRepository.CreateAsync(deleted);
            await noteRepository.SoftDeleteAsync(deleted.Id);

            await trash.RefreshAsync();

            Assert.Single(trash.DeletedNotes);
            Assert.Equal(deleted.Id, trash.DeletedNotes[0].Note.Id);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>sa_implement (Notes専用「外観」設定, Y:\Temp\sa_implementation_plan_notes_appearance.md
    /// Task 4; extended by sa_minimal_fix per Y:\Temp\sa_fix_plan_notes_appearance_restructure.md):
    /// the Trash dialog's cards (and, via the top-level BodyFontSize/BodyTextColor/BodyBackgroundColor
    /// properties, the Orphaned Files tab's ancestor-bound string list) must carry Settings > Notes'
    /// appearance values - independent of Settings > Theme/Fonts - same rationale as
    /// NoteTimelineViewModelTests' equivalent coverage for the main timeline.</summary>
    [Fact]
    public async Task RefreshAsync_WithCustomBodyFontSizeAndColor_AppliesToTrashItemsAndTopLevelProperties()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var bodyTextColor = IndicatorColor.FromRgb(0x77, 0x88, 0x99);
            var bodyBackgroundColor = IndicatorColor.FromRgb(0x10, 0x20, 0x30);
            var notesSettingsManager = new FakeNotesSettingsManager();
            notesSettingsManager.SetBodyFontSize(20.0);
            notesSettingsManager.SetBodyTextColor(bodyTextColor);
            notesSettingsManager.SetBodyBackgroundColor(bodyBackgroundColor);

            var (trash, noteRepository) = await CreateTrashAsync(tempDir, notesSettingsManager: notesSettingsManager);
            var deleted = MakeNote("deleted", DateTime.Now);
            await noteRepository.CreateAsync(deleted);
            await noteRepository.SoftDeleteAsync(deleted.Id);

            await trash.RefreshAsync();

            Assert.Equal(20.0, trash.BodyFontSize);
            Assert.Equal(bodyTextColor, trash.BodyTextColor);
            Assert.Equal(bodyBackgroundColor, trash.BodyBackgroundColor);
            Assert.Single(trash.DeletedNotes);
            Assert.Equal(20.0, trash.DeletedNotes[0].BodyFontSize);
            Assert.Equal(bodyTextColor, trash.DeletedNotes[0].BodyTextColor);
            Assert.Equal(bodyBackgroundColor, trash.DeletedNotes[0].BodyBackgroundColor);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>sa_minimal_fix (メモリリーク修正, Y:\Temp\sa_fix_plan_notetrash_memory_leak.md /
    /// sa_constraint_check Phase 1): NoteTrashViewModel is DI Transient while INotesSettingsManager is
    /// DI Singleton (ServiceCollectionExtensions.cs), so the PropertyChanged subscription taken in the
    /// constructor MUST be released by Dispose() - otherwise every dialog open leaks a subscriber on
    /// the Singleton for the app's remaining lifetime. Verified behaviorally (post-Dispose, a manager
    /// change no longer updates this instance's properties) rather than via reflection on the event's
    /// invocation list, since only the behavioral effect is a load-bearing contract.</summary>
    [Fact]
    public async Task Dispose_UnsubscribesFromNotesSettingsManager()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var notesSettingsManager = new FakeNotesSettingsManager();
            var (trash, _) = await CreateTrashAsync(tempDir, notesSettingsManager: notesSettingsManager);

            trash.Dispose();
            notesSettingsManager.SetBodyFontSize(22.0);

            Assert.NotEqual(22.0, trash.BodyFontSize);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task RestoreCommand_MakesNoteReappearInActiveTimeline_AndUpdatesCache()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (trash, noteRepository) = await CreateTrashAsync(tempDir);
            var ticker = UniqueTicker();
            var note = MakeNote("body", DateTime.Now, ticker);
            await noteRepository.CreateAsync(note);
            await noteRepository.SoftDeleteAsync(note.Id);
            Assert.Null(UserStrategyMetadataRepository.Instance.GetStrategy(ticker)?.Notes);
            await trash.RefreshAsync();

            await trash.DeletedNotes[0].RestoreCommand.ExecuteAsync(null);

            Assert.Empty(trash.DeletedNotes);
            var stored = await noteRepository.GetByIdAsync(note.Id);
            Assert.False(stored!.IsDeleted);
            var active = await noteRepository.GetAllActiveAsync();
            Assert.Contains(active, n => n.Id == note.Id);
            Assert.Equal("body", UserStrategyMetadataRepository.Instance.GetStrategy(ticker)!.Notes);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task PermanentlyDeleteCommand_RemovesNoteFromDatabaseEntirely()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (trash, noteRepository) = await CreateTrashAsync(tempDir);
            var note = MakeNote("body", DateTime.Now);
            await noteRepository.CreateAsync(note);
            await noteRepository.SoftDeleteAsync(note.Id);
            await trash.RefreshAsync();

            await trash.DeletedNotes[0].PermanentlyDeleteCommand.ExecuteAsync(null);

            Assert.Empty(trash.DeletedNotes);
            Assert.Null(await noteRepository.GetByIdAsync(note.Id));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task Constructor_PopulatesOrphanedAttachmentFileNames_FromHolder()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var holder = new OrphanedAttachmentScanResultHolder();
            holder.SetLatestReport(new OrphanedAttachmentReport(new[] { "a.png", "b.jpg" }));

            var (trash, _) = await CreateTrashAsync(tempDir, holder);

            Assert.Equal(new[] { "a.png", "b.jpg" }, trash.OrphanedAttachmentFileNames);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task Constructor_WithNoScanYetPerformed_LeavesOrphanedAttachmentFileNamesEmpty()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (trash, _) = await CreateTrashAsync(tempDir, new OrphanedAttachmentScanResultHolder());

            Assert.Empty(trash.OrphanedAttachmentFileNames);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task EmptyTrashCommand_RemovesAllDeletedNotes_KeepsActiveOnes()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (trash, noteRepository) = await CreateTrashAsync(tempDir);
            var active = MakeNote("active", DateTime.Now);
            var deleted1 = MakeNote("deleted1", DateTime.Now);
            var deleted2 = MakeNote("deleted2", DateTime.Now);
            await noteRepository.CreateAsync(active);
            await noteRepository.CreateAsync(deleted1);
            await noteRepository.CreateAsync(deleted2);
            await noteRepository.SoftDeleteAsync(deleted1.Id);
            await noteRepository.SoftDeleteAsync(deleted2.Id);
            await trash.RefreshAsync();
            Assert.Equal(2, trash.DeletedNotes.Count);

            await trash.EmptyTrashCommand.ExecuteAsync(null);

            Assert.Empty(trash.DeletedNotes);
            Assert.NotNull(await noteRepository.GetByIdAsync(active.Id));
            Assert.Null(await noteRepository.GetByIdAsync(deleted1.Id));
            Assert.Null(await noteRepository.GetByIdAsync(deleted2.Id));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }
}
