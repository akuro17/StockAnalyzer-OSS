using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Notes;
using StockAnalyzer.Core.Services.Notes;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services.Notes;

public class NoteRepositoryTests
{
    private static string CreateIsolatedTempDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "sa_notes_repo_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    private static async Task<(NoteRepository Repository, NoteDatabaseConnectionManager ConnectionManager)> CreateInitializedRepositoryAsync(string tempDir)
    {
        var connectionManager = new NoteDatabaseConnectionManager(NullLogger<NoteDatabaseConnectionManager>.Instance, tempDir);
        var schemaInitializer = new NoteSchemaInitializer(connectionManager, NullLogger<NoteSchemaInitializer>.Instance);
        await schemaInitializer.InitializeAsync();

        return (new NoteRepository(connectionManager, NullLogger<NoteRepository>.Instance), connectionManager);
    }

    [Fact]
    public async Task CreateAsync_ThenGetByIdAsync_RoundTripsAllFields()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (repository, _) = await CreateInitializedRepositoryAsync(tempDir);

            var createdAt = new DateTime(2026, 8, 12, 16, 32, 0, DateTimeKind.Local);
            var chartAnchorDate = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Local);
            var note = new Note(Guid.NewGuid(), "中国市場について考察 #EV #中国", createdAt, createdAt)
            {
                ChartAnchorDate = chartAnchorDate,
                ChartTimeframe = TimeFrame.D1,
                RelatedTicker = "7203.T",
                Hashtags = ImmutableArray.Create("ev", "china"),
                AttachmentIds = ImmutableArray.Create(Guid.NewGuid(), Guid.NewGuid()),
                LinkUrls = ImmutableArray.Create("https://example.com/report"),
                IsPinned = true,
            };

            await repository.CreateAsync(note);
            var fetched = await repository.GetByIdAsync(note.Id);

            Assert.NotNull(fetched);
            Assert.Equal(note.Id, fetched!.Id);
            Assert.Equal(note.Body, fetched.Body);
            Assert.Equal(note.CreatedAt, fetched.CreatedAt);
            Assert.Equal(note.UpdatedAt, fetched.UpdatedAt);
            Assert.Equal(note.ChartAnchorDate, fetched.ChartAnchorDate);
            Assert.Equal(note.ChartTimeframe, fetched.ChartTimeframe);
            Assert.Equal(note.RelatedTicker, fetched.RelatedTicker);
            Assert.Equal(note.Hashtags, fetched.Hashtags);
            Assert.Equal(note.AttachmentIds.OrderBy(a => a), fetched.AttachmentIds.OrderBy(a => a));
            Assert.Equal(note.LinkUrls, fetched.LinkUrls);
            Assert.True(fetched.IsPinned);
            Assert.False(fetched.IsDeleted);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task CreateAsync_ThenGetByIdAsync_RoundTripsQuotedNoteIdAndParentNoteId()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (repository, _) = await CreateInitializedRepositoryAsync(tempDir);

            var original = new Note(Guid.NewGuid(), "original", DateTime.Now, DateTime.Now);
            await repository.CreateAsync(original);

            var quoteNote = new Note(Guid.NewGuid(), "quoting the original", DateTime.Now, DateTime.Now) { QuotedNoteId = original.Id };
            var replyNote = new Note(Guid.NewGuid(), "replying to the original", DateTime.Now, DateTime.Now) { ParentNoteId = original.Id };
            await repository.CreateAsync(quoteNote);
            await repository.CreateAsync(replyNote);

            var fetchedQuote = await repository.GetByIdAsync(quoteNote.Id);
            var fetchedReply = await repository.GetByIdAsync(replyNote.Id);

            Assert.Equal(original.Id, fetchedQuote!.QuotedNoteId);
            Assert.Null(fetchedQuote.ParentNoteId);
            Assert.Equal(original.Id, fetchedReply!.ParentNoteId);
            Assert.Null(fetchedReply.QuotedNoteId);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task UpdateAsync_DoesNotAllowChangingQuotedNoteIdOrParentNoteId()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (repository, _) = await CreateInitializedRepositoryAsync(tempDir);

            var parent = new Note(Guid.NewGuid(), "parent", DateTime.Now, DateTime.Now);
            var otherParent = new Note(Guid.NewGuid(), "other parent", DateTime.Now, DateTime.Now);
            await repository.CreateAsync(parent);
            await repository.CreateAsync(otherParent);

            var reply = new Note(Guid.NewGuid(), "reply", DateTime.Now, DateTime.Now) { ParentNoteId = parent.Id };
            await repository.CreateAsync(reply);

            // Attempt to re-parent via UpdateAsync; the Quote/Reply relationship is set only at
            // creation time (spec: not mutable through the normal edit flow).
            var attemptedRetarget = reply with { ParentNoteId = otherParent.Id, Body = "edited reply" };
            await repository.UpdateAsync(attemptedRetarget);

            var fetched = await repository.GetByIdAsync(reply.Id);

            Assert.Equal("edited reply", fetched!.Body);
            Assert.Equal(parent.Id, fetched.ParentNoteId);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task GetRepliesAsync_ReturnsDirectRepliesOrderedByCreatedAtAscending_ExcludingDeleted()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (repository, _) = await CreateInitializedRepositoryAsync(tempDir);

            var parent = new Note(Guid.NewGuid(), "parent", DateTime.Now, DateTime.Now);
            await repository.CreateAsync(parent);

            var earlierReply = new Note(Guid.NewGuid(), "earlier reply", new DateTime(2026, 8, 1), new DateTime(2026, 8, 1)) { ParentNoteId = parent.Id };
            var laterReply = new Note(Guid.NewGuid(), "later reply", new DateTime(2026, 8, 10), new DateTime(2026, 8, 10)) { ParentNoteId = parent.Id };
            var deletedReply = new Note(Guid.NewGuid(), "deleted reply", new DateTime(2026, 8, 5), new DateTime(2026, 8, 5)) { ParentNoteId = parent.Id };
            var unrelatedNote = new Note(Guid.NewGuid(), "unrelated", DateTime.Now, DateTime.Now);
            await repository.CreateAsync(earlierReply);
            await repository.CreateAsync(laterReply);
            await repository.CreateAsync(deletedReply);
            await repository.CreateAsync(unrelatedNote);
            await repository.SoftDeleteAsync(deletedReply.Id);

            var replies = await repository.GetRepliesAsync(parent.Id);

            Assert.Equal(new[] { earlierReply.Id, laterReply.Id }, replies.Select(n => n.Id));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task GetRepliesAsync_WhenNoReplies_ReturnsEmpty()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (repository, _) = await CreateInitializedRepositoryAsync(tempDir);
            var note = new Note(Guid.NewGuid(), "note", DateTime.Now, DateTime.Now);
            await repository.CreateAsync(note);

            var replies = await repository.GetRepliesAsync(note.Id);

            Assert.Empty(replies);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task GetRepliesIncludingDeletedAsync_ReturnsDirectRepliesOrderedByCreatedAtAscending_IncludingDeleted()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (repository, _) = await CreateInitializedRepositoryAsync(tempDir);

            var parent = new Note(Guid.NewGuid(), "parent", DateTime.Now, DateTime.Now);
            await repository.CreateAsync(parent);

            var earlierReply = new Note(Guid.NewGuid(), "earlier reply", new DateTime(2026, 8, 1), new DateTime(2026, 8, 1)) { ParentNoteId = parent.Id };
            var deletedReply = new Note(Guid.NewGuid(), "deleted reply", new DateTime(2026, 8, 5), new DateTime(2026, 8, 5)) { ParentNoteId = parent.Id };
            var laterReply = new Note(Guid.NewGuid(), "later reply", new DateTime(2026, 8, 10), new DateTime(2026, 8, 10)) { ParentNoteId = parent.Id };
            await repository.CreateAsync(earlierReply);
            await repository.CreateAsync(deletedReply);
            await repository.CreateAsync(laterReply);
            await repository.SoftDeleteAsync(deletedReply.Id);

            var replies = await repository.GetRepliesIncludingDeletedAsync(parent.Id);

            Assert.Equal(new[] { earlierReply.Id, deletedReply.Id, laterReply.Id }, replies.Select(n => n.Id));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task GetDescendantRepliesIncludingDeletedAsync_ReturnsWholeMultiLevelSubtree_IncludingDeleted()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (repository, _) = await CreateInitializedRepositoryAsync(tempDir);

            var root = new Note(Guid.NewGuid(), "root", DateTime.Now, DateTime.Now);
            await repository.CreateAsync(root);

            var deletedChild = new Note(Guid.NewGuid(), "deleted child", DateTime.Now, DateTime.Now) { ParentNoteId = root.Id };
            await repository.CreateAsync(deletedChild);
            await repository.SoftDeleteAsync(deletedChild.Id);

            var grandchild = new Note(Guid.NewGuid(), "grandchild below deleted child", DateTime.Now, DateTime.Now) { ParentNoteId = deletedChild.Id };
            await repository.CreateAsync(grandchild);

            var unrelated = new Note(Guid.NewGuid(), "unrelated root", DateTime.Now, DateTime.Now);
            await repository.CreateAsync(unrelated);

            var descendants = await repository.GetDescendantRepliesIncludingDeletedAsync(root.Id);

            Assert.Equal(
                new[] { deletedChild.Id, grandchild.Id }.OrderBy(id => id),
                descendants.Select(n => n.Id).OrderBy(id => id));
            Assert.DoesNotContain(unrelated.Id, descendants.Select(n => n.Id));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task GetDeletedNotesReferencedAsParentAsync_ReturnsOnlyDeletedNotesThatAreSomeNotesParent()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (repository, _) = await CreateInitializedRepositoryAsync(tempDir);

            var deletedWithChild = new Note(Guid.NewGuid(), "deleted, has a child", DateTime.Now, DateTime.Now);
            await repository.CreateAsync(deletedWithChild);
            var child = new Note(Guid.NewGuid(), "child of deleted", DateTime.Now, DateTime.Now) { ParentNoteId = deletedWithChild.Id };
            await repository.CreateAsync(child);
            await repository.SoftDeleteAsync(deletedWithChild.Id);

            var deletedWithoutChild = new Note(Guid.NewGuid(), "deleted, no children", DateTime.Now, DateTime.Now);
            await repository.CreateAsync(deletedWithoutChild);
            await repository.SoftDeleteAsync(deletedWithoutChild.Id);

            var liveNote = new Note(Guid.NewGuid(), "live, unrelated", DateTime.Now, DateTime.Now);
            await repository.CreateAsync(liveNote);

            var result = await repository.GetDeletedNotesReferencedAsParentAsync();

            Assert.Equal(new[] { deletedWithChild.Id }, result.Select(n => n.Id));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task GetAncestorChainAsync_ReturnsAncestorsOrderedRootFirst()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (repository, _) = await CreateInitializedRepositoryAsync(tempDir);

            var root = new Note(Guid.NewGuid(), "root", new DateTime(2026, 8, 1), new DateTime(2026, 8, 1));
            await repository.CreateAsync(root);
            var middle = new Note(Guid.NewGuid(), "middle", new DateTime(2026, 8, 2), new DateTime(2026, 8, 2)) { ParentNoteId = root.Id };
            await repository.CreateAsync(middle);
            var leaf = new Note(Guid.NewGuid(), "leaf", new DateTime(2026, 8, 3), new DateTime(2026, 8, 3)) { ParentNoteId = middle.Id };
            await repository.CreateAsync(leaf);

            var chain = await repository.GetAncestorChainAsync(leaf.Id);

            Assert.Equal(new[] { root.Id, middle.Id }, chain.Select(n => n.Id));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task GetAncestorChainAsync_WhenNoteHasNoParent_ReturnsEmpty()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (repository, _) = await CreateInitializedRepositoryAsync(tempDir);
            var note = new Note(Guid.NewGuid(), "root", DateTime.Now, DateTime.Now);
            await repository.CreateAsync(note);

            var chain = await repository.GetAncestorChainAsync(note.Id);

            Assert.Empty(chain);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task GetAncestorChainAsync_WhenParentWasPermanentlyDeleted_StopsChainGracefully()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (repository, _) = await CreateInitializedRepositoryAsync(tempDir);

            var root = new Note(Guid.NewGuid(), "root", new DateTime(2026, 8, 1), new DateTime(2026, 8, 1));
            await repository.CreateAsync(root);
            var middle = new Note(Guid.NewGuid(), "middle", new DateTime(2026, 8, 2), new DateTime(2026, 8, 2)) { ParentNoteId = root.Id };
            await repository.CreateAsync(middle);
            var leaf = new Note(Guid.NewGuid(), "leaf", new DateTime(2026, 8, 3), new DateTime(2026, 8, 3)) { ParentNoteId = middle.Id };
            await repository.CreateAsync(leaf);

            await repository.SoftDeleteAsync(root.Id);
            await repository.PermanentlyDeleteAsync(root.Id);

            var chain = await repository.GetAncestorChainAsync(leaf.Id);

            Assert.Equal(new[] { middle.Id }, chain.Select(n => n.Id));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task CreateAsync_WithMinimalFields_PersistsNullsAndEmptyCollections()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (repository, _) = await CreateInitializedRepositoryAsync(tempDir);

            var now = DateTime.Now;
            var note = new Note(Guid.NewGuid(), "no ticker note", now, now);

            await repository.CreateAsync(note);
            var fetched = await repository.GetByIdAsync(note.Id);

            Assert.NotNull(fetched);
            Assert.Null(fetched!.RelatedTicker);
            Assert.Null(fetched.ChartAnchorDate);
            Assert.Null(fetched.ChartTimeframe);
            Assert.Empty(fetched.Hashtags);
            Assert.Empty(fetched.AttachmentIds);
            Assert.Empty(fetched.LinkUrls);
            Assert.False(fetched.IsPinned);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task GetByIdAsync_WhenNotFound_ReturnsNull()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (repository, _) = await CreateInitializedRepositoryAsync(tempDir);

            var fetched = await repository.GetByIdAsync(Guid.NewGuid());

            Assert.Null(fetched);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>sa_constraint_check remediation (Phase 1): GetByIdsAsync replaces
    /// NoteTimelineViewModel.ResolveQuotedNotesAsync's old one-GetByIdAsync-call-per-id loop with a
    /// single batched query. Proves it returns every matching Note (in any order), silently omits ids
    /// with no matching row instead of throwing (mirrors GetByIdAsync's null-for-missing contract),
    /// and only queries each distinct id once even if it's passed in twice.</summary>
    [Fact]
    public async Task GetByIdsAsync_ReturnsMatchingNotes_OmitsMissingIds_DeduplicatesInput()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (repository, _) = await CreateInitializedRepositoryAsync(tempDir);

            var first = new Note(Guid.NewGuid(), "first", DateTime.Now, DateTime.Now);
            var second = new Note(Guid.NewGuid(), "second", DateTime.Now, DateTime.Now);
            await repository.CreateAsync(first);
            await repository.CreateAsync(second);
            var missingId = Guid.NewGuid();

            var fetched = await repository.GetByIdsAsync(new[] { first.Id, second.Id, missingId, first.Id });

            Assert.Equal(2, fetched.Count);
            Assert.Contains(fetched, n => n.Id == first.Id && n.Body == "first");
            Assert.Contains(fetched, n => n.Id == second.Id && n.Body == "second");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task GetByIdsAsync_WithEmptyInput_ReturnsEmpty_WithoutQuerying()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (repository, _) = await CreateInitializedRepositoryAsync(tempDir);

            var fetched = await repository.GetByIdsAsync(Array.Empty<Guid>());

            Assert.Empty(fetched);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task GetAllActiveAsync_ReturnsNonDeletedNotes_OrderedByCreatedAtDescending()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (repository, _) = await CreateInitializedRepositoryAsync(tempDir);

            var older = new Note(Guid.NewGuid(), "older", new DateTime(2026, 8, 1), new DateTime(2026, 8, 1));
            var newer = new Note(Guid.NewGuid(), "newer", new DateTime(2026, 8, 10), new DateTime(2026, 8, 10));
            await repository.CreateAsync(older);
            await repository.CreateAsync(newer);

            var results = await repository.GetAllActiveAsync();

            Assert.Equal(2, results.Count);
            Assert.Equal(newer.Id, results[0].Id);
            Assert.Equal(older.Id, results[1].Id);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task GetAllActiveAsync_ExcludesLogicallyDeletedNotes()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (repository, _) = await CreateInitializedRepositoryAsync(tempDir);

            var active = new Note(Guid.NewGuid(), "active", DateTime.Now, DateTime.Now);
            var deleted = new Note(Guid.NewGuid(), "deleted", DateTime.Now, DateTime.Now);
            await repository.CreateAsync(active);
            await repository.CreateAsync(deleted);

            await repository.SoftDeleteAsync(deleted.Id);

            var results = await repository.GetAllActiveAsync();

            Assert.Single(results);
            Assert.Equal(active.Id, results[0].Id);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task GetAllDeletedAsync_ReturnsOnlyDeletedNotes_OrderedByCreatedAtDescending()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (repository, _) = await CreateInitializedRepositoryAsync(tempDir);

            var active = new Note(Guid.NewGuid(), "active", DateTime.Now, DateTime.Now);
            var olderDeleted = new Note(Guid.NewGuid(), "older deleted", new DateTime(2026, 8, 1), new DateTime(2026, 8, 1));
            var newerDeleted = new Note(Guid.NewGuid(), "newer deleted", new DateTime(2026, 8, 10), new DateTime(2026, 8, 10));
            await repository.CreateAsync(active);
            await repository.CreateAsync(olderDeleted);
            await repository.CreateAsync(newerDeleted);
            await repository.SoftDeleteAsync(olderDeleted.Id);
            await repository.SoftDeleteAsync(newerDeleted.Id);

            var results = await repository.GetAllDeletedAsync();

            Assert.Equal(new[] { newerDeleted.Id, olderDeleted.Id }, results.Select(n => n.Id));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task GetAllDeletedAsync_WhenTrashEmpty_ReturnsEmpty()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (repository, _) = await CreateInitializedRepositoryAsync(tempDir);
            await repository.CreateAsync(new Note(Guid.NewGuid(), "active", DateTime.Now, DateTime.Now));

            var results = await repository.GetAllDeletedAsync();

            Assert.Empty(results);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task CountDeletedAsync_MatchesGetAllDeletedAsyncCount()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (repository, _) = await CreateInitializedRepositoryAsync(tempDir);

            var active = new Note(Guid.NewGuid(), "active", DateTime.Now, DateTime.Now);
            var deletedOne = new Note(Guid.NewGuid(), "deleted one", DateTime.Now, DateTime.Now);
            var deletedTwo = new Note(Guid.NewGuid(), "deleted two", DateTime.Now, DateTime.Now);
            await repository.CreateAsync(active);
            await repository.CreateAsync(deletedOne);
            await repository.CreateAsync(deletedTwo);
            await repository.SoftDeleteAsync(deletedOne.Id);
            await repository.SoftDeleteAsync(deletedTwo.Id);

            var count = await repository.CountDeletedAsync();

            Assert.Equal(2, count);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task CountDeletedAsync_WhenTrashEmpty_ReturnsZero()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (repository, _) = await CreateInitializedRepositoryAsync(tempDir);
            await repository.CreateAsync(new Note(Guid.NewGuid(), "active", DateTime.Now, DateTime.Now));

            var count = await repository.CountDeletedAsync();

            Assert.Equal(0, count);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task UpdateAsync_ChangesBodyAndAdvancesUpdatedAt_ButPreservesCreatedAt()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (repository, _) = await CreateInitializedRepositoryAsync(tempDir);

            var createdAt = new DateTime(2026, 8, 1, 9, 0, 0);
            var note = new Note(Guid.NewGuid(), "original body", createdAt, createdAt) { RelatedTicker = "7203.T" };
            await repository.CreateAsync(note);

            var edited = note with { Body = "corrected body (typo fix)" };
            await repository.UpdateAsync(edited);

            var fetched = await repository.GetByIdAsync(note.Id);

            Assert.NotNull(fetched);
            Assert.Equal("corrected body (typo fix)", fetched!.Body);
            Assert.Equal(createdAt, fetched.CreatedAt);
            Assert.True(fetched.UpdatedAt >= fetched.CreatedAt);
            Assert.True(fetched.UpdatedAt > createdAt);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task UpdateAsync_WhenNoteDoesNotExist_Throws()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (repository, _) = await CreateInitializedRepositoryAsync(tempDir);
            var missing = new Note(Guid.NewGuid(), "body", DateTime.Now, DateTime.Now);

            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.UpdateAsync(missing));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task SoftDeleteAsync_ThenRestoreAsync_MakesNoteActiveAgain()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (repository, _) = await CreateInitializedRepositoryAsync(tempDir);
            var note = new Note(Guid.NewGuid(), "body", DateTime.Now, DateTime.Now);
            await repository.CreateAsync(note);

            await repository.SoftDeleteAsync(note.Id);
            Assert.Empty(await repository.GetAllActiveAsync());
            var deletedRow = await repository.GetByIdAsync(note.Id);
            Assert.True(deletedRow!.IsDeleted);

            await repository.RestoreAsync(note.Id);
            var restored = await repository.GetAllActiveAsync();

            Assert.Single(restored);
            Assert.Equal(note.Id, restored[0].Id);
            Assert.False(restored[0].IsDeleted);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task SoftDeleteAsync_WhenNoteDoesNotExist_Throws()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (repository, _) = await CreateInitializedRepositoryAsync(tempDir);

            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SoftDeleteAsync(Guid.NewGuid()));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task PermanentlyDeleteAsync_RemovesNoteRow()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (repository, _) = await CreateInitializedRepositoryAsync(tempDir);
            var note = new Note(Guid.NewGuid(), "to be purged", DateTime.Now, DateTime.Now);
            await repository.CreateAsync(note);
            await repository.SoftDeleteAsync(note.Id);

            await repository.PermanentlyDeleteAsync(note.Id);

            Assert.Null(await repository.GetByIdAsync(note.Id));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task PermanentlyDeleteAsync_WhenNoteDoesNotExist_Throws()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (repository, _) = await CreateInitializedRepositoryAsync(tempDir);

            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.PermanentlyDeleteAsync(Guid.NewGuid()));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task PermanentlyDeleteAsync_DeletesLinkedAttachmentFileAndRow()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (repository, connectionManager) = await CreateInitializedRepositoryAsync(tempDir);

            var attachmentId = Guid.NewGuid();
            var storedFileName = $"{attachmentId}.jpg";
            var attachmentsDir = Path.Combine(tempDir, "Attachments");
            Directory.CreateDirectory(attachmentsDir);
            var filePath = Path.Combine(attachmentsDir, storedFileName);
            await File.WriteAllBytesAsync(filePath, new byte[] { 1, 2, 3 });

            using (var connection = await connectionManager.OpenConnectionAsync())
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO Attachments (Id, StoredFileName, OriginalFileName, MimeType, Width, Height, FileSizeBytes, CreatedAt)
                    VALUES ($id, $storedFileName, 'original.jpg', 'image/jpeg', 100, 100, 3, $createdAt);
                    """;
                command.Parameters.AddWithValue("$id", attachmentId.ToString());
                command.Parameters.AddWithValue("$storedFileName", storedFileName);
                command.Parameters.AddWithValue("$createdAt", DateTime.Now.ToString("o"));
                await command.ExecuteNonQueryAsync();
            }

            var note = new Note(Guid.NewGuid(), "note with attachment", DateTime.Now, DateTime.Now)
            {
                AttachmentIds = ImmutableArray.Create(attachmentId),
            };
            await repository.CreateAsync(note);
            await repository.SoftDeleteAsync(note.Id);
            Assert.True(File.Exists(filePath));

            await repository.PermanentlyDeleteAsync(note.Id);

            Assert.Null(await repository.GetByIdAsync(note.Id));
            Assert.False(File.Exists(filePath));

            using (var connection = await connectionManager.OpenConnectionAsync())
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM Attachments WHERE Id = $id;";
                command.Parameters.AddWithValue("$id", attachmentId.ToString());
                var count = (long)(await command.ExecuteScalarAsync())!;
                Assert.Equal(0, count);
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task EmptyTrashAsync_RemovesAllLogicallyDeletedNotes_KeepsActiveOnes()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (repository, _) = await CreateInitializedRepositoryAsync(tempDir);

            var active = new Note(Guid.NewGuid(), "active", DateTime.Now, DateTime.Now);
            var trashed1 = new Note(Guid.NewGuid(), "trashed1", DateTime.Now, DateTime.Now);
            var trashed2 = new Note(Guid.NewGuid(), "trashed2", DateTime.Now, DateTime.Now);
            await repository.CreateAsync(active);
            await repository.CreateAsync(trashed1);
            await repository.CreateAsync(trashed2);
            await repository.SoftDeleteAsync(trashed1.Id);
            await repository.SoftDeleteAsync(trashed2.Id);

            await repository.EmptyTrashAsync();

            Assert.NotNull(await repository.GetByIdAsync(active.Id));
            Assert.Null(await repository.GetByIdAsync(trashed1.Id));
            Assert.Null(await repository.GetByIdAsync(trashed2.Id));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task EmptyTrashAsync_WhenTrashIsEmpty_DoesNotThrow()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (repository, _) = await CreateInitializedRepositoryAsync(tempDir);
            var active = new Note(Guid.NewGuid(), "active", DateTime.Now, DateTime.Now);
            await repository.CreateAsync(active);

            await repository.EmptyTrashAsync();

            Assert.NotNull(await repository.GetByIdAsync(active.Id));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }
}
