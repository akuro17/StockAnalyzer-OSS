using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Models;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.ViewModels.Notes;
using StockAnalyzer.Avalonia.ViewModels.TickerList;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using static StockAnalyzer.Avalonia.Tests.TestHelpers.NoteTimelineTestFixture;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Notes;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Notes;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels.Notes;

// FakeDialogService/FakeWatchlistManager/FakeMarketDataProvider/FakeTickerStateStore, and the
// NoteTimelineViewModel construction helper (CreateTimelineAsync) itself, live in
// StockAnalyzer.Avalonia.Tests.TestHelpers.NoteTestDoubles.cs (sa_constraint_check Phase 2: shared
// across this file, NoteTimelineView_UnifiedSearchTests.cs, NoteDetailViewTests.cs,
// NoteTimelineView_ComposePanelTests.cs and NoteTimelineView_ReplyConnectorLineTests.cs via the
// `using static` import below). IDispatcherService uses the project-wide
// StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService.
public class NoteTimelineViewModelTests
{
    private static string CreateIsolatedTempDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "sa_note_timeline_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    private static readonly Func<NoteTimelineItemViewModel, Task> NoOpAsyncCallback = _ => Task.CompletedTask;
    private static readonly Action<NoteTimelineItemViewModel> NoOpItemCallback = _ => { };
    private static readonly Action<string> NoOpUrlCallback = _ => { };

    private static Note MakeNote(string body, DateTime createdAt, string? ticker = null, bool isPinned = false, ImmutableArray<string>? hashtags = null, ImmutableArray<string>? linkUrls = null) =>
        new(Guid.NewGuid(), body, createdAt, createdAt)
        {
            RelatedTicker = ticker,
            IsPinned = isPinned,
            Hashtags = hashtags ?? ImmutableArray<string>.Empty,
            LinkUrls = linkUrls ?? ImmutableArray<string>.Empty,
        };

    [Fact]
    public async Task RefreshAsync_SortsPinnedNotesFirst_ThenByCreatedAtDescendingWithinEachGroup()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir);

            var oldUnpinned = MakeNote("old unpinned", new DateTime(2026, 8, 1));
            var newUnpinned = MakeNote("new unpinned", new DateTime(2026, 8, 10));
            var oldPinned = MakeNote("old pinned", new DateTime(2026, 8, 2), isPinned: true);
            var newPinned = MakeNote("new pinned", new DateTime(2026, 8, 12), isPinned: true);

            await noteRepository.CreateAsync(oldUnpinned);
            await noteRepository.CreateAsync(newUnpinned);
            await noteRepository.CreateAsync(oldPinned);
            await noteRepository.CreateAsync(newPinned);

            await timeline.RefreshAsync();

            var orderedIds = timeline.DisplayedNotes.Select(i => i.Note.Id).ToList();
            Assert.Equal(new[] { newPinned.Id, oldPinned.Id, newUnpinned.Id, oldUnpinned.Id }, orderedIds);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task RefreshAsync_AppliesFilterCriteria_ChangesDisplayedCount()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir);

            await noteRepository.CreateAsync(MakeNote("a", DateTime.Now, "7203.T"));
            await noteRepository.CreateAsync(MakeNote("b", DateTime.Now, "9984.T"));
            await noteRepository.CreateAsync(MakeNote("c", DateTime.Now));

            await timeline.RefreshAsync();
            Assert.Equal(3, timeline.DisplayedNotes.Count);

            timeline.FilterCriteria = new NoteFilterCriteria { SelectedTicker = "7203.T" };
            await timeline.RefreshAsync();

            Assert.Single(timeline.DisplayedNotes);
            Assert.Equal("a", timeline.DisplayedNotes[0].Note.Body);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task RefreshAsync_ExcludesLogicallyDeletedNotes()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir);
            var active = MakeNote("active", DateTime.Now);
            var deleted = MakeNote("deleted", DateTime.Now);
            await noteRepository.CreateAsync(active);
            await noteRepository.CreateAsync(deleted);
            await noteRepository.SoftDeleteAsync(deleted.Id);

            await timeline.RefreshAsync();

            Assert.Single(timeline.DisplayedNotes);
            Assert.Equal(active.Id, timeline.DisplayedNotes[0].Note.Id);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task ToggleExpandedCommand_FlipsStateAndSurvivesNextRefresh()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir);
            var note = MakeNote("body", DateTime.Now);
            await noteRepository.CreateAsync(note);
            await timeline.RefreshAsync();

            Assert.False(timeline.DisplayedNotes[0].IsExpanded);

            timeline.DisplayedNotes[0].ToggleExpandedCommand.Execute(null);
            Assert.True(timeline.DisplayedNotes[0].IsExpanded);

            // A later refresh rebuilds DisplayedNotes from scratch; the in-memory dictionary
            // (spec section 5.3: session-only, never persisted to disk) must restore the state.
            await timeline.RefreshAsync();

            Assert.True(timeline.DisplayedNotes[0].IsExpanded);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task TogglePinnedCommand_FlipsIsPinnedAndPersists()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir);
            var note = MakeNote("body", DateTime.Now);
            await noteRepository.CreateAsync(note);
            await timeline.RefreshAsync();

            await timeline.DisplayedNotes[0].TogglePinnedCommand.ExecuteAsync(null);

            var stored = await noteRepository.GetByIdAsync(note.Id);
            Assert.True(stored!.IsPinned);
            Assert.True(timeline.DisplayedNotes[0].Note.IsPinned);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task DeleteCommand_SoftDeletesNote_RemovesFromTimelineAndUpdatesCache()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir);
            var ticker = $"NOTE_TIMELINE_TEST_{Guid.NewGuid():N}";
            var note = MakeNote("body", DateTime.Now, ticker);
            await noteRepository.CreateAsync(note);
            await timeline.RefreshAsync();

            await timeline.DisplayedNotes[0].DeleteCommand.ExecuteAsync(null);

            Assert.Empty(timeline.DisplayedNotes);
            var stored = await noteRepository.GetByIdAsync(note.Id);
            Assert.True(stored!.IsDeleted);
            Assert.Null(UserStrategyMetadataRepository.Instance.GetStrategy(ticker)!.Notes);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task EditCommand_OpensEditorPreloadedWithNoteBody()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir);
            var note = MakeNote("original body", DateTime.Now);
            await noteRepository.CreateAsync(note);
            await timeline.RefreshAsync();

            Assert.Null(timeline.EditingNote);
            timeline.DisplayedNotes[0].EditCommand.Execute(null);

            Assert.NotNull(timeline.EditingNote);
            Assert.Equal("original body", timeline.EditingNote!.Body);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task QuoteCommand_OpensFreshEditor_AndSaveProducesNoteWithQuotedNoteIdSet()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir);
            var original = MakeNote("the original post", DateTime.Now);
            await noteRepository.CreateAsync(original);
            await timeline.RefreshAsync();

            Assert.Null(timeline.EditingNote);
            timeline.DisplayedNotes[0].QuoteCommand.Execute(null);

            Assert.NotNull(timeline.EditingNote);
            Assert.False(timeline.EditingNote!.IsEditingExistingNote);
            Assert.Equal(string.Empty, timeline.EditingNote.Body);

            timeline.EditingNote.Body = "quoting the original";
            await timeline.SaveEditCommand.ExecuteAsync(null);

            var quoteNote = await noteRepository.GetByIdAsync(
                timeline.DisplayedNotes.Single(n => n.Note.Body == "quoting the original").Note.Id);
            Assert.Equal(original.Id, quoteNote!.QuotedNoteId);
            Assert.Null(quoteNote.ParentNoteId);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task ReplyCommand_OpensFreshEditor_AndSaveProducesNoteWithParentNoteIdSet()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir);
            var parent = MakeNote("the parent post", DateTime.Now);
            await noteRepository.CreateAsync(parent);
            await timeline.RefreshAsync();

            Assert.Null(timeline.EditingNote);
            timeline.DisplayedNotes[0].ReplyCommand.Execute(null);

            Assert.NotNull(timeline.EditingNote);
            Assert.False(timeline.EditingNote!.IsEditingExistingNote);

            timeline.EditingNote.Body = "replying to the parent";
            await timeline.SaveEditCommand.ExecuteAsync(null);

            var replies = await noteRepository.GetRepliesAsync(parent.Id);
            Assert.Single(replies);
            Assert.Equal("replying to the parent", replies[0].Body);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task RefreshAsync_ResolvesQuotedNotePreview_ForNotesThatQuoteAnExistingNote()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir);
            var quoted = MakeNote("the original post being quoted", new DateTime(2026, 8, 1, 9, 0, 0));
            await noteRepository.CreateAsync(quoted);
            var quoting = MakeNote("my take on it", new DateTime(2026, 8, 2, 10, 0, 0)) with { QuotedNoteId = quoted.Id };
            await noteRepository.CreateAsync(quoting);

            await timeline.RefreshAsync();

            var quotingItem = timeline.DisplayedNotes.Single(n => n.Note.Id == quoting.Id);
            Assert.True(quotingItem.IsQuote);
            Assert.True(quotingItem.HasQuotedNotePreview);
            Assert.False(quotingItem.QuotedNoteWasDeleted);
            Assert.Equal(quoted.Id, quotingItem.QuotedNotePreview!.Id);
            Assert.Equal("the original post being quoted", quotingItem.QuotedNotePreviewExcerpt);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task RefreshAsync_SetsQuotedNoteWasDeleted_WhenTheQuotedNoteWasPermanentlyDeleted()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir);
            var quoted = MakeNote("will be purged", DateTime.Now);
            await noteRepository.CreateAsync(quoted);
            var quoting = MakeNote("quoting something now gone", DateTime.Now) with { QuotedNoteId = quoted.Id };
            await noteRepository.CreateAsync(quoting);

            await noteRepository.SoftDeleteAsync(quoted.Id);
            await noteRepository.PermanentlyDeleteAsync(quoted.Id);
            await timeline.RefreshAsync();

            var quotingItem = timeline.DisplayedNotes.Single(n => n.Note.Id == quoting.Id);
            Assert.True(quotingItem.IsQuote);
            Assert.True(quotingItem.QuotedNoteWasDeleted);
            Assert.False(quotingItem.HasQuotedNotePreview);
            Assert.Null(quotingItem.QuotedNotePreview);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task RefreshAsync_OrdinaryNote_IsQuoteIsFalse()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir);
            var note = MakeNote("just a regular note", DateTime.Now);
            await noteRepository.CreateAsync(note);

            await timeline.RefreshAsync();

            var item = timeline.DisplayedNotes.Single(n => n.Note.Id == note.Id);
            Assert.False(item.IsQuote);
            Assert.False(item.HasQuotedNotePreview);
            Assert.False(item.QuotedNoteWasDeleted);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>sa_minimal_fix (Quote/Reply Chain fix request #2, 2nd round): a reply must always be
    /// grouped directly under the Note it replies to - here the reply is chronologically the newest
    /// post, but it must still be displayed right after (below) its parent, with the parent flagged
    /// to draw the connector line down to it, not floating at the top as an unrelated post.</summary>
    [Fact]
    public async Task RefreshAsync_GroupsReplyDirectlyUnderItsParent()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir);
            var parent = MakeNote("parent post", new DateTime(2026, 8, 1));
            await noteRepository.CreateAsync(parent);
            var reply = MakeNote("reply post", new DateTime(2026, 8, 2)) with { ParentNoteId = parent.Id };
            await noteRepository.CreateAsync(reply);

            await timeline.RefreshAsync();

            // The reply is newer, but it is grouped directly under its parent instead of sorting above it.
            Assert.Equal(new[] { parent.Id, reply.Id }, timeline.DisplayedNotes.Select(n => n.Note.Id));
            Assert.True(timeline.DisplayedNotes[0].ConnectsDownToReplyCard);
            Assert.False(timeline.DisplayedNotes[1].ConnectsDownToReplyCard);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>Fix request #2 (2nd round): a reply must connect to its parent even when another,
    /// chronologically-interleaved post exists - the reply is still moved to sit directly under its
    /// parent rather than staying in its natural CreatedAt-descending slot at the top.</summary>
    [Fact]
    public async Task RefreshAsync_GroupsReplyUnderItsParent_EvenWhenAnotherPostWasCreatedBetweenThem()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir);
            var parent = MakeNote("parent post", new DateTime(2026, 8, 1));
            await noteRepository.CreateAsync(parent);
            var unrelated = MakeNote("unrelated post", new DateTime(2026, 8, 2));
            await noteRepository.CreateAsync(unrelated);
            var reply = MakeNote("reply post", new DateTime(2026, 8, 3)) with { ParentNoteId = parent.Id };
            await noteRepository.CreateAsync(reply);

            await timeline.RefreshAsync();

            // Roots (unrelated, parent) keep their normal CreatedAt-descending order; the reply is
            // pulled out of its own chronological slot to sit directly after its parent.
            Assert.Equal(new[] { unrelated.Id, parent.Id, reply.Id }, timeline.DisplayedNotes.Select(n => n.Note.Id));
            Assert.True(timeline.DisplayedNotes.Single(n => n.Note.Id == parent.Id).ConnectsDownToReplyCard);
            Assert.False(timeline.DisplayedNotes.Single(n => n.Note.Id == reply.Id).ConnectsDownToReplyCard);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>sa_implement (Notes-only Settings & Thread Collapsing Task 5, Y:\Temp\sa_implementation_plan.md);
    /// updated by sa_minimal_fix (bug-list item #2): a 5-post reply chain (root + 4 nested replies)
    /// exceeds the production-default ThreadCollapseThreshold(3) and 1+TailVisibleCount(3), so only
    /// the thread-starting root plus the last 2 posts are shown - B and C are omitted and the root
    /// (A) itself, not a reply, carries the collapse indicator with the correct hidden count.</summary>
    [Fact]
    public async Task RefreshAsync_LongReplyChain_CollapsesMiddleAndFlagsRootCard()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir, notesSettingsManager: new FakeNotesSettingsManager());
            var a = MakeNote("A", new DateTime(2026, 8, 1));
            await noteRepository.CreateAsync(a);
            var b = MakeNote("B", new DateTime(2026, 8, 2)) with { ParentNoteId = a.Id };
            await noteRepository.CreateAsync(b);
            var c = MakeNote("C", new DateTime(2026, 8, 3)) with { ParentNoteId = b.Id };
            await noteRepository.CreateAsync(c);
            var d = MakeNote("D", new DateTime(2026, 8, 4)) with { ParentNoteId = c.Id };
            await noteRepository.CreateAsync(d);
            var e = MakeNote("E", new DateTime(2026, 8, 5)) with { ParentNoteId = d.Id };
            await noteRepository.CreateAsync(e);

            await timeline.RefreshAsync();

            // B and C are omitted; only the root (A) and the last 2 replies (D, E) remain.
            Assert.Equal(new[] { a.Id, d.Id, e.Id }, timeline.DisplayedNotes.Select(n => n.Note.Id));

            var aItem = timeline.DisplayedNotes.Single(n => n.Note.Id == a.Id);
            Assert.True(aItem.HasCollapsedRepliesBelow);
            Assert.Equal(2, aItem.CollapsedReplyCount);
            Assert.False(aItem.ConnectsDownToReplyCard);

            foreach (var otherId in new[] { d.Id, e.Id })
            {
                Assert.False(timeline.DisplayedNotes.Single(n => n.Note.Id == otherId).HasCollapsedRepliesBelow);
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>sa_implement (Reply-Leaf Restriction & Deletion Tombstone, Task 4): a soft-deleted
    /// middle Note (B, between live A and C) must still render as a tombstone card - not vanish and
    /// orphan-promote C to root - and the solid connector line must still visually bridge A through
    /// the tombstone to C (ConnectsDownToReplyCard is a positional fact, independent of deletion
    /// status).</summary>
    [Fact]
    public async Task RefreshAsync_MiddleNoteDeleted_ShowsTombstoneAndKeepsChainVisuallyConnected()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir);
            var a = MakeNote("A", new DateTime(2026, 8, 1));
            await noteRepository.CreateAsync(a);
            var b = MakeNote("B", new DateTime(2026, 8, 2)) with { ParentNoteId = a.Id };
            await noteRepository.CreateAsync(b);
            var c = MakeNote("C", new DateTime(2026, 8, 3)) with { ParentNoteId = b.Id };
            await noteRepository.CreateAsync(c);
            await noteRepository.SoftDeleteAsync(b.Id);

            await timeline.RefreshAsync();

            Assert.Equal(new[] { a.Id, b.Id, c.Id }, timeline.DisplayedNotes.Select(n => n.Note.Id));

            var aItem = timeline.DisplayedNotes.Single(n => n.Note.Id == a.Id);
            var bItem = timeline.DisplayedNotes.Single(n => n.Note.Id == b.Id);
            var cItem = timeline.DisplayedNotes.Single(n => n.Note.Id == c.Id);

            Assert.False(aItem.IsTombstone);
            Assert.True(bItem.IsTombstone);
            Assert.False(cItem.IsTombstone);

            Assert.True(aItem.ConnectsDownToReplyCard, "The connector line must still bridge A down to the tombstone.");
            Assert.True(bItem.ConnectsDownToReplyCard, "The connector line must still bridge the tombstone down to C.");

            // A already has a (tombstoned) reply occupying its position, so it must stay non-repliable;
            // the tombstone itself must never be repliable/quotable; C is the actual leaf.
            Assert.False(aItem.ReplyCommand.CanExecute(null));
            Assert.False(bItem.ReplyCommand.CanExecute(null));
            Assert.False(bItem.QuoteCommand.CanExecute(null));
            Assert.True(cItem.ReplyCommand.CanExecute(null));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>Regression guard for the cascading-removal + reply-restoration requirement together:
    /// deleting the actual tail of a chain must remove its tombstone entirely (nothing survives below
    /// it) and make its former parent repliable again (spec: deleting the replied-to Note makes its parent repliable again).</summary>
    [Fact]
    public async Task RefreshAsync_TrailingNoteDeleted_CascadesAwayEntirely_AndParentBecomesRepliableAgain()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir);
            var a = MakeNote("A", new DateTime(2026, 8, 1));
            await noteRepository.CreateAsync(a);
            var b = MakeNote("B", new DateTime(2026, 8, 2)) with { ParentNoteId = a.Id };
            await noteRepository.CreateAsync(b);
            await noteRepository.SoftDeleteAsync(b.Id);

            await timeline.RefreshAsync();

            Assert.Equal(new[] { a.Id }, timeline.DisplayedNotes.Select(n => n.Note.Id));

            var aItem = timeline.DisplayedNotes.Single(n => n.Note.Id == a.Id);
            Assert.False(aItem.IsTombstone);
            Assert.False(aItem.ConnectsDownToReplyCard);
            Assert.True(aItem.ReplyCommand.CanExecute(null), "Deleting A's only reply must make A repliable again.");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>A soft-deleted root Note with a surviving child must itself render as a tombstone
    /// "thread" (not silently disappear) so the child's continuity is preserved.</summary>
    [Fact]
    public async Task RefreshAsync_DeletedRootWithSurvivingChild_ShowsRootAsTombstone()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir);
            var root = MakeNote("root", new DateTime(2026, 8, 1));
            await noteRepository.CreateAsync(root);
            var child = MakeNote("child", new DateTime(2026, 8, 2)) with { ParentNoteId = root.Id };
            await noteRepository.CreateAsync(child);
            await noteRepository.SoftDeleteAsync(root.Id);

            await timeline.RefreshAsync();

            Assert.Equal(new[] { root.Id, child.Id }, timeline.DisplayedNotes.Select(n => n.Note.Id));
            Assert.True(timeline.DisplayedNotes.Single(n => n.Note.Id == root.Id).IsTombstone);
            Assert.False(timeline.DisplayedNotes.Single(n => n.Note.Id == child.Id).IsTombstone);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>A soft-deleted root whose only child is also soft-deleted preserves no continuity for
    /// anything - the whole "thread" must be omitted entirely rather than showing an isolated
    /// tombstone with nothing under it.</summary>
    [Fact]
    public async Task RefreshAsync_DeletedRootWithNoSurvivingDescendants_OmitsTheWholeThread()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir);
            var root = MakeNote("root", new DateTime(2026, 8, 1));
            await noteRepository.CreateAsync(root);
            var child = MakeNote("child", new DateTime(2026, 8, 2)) with { ParentNoteId = root.Id };
            await noteRepository.CreateAsync(child);
            await noteRepository.SoftDeleteAsync(root.Id);
            await noteRepository.SoftDeleteAsync(child.Id);

            var unrelated = MakeNote("unrelated live post", new DateTime(2026, 8, 3));
            await noteRepository.CreateAsync(unrelated);

            await timeline.RefreshAsync();

            Assert.Equal(new[] { unrelated.Id }, timeline.DisplayedNotes.Select(n => n.Note.Id));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>Sanity check for the ordinary (no-deletion) case: only the chain's actual leaf is
    /// repliable, matching the confirmed "branching must be disabled...only the leaf article may be replied to" intent.</summary>
    [Fact]
    public async Task RefreshAsync_LiveChainWithNoDeletions_OnlyTheLeafIsRepliable()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir);
            var a = MakeNote("A", new DateTime(2026, 8, 1));
            await noteRepository.CreateAsync(a);
            var b = MakeNote("B", new DateTime(2026, 8, 2)) with { ParentNoteId = a.Id };
            await noteRepository.CreateAsync(b);

            await timeline.RefreshAsync();

            Assert.False(timeline.DisplayedNotes.Single(n => n.Note.Id == a.Id).ReplyCommand.CanExecute(null));
            Assert.True(timeline.DisplayedNotes.Single(n => n.Note.Id == b.Id).ReplyCommand.CanExecute(null));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>Companion to the above: with a custom TailVisibleCount(3), a 4-post chain exceeding
    /// ThreadCollapseThreshold(1) alone still has its root+tail window (1+3=4) already cover every
    /// post, so nothing would actually be hidden - BuildThreadedDisplayOrder's second guard
    /// (hiddenCount &gt; 0) must skip collapsing here rather than showing a collapse indicator with a
    /// hidden count of 0.</summary>
    [Fact]
    public async Task RefreshAsync_ChainExactlyCoveredByRootPlusTailWindow_DoesNotCollapse()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var notesSettingsManager = new FakeNotesSettingsManager();
            notesSettingsManager.SetThreadCollapseThreshold(1);
            notesSettingsManager.SetTailVisibleCount(3);
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir, notesSettingsManager: notesSettingsManager);
            var a = MakeNote("A", new DateTime(2026, 8, 1));
            await noteRepository.CreateAsync(a);
            var b = MakeNote("B", new DateTime(2026, 8, 2)) with { ParentNoteId = a.Id };
            await noteRepository.CreateAsync(b);
            var c = MakeNote("C", new DateTime(2026, 8, 3)) with { ParentNoteId = b.Id };
            await noteRepository.CreateAsync(c);
            var d = MakeNote("D", new DateTime(2026, 8, 4)) with { ParentNoteId = c.Id };
            await noteRepository.CreateAsync(d);

            await timeline.RefreshAsync();

            Assert.Equal(new[] { a.Id, b.Id, c.Id, d.Id }, timeline.DisplayedNotes.Select(n => n.Note.Id));
            Assert.All(timeline.DisplayedNotes, item => Assert.False(item.HasCollapsedRepliesBelow));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>Custom (lower) ThreadCollapseThreshold/TailVisibleCount settings must drive the
    /// collapsing decision, not just the production defaults exercised above.</summary>
    [Fact]
    public async Task RefreshAsync_WithCustomThreadCollapseSettings_CollapsesAtConfiguredThreshold()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var notesSettingsManager = new FakeNotesSettingsManager();
            notesSettingsManager.SetThreadCollapseThreshold(1);
            notesSettingsManager.SetTailVisibleCount(1);
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir, notesSettingsManager: notesSettingsManager);
            var a = MakeNote("A", new DateTime(2026, 8, 1));
            await noteRepository.CreateAsync(a);
            var b = MakeNote("B", new DateTime(2026, 8, 2)) with { ParentNoteId = a.Id };
            await noteRepository.CreateAsync(b);
            var c = MakeNote("C", new DateTime(2026, 8, 3)) with { ParentNoteId = b.Id };
            await noteRepository.CreateAsync(c);

            await timeline.RefreshAsync();

            // threshold=1 (3 > 1) and root+tail=1+1=2 (3 > 2): collapses to root[A] + tail[C], B hidden.
            Assert.Equal(new[] { a.Id, c.Id }, timeline.DisplayedNotes.Select(n => n.Note.Id));
            var aItem = timeline.DisplayedNotes.Single(n => n.Note.Id == a.Id);
            Assert.True(aItem.HasCollapsedRepliesBelow);
            Assert.Equal(1, aItem.CollapsedReplyCount);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>Fix request #2: the detail thread's ancestor/reply lists never show the connector line
    /// (user requirement: no line between cards on the individual page), even though the ancestor
    /// chain there is inherently sequential.</summary>
    [Fact]
    public async Task LoadDetailThread_NeverFlagsAncestorOrReplyCardsAsConnected()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir);
            var root = MakeNote("root", new DateTime(2026, 8, 1));
            await noteRepository.CreateAsync(root);
            var middle = MakeNote("middle", new DateTime(2026, 8, 2)) with { ParentNoteId = root.Id };
            await noteRepository.CreateAsync(middle);
            var leaf = MakeNote("leaf", new DateTime(2026, 8, 3)) with { ParentNoteId = middle.Id };
            await noteRepository.CreateAsync(leaf);
            await timeline.RefreshAsync();

            timeline.DisplayedNotes.Single(n => n.Note.Id == leaf.Id).OpenDetailCommand.Execute(null);
            await timeline.DetailThreadLoadTask!;

            Assert.All(timeline.DetailAncestorChain, i => Assert.False(i.ConnectsDownToReplyCard));
            Assert.All(timeline.DetailReplies, i => Assert.False(i.ConnectsDownToReplyCard));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>sa_implement Task 5: the individual detail page must remain the "escape hatch" for
    /// a collapsed thread (confirmed design decision - no re-expand affordance in the timeline
    /// itself), so it must never apply thread collapsing to its own ancestor/reply lists even under
    /// an aggressive ThreadCollapseThreshold/TailVisibleCount that would collapse the same chain in
    /// the main timeline.</summary>
    [Fact]
    public async Task LoadDetailThread_NeverCollapsesAncestorOrReplyChain_EvenUnderAggressiveThreadSettings()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var notesSettingsManager = new FakeNotesSettingsManager();
            notesSettingsManager.SetThreadCollapseThreshold(1);
            notesSettingsManager.SetTailVisibleCount(1);
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir, notesSettingsManager: notesSettingsManager);
            var root = MakeNote("root", new DateTime(2026, 8, 1));
            await noteRepository.CreateAsync(root);
            var middle = MakeNote("middle", new DateTime(2026, 8, 2)) with { ParentNoteId = root.Id };
            await noteRepository.CreateAsync(middle);
            var leaf = MakeNote("leaf", new DateTime(2026, 8, 3)) with { ParentNoteId = middle.Id };
            await noteRepository.CreateAsync(leaf);
            await timeline.RefreshAsync();

            timeline.DisplayedNotes.Single(n => n.Note.Id == leaf.Id).OpenDetailCommand.Execute(null);
            await timeline.DetailThreadLoadTask!;

            Assert.Equal(new[] { root.Id, middle.Id }, timeline.DetailAncestorChain.Select(i => i.Note.Id));
            Assert.All(timeline.DetailAncestorChain, i => Assert.False(i.HasCollapsedRepliesBelow));
            Assert.All(timeline.DetailReplies, i => Assert.False(i.HasCollapsedRepliesBelow));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task OpenDetailCommand_SetsSelectedDetailItemToTheClickedCard()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir);
            var note = MakeNote("detail target", DateTime.Now);
            await noteRepository.CreateAsync(note);
            await timeline.RefreshAsync();

            Assert.Null(timeline.SelectedDetailItem);
            timeline.DisplayedNotes[0].OpenDetailCommand.Execute(null);

            Assert.NotNull(timeline.SelectedDetailItem);
            Assert.Equal(note.Id, timeline.SelectedDetailItem!.Note.Id);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task BackToListCommand_ClearsSelectedDetailItem()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir);
            await noteRepository.CreateAsync(MakeNote("detail target", DateTime.Now));
            await timeline.RefreshAsync();

            timeline.DisplayedNotes[0].OpenDetailCommand.Execute(null);
            Assert.NotNull(timeline.SelectedDetailItem);

            timeline.BackToListCommand.Execute(null);

            Assert.Null(timeline.SelectedDetailItem);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task OpenDetailCommand_PopulatesDetailAncestorChain_RootFirst_ForAThreeLevelReplyChain()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir);
            var root = MakeNote("root post", new DateTime(2026, 8, 1));
            await noteRepository.CreateAsync(root);
            var middle = MakeNote("middle reply", new DateTime(2026, 8, 2)) with { ParentNoteId = root.Id };
            await noteRepository.CreateAsync(middle);
            var leaf = MakeNote("leaf reply", new DateTime(2026, 8, 3)) with { ParentNoteId = middle.Id };
            await noteRepository.CreateAsync(leaf);
            await timeline.RefreshAsync();

            var leafItem = timeline.DisplayedNotes.Single(n => n.Note.Id == leaf.Id);
            leafItem.OpenDetailCommand.Execute(null);
            await timeline.DetailThreadLoadTask!;

            Assert.Equal(new[] { root.Id, middle.Id }, timeline.DetailAncestorChain.Select(i => i.Note.Id));
            Assert.Empty(timeline.DetailReplies);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task OpenDetailCommand_PopulatesDetailReplies_OldestFirst()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir);
            var parent = MakeNote("parent post", DateTime.Now);
            await noteRepository.CreateAsync(parent);
            var earlierReply = MakeNote("earlier reply", new DateTime(2026, 8, 1)) with { ParentNoteId = parent.Id };
            var laterReply = MakeNote("later reply", new DateTime(2026, 8, 10)) with { ParentNoteId = parent.Id };
            await noteRepository.CreateAsync(earlierReply);
            await noteRepository.CreateAsync(laterReply);
            await timeline.RefreshAsync();

            var parentItem = timeline.DisplayedNotes.Single(n => n.Note.Id == parent.Id);
            parentItem.OpenDetailCommand.Execute(null);
            await timeline.DetailThreadLoadTask!;

            Assert.Equal(new[] { earlierReply.Id, laterReply.Id }, timeline.DetailReplies.Select(i => i.Note.Id));
            Assert.Empty(timeline.DetailAncestorChain);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>sa_minimal_fix (bug-list item #3a/#3b): NoteRepository.GetRepliesAsync only returns
    /// the direct (one level) replies to a Note, so opening the root's detail page used to show only
    /// "middle" and stop there - "leaf" was entirely invisible from the individual page even though
    /// it's part of the same connected thread. The whole descendant chain must now be reachable from
    /// a single detail page.</summary>
    [Fact]
    public async Task OpenDetailCommand_PopulatesDetailReplies_WithEntireNestedReplyChain_NotJustDirectChildren()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir);
            var root = MakeNote("root post", new DateTime(2026, 8, 1));
            await noteRepository.CreateAsync(root);
            var middle = MakeNote("middle reply", new DateTime(2026, 8, 2)) with { ParentNoteId = root.Id };
            await noteRepository.CreateAsync(middle);
            var leaf = MakeNote("leaf reply", new DateTime(2026, 8, 3)) with { ParentNoteId = middle.Id };
            await noteRepository.CreateAsync(leaf);
            await timeline.RefreshAsync();

            var rootItem = timeline.DisplayedNotes.Single(n => n.Note.Id == root.Id);
            rootItem.OpenDetailCommand.Execute(null);
            await timeline.DetailThreadLoadTask!;

            Assert.Equal(new[] { middle.Id, leaf.Id }, timeline.DetailReplies.Select(i => i.Note.Id));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>sa_implement (Reply-Leaf Restriction & Deletion Tombstone, Task 5): a soft-deleted
    /// ancestor used to leak its real Body unfiltered into DetailAncestorChain (GetAncestorChainAsync
    /// never filtered IsDeleted) - it must now render as a tombstone (IsTombstone=true) instead, and
    /// - like every ancestor - never be repliable (it always has a child: the next ancestor, or the
    /// focal Note itself).</summary>
    [Fact]
    public async Task OpenDetailCommand_DeletedAncestor_RendersAsTombstone_NeverRepliable()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir);
            var root = MakeNote("root post", new DateTime(2026, 8, 1));
            await noteRepository.CreateAsync(root);
            await noteRepository.SoftDeleteAsync(root.Id);
            var leaf = MakeNote("leaf reply", new DateTime(2026, 8, 2)) with { ParentNoteId = root.Id };
            await noteRepository.CreateAsync(leaf);
            await timeline.RefreshAsync();

            timeline.DisplayedNotes.Single(n => n.Note.Id == leaf.Id).OpenDetailCommand.Execute(null);
            await timeline.DetailThreadLoadTask!;

            var rootAncestorItem = timeline.DetailAncestorChain.Single(i => i.Note.Id == root.Id);
            Assert.True(rootAncestorItem.IsTombstone);
            Assert.False(rootAncestorItem.ReplyCommand.CanExecute(null));
            Assert.False(rootAncestorItem.QuoteCommand.CanExecute(null));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>A soft-deleted reply in the middle of the detail page's own reply chain must still
    /// render (as a tombstone) so the deeper reply below it stays reachable, mirroring the main
    /// timeline's equivalent behavior (Task 4) but via GetRepliesIncludingDeletedAsync instead of
    /// GetRepliesAsync.</summary>
    [Fact]
    public async Task OpenDetailCommand_MiddleReplyDeleted_ShowsTombstoneAndKeepsDeeperReplyReachable()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir);
            var focal = MakeNote("focal", new DateTime(2026, 8, 1));
            await noteRepository.CreateAsync(focal);
            var middle = MakeNote("middle", new DateTime(2026, 8, 2)) with { ParentNoteId = focal.Id };
            await noteRepository.CreateAsync(middle);
            var deep = MakeNote("deep", new DateTime(2026, 8, 3)) with { ParentNoteId = middle.Id };
            await noteRepository.CreateAsync(deep);
            await noteRepository.SoftDeleteAsync(middle.Id);
            await timeline.RefreshAsync();

            timeline.DisplayedNotes.Single(n => n.Note.Id == focal.Id).OpenDetailCommand.Execute(null);
            await timeline.DetailThreadLoadTask!;

            Assert.Equal(new[] { middle.Id, deep.Id }, timeline.DetailReplies.Select(i => i.Note.Id));
            var middleItem = timeline.DetailReplies.Single(i => i.Note.Id == middle.Id);
            var deepItem = timeline.DetailReplies.Single(i => i.Note.Id == deep.Id);
            Assert.True(middleItem.IsTombstone);
            Assert.False(middleItem.ReplyCommand.CanExecute(null));
            Assert.False(deepItem.IsTombstone);
            Assert.True(deepItem.ReplyCommand.CanExecute(null));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>Regression guard for the cascading-removal requirement applied to the detail page's own
    /// reply chain: a trailing deleted reply with nothing below it must vanish from DetailReplies
    /// entirely rather than showing an orphaned tombstone.</summary>
    [Fact]
    public async Task OpenDetailCommand_TrailingReplyDeleted_CascadesAwayFromDetailReplies()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir);
            var focal = MakeNote("focal", new DateTime(2026, 8, 1));
            await noteRepository.CreateAsync(focal);
            var reply = MakeNote("reply", new DateTime(2026, 8, 2)) with { ParentNoteId = focal.Id };
            await noteRepository.CreateAsync(reply);
            await noteRepository.SoftDeleteAsync(reply.Id);
            await timeline.RefreshAsync();

            timeline.DisplayedNotes.Single(n => n.Note.Id == focal.Id).OpenDetailCommand.Execute(null);
            await timeline.DetailThreadLoadTask!;

            Assert.Empty(timeline.DetailReplies);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>sa_minimal_fix (bug-list item #3c): SelectedDetailItem used to be assigned the exact
    /// same NoteTimelineItemViewModel instance rendered in the main list, so a card's
    /// ConnectsDownToReplyCard flag (true there because its reply sits directly below it) leaked into
    /// the detail page too, drawing a stray connector line under the focal card.</summary>
    [Fact]
    public async Task OpenDetailCommand_NeverCarriesOverConnectsDownToReplyCardFromTheMainListCard()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir);
            var parent = MakeNote("parent post", DateTime.Now);
            await noteRepository.CreateAsync(parent);
            var reply = MakeNote("a reply", DateTime.Now) with { ParentNoteId = parent.Id };
            await noteRepository.CreateAsync(reply);
            await timeline.RefreshAsync();

            var parentItem = timeline.DisplayedNotes.Single(n => n.Note.Id == parent.Id);
            Assert.True(parentItem.ConnectsDownToReplyCard);

            parentItem.OpenDetailCommand.Execute(null);

            Assert.NotNull(timeline.SelectedDetailItem);
            Assert.False(timeline.SelectedDetailItem!.ConnectsDownToReplyCard, "SelectedDetailItem must not carry over ConnectsDownToReplyCard from the main list's own card instance.");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>sa_minimal_fix (bug-list item #3c): same leak as above, but for the collapsed-thread
    /// dotted indicator - opening the detail page of the head card of a collapsed thread used to
    /// still show the collapse indicator there too, even though the detail page always renders a
    /// thread in full (no collapsing).</summary>
    [Fact]
    public async Task OpenDetailCommand_NeverCarriesOverHasCollapsedRepliesBelowFromTheMainListCard()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir, notesSettingsManager: new FakeNotesSettingsManager());
            var a = MakeNote("A", new DateTime(2026, 8, 1));
            await noteRepository.CreateAsync(a);
            var b = MakeNote("B", new DateTime(2026, 8, 2)) with { ParentNoteId = a.Id };
            await noteRepository.CreateAsync(b);
            var c = MakeNote("C", new DateTime(2026, 8, 3)) with { ParentNoteId = b.Id };
            await noteRepository.CreateAsync(c);
            var d = MakeNote("D", new DateTime(2026, 8, 4)) with { ParentNoteId = c.Id };
            await noteRepository.CreateAsync(d);
            var e = MakeNote("E", new DateTime(2026, 8, 5)) with { ParentNoteId = d.Id };
            await noteRepository.CreateAsync(e);
            await timeline.RefreshAsync();

            var aItem = timeline.DisplayedNotes.Single(n => n.Note.Id == a.Id);
            Assert.True(aItem.HasCollapsedRepliesBelow);

            aItem.OpenDetailCommand.Execute(null);

            Assert.NotNull(timeline.SelectedDetailItem);
            Assert.False(timeline.SelectedDetailItem!.HasCollapsedRepliesBelow, "SelectedDetailItem must not carry over HasCollapsedRepliesBelow from the main list's own card instance.");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task BackToListCommand_ClearsDetailAncestorChainAndReplies()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir);
            var parent = MakeNote("parent post", DateTime.Now);
            await noteRepository.CreateAsync(parent);
            var reply = MakeNote("a reply", DateTime.Now) with { ParentNoteId = parent.Id };
            await noteRepository.CreateAsync(reply);
            await timeline.RefreshAsync();

            timeline.DisplayedNotes.Single(n => n.Note.Id == parent.Id).OpenDetailCommand.Execute(null);
            await timeline.DetailThreadLoadTask!;
            Assert.NotEmpty(timeline.DetailReplies);

            timeline.BackToListCommand.Execute(null);

            Assert.Empty(timeline.DetailAncestorChain);
            Assert.Empty(timeline.DetailReplies);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task ViewChartCommand_ForNoteWithTickerAndExplicitAnchor_BroadcastsNoteChartJumpRequestedMessage()
    {
        var tempDir = CreateIsolatedTempDirectory();
        var probe = new object();
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir);
            var anchorDate = new DateTime(2026, 8, 1);
            var note = new Note(Guid.NewGuid(), "body", DateTime.Now, DateTime.Now)
            {
                RelatedTicker = "7203.T",
                ChartAnchorDate = anchorDate,
                ChartTimeframe = TimeFrame.W1,
            };
            await noteRepository.CreateAsync(note);
            await timeline.RefreshAsync();

            NoteChartJumpRequestedMessage? received = null;
            WeakReferenceMessenger.Default.Register<NoteChartJumpRequestedMessage>(probe, (r, m) => received = m);

            timeline.DisplayedNotes[0].ViewChartCommand.Execute(null);

            Assert.NotNull(received);
            Assert.Equal("7203.T", received!.Ticker);
            Assert.Equal(anchorDate, received.AnchorDate);
            Assert.Equal(TimeFrame.W1, received.Timeframe);
        }
        finally
        {
            WeakReferenceMessenger.Default.Unregister<NoteChartJumpRequestedMessage>(probe);
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task ViewChartCommand_WithoutExplicitAnchorOrTimeframe_DefaultsToCreatedAtDateAndDaily()
    {
        var tempDir = CreateIsolatedTempDirectory();
        var probe = new object();
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir);
            var createdAt = new DateTime(2026, 8, 12, 16, 32, 0);
            var note = new Note(Guid.NewGuid(), "body", createdAt, createdAt) { RelatedTicker = "9984.T" };
            await noteRepository.CreateAsync(note);
            await timeline.RefreshAsync();

            NoteChartJumpRequestedMessage? received = null;
            WeakReferenceMessenger.Default.Register<NoteChartJumpRequestedMessage>(probe, (r, m) => received = m);

            timeline.DisplayedNotes[0].ViewChartCommand.Execute(null);

            Assert.NotNull(received);
            Assert.Equal(createdAt.Date, received!.AnchorDate); // spec section 3: falls back to CreatedAt's date
            Assert.Equal(TimeFrame.D1, received.Timeframe);
        }
        finally
        {
            WeakReferenceMessenger.Default.Unregister<NoteChartJumpRequestedMessage>(probe);
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task ViewChartCommand_ForNoteWithoutRelatedTicker_CanExecuteIsFalse()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir);
            await noteRepository.CreateAsync(MakeNote("body without a ticker", DateTime.Now));
            await timeline.RefreshAsync();

            Assert.False(timeline.DisplayedNotes[0].ViewChartCommand.CanExecute(null));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task BeginCreateThenSaveEdit_AddsNewNoteToTimeline()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, _, _) = await CreateTimelineAsync(tempDir);
            await timeline.RefreshAsync();
            Assert.Empty(timeline.DisplayedNotes);

            timeline.BeginCreateCommand.Execute(null);
            Assert.NotNull(timeline.EditingNote);
            timeline.EditingNote!.Body = "brand new note";

            await timeline.SaveEditCommand.ExecuteAsync(null);

            Assert.Null(timeline.EditingNote);
            Assert.Single(timeline.DisplayedNotes);
            Assert.Equal("brand new note", timeline.DisplayedNotes[0].Note.Body);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>Regression test for fix request #1/#4 (3rd round): RefreshAsync's ContinueWith(...,
    /// TaskScheduler.Default) starts RefreshCoreAsync off the UI thread, so mutating DisplayedNotes
    /// (bound to the timeline's ItemsControl) must be routed through IDispatcherService.PostAsync
    /// rather than done directly - a single Note appeared to double-post, and deleting either "copy"
    /// deleted the one real underlying Note, because Avalonia's CollectionChanged handling doesn't
    /// reliably reflect off-thread mutations. This asserts the dispatcher is actually used (not just
    /// that the end state happens to be correct, which the synchronous SynchronousDispatcherService
    /// would satisfy even without routing through it).</summary>
    [Fact]
    public async Task RefreshAsync_RoutesDisplayedNotesRebuildThroughDispatcher()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var dispatcherService = new SynchronousDispatcherService();
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir, dispatcherService: dispatcherService);
            await noteRepository.CreateAsync(MakeNote("dispatcher regression check", DateTime.Now));

            var callCountBefore = dispatcherService.PostAsyncCallCount;
            await timeline.RefreshAsync();

            Assert.True(dispatcherService.PostAsyncCallCount > callCountBefore);
            Assert.Single(timeline.DisplayedNotes);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task SaveEditCommand_WhenExecutedTwiceConcurrently_AddsOnlyOneNoteToTimeline()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, _, _) = await CreateTimelineAsync(tempDir);
            await timeline.RefreshAsync();
            timeline.BeginCreateCommand.Execute(null);
            timeline.EditingNote!.Body = "double-click regression check";

            // Simulates a rapid double-click on the compose panel's Save button.
            var firstCall = timeline.SaveEditCommand.ExecuteAsync(null);
            var secondCall = timeline.SaveEditCommand.ExecuteAsync(null);
            await Task.WhenAll(firstCall, secondCall);
            await timeline.RefreshAsync();

            Assert.Single(timeline.DisplayedNotes);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task SaveEditCommand_WithBlankBody_KeepsEditorOpenAndDoesNotAddNote()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, _, _) = await CreateTimelineAsync(tempDir);
            timeline.BeginCreateCommand.Execute(null);
            timeline.EditingNote!.Body = "   ";

            await timeline.SaveEditCommand.ExecuteAsync(null);

            Assert.NotNull(timeline.EditingNote);
            Assert.Empty(timeline.DisplayedNotes);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task OpenTrashCommand_ShowsTrashDialogThenRefreshesTimeline()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, noteRepository, dialogService) = await CreateTimelineAsync(tempDir);
            await timeline.RefreshAsync();
            Assert.Empty(timeline.DisplayedNotes);

            // Simulate a Note being created (e.g. restored from the trash dialog) while the trash
            // dialog is open - OpenTrashCommand must refresh the timeline after the dialog closes
            // to pick this up, since the timeline has no other way to learn about it.
            await noteRepository.CreateAsync(new Note(Guid.NewGuid(), "restored", DateTime.Now, DateTime.Now));

            await timeline.OpenTrashCommand.ExecuteAsync(null);

            Assert.Equal(1, dialogService.ShowNoteTrashDialogAsyncCallCount);
            Assert.Single(timeline.DisplayedNotes);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task LoadAvailableFilterOptionsAsync_PopulatesRegisteredTags()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var watchlist = new WatchlistProfile(Guid.NewGuid(), "Semiconductor", IndicatorColor.FromRgb(0, 0, 255), isPortfolio: false,
                new[] { new WatchlistItem("7203.T", DateTimeOffset.UtcNow) });
            var portfolio = new WatchlistProfile(Guid.NewGuid(), "My Portfolio", IndicatorColor.FromRgb(255, 0, 0), isPortfolio: true,
                new[] { new WatchlistItem("9984.T", DateTimeOffset.UtcNow) });
            var watchlistManager = new FakeWatchlistManager(new[] { watchlist, portfolio });
            var marketDataProvider = new FakeMarketDataProvider(
                availableTickers: new[] { "7203.T", "9984.T" },
                tickerToTag: new Dictionary<string, string> { ["7203.T"] = "Automotive, Earnings" });

            var (timeline, _, _) = await CreateTimelineAsync(tempDir, watchlistManager, marketDataProvider);
            await timeline.LoadAvailableFilterOptionsAsync();

            Assert.Equal(new[] { "Automotive", "Earnings" }, timeline.AvailableRegisteredTags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>sa_implement Task 1 (unified Ticker/Watchlist/Portfolio/Filter/Tag search
    /// suggestions, sa_analysis_report.md §10): after loading, AvailableSearchSuggestions must
    /// contain one entry per Ticker, per Watchlist/Portfolio/Filter node and per registered tag - all
    /// five kinds represented, each carrying the right DisplayName.</summary>
    [Fact]
    public async Task LoadAvailableFilterOptionsAsync_PopulatesAvailableSearchSuggestions_WithAllFiveKinds()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var watchlist = new WatchlistProfile(Guid.NewGuid(), "Semiconductor", IndicatorColor.FromRgb(0, 0, 255), isPortfolio: false,
                new[] { new WatchlistItem("7203.T", DateTimeOffset.UtcNow) });
            var portfolio = new WatchlistProfile(Guid.NewGuid(), "My Portfolio", IndicatorColor.FromRgb(255, 0, 0), isPortfolio: true,
                new[] { new WatchlistItem("9984.T", DateTimeOffset.UtcNow) });
            var watchlistManager = new FakeWatchlistManager(new[] { watchlist, portfolio });
            var marketDataProvider = new FakeMarketDataProvider(
                availableTickers: new[] { "7203.T", "9984.T" },
                tickerToTag: new Dictionary<string, string> { ["7203.T"] = "Earnings" });

            var tickerStateStore = FakeTickerStateStore.FromProfiles(watchlistManager.GetAllProfiles());
            tickerStateStore.Groups.Add(new FilterNode(new StockAnalyzer.Core.Models.Settings.FilterSettings { Name = "Surging" }));

            var (timeline, _, _) = await CreateTimelineAsync(tempDir, watchlistManager, marketDataProvider, tickerStateStore: tickerStateStore);
            await timeline.LoadAvailableFilterOptionsAsync();

            var kinds = timeline.AvailableSearchSuggestions.Select(s => s.Kind).ToHashSet();
            Assert.Equal(
                new HashSet<NoteScopeSuggestionKind>
                {
                    NoteScopeSuggestionKind.Ticker,
                    NoteScopeSuggestionKind.Watchlist,
                    NoteScopeSuggestionKind.Portfolio,
                    NoteScopeSuggestionKind.Filter,
                    NoteScopeSuggestionKind.Tag,
                },
                kinds);

            Assert.Contains(timeline.AvailableSearchSuggestions, s => s.Kind == NoteScopeSuggestionKind.Ticker && s.DisplayName == "7203.T");
            Assert.Contains(timeline.AvailableSearchSuggestions, s => s.Kind == NoteScopeSuggestionKind.Watchlist && s.DisplayName == "Semiconductor");
            Assert.Contains(timeline.AvailableSearchSuggestions, s => s.Kind == NoteScopeSuggestionKind.Portfolio && s.DisplayName == "My Portfolio");
            Assert.Contains(timeline.AvailableSearchSuggestions, s => s.Kind == NoteScopeSuggestionKind.Filter && s.DisplayName == "Surging");
            Assert.Contains(timeline.AvailableSearchSuggestions, s => s.Kind == NoteScopeSuggestionKind.Tag && s.DisplayName == "Earnings");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>sa_minimal_fix (5th round) fix request #1: Watchlist/Portfolio/Filter names are not
    /// required to be unique, so two Watchlists both named "List" must appear as a single suggestion
    /// entry, not two independent ones.</summary>
    [Fact]
    public async Task AvailableSearchSuggestions_TwoWatchlistsWithSameName_MergeIntoOneSuggestion()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var listA = new WatchlistProfile(Guid.NewGuid(), "List", IndicatorColor.FromRgb(0, 0, 255), isPortfolio: false,
                new[] { new WatchlistItem("7203.T", DateTimeOffset.UtcNow) });
            var listB = new WatchlistProfile(Guid.NewGuid(), "List", IndicatorColor.FromRgb(0, 255, 0), isPortfolio: false,
                new[] { new WatchlistItem("9984.T", DateTimeOffset.UtcNow) });
            var watchlistManager = new FakeWatchlistManager(new[] { listA, listB });

            var (timeline, _, _) = await CreateTimelineAsync(tempDir, watchlistManager);
            await timeline.LoadAvailableFilterOptionsAsync();

            var listSuggestions = timeline.AvailableSearchSuggestions.Where(s => s.Kind == NoteScopeSuggestionKind.Watchlist && s.DisplayName == "List").ToList();
            Assert.Single(listSuggestions);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>sa_minimal_fix (5th round) fix request #1: selecting the merged "List" suggestion
    /// must filter to the union of both same-named Watchlists' tickers, not just whichever profile
    /// happened to back the single suggestion entry.</summary>
    [Fact]
    public async Task SelectSuggestion_WatchlistWithDuplicateName_FiltersToUnionOfBothProfilesTickers()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var listA = new WatchlistProfile(Guid.NewGuid(), "List", IndicatorColor.FromRgb(0, 0, 255), isPortfolio: false,
                new[] { new WatchlistItem("7203.T", DateTimeOffset.UtcNow) });
            var listB = new WatchlistProfile(Guid.NewGuid(), "List", IndicatorColor.FromRgb(0, 255, 0), isPortfolio: false,
                new[] { new WatchlistItem("9984.T", DateTimeOffset.UtcNow) });
            var watchlistManager = new FakeWatchlistManager(new[] { listA, listB });

            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir, watchlistManager);
            await timeline.LoadAvailableFilterOptionsAsync();
            await noteRepository.CreateAsync(MakeNote("in list a", DateTime.Now, "7203.T"));
            await noteRepository.CreateAsync(MakeNote("in list b", DateTime.Now, "9984.T"));
            await noteRepository.CreateAsync(MakeNote("in neither", DateTime.Now, "6758.T"));

            var listSuggestion = timeline.AvailableSearchSuggestions.Single(s => s.Kind == NoteScopeSuggestionKind.Watchlist && s.DisplayName == "List");
            timeline.SelectSuggestion(listSuggestion);
            await timeline.RefreshAsync();

            var bodies = timeline.DisplayedNotes.Select(i => i.Note.Body).ToHashSet();
            Assert.Equal(new HashSet<string> { "in list a", "in list b" }, bodies);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>sa_minimal_fix (5th round) fix request #6: SearchSuggestionBox.Text is OneWay-bound,
    /// so manually deleting the box's typed text back to empty does not by itself notify the
    /// ViewModel - ClearActiveSuggestionFilter is the method the code-behind's TextChanged handler
    /// calls to actually restore "All Tickers" in that case.</summary>
    [Fact]
    public async Task ClearActiveSuggestionFilter_RestoresAllTickers_WithoutTouchingSearchTextOrPeriod()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var watchlist = new WatchlistProfile(Guid.NewGuid(), "Semiconductor", IndicatorColor.FromRgb(0, 0, 255), isPortfolio: false,
                new[] { new WatchlistItem("7203.T", DateTimeOffset.UtcNow) });
            var watchlistManager = new FakeWatchlistManager(new[] { watchlist });

            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir, watchlistManager);
            await noteRepository.CreateAsync(MakeNote("in watchlist", DateTime.Now, "7203.T"));
            await noteRepository.CreateAsync(MakeNote("not in watchlist", DateTime.Now, "9984.T"));
            timeline.SelectedScopeNode = timeline.AvailableScopeNodes.Single(n => n is WatchlistNode);
            await timeline.RefreshAsync();
            Assert.Single(timeline.DisplayedNotes);

            timeline.SearchText = "keep me";

            timeline.ClearActiveSuggestionFilter();
            await timeline.RefreshAsync();

            Assert.IsType<AllTickersNode>(timeline.SelectedScopeNode);
            Assert.Equal(string.Empty, timeline.TickerFilterText);
            Assert.Equal("keep me", timeline.SearchText); // ClearActiveSuggestionFilter must not touch SearchText.
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>Regression test for sa_implement Task 3 (unified single-select scope menu):
    /// AvailableScopeNodes mirrors ITickerStateStore.Groups (Tickers-tab node tree, same order), and
    /// picking a WatchlistNode resolves via GetTickersForNode into ScopeTickers exactly like the old
    /// UpdateSelectedWatchlists multi-select did for a single selection.</summary>
    [Fact]
    public async Task SelectedScopeNode_SetToWatchlistNode_FiltersTimelineToTickersInThatWatchlist()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var watchlist = new WatchlistProfile(Guid.NewGuid(), "Semiconductor", IndicatorColor.FromRgb(0, 0, 255), isPortfolio: false,
                new[] { new WatchlistItem("7203.T", DateTimeOffset.UtcNow) });
            var watchlistManager = new FakeWatchlistManager(new[] { watchlist });

            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir, watchlistManager);
            await noteRepository.CreateAsync(MakeNote("in watchlist", DateTime.Now, "7203.T"));
            await noteRepository.CreateAsync(MakeNote("not in watchlist", DateTime.Now, "9984.T"));
            await timeline.RefreshAsync();
            Assert.Equal(2, timeline.DisplayedNotes.Count);

            var watchlistNode = Assert.IsType<WatchlistNode>(timeline.AvailableScopeNodes.Single(n => n is WatchlistNode));
            timeline.SelectedScopeNode = watchlistNode;
            await timeline.RefreshAsync();

            Assert.Single(timeline.DisplayedNotes);
            Assert.Equal("in watchlist", timeline.DisplayedNotes[0].Note.Body);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>Regression test for sa_implement Task 3: the default selection is "All Tickers"
    /// (fix request #2), which must impose no ticker-scope constraint at all.</summary>
    [Fact]
    public async Task Constructor_DefaultsSelectedScopeNodeToAllTickers_ImposingNoConstraint()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir);
            await noteRepository.CreateAsync(MakeNote("has ticker", DateTime.Now, "7203.T"));
            await noteRepository.CreateAsync(MakeNote("no ticker", DateTime.Now));

            Assert.IsType<AllTickersNode>(timeline.SelectedScopeNode);

            await timeline.RefreshAsync();

            Assert.Equal(2, timeline.DisplayedNotes.Count);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>sa_implement Task 2: picking a Ticker suggestion sets TickerFilterText and clears a
    /// previously-active Tag selection, matching the "only one active dimension at a time" Converse
    /// requirement in sa_analysis_report.md §6.</summary>
    [Fact]
    public async Task SelectSuggestion_Ticker_SetsTickerFilterText_AndClearsPriorTagSelection()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var marketDataProvider = new FakeMarketDataProvider(
                availableTickers: new[] { "7203.T", "9984.T" },
                tickerToTag: new Dictionary<string, string> { ["7203.T"] = "Automotive" });

            var (timeline, _, _) = await CreateTimelineAsync(tempDir, marketDataProvider: marketDataProvider);
            await timeline.LoadAvailableFilterOptionsAsync();
            timeline.SelectSuggestion(NoteScopeSuggestion.ForTag("Automotive"));
            Assert.NotNull(timeline.FilterCriteria.RegisteredTagTickers);

            timeline.SelectSuggestion(NoteScopeSuggestion.ForTicker("9984.T"));

            Assert.Equal("9984.T", timeline.TickerFilterText);
            Assert.IsType<AllTickersNode>(timeline.SelectedScopeNode);
            Assert.Null(timeline.FilterCriteria.RegisteredTagTickers);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>sa_implement Task 2: picking a Watchlist/Portfolio/Filter suggestion sets
    /// SelectedScopeNode and clears a previously-active TickerFilterText.</summary>
    [Fact]
    public async Task SelectSuggestion_WatchlistNode_SetsSelectedScopeNode_AndClearsPriorTickerSelection()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var watchlist = new WatchlistProfile(Guid.NewGuid(), "Semiconductor", IndicatorColor.FromRgb(0, 0, 255), isPortfolio: false,
                new[] { new WatchlistItem("7203.T", DateTimeOffset.UtcNow) });
            var watchlistManager = new FakeWatchlistManager(new[] { watchlist });

            var (timeline, _, _) = await CreateTimelineAsync(tempDir, watchlistManager);
            timeline.TickerFilterText = "9984.T";

            var watchlistNode = timeline.AvailableScopeNodes.Single(n => n is WatchlistNode);
            timeline.SelectSuggestion(NoteScopeSuggestion.ForNode(NoteScopeSuggestionKind.Watchlist, watchlistNode));

            Assert.Same(watchlistNode, timeline.SelectedScopeNode);
            Assert.Equal(string.Empty, timeline.TickerFilterText);
            Assert.Null(timeline.FilterCriteria.RegisteredTagTickers);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>sa_implement Task 2: picking a Tag suggestion filters the timeline to tickers
    /// carrying that tag and resets TickerFilterText/scope selection.</summary>
    [Fact]
    public async Task SelectSuggestion_Tag_FiltersTimelineToTickersCarryingTheTag_AndClearsPriorTickerAndScopeSelections()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var watchlist = new WatchlistProfile(Guid.NewGuid(), "Semiconductor", IndicatorColor.FromRgb(0, 0, 255), isPortfolio: false,
                new[] { new WatchlistItem("7203.T", DateTimeOffset.UtcNow) });
            var watchlistManager = new FakeWatchlistManager(new[] { watchlist });
            var marketDataProvider = new FakeMarketDataProvider(
                availableTickers: new[] { "7203.T", "9984.T" },
                tickerToTag: new Dictionary<string, string> { ["7203.T"] = "Automotive" });

            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir, watchlistManager, marketDataProvider);
            await timeline.LoadAvailableFilterOptionsAsync();
            await noteRepository.CreateAsync(MakeNote("tagged", DateTime.Now, "7203.T"));
            await noteRepository.CreateAsync(MakeNote("untagged", DateTime.Now, "9984.T"));
            timeline.TickerFilterText = "9984.T";
            timeline.SelectedScopeNode = timeline.AvailableScopeNodes.Single(n => n is WatchlistNode);
            await timeline.RefreshAsync();

            timeline.SelectSuggestion(NoteScopeSuggestion.ForTag("Automotive"));
            await timeline.RefreshAsync();

            Assert.Single(timeline.DisplayedNotes);
            Assert.Equal("tagged", timeline.DisplayedNotes[0].Note.Body);
            Assert.Equal(string.Empty, timeline.TickerFilterText);
            Assert.IsType<AllTickersNode>(timeline.SelectedScopeNode);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>sa_implement Task 2 (Note Hashtag Filtering): RefreshAsync must aggregate the
    /// distinct set of Note.Hashtags across every active Note into AvailableHashtags, and
    /// AvailableSearchSuggestions must expose one Hashtag-kind entry per distinct tag - sourced from
    /// the same activeNotes list RefreshCoreAsync already fetches for display, independent of
    /// LoadAvailableFilterOptionsAsync (which only covers Ticker/Watchlist/Portfolio/Filter/registered-Tag).</summary>
    [Fact]
    public async Task RefreshAsync_PopulatesAvailableHashtags_AndSearchSuggestions_FromActiveNotes()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir);
            await noteRepository.CreateAsync(MakeNote("first", DateTime.Now, hashtags: ImmutableArray.Create("Earnings", "AI")));
            await noteRepository.CreateAsync(MakeNote("second", DateTime.Now, hashtags: ImmutableArray.Create("AI")));
            await noteRepository.CreateAsync(MakeNote("no hashtags", DateTime.Now));

            await timeline.RefreshAsync();

            Assert.Equal(new[] { "AI", "Earnings" }, timeline.AvailableHashtags.OrderBy(t => t, StringComparer.Ordinal));
            Assert.Contains(timeline.AvailableSearchSuggestions,
                s => s.Kind == NoteScopeSuggestionKind.Hashtag && s.DisplayName == "#AI" && s.Value == "AI");
            Assert.Contains(timeline.AvailableSearchSuggestions,
                s => s.Kind == NoteScopeSuggestionKind.Hashtag && s.DisplayName == "#Earnings" && s.Value == "Earnings");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>sa_implement Task 2: picking a Hashtag suggestion filters the timeline to Notes
    /// whose Hashtags contain that tag, and resets TickerFilterText/scope selection - the same
    /// single-active-dimension contract the Ticker/Watchlist/Tag cases already prove.</summary>
    [Fact]
    public async Task SelectSuggestion_Hashtag_FiltersTimelineToNotesCarryingTheHashtag_AndClearsPriorTickerAndScopeSelections()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var watchlist = new WatchlistProfile(Guid.NewGuid(), "Semiconductor", IndicatorColor.FromRgb(0, 0, 255), isPortfolio: false,
                new[] { new WatchlistItem("7203.T", DateTimeOffset.UtcNow) });
            var watchlistManager = new FakeWatchlistManager(new[] { watchlist });

            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir, watchlistManager);
            await noteRepository.CreateAsync(MakeNote("tagged", DateTime.Now, "7203.T", hashtags: ImmutableArray.Create("Earnings")));
            await noteRepository.CreateAsync(MakeNote("untagged", DateTime.Now, "9984.T"));
            timeline.TickerFilterText = "9984.T";
            timeline.SelectedScopeNode = timeline.AvailableScopeNodes.Single(n => n is WatchlistNode);
            await timeline.RefreshAsync();

            var hashtagSuggestion = timeline.AvailableSearchSuggestions.Single(s => s.Kind == NoteScopeSuggestionKind.Hashtag && s.Value == "Earnings");
            timeline.SelectSuggestion(hashtagSuggestion);
            await timeline.RefreshAsync();

            Assert.Single(timeline.DisplayedNotes);
            Assert.Equal("tagged", timeline.DisplayedNotes[0].Note.Body);
            Assert.Equal(string.Empty, timeline.TickerFilterText);
            Assert.IsType<AllTickersNode>(timeline.SelectedScopeNode);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>sa_implement Task 2 (Converse/single-dimension requirement, sa_analysis_report.md
    /// §6): selecting a Hashtag must clear a previously-active registered Tag selection, and vice
    /// versa - the two "tag-like" dimensions are still mutually exclusive, not additive.</summary>
    [Fact]
    public async Task SelectSuggestion_HashtagAndRegisteredTag_AreMutuallyExclusive()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var marketDataProvider = new FakeMarketDataProvider(
                availableTickers: new[] { "7203.T" },
                tickerToTag: new Dictionary<string, string> { ["7203.T"] = "Automotive" });

            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir, marketDataProvider: marketDataProvider);
            await timeline.LoadAvailableFilterOptionsAsync();
            await noteRepository.CreateAsync(MakeNote("hashtagged", DateTime.Now, hashtags: ImmutableArray.Create("Earnings")));
            await timeline.RefreshAsync();

            timeline.SelectSuggestion(NoteScopeSuggestion.ForTag("Automotive"));
            Assert.NotNull(timeline.FilterCriteria.RegisteredTagTickers);
            Assert.Null(timeline.FilterCriteria.SelectedHashtag);

            var hashtagSuggestion = timeline.AvailableSearchSuggestions.Single(s => s.Kind == NoteScopeSuggestionKind.Hashtag && s.Value == "Earnings");
            timeline.SelectSuggestion(hashtagSuggestion);

            Assert.Equal("Earnings", timeline.FilterCriteria.SelectedHashtag);
            Assert.Null(timeline.FilterCriteria.RegisteredTagTickers);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>sa_implement Task 2: ClearFiltersCommand must also restore the Hashtag dimension to
    /// inactive, matching the existing Scope/registered-Tag reset behavior.</summary>
    [Fact]
    public async Task ClearFiltersCommand_AlsoResetsHashtagSelection()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir);
            await noteRepository.CreateAsync(MakeNote("tagged", DateTime.Now, hashtags: ImmutableArray.Create("Earnings")));
            await noteRepository.CreateAsync(MakeNote("untagged", DateTime.Now));
            await timeline.RefreshAsync();

            var hashtagSuggestion = timeline.AvailableSearchSuggestions.Single(s => s.Kind == NoteScopeSuggestionKind.Hashtag && s.Value == "Earnings");
            timeline.SelectSuggestion(hashtagSuggestion);
            await timeline.RefreshAsync();
            Assert.Single(timeline.DisplayedNotes);

            timeline.ClearFiltersCommand.Execute(null);
            await timeline.RefreshAsync();

            Assert.Null(timeline.FilterCriteria.SelectedHashtag);
            Assert.Equal(2, timeline.DisplayedNotes.Count);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task ClearFiltersCommand_ResetsScopeSelectionAndTagSelections()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var watchlist = new WatchlistProfile(Guid.NewGuid(), "Semiconductor", IndicatorColor.FromRgb(0, 0, 255), isPortfolio: false,
                new[] { new WatchlistItem("7203.T", DateTimeOffset.UtcNow) });
            var watchlistManager = new FakeWatchlistManager(new[] { watchlist });

            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir, watchlistManager);
            await noteRepository.CreateAsync(MakeNote("in watchlist", DateTime.Now, "7203.T"));
            await noteRepository.CreateAsync(MakeNote("not in watchlist", DateTime.Now, "9984.T"));
            timeline.SelectedScopeNode = timeline.AvailableScopeNodes.Single(n => n is WatchlistNode);
            await timeline.RefreshAsync();
            Assert.Single(timeline.DisplayedNotes);

            timeline.ClearFiltersCommand.Execute(null);
            await timeline.RefreshAsync();

            Assert.IsType<AllTickersNode>(timeline.SelectedScopeNode);
            Assert.Equal(2, timeline.DisplayedNotes.Count);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>sa_implement (Note body hashtag click -&gt; Filter/Search selection popup) Task 1:
    /// BodySegments must mark a "#tag" occurrence clickable only when it's actually present in
    /// Note.Hashtags, and must leave a plain "#word" that never made it into Hashtags (e.g. it was
    /// truncated by the collapsed-preview cap, or lost the 30-tag extraction cap at save time) as
    /// non-clickable plain text.</summary>
    [Fact]
    public void BodySegments_MarksOnlyRealHashtagsClickable()
    {
        var note = MakeNote("見て #AI 良い #未保存", DateTime.Now) with { Hashtags = ImmutableArray.Create("ai") };
        var item = new NoteTimelineItemViewModel(note, false, NoOpAsyncCallback, NoOpAsyncCallback, NoOpItemCallback, NoOpUrlCallback, NoOpItemCallback, NoOpItemCallback, NoOpUrlCallback, NoOpUrlCallback);

        var segments = item.BodySegments;

        Assert.Contains(segments, s => s.Text == "#AI" && s.ClickableHashtag == "ai");
        Assert.Contains(segments, s => s.Text == "#未保存" && s.ClickableHashtag == null);
    }

    [Fact]
    public void QuotedNotePreviewExcerpt_TruncatesLongBodyWithEllipsis()
    {
        var note = MakeNote("quoting", DateTime.Now) with { QuotedNoteId = Guid.NewGuid() };
        var quoted = MakeNote(new string('x', 150), DateTime.Now);
        var item = new NoteTimelineItemViewModel(
            note, false, NoOpAsyncCallback, NoOpAsyncCallback, NoOpItemCallback, NoOpUrlCallback, NoOpItemCallback, NoOpItemCallback, NoOpUrlCallback, NoOpUrlCallback,
            quotedNotePreview: quoted);

        Assert.Equal(new string('x', 120) + "…", item.QuotedNotePreviewExcerpt);
    }

    [Fact]
    public void QuotedNotePreviewExcerpt_ShortBody_IsNotTruncated()
    {
        var note = MakeNote("quoting", DateTime.Now) with { QuotedNoteId = Guid.NewGuid() };
        var quoted = MakeNote("short body", DateTime.Now);
        var item = new NoteTimelineItemViewModel(
            note, false, NoOpAsyncCallback, NoOpAsyncCallback, NoOpItemCallback, NoOpUrlCallback, NoOpItemCallback, NoOpItemCallback, NoOpUrlCallback, NoOpUrlCallback,
            quotedNotePreview: quoted);

        Assert.Equal("short body", item.QuotedNotePreviewExcerpt);
    }

    /// <summary>sa_implement (Note body URL inline linking) Task 2: BodySegments must mark a URL
    /// occurrence clickable only when it's actually present in Note.LinkUrls, and render its display
    /// text through UrlDisplayConverter.FormatUrlForDisplay (scheme stripped) rather than the raw
    /// body substring - the same real-vs-truncated guard and display formatting the removed LinkUrls
    /// button row used to apply.</summary>
    [Fact]
    public void BodySegments_MarksOnlyRealUrlsClickable_AndFormatsDisplayText()
    {
        var note = MakeNote("見て https://example.com/a 良い https://example.com/not-recorded", DateTime.Now,
            linkUrls: ImmutableArray.Create("https://example.com/a"));
        var item = new NoteTimelineItemViewModel(note, false, NoOpAsyncCallback, NoOpAsyncCallback, NoOpItemCallback, NoOpUrlCallback, NoOpItemCallback, NoOpItemCallback, NoOpUrlCallback, NoOpUrlCallback);

        var segments = item.BodySegments;

        Assert.Contains(segments, s => s.Text == "example.com/a" && s.ClickableUrl == "https://example.com/a");
        Assert.DoesNotContain(segments, s => s.ClickableUrl == "https://example.com/not-recorded");
    }

    /// <summary>sa_implement (Note body URL inline linking) Task 2: a URL's own '#' fragment must
    /// stay part of the single clickable URL segment - hashtag tokenization must never see it and
    /// carve out a spurious second clickable "#section" hashtag segment.</summary>
    [Fact]
    public void BodySegments_UrlWithFragment_IsOneClickableUrlSegment_NotSplitIntoAHashtag()
    {
        var note = MakeNote("参照 https://example.com/page#section 続き", DateTime.Now,
            linkUrls: ImmutableArray.Create("https://example.com/page#section"));
        var item = new NoteTimelineItemViewModel(note, false, NoOpAsyncCallback, NoOpAsyncCallback, NoOpItemCallback, NoOpUrlCallback, NoOpItemCallback, NoOpItemCallback, NoOpUrlCallback, NoOpUrlCallback);

        var segments = item.BodySegments;

        Assert.Contains(segments, s => s.ClickableUrl == "https://example.com/page#section");
        Assert.DoesNotContain(segments, s => s.ClickableHashtag is not null);
    }

    /// <summary>sa_implement Task 2: a hashtag and a URL in the same body must both be recognized,
    /// each as its own independently-clickable segment.</summary>
    [Fact]
    public void BodySegments_HashtagAndUrlInSameBody_BothBecomeClickable()
    {
        var note = MakeNote("#earnings 決算資料 https://example.com/ir", DateTime.Now,
            hashtags: ImmutableArray.Create("earnings"),
            linkUrls: ImmutableArray.Create("https://example.com/ir"));
        var item = new NoteTimelineItemViewModel(note, false, NoOpAsyncCallback, NoOpAsyncCallback, NoOpItemCallback, NoOpUrlCallback, NoOpItemCallback, NoOpItemCallback, NoOpUrlCallback, NoOpUrlCallback);

        var segments = item.BodySegments;

        Assert.Contains(segments, s => s.ClickableHashtag == "earnings");
        Assert.Contains(segments, s => s.ClickableUrl == "https://example.com/ir");
    }

    /// <summary>sa_implement Task 1: RequestHashtagFilter/RequestHashtagSearch on the item must relay
    /// to the owning NoteTimelineViewModel's ApplyHashtagToFilter/ApplyHashtagToSearch (the
    /// constructor-callback pattern shared with pin/delete/edit/openUrl/viewChart).</summary>
    [Fact]
    public async Task RequestHashtagFilter_FiltersTimelineToNotesCarryingTheHashtag()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir);
            await noteRepository.CreateAsync(MakeNote("tagged #earnings", DateTime.Now, hashtags: ImmutableArray.Create("earnings")));
            await noteRepository.CreateAsync(MakeNote("untagged", DateTime.Now));
            await timeline.RefreshAsync();
            Assert.Equal(2, timeline.DisplayedNotes.Count);

            var taggedItem = timeline.DisplayedNotes.Single(i => i.Note.Body == "tagged #earnings");
            taggedItem.RequestHashtagFilter("earnings");
            await timeline.RefreshAsync();

            Assert.Single(timeline.DisplayedNotes);
            Assert.Equal("tagged #earnings", timeline.DisplayedNotes[0].Note.Body);
            Assert.Equal("earnings", timeline.FilterCriteria.SelectedHashtag);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task RequestHashtagSearch_SetsSearchTextToHashHashtag()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir);
            await noteRepository.CreateAsync(MakeNote("tagged #earnings", DateTime.Now, hashtags: ImmutableArray.Create("earnings")));
            await timeline.RefreshAsync();

            var taggedItem = timeline.DisplayedNotes.Single();
            taggedItem.RequestHashtagSearch("earnings");

            Assert.Equal("#earnings", timeline.SearchText);
            Assert.Equal("#earnings", timeline.FilterCriteria.SearchText);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>sa_implement (Note tab UI polish Task 3, Y:\Temp\sa_implementation_plan_note_ui_polish.md):
    /// RefreshAsync must populate DeletedNotesCount from a fresh CountDeletedAsync read and
    /// OrphanedFilesCount from the injected OrphanedAttachmentScanResultHolder's latest report,
    /// independent of the active FilterCriteria (deleted Notes are never part of DisplayedNotes).</summary>
    [Fact]
    public async Task RefreshAsync_PopulatesDeletedNotesCountAndOrphanedFilesCount()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var connectionManager = new NoteDatabaseConnectionManager(NullLogger<NoteDatabaseConnectionManager>.Instance, tempDir);
            var schemaInitializer = new NoteSchemaInitializer(connectionManager, NullLogger<NoteSchemaInitializer>.Instance);
            await schemaInitializer.InitializeAsync();
            var noteRepository = new NoteRepository(connectionManager, NullLogger<NoteRepository>.Instance);
            var attachmentRepository = new AttachmentRepository(connectionManager, NullLogger<AttachmentRepository>.Instance);
            var cacheSynchronizer = new TickerMetadataNotesCacheSynchronizer(
                noteRepository, UserStrategyMetadataRepository.Instance, new FakeNotesSettingsManager(), NullLogger<TickerMetadataNotesCacheSynchronizer>.Instance);
            var dispatcherService = new SynchronousDispatcherService();
            NoteEditorViewModel EditorFactory() => new(noteRepository, attachmentRepository, cacheSynchronizer, new FakeMarketDataProvider(), dispatcherService, NullLogger<NoteEditorViewModel>.Instance);

            var orphanedHolder = new OrphanedAttachmentScanResultHolder();
            orphanedHolder.SetLatestReport(new OrphanedAttachmentReport(new[] { "orphan1.png", "orphan2.png" }));

            var deletedNote = MakeNote("to be deleted", DateTime.Now);
            await noteRepository.CreateAsync(deletedNote);
            await noteRepository.SoftDeleteAsync(deletedNote.Id);

            var timeline = new NoteTimelineViewModel(
                noteRepository, schemaInitializer, cacheSynchronizer, EditorFactory, new FakeDialogService(),
                new FakeWatchlistManager(), new FakeMarketDataProvider(), attachmentRepository, dispatcherService,
                FakeTickerStateStore.FromProfiles(Enumerable.Empty<WatchlistProfile>()), orphanedHolder, new FakeNotesSettingsManager());

            await timeline.RefreshAsync();

            Assert.Equal(1, timeline.DeletedNotesCount);
            Assert.Equal(2, timeline.OrphanedFilesCount);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>sa_implement (Note tab UI polish Task 3): the toolbar's "Trash"/"Orphaned Files" icon
    /// buttons must each request their own tab of the shared trash dialog.</summary>
    [Fact]
    public async Task OpenTrashCommand_And_OpenOrphanedFilesCommand_RequestTheirOwnDialogTab()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, _, dialogService) = await CreateTimelineAsync(tempDir);

            await timeline.OpenTrashCommand.ExecuteAsync(null);
            Assert.Equal(NoteTrashInitialTab.Deleted, dialogService.LastRequestedInitialTab);

            await timeline.OpenOrphanedFilesCommand.ExecuteAsync(null);
            Assert.Equal(NoteTrashInitialTab.Orphaned, dialogService.LastRequestedInitialTab);

            Assert.Equal(2, dialogService.ShowNoteTrashDialogAsyncCallCount);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void Constructor_WithAttachmentsButNoAttachmentRepository_LeavesThumbnailBitmapsEmpty()
    {
        var note = MakeNote("body", DateTime.Now) with { AttachmentIds = ImmutableArray.Create(Guid.NewGuid()) };
        var item = new NoteTimelineItemViewModel(note, false, NoOpAsyncCallback, NoOpAsyncCallback, NoOpItemCallback, NoOpUrlCallback, NoOpItemCallback, NoOpItemCallback, NoOpUrlCallback, NoOpUrlCallback);

        Assert.Null(item.ThumbnailLoadTask);
        Assert.Empty(item.ThumbnailBitmaps);
        Assert.Equal("\U0001F4CE 1", item.AttachmentCountText);
    }

    [Fact]
    public async Task Constructor_WithAttachmentIdThatDoesNotResolve_LeavesThumbnailBitmapsEmptyWithoutThrowing()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var connectionManager = new NoteDatabaseConnectionManager(NullLogger<NoteDatabaseConnectionManager>.Instance, tempDir);
            var schemaInitializer = new NoteSchemaInitializer(connectionManager, NullLogger<NoteSchemaInitializer>.Instance);
            await schemaInitializer.InitializeAsync();
            var attachmentRepository = new AttachmentRepository(connectionManager, NullLogger<AttachmentRepository>.Instance);

            var note = MakeNote("body", DateTime.Now) with { AttachmentIds = ImmutableArray.Create(Guid.NewGuid()) };
            var item = new NoteTimelineItemViewModel(note, false, NoOpAsyncCallback, NoOpAsyncCallback, NoOpItemCallback, NoOpUrlCallback, NoOpItemCallback, NoOpItemCallback, NoOpUrlCallback, NoOpUrlCallback, attachmentRepository);

            Assert.NotNull(item.ThumbnailLoadTask);
            await item.ThumbnailLoadTask!;

            Assert.Empty(item.ThumbnailBitmaps);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void ExtraAttachmentCountText_WithFourAttachments_ReportsOneExtra()
    {
        var note = MakeNote("body", DateTime.Now) with
        {
            AttachmentIds = ImmutableArray.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        };
        var item = new NoteTimelineItemViewModel(note, false, NoOpAsyncCallback, NoOpAsyncCallback, NoOpItemCallback, NoOpUrlCallback, NoOpItemCallback, NoOpItemCallback, NoOpUrlCallback, NoOpUrlCallback);

        Assert.Equal("+1", item.ExtraAttachmentCountText);
    }

    [Fact]
    public void ExtraAttachmentCountText_WithThreeOrFewerAttachments_IsEmpty()
    {
        var note = MakeNote("body", DateTime.Now) with
        {
            AttachmentIds = ImmutableArray.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        };
        var item = new NoteTimelineItemViewModel(note, false, NoOpAsyncCallback, NoOpAsyncCallback, NoOpItemCallback, NoOpUrlCallback, NoOpItemCallback, NoOpItemCallback, NoOpUrlCallback, NoOpUrlCallback);

        Assert.Equal(string.Empty, item.ExtraAttachmentCountText);
    }

    [Fact]
    public void RequiresCollapseToggle_TrueWhenBodyExceedsDefaultMaxCharacters()
    {
        var note = MakeNote(new string('a', 151), DateTime.Now);
        var item = new NoteTimelineItemViewModel(note, false, NoOpAsyncCallback, NoOpAsyncCallback, NoOpItemCallback, NoOpUrlCallback, NoOpItemCallback, NoOpItemCallback, NoOpUrlCallback, NoOpUrlCallback);

        Assert.True(item.RequiresCollapseToggle);
    }

    /// <summary>sa_minimal_fix (Notes-only Settings, ReadMoreThreshold fix request #1): kept at 10/11
    /// newlines regardless of the production default (150/5 as of the latest fix request) so this
    /// test still isolates the newline-counting logic (CountNewlines) from whatever that default
    /// happens to be -
    /// explicitly passing readMoreMaxLines rather than relying on NoteTimelineItemViewModel's own
    /// default parameter value.</summary>
    [Fact]
    public void RequiresCollapseToggle_TrueWhenMoreThanConfiguredMaxLines()
    {
        var body = string.Join("\n", Enumerable.Repeat("line", 12)); // 11 newlines
        var note = MakeNote(body, DateTime.Now);
        var item = new NoteTimelineItemViewModel(
            note, false, NoOpAsyncCallback, NoOpAsyncCallback, NoOpItemCallback, NoOpUrlCallback, NoOpItemCallback, NoOpItemCallback, NoOpUrlCallback, NoOpUrlCallback,
            readMoreMaxCharacters: 200, readMoreMaxLines: 10);

        Assert.True(item.RequiresCollapseToggle);
    }

    [Fact]
    public void RequiresCollapseToggle_FalseForShortSingleLineBody()
    {
        var note = MakeNote("short note", DateTime.Now);
        var item = new NoteTimelineItemViewModel(note, false, NoOpAsyncCallback, NoOpAsyncCallback, NoOpItemCallback, NoOpUrlCallback, NoOpItemCallback, NoOpItemCallback, NoOpUrlCallback, NoOpUrlCallback);

        Assert.False(item.RequiresCollapseToggle);
    }

    [Fact]
    public void DisplayBody_WhenCollapsedAndTooLong_IsTruncatedToDefaultMaxCharacters()
    {
        var note = MakeNote(new string('a', 400), DateTime.Now);
        var item = new NoteTimelineItemViewModel(note, false, NoOpAsyncCallback, NoOpAsyncCallback, NoOpItemCallback, NoOpUrlCallback, NoOpItemCallback, NoOpItemCallback, NoOpUrlCallback, NoOpUrlCallback);

        Assert.Equal(150, item.DisplayBody.Length);
    }

    [Fact]
    public void DisplayBody_WhenExpanded_ReturnsFullBody()
    {
        var note = MakeNote(new string('a', 400), DateTime.Now);
        var item = new NoteTimelineItemViewModel(note, true, NoOpAsyncCallback, NoOpAsyncCallback, NoOpItemCallback, NoOpUrlCallback, NoOpItemCallback, NoOpItemCallback, NoOpUrlCallback, NoOpUrlCallback);

        Assert.Equal(400, item.DisplayBody.Length);
    }

    /// <summary>sa_implement (Notes-only Settings & Thread Collapsing Task 2, Y:\Temp\sa_implementation_plan.md):
    /// the "Read more" character-count threshold must be driven by the caller-supplied
    /// readMoreMaxCharacters/readMoreMaxLines constructor values (sourced from
    /// INotesSettingsManager in production) rather than a fixed 300, so Settings &gt; Notes changes
    /// actually take effect on the next-created card.</summary>
    [Fact]
    public void DisplayBody_WithCustomReadMoreMaxCharacters_TruncatesAtConfiguredThreshold()
    {
        var note = MakeNote(new string('a', 400), DateTime.Now);
        var item = new NoteTimelineItemViewModel(
            note, false, NoOpAsyncCallback, NoOpAsyncCallback, NoOpItemCallback, NoOpUrlCallback, NoOpItemCallback, NoOpItemCallback, NoOpUrlCallback, NoOpUrlCallback,
            readMoreMaxCharacters: 50, readMoreMaxLines: 10);

        Assert.True(item.RequiresCollapseToggle);
        Assert.Equal(50, item.DisplayBody.Length);
    }

    /// <summary>Companion to the above for the newline-count threshold: a body with 6 newlines must
    /// collapse when readMoreMaxLines is configured to 5, even though it is far under the default
    /// 300-character limit.</summary>
    [Fact]
    public void RequiresCollapseToggle_WithCustomReadMoreMaxLines_CollapsesAtConfiguredThreshold()
    {
        var body = string.Join("\n", Enumerable.Repeat("line", 7)); // 6 newlines
        var note = MakeNote(body, DateTime.Now);
        var item = new NoteTimelineItemViewModel(
            note, false, NoOpAsyncCallback, NoOpAsyncCallback, NoOpItemCallback, NoOpUrlCallback, NoOpItemCallback, NoOpItemCallback, NoOpUrlCallback, NoOpUrlCallback,
            readMoreMaxCharacters: 300, readMoreMaxLines: 5);

        Assert.True(item.RequiresCollapseToggle);
    }

    /// <summary>sa_implement (Reply-Leaf Restriction & Deletion Tombstone, Task 3): the default
    /// construction (canReply=true, isTombstone=false) represents an ordinary, repliable leaf Note -
    /// ReplyCommand must be executable.</summary>
    [Fact]
    public void ReplyCommand_CanExecute_TrueForOrdinaryLeafNote()
    {
        var note = MakeNote("leaf", DateTime.Now);
        var item = new NoteTimelineItemViewModel(note, false, NoOpAsyncCallback, NoOpAsyncCallback, NoOpItemCallback, NoOpUrlCallback, NoOpItemCallback, NoOpItemCallback, NoOpUrlCallback, NoOpUrlCallback);

        Assert.True(item.ReplyCommand.CanExecute(null));
    }

    /// <summary>canReply=false represents a Note that already has a live (or surviving-tombstone)
    /// reply of its own - not the current end of its branch, so Reply must be disabled (spec: only
    /// the last article of the thread may be replied to).</summary>
    [Fact]
    public void ReplyCommand_CanExecute_FalseWhenCanReplyIsFalse()
    {
        var note = MakeNote("has a reply already", DateTime.Now);
        var item = new NoteTimelineItemViewModel(
            note, false, NoOpAsyncCallback, NoOpAsyncCallback, NoOpItemCallback, NoOpUrlCallback, NoOpItemCallback, NoOpItemCallback, NoOpUrlCallback, NoOpUrlCallback,
            canReply: false);

        Assert.False(item.ReplyCommand.CanExecute(null));
    }

    /// <summary>Regression guard: a tombstone must never be repliable even if its raw leaf status
    /// (canReply) happens to be true - CanExecuteReply must combine both, not just gate on CanReply
    /// alone.</summary>
    [Fact]
    public void ReplyCommand_CanExecute_FalseWhenIsTombstone_EvenIfCanReplyIsTrue()
    {
        var note = MakeNote("deleted", DateTime.Now);
        var item = new NoteTimelineItemViewModel(
            note, false, NoOpAsyncCallback, NoOpAsyncCallback, NoOpItemCallback, NoOpUrlCallback, NoOpItemCallback, NoOpItemCallback, NoOpUrlCallback, NoOpUrlCallback,
            isTombstone: true, canReply: true);

        Assert.False(item.ReplyCommand.CanExecute(null));
    }

    /// <summary>Quote, unlike Reply, is not restricted to the thread's leaf - only a tombstone
    /// disables it.</summary>
    [Fact]
    public void QuoteCommand_CanExecute_TrueForNonLeafLiveNote_RegardlessOfCanReply()
    {
        var note = MakeNote("has a reply already", DateTime.Now);
        var item = new NoteTimelineItemViewModel(
            note, false, NoOpAsyncCallback, NoOpAsyncCallback, NoOpItemCallback, NoOpUrlCallback, NoOpItemCallback, NoOpItemCallback, NoOpUrlCallback, NoOpUrlCallback,
            canReply: false);

        Assert.True(item.QuoteCommand.CanExecute(null));
    }

    /// <summary>Quoting a deletion tombstone has no product meaning (spec: reply/quote on a deletion
    /// trace is always disallowed).</summary>
    [Fact]
    public void QuoteCommand_CanExecute_FalseWhenIsTombstone()
    {
        var note = MakeNote("deleted", DateTime.Now);
        var item = new NoteTimelineItemViewModel(
            note, false, NoOpAsyncCallback, NoOpAsyncCallback, NoOpItemCallback, NoOpUrlCallback, NoOpItemCallback, NoOpItemCallback, NoOpUrlCallback, NoOpUrlCallback,
            isTombstone: true);

        Assert.False(item.QuoteCommand.CanExecute(null));
    }

    /// <summary>sa_minimal_fix (Notes-only Settings, ReadMoreThreshold fix request #1): reproduces the
    /// reported bug - changing a Settings &gt; Notes value while the Notes tab is already open must
    /// update the already-rendered cards WITHOUT any other action (switching tabs, editing a Note,
    /// etc.) forcing a RefreshAsync. Root cause was that NoteTimelineViewModel never subscribed to
    /// INotesSettingsManager.PropertyChanged, so a value change only reached CreateItemViewModel on
    /// some unrelated next refresh. Deliberately does NOT call timeline.RefreshAsync() itself -
    /// doing so would trivially pass even without the constructor subscription fix, since
    /// CreateItemViewModel always re-reads current settings on any refresh regardless of what
    /// triggered it.</summary>
    [Fact]
    public async Task ChangingNotesSettingsManagerProperty_AutomaticallyRefreshesAlreadyDisplayedCards()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var notesSettingsManager = new FakeNotesSettingsManager();
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir, notesSettingsManager: notesSettingsManager);
            var note = MakeNote(new string('a', 250), DateTime.Now);
            await noteRepository.CreateAsync(note);
            await timeline.RefreshAsync();

            Assert.True(timeline.DisplayedNotes.Single().RequiresCollapseToggle);

            var refreshedTcs = new TaskCompletionSource();
            void OnDisplayedNotesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => refreshedTcs.TrySetResult();
            timeline.DisplayedNotes.CollectionChanged += OnDisplayedNotesChanged;
            try
            {
                notesSettingsManager.SetReadMoreMaxCharacters(500);
                await refreshedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                timeline.DisplayedNotes.CollectionChanged -= OnDisplayedNotesChanged;
            }

            Assert.False(timeline.DisplayedNotes.Single().RequiresCollapseToggle);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>sa_implement (Notes専用「外観」設定, Y:\Temp\sa_implementation_plan_notes_appearance.md
    /// Task 3): when Settings &gt; Notes has a custom BodyFontSize/BodyTextColor/BodyBackgroundColor
    /// configured, cards produced by RefreshAsync must carry the matching values so NoteCardView
    /// renders independently of Settings &gt; Theme/Fonts.</summary>
    [Fact]
    public async Task RefreshAsync_WithCustomBodyFontSizeAndColor_ProducesItemsCarryingMatchingValues()
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

            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir, notesSettingsManager: notesSettingsManager);

            var note = MakeNote("plain note body", DateTime.Now);
            await noteRepository.CreateAsync(note);

            await timeline.RefreshAsync();

            var item = Assert.Single(timeline.DisplayedNotes);
            Assert.Equal(20.0, item.BodyFontSize);
            Assert.Equal(bodyTextColor, item.BodyTextColor);
            Assert.Equal(bodyBackgroundColor, item.BodyBackgroundColor);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>sa_minimal_fix (Notes専用「外観」設定の再構成, Y:\Temp\sa_fix_plan_notes_appearance_restructure.md):
    /// when Settings &gt; Notes has a custom UrlColor/HashtagColor configured, cards produced by
    /// RefreshAsync must carry the matching Color on UrlColor/HashtagColor so NoteCardView's
    /// code-behind can turn it into a Brush when actually rendering (on the UI thread) - the default
    /// (unconfigured) case is covered implicitly by every other test in this file leaving both null
    /// and relying on the style's own foreground (itself bound to BodyTextColor). Asserts the plain
    /// Color value (not a SolidColorBrush) deliberately: constructing an Avalonia Brush off the UI
    /// thread throws "Call from invalid thread", which is exactly why NoteTimelineItemViewModel
    /// exposes Color instead of IBrush in the first place.</summary>
    [Fact]
    public async Task RefreshAsync_WithCustomUrlAndHashtagColors_ProducesItemsCarryingMatchingColors()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var urlColor = IndicatorColor.FromRgb(0x11, 0x22, 0x33);
            var hashtagColor = IndicatorColor.FromRgb(0x44, 0x55, 0x66);
            var notesSettingsManager = new FakeNotesSettingsManager();
            notesSettingsManager.SetUrlColor(urlColor);
            notesSettingsManager.SetHashtagColor(hashtagColor);

            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir, notesSettingsManager: notesSettingsManager);

            var note = MakeNote("#earnings https://example.com/ir", DateTime.Now,
                hashtags: ImmutableArray.Create("earnings"), linkUrls: ImmutableArray.Create("https://example.com/ir"));
            await noteRepository.CreateAsync(note);

            await timeline.RefreshAsync();

            var item = Assert.Single(timeline.DisplayedNotes);
            Assert.Equal(urlColor, item.UrlColor);
            Assert.Equal(hashtagColor, item.HashtagColor);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>sa_implement (Notes メインタイムライン インフィニットスクロール Task 4,
    /// Y:\Temp\sa_implementation_plan.md): with 5 unrelated root Notes and Settings &gt; Notes'
    /// TimelinePageSize set to 2, RefreshAsync must only materialize the first 2 (pinned-first/CreatedAt-
    /// descending, same order as before pagination existed) into DisplayedNotes and report that more
    /// remain via HasMoreVisibleNotes.</summary>
    [Fact]
    public async Task RefreshAsync_WithSmallTimelinePageSize_OnlyLoadsFirstPage()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var notesSettingsManager = new FakeNotesSettingsManager();
            notesSettingsManager.SetTimelinePageSize(2);
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir, notesSettingsManager: notesSettingsManager);

            var notes = Enumerable.Range(1, 5).Select(day => MakeNote($"Note {day}", new DateTime(2026, 8, day))).ToList();
            foreach (var note in notes)
            {
                await noteRepository.CreateAsync(note);
            }

            await timeline.RefreshAsync();

            // CreatedAt-descending: day 5, day 4 are the newest two.
            Assert.Equal(new[] { notes[4].Id, notes[3].Id }, timeline.DisplayedNotes.Select(n => n.Note.Id));
            Assert.True(timeline.HasMoreVisibleNotes);
            Assert.False(timeline.IsLoadingMoreNotes);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>sa_implement (Notes メインタイムライン インフィニットスクロール Task 4):
    /// LoadMoreVisibleNotesAsync must append the next page onto the end of DisplayedNotes - via
    /// BulkObservableCollection.AddRange, not a ReplaceRange - so the already-displayed first-page
    /// card instances are left untouched (a scroll-position-preserving requirement no reference-equality
    /// check on the underlying data alone would catch). A second call once every visible Note is
    /// already displayed must be a no-op (Inverse: ¬P ⇒ ¬Q from the implementation plan's Formal
    /// Methods section) rather than duplicating cards or throwing.</summary>
    [Fact]
    public async Task LoadMoreVisibleNotesAsync_AppendsNextPage_PreservesExistingInstances_AndNoOpsOnceExhausted()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var notesSettingsManager = new FakeNotesSettingsManager();
            notesSettingsManager.SetTimelinePageSize(2);
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir, notesSettingsManager: notesSettingsManager);

            var notes = Enumerable.Range(1, 5).Select(day => MakeNote($"Note {day}", new DateTime(2026, 8, day))).ToList();
            foreach (var note in notes)
            {
                await noteRepository.CreateAsync(note);
            }

            await timeline.RefreshAsync();
            var firstPageInstances = timeline.DisplayedNotes.ToList();

            await timeline.LoadMoreVisibleNotesAsync();

            Assert.Equal(4, timeline.DisplayedNotes.Count);
            Assert.Equal(new[] { notes[4].Id, notes[3].Id, notes[2].Id, notes[1].Id }, timeline.DisplayedNotes.Select(n => n.Note.Id));
            Assert.Same(firstPageInstances[0], timeline.DisplayedNotes[0]);
            Assert.Same(firstPageInstances[1], timeline.DisplayedNotes[1]);
            Assert.True(timeline.HasMoreVisibleNotes);

            await timeline.LoadMoreVisibleNotesAsync();
            Assert.Equal(5, timeline.DisplayedNotes.Count);
            Assert.False(timeline.HasMoreVisibleNotes);

            // Every visible Note is already displayed - a further call must not duplicate anything.
            await timeline.LoadMoreVisibleNotesAsync();
            Assert.Equal(5, timeline.DisplayedNotes.Count);
            Assert.False(timeline.IsLoadingMoreNotes);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>sa_implement (Notes メインタイムライン インフィニットスクロール Task 4): with
    /// TimelinePageSize set to 1 but a 2-post reply chain (root + 1 reply, below the collapse
    /// threshold so both stay visible), a naive flat-index page cut after the first post would reveal
    /// the root with its connector line drawn toward a reply that isn't displayed yet. RefreshAsync
    /// must instead extend the first page to keep the whole chain together.</summary>
    [Fact]
    public async Task RefreshAsync_PageSizeWouldSplitAReplyChain_ExtendsThePageToKeepItTogether()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var notesSettingsManager = new FakeNotesSettingsManager();
            notesSettingsManager.SetTimelinePageSize(1);
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir, notesSettingsManager: notesSettingsManager);

            var a = MakeNote("A", new DateTime(2026, 8, 1));
            await noteRepository.CreateAsync(a);
            var b = MakeNote("B", new DateTime(2026, 8, 2)) with { ParentNoteId = a.Id };
            await noteRepository.CreateAsync(b);

            await timeline.RefreshAsync();

            Assert.Equal(new[] { a.Id, b.Id }, timeline.DisplayedNotes.Select(n => n.Note.Id));
            Assert.False(timeline.HasMoreVisibleNotes);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>sa_implement (Notes メインタイムライン インフィニットスクロール Task 4): reuses the
    /// same 5-post collapsing thread as RefreshAsync_LongReplyChain_CollapsesMiddleAndFlagsRootCard
    /// (only A/D/E survive collapse; B/C are hidden) with TimelinePageSize=1, to prove the
    /// collapse-hidden posts never count toward the page size or an infinite-scroll load: the visible
    /// universe is 3 (not 5), the first page is exactly [A] (D doesn't directly follow A once B/C are
    /// collapsed out, so there is nothing to extend across), and D/E - which do connect to each other -
    /// load together as one inseparable page rather than being split further.</summary>
    [Fact]
    public async Task RefreshAsync_CollapsedThread_HiddenRepliesNeverCountTowardPagingOrLoadMore()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var notesSettingsManager = new FakeNotesSettingsManager();
            notesSettingsManager.SetTimelinePageSize(1);
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir, notesSettingsManager: notesSettingsManager);

            var a = MakeNote("A", new DateTime(2026, 8, 1));
            await noteRepository.CreateAsync(a);
            var b = MakeNote("B", new DateTime(2026, 8, 2)) with { ParentNoteId = a.Id };
            await noteRepository.CreateAsync(b);
            var c = MakeNote("C", new DateTime(2026, 8, 3)) with { ParentNoteId = b.Id };
            await noteRepository.CreateAsync(c);
            var d = MakeNote("D", new DateTime(2026, 8, 4)) with { ParentNoteId = c.Id };
            await noteRepository.CreateAsync(d);
            var e = MakeNote("E", new DateTime(2026, 8, 5)) with { ParentNoteId = d.Id };
            await noteRepository.CreateAsync(e);

            await timeline.RefreshAsync();

            Assert.Equal(new[] { a.Id }, timeline.DisplayedNotes.Select(n => n.Note.Id));
            Assert.True(timeline.HasMoreVisibleNotes);

            await timeline.LoadMoreVisibleNotesAsync();

            Assert.Equal(new[] { a.Id, d.Id, e.Id }, timeline.DisplayedNotes.Select(n => n.Note.Id));
            Assert.False(timeline.HasMoreVisibleNotes);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>sa_implement (Notes メインタイムライン インフィニットスクロール Task 4, Converse
    /// check from the implementation plan's Formal Methods section): a fresh RefreshAsync call - the
    /// same one every filter/search-box keystroke already triggers - must reset the infinite-scroll
    /// cursor back down to a single page, not keep whatever expanded count LoadMoreVisibleNotesAsync
    /// had previously reached. Otherwise, changing the filter after scrolling deep into the old result
    /// set would silently carry the old page count into the unrelated new one.</summary>
    [Fact]
    public async Task RefreshAsync_CalledAgainAfterLoadMore_ResetsBackToASinglePage()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var notesSettingsManager = new FakeNotesSettingsManager();
            notesSettingsManager.SetTimelinePageSize(2);
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir, notesSettingsManager: notesSettingsManager);

            var notes = Enumerable.Range(1, 5).Select(day => MakeNote($"Note {day}", new DateTime(2026, 8, day))).ToList();
            foreach (var note in notes)
            {
                await noteRepository.CreateAsync(note);
            }

            await timeline.RefreshAsync();
            await timeline.LoadMoreVisibleNotesAsync();
            Assert.Equal(4, timeline.DisplayedNotes.Count);

            await timeline.RefreshAsync();

            Assert.Equal(2, timeline.DisplayedNotes.Count);
            Assert.True(timeline.HasMoreVisibleNotes);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }
}
