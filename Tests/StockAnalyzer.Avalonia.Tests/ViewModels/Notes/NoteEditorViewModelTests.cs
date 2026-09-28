using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels.Notes;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Notes;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Notes;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels.Notes;

public class NoteEditorViewModelTests
{
    /// <summary>Minimal IMarketDataProvider test double: only GetAvailableTickersAsync is
    /// exercised (the "Related ticker" AutoCompleteBox's suggestion source); every other member is
    /// unused here (same pattern as EditTickerNotesDialogViewModelTests.FakeMarketDataProvider).</summary>
    private class FakeMarketDataProvider : IMarketDataProvider
    {
        private readonly IReadOnlyList<string> _availableTickers;

        public FakeMarketDataProvider(IReadOnlyList<string>? availableTickers = null) =>
            _availableTickers = availableTickers ?? Array.Empty<string>();

        public Task<IReadOnlyList<string>> GetAvailableTickersAsync() => Task.FromResult(_availableTickers);
        public Task<IReadOnlyList<CandleData>> GetTickersDataAsync(string symbol, TimeFrame timeFrame) => Task.FromResult<IReadOnlyList<CandleData>>(Array.Empty<CandleData>());
        public Task<IReadOnlyList<string>> ScreenAsync(ScreeningCriteria criteria) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task<IReadOnlyDictionary<string, decimal>> GetLatestPricesAsync(IEnumerable<string> symbols) => Task.FromResult<IReadOnlyDictionary<string, decimal>>(new Dictionary<string, decimal>());
        public ValueTask<TickerMetadata> GetMetadataAsync(string ticker) => ValueTask.FromResult(TickerMetadata.Unknown);
        public Task<TickerMetadata> FetchMetadataFromPythonAsync(string ticker) => Task.FromResult(TickerMetadata.Unknown);
        public Task SaveMetadataAsync(string ticker, TickerMetadata meta) => Task.CompletedTask;
        public Task AddTickerAsync(string symbol) => Task.CompletedTask;
        public Task AddTickersAsync(IEnumerable<string> symbols) => Task.CompletedTask;
        public Task RemoveTickerAsync(string symbol) => Task.CompletedTask;
        public Task RemoveTickersAsync(IEnumerable<string> symbols) => Task.CompletedTask;
        public void InvalidateMetadataCache(string ticker) { }
        public Task<DateTimeOffset?> GetTimeSeriesLastUpdatedAsync(string symbol) => Task.FromResult<DateTimeOffset?>(null);
        public Task<int> DeleteTickerDataFromDateAsync(string symbol, DateTime cutoffDate) => Task.FromResult(0);
    }

    /// <summary>Minimal IDispatcherService test double: these are plain xunit [Fact] tests with no
    /// real Avalonia UI thread/dispatcher running, so Post just invokes synchronously - equivalent
    /// to the production DispatcherService's behavior from the test's point of view (the awaited
    /// load task is already complete by the time Post runs).</summary>
    private sealed class FakeDispatcherService : IDispatcherService
    {
        public void Post(Action action) => action();
        public void Post<T>(Action<T> action, T state) => action(state);
        public Task PostAsync(Func<Task> action) => action();
        public Task PostAsync<TState>(Func<TState, Task> action, TState state) => action(state);
        public bool CheckAccess() => true;
        public void VerifyAccess() { }
    }

    private static string CreateIsolatedTempDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "sa_note_editor_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    private static string UniqueTicker() => $"NOTE_EDITOR_TEST_{Guid.NewGuid():N}";

    private static byte[] CreateTestPngBytes(int width = 40, int height = 40)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    private static async Task<(NoteEditorViewModel Editor, NoteRepository NoteRepository)> CreateEditorAsync(
        string tempDir, IMarketDataProvider? marketDataProvider = null)
    {
        var connectionManager = new NoteDatabaseConnectionManager(NullLogger<NoteDatabaseConnectionManager>.Instance, tempDir);
        var schemaInitializer = new NoteSchemaInitializer(connectionManager, NullLogger<NoteSchemaInitializer>.Instance);
        await schemaInitializer.InitializeAsync();

        var noteRepository = new NoteRepository(connectionManager, NullLogger<NoteRepository>.Instance);
        var attachmentRepository = new AttachmentRepository(connectionManager, NullLogger<AttachmentRepository>.Instance);
        var notesSettingsManager = new FakeNotesSettingsManager();
        var cacheSynchronizer = new TickerMetadataNotesCacheSynchronizer(
            noteRepository, UserStrategyMetadataRepository.Instance, notesSettingsManager, NullLogger<TickerMetadataNotesCacheSynchronizer>.Instance);

        var editor = new NoteEditorViewModel(noteRepository, attachmentRepository, cacheSynchronizer, marketDataProvider ?? new FakeMarketDataProvider(), new FakeDispatcherService(), notesSettingsManager, NullLogger<NoteEditorViewModel>.Instance);
        return (editor, noteRepository);
    }

    [Fact]
    public async Task SaveAsync_CreatesNewNote_WithHashtagsExtractedAndCacheUpdated()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (editor, noteRepository) = await CreateEditorAsync(tempDir);
            var ticker = UniqueTicker();
            editor.Body = "中国市場について考察。 #EV #中国";
            editor.RelatedTicker = ticker;

            await editor.SaveCommand.ExecuteAsync(null);

            Assert.NotNull(editor.SavedNote);
            var stored = await noteRepository.GetByIdAsync(editor.SavedNote!.Id);
            Assert.NotNull(stored);
            Assert.Equal(new[] { "ev", "中国" }, stored!.Hashtags);
            Assert.Equal(ticker, stored.RelatedTicker);

            var strategy = UserStrategyMetadataRepository.Instance.GetStrategy(ticker);
            Assert.NotNull(strategy);
            Assert.Equal("中国市場について考察。", strategy!.Notes);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>Regression test for fix request #1 (2nd round): the prior implementation mutated
    /// AvailableTickers straight off the background thread that GetAvailableTickersAsync's
    /// ConfigureAwait(false) resumed on, so the "Related ticker" AutoCompleteBox's suggestion list
    /// never reliably populated. This asserts the constructor's fire-and-forget load, once routed
    /// through IDispatcherService.Post, actually lands in AvailableTickers.</summary>
    [Fact]
    public async Task Constructor_PopulatesAvailableTickersFromMarketDataProvider()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (editor, _) = await CreateEditorAsync(tempDir, new FakeMarketDataProvider(new[] { "AAPL", "MSFT" }));

            // The constructor's load is fire-and-forget; give it a moment to complete.
            for (var i = 0; i < 50 && editor.AvailableTickers.Count == 0; i++)
            {
                await Task.Delay(10);
            }

            Assert.Equal(new[] { "AAPL", "MSFT" }, editor.AvailableTickers);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>Regression test for fix request #2 (2nd round, "single click still double-posts"):
    /// exercises the exact synchronous ICommand.Execute entry point Avalonia's Button invokes on a
    /// real click (as opposed to ExecuteAsync, used elsewhere in this file) exactly once, to confirm
    /// a single genuine invocation never creates more than one Note.</summary>
    [Fact]
    public async Task SaveCommand_SingleSyncExecute_CreatesExactlyOneNote()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (editor, noteRepository) = await CreateEditorAsync(tempDir);
            editor.Body = "single click regression check";

            Assert.True(((System.Windows.Input.ICommand)editor.SaveCommand).CanExecute(null));
            ((System.Windows.Input.ICommand)editor.SaveCommand).Execute(null);

            // Execute() on an AsyncRelayCommand fires the task without awaiting it; poll for completion.
            for (var i = 0; i < 100 && editor.SavedNote is null; i++)
            {
                await Task.Delay(10);
            }

            Assert.NotNull(editor.SavedNote);
            var allNotes = await noteRepository.GetAllActiveAsync();
            Assert.Single(allNotes);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task SaveCommand_WhenExecutedTwiceConcurrently_CreatesOnlyOneNote()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (editor, noteRepository) = await CreateEditorAsync(tempDir);
            editor.Body = "double-click regression check";

            // Simulates a rapid double-click on the Save button: two ExecuteAsync calls fired
            // before the first has completed. Without AllowConcurrentExecutions = false, both
            // would independently see _editingNoteId == null and each CreateAsync a new Note.
            var firstCall = editor.SaveCommand.ExecuteAsync(null);
            var secondCall = editor.SaveCommand.ExecuteAsync(null);
            await Task.WhenAll(firstCall, secondCall);

            var active = await noteRepository.GetAllActiveAsync();
            Assert.Single(active);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task SaveAsync_WithBlankBody_DoesNotCreateANote()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (editor, _) = await CreateEditorAsync(tempDir);
            editor.Body = "   ";

            await editor.SaveCommand.ExecuteAsync(null);

            Assert.Null(editor.SavedNote);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task SaveAsync_WithValidPendingAttachment_LinksAttachmentIdToNote()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (editor, noteRepository) = await CreateEditorAsync(tempDir);
            editor.Body = "chart screenshot attached";
            editor.AddPendingAttachment(CreateTestPngBytes(), "chart.png");

            await editor.SaveCommand.ExecuteAsync(null);

            var stored = await noteRepository.GetByIdAsync(editor.SavedNote!.Id);
            Assert.NotNull(stored);
            Assert.Single(stored!.AttachmentIds);
            Assert.Empty(editor.AttachmentErrors);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task SaveAsync_WithOneCorruptedAttachment_SkipsItButStillSavesNote()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (editor, noteRepository) = await CreateEditorAsync(tempDir);
            editor.Body = "note with a bad attachment";
            editor.AddPendingAttachment(CreateTestPngBytes(), "good.png");
            editor.AddPendingAttachment(new byte[] { 0x00, 0x01, 0x02 }, "corrupted.png");

            await editor.SaveCommand.ExecuteAsync(null);

            Assert.NotNull(editor.SavedNote);
            var stored = await noteRepository.GetByIdAsync(editor.SavedNote!.Id);
            Assert.Single(stored!.AttachmentIds); // only the good one linked
            Assert.Single(editor.AttachmentErrors); // the corrupted one recorded as a skip, not a crash
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>Task D (SAで実装, Note Tab Enhancements): AddPendingAttachment must decode a
    /// preview Bitmap so the compose panel can show it before Save. Bitmap decoding requires the
    /// Avalonia headless platform (see Lesson Learned in sa_step_log_NoteTabEnhancements.md) - a
    /// plain [Fact] would silently leave Preview null instead of failing.</summary>
    [AvaloniaFact]
    public async Task AddPendingAttachment_ValidImageBytes_DecodesPreviewBitmap()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (editor, _) = await CreateEditorAsync(tempDir);

            editor.AddPendingAttachment(CreateTestPngBytes(), "chart.png");

            var pending = Assert.Single(editor.PendingAttachments);
            Assert.NotNull(pending.Preview);
            Assert.NotEqual(Guid.Empty, pending.LocalId);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>Task D: the compose panel's preview size must follow the same Settings &gt; Notes
    /// ThumbnailSizePixels value used for posted Notes' thumbnail row (FR-05).</summary>
    [Fact]
    public async Task ThumbnailSizePixels_ReflectsInjectedNotesSettingsManager()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var notesSettingsManager = new FakeNotesSettingsManager();
            notesSettingsManager.SetThumbnailSizePixels(96.0);
            var connectionManager = new NoteDatabaseConnectionManager(NullLogger<NoteDatabaseConnectionManager>.Instance, tempDir);
            var schemaInitializer = new NoteSchemaInitializer(connectionManager, NullLogger<NoteSchemaInitializer>.Instance);
            await schemaInitializer.InitializeAsync();
            var noteRepository = new NoteRepository(connectionManager, NullLogger<NoteRepository>.Instance);
            var attachmentRepository = new AttachmentRepository(connectionManager, NullLogger<AttachmentRepository>.Instance);
            var cacheSynchronizer = new TickerMetadataNotesCacheSynchronizer(
                noteRepository, UserStrategyMetadataRepository.Instance, notesSettingsManager, NullLogger<TickerMetadataNotesCacheSynchronizer>.Instance);

            var editor = new NoteEditorViewModel(noteRepository, attachmentRepository, cacheSynchronizer, new FakeMarketDataProvider(), new FakeDispatcherService(), notesSettingsManager, NullLogger<NoteEditorViewModel>.Instance);

            Assert.Equal(96.0, editor.ThumbnailSizePixels);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task SaveAsync_BodyContainingHttpsUrl_AutoExtractsItIntoLinkUrls()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (editor, noteRepository) = await CreateEditorAsync(tempDir);
            editor.Body = "決算資料 https://example.com/report を確認。";

            await editor.SaveCommand.ExecuteAsync(null);

            var stored = await noteRepository.GetByIdAsync(editor.SavedNote!.Id);
            Assert.Contains("https://example.com/report", stored!.LinkUrls);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,evil")]
    [InlineData("file:///etc/passwd")]
    [InlineData("not a url")]
    public async Task SaveAsync_BodyContainingDisallowedOrInvalidScheme_DoesNotAddToLinkUrls(string candidate)
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (editor, noteRepository) = await CreateEditorAsync(tempDir);
            editor.Body = $"note body with {candidate} inside";

            await editor.SaveCommand.ExecuteAsync(null);

            var stored = await noteRepository.GetByIdAsync(editor.SavedNote!.Id);
            Assert.Empty(stored!.LinkUrls);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task LoadForEdit_ThenSaveAsync_UpdatesExistingNote_PreservesCreatedAt()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (editor, noteRepository) = await CreateEditorAsync(tempDir);
            editor.Body = "original body";
            await editor.SaveCommand.ExecuteAsync(null);
            var original = editor.SavedNote!;

            editor.LoadForEdit(original);
            editor.Body = "corrected body (typo fix)";
            await editor.SaveCommand.ExecuteAsync(null);

            var stored = await noteRepository.GetByIdAsync(original.Id);
            Assert.NotNull(stored);
            Assert.Equal("corrected body (typo fix)", stored!.Body);
            Assert.Equal(original.CreatedAt, stored.CreatedAt);
            Assert.True(stored.UpdatedAt >= stored.CreatedAt);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task LoadForEdit_ThenChangeTicker_SaveAsync_RecalculatesBothOldAndNewTickerCache()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (editor, _) = await CreateEditorAsync(tempDir);
            var oldTicker = UniqueTicker();
            var newTicker = UniqueTicker();

            editor.Body = "moving between tickers";
            editor.RelatedTicker = oldTicker;
            await editor.SaveCommand.ExecuteAsync(null);
            var original = editor.SavedNote!;
            Assert.Equal("moving between tickers", UserStrategyMetadataRepository.Instance.GetStrategy(oldTicker)!.Notes);

            editor.LoadForEdit(original);
            editor.RelatedTicker = newTicker;
            await editor.SaveCommand.ExecuteAsync(null);

            Assert.Null(UserStrategyMetadataRepository.Instance.GetStrategy(oldTicker)!.Notes);
            Assert.Equal("moving between tickers", UserStrategyMetadataRepository.Instance.GetStrategy(newTicker)!.Notes);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task LoadForQuote_ThenSaveAsync_CreatesNewNoteWithQuotedNoteIdSet()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (editor, noteRepository) = await CreateEditorAsync(tempDir);
            editor.Body = "the original post";
            await editor.SaveCommand.ExecuteAsync(null);
            var original = editor.SavedNote!;

            editor.LoadForQuote(original);
            editor.Body = "quoting the original";
            await editor.SaveCommand.ExecuteAsync(null);
            var quoteNote = editor.SavedNote!;

            Assert.NotEqual(original.Id, quoteNote.Id);
            Assert.Equal(original.Id, quoteNote.QuotedNoteId);
            Assert.Null(quoteNote.ParentNoteId);

            var storedQuote = await noteRepository.GetByIdAsync(quoteNote.Id);
            Assert.Equal(original.Id, storedQuote!.QuotedNoteId);
            var storedOriginal = await noteRepository.GetByIdAsync(original.Id);
            Assert.Equal("the original post", storedOriginal!.Body);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task LoadForReply_ThenSaveAsync_CreatesNewNoteWithParentNoteIdSet()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (editor, noteRepository) = await CreateEditorAsync(tempDir);
            editor.Body = "the parent post";
            await editor.SaveCommand.ExecuteAsync(null);
            var parent = editor.SavedNote!;

            editor.LoadForReply(parent);
            editor.Body = "replying to the parent";
            await editor.SaveCommand.ExecuteAsync(null);
            var replyNote = editor.SavedNote!;

            Assert.NotEqual(parent.Id, replyNote.Id);
            Assert.Equal(parent.Id, replyNote.ParentNoteId);
            Assert.Null(replyNote.QuotedNoteId);

            var replies = await noteRepository.GetRepliesAsync(parent.Id);
            Assert.Single(replies);
            Assert.Equal(replyNote.Id, replies[0].Id);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task LoadForQuote_ResetsEditorToCreateMode_EvenWhenPreviouslyEditing()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (editor, _) = await CreateEditorAsync(tempDir);
            editor.Body = "note being edited";
            await editor.SaveCommand.ExecuteAsync(null);
            var editingTarget = editor.SavedNote!;
            editor.LoadForEdit(editingTarget);
            Assert.True(editor.IsEditingExistingNote);

            var toQuote = new Note(Guid.NewGuid(), "someone else's post", DateTime.Now, DateTime.Now);
            editor.LoadForQuote(toQuote);

            Assert.False(editor.IsEditingExistingNote);
            Assert.Equal(string.Empty, editor.Body);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }
}
