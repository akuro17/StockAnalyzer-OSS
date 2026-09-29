using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Models.Notes;
using StockAnalyzer.Core.Services.Notes;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services.Notes;

public class NoteSearchServiceTests
{
    private static string CreateIsolatedTempDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "sa_notes_search_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    private static async Task<(NoteSearchService SearchService, NoteRepository NoteRepository)> CreateSearchServiceAsync(string tempDir)
    {
        var connectionManager = new NoteDatabaseConnectionManager(NullLogger<NoteDatabaseConnectionManager>.Instance, tempDir);
        var schemaInitializer = new NoteSchemaInitializer(connectionManager, NullLogger<NoteSchemaInitializer>.Instance);
        await schemaInitializer.InitializeAsync();

        var noteRepository = new NoteRepository(connectionManager, NullLogger<NoteRepository>.Instance);
        var searchService = new NoteSearchService(connectionManager, noteRepository, NullLogger<NoteSearchService>.Instance);
        return (searchService, noteRepository);
    }

    [Fact]
    public async Task SearchAsync_MatchesByBodyKeyword_CaseInsensitive()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (search, noteRepository) = await CreateSearchServiceAsync(tempDir);
            var note = new Note(Guid.NewGuid(), "The EV market in China is growing fast", DateTime.Now, DateTime.Now);
            await noteRepository.CreateAsync(note);
            await noteRepository.CreateAsync(new Note(Guid.NewGuid(), "unrelated content about semiconductors", DateTime.Now, DateTime.Now));

            var results = await search.SearchAsync("ev");

            Assert.Single(results);
            Assert.Equal(note.Id, results[0].Id);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task SearchAsync_MatchesByHashtag()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (search, noteRepository) = await CreateSearchServiceAsync(tempDir);
            var body = "中国市場について考察している。 #EV #中国";
            var note = new Note(Guid.NewGuid(), body, DateTime.Now, DateTime.Now)
            {
                Hashtags = HashtagExtractor.Extract(body),
            };
            await noteRepository.CreateAsync(note);

            var results = await search.SearchAsync("中国");

            Assert.Single(results);
            Assert.Equal(note.Id, results[0].Id);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task SearchAsync_MultipleSpaceSeparatedKeywords_AreOrCombined()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (search, noteRepository) = await CreateSearchServiceAsync(tempDir);
            var noteA = new Note(Guid.NewGuid(), "discussion about semiconductors", DateTime.Now, DateTime.Now);
            var noteB = new Note(Guid.NewGuid(), "electric vehicle demand forecast", DateTime.Now, DateTime.Now);
            var unrelated = new Note(Guid.NewGuid(), "quarterly earnings summary", DateTime.Now, DateTime.Now);
            await noteRepository.CreateAsync(noteA);
            await noteRepository.CreateAsync(noteB);
            await noteRepository.CreateAsync(unrelated);

            var results = await search.SearchAsync("semiconductors vehicle");

            Assert.Equal(2, results.Count);
            Assert.Contains(results, n => n.Id == noteA.Id);
            Assert.Contains(results, n => n.Id == noteB.Id);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task SearchAsync_PrefixMatch_FindsWordsStartingWithTheKeyword()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (search, noteRepository) = await CreateSearchServiceAsync(tempDir);
            var note = new Note(Guid.NewGuid(), "manufacturing capacity is expanding rapidly", DateTime.Now, DateTime.Now);
            await noteRepository.CreateAsync(note);

            var results = await search.SearchAsync("manu");

            Assert.Single(results);
            Assert.Equal(note.Id, results[0].Id);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task SearchAsync_ExcludesLogicallyDeletedNotes()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (search, noteRepository) = await CreateSearchServiceAsync(tempDir);
            var note = new Note(Guid.NewGuid(), "confidential strategy notes", DateTime.Now, DateTime.Now);
            await noteRepository.CreateAsync(note);
            await noteRepository.SoftDeleteAsync(note.Id);

            var results = await search.SearchAsync("confidential");

            Assert.Empty(results);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task SearchAsync_WithBlankSearchText_ReturnsEmpty()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (search, noteRepository) = await CreateSearchServiceAsync(tempDir);
            await noteRepository.CreateAsync(new Note(Guid.NewGuid(), "some content", DateTime.Now, DateTime.Now));

            var results = await search.SearchAsync("   ");

            Assert.Empty(results);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task SearchAsync_ReflectsBodyUpdatedViaUpdateAsync()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (search, noteRepository) = await CreateSearchServiceAsync(tempDir);
            var note = new Note(Guid.NewGuid(), "original wording", DateTime.Now, DateTime.Now);
            await noteRepository.CreateAsync(note);
            await noteRepository.UpdateAsync(note with { Body = "revised wording about aluminum tariffs" });

            var oldTextResults = await search.SearchAsync("original");
            var newTextResults = await search.SearchAsync("aluminum");

            Assert.Empty(oldTextResults);
            Assert.Single(newTextResults);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task SearchAsync_NotesCreatedBeforeFirstSearchCall_AreStillFoundViaBackfill()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            // Create Notes directly through the repository, entirely independent of NoteSearchService,
            // before the search service (and therefore its FTS index/triggers) has ever run.
            var connectionManager = new NoteDatabaseConnectionManager(NullLogger<NoteDatabaseConnectionManager>.Instance, tempDir);
            var schemaInitializer = new NoteSchemaInitializer(connectionManager, NullLogger<NoteSchemaInitializer>.Instance);
            await schemaInitializer.InitializeAsync();
            var noteRepository = new NoteRepository(connectionManager, NullLogger<NoteRepository>.Instance);
            var preExisting = new Note(Guid.NewGuid(), "pre-existing note about lithium supply", DateTime.Now, DateTime.Now);
            await noteRepository.CreateAsync(preExisting);

            var search = new NoteSearchService(connectionManager, noteRepository, NullLogger<NoteSearchService>.Instance);
            var results = await search.SearchAsync("lithium");

            Assert.Single(results);
            Assert.Equal(preExisting.Id, results[0].Id);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }
}
