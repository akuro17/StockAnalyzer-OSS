using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.Notes;
using StockAnalyzer.Core.Services.Notes;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services.Notes;

public class NoteThreadOrganizerTests
{
    private static Note MakeNote(string body, DateTime createdAt) => new(Guid.NewGuid(), body, createdAt, createdAt);

    /// <summary>Builds a parent→children lookup exactly as <see cref="NoteThreadOrganizer.CollectPrunedSubtree"/>
    /// expects it (oldest-first per parent), from a flat list of Notes wired up via ParentNoteId.</summary>
    private static Dictionary<Guid, List<Note>> BuildRepliesByParentId(params Note[] notes) =>
        notes.Where(n => n.ParentNoteId.HasValue)
            .GroupBy(n => n.ParentNoteId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(n => n.CreatedAt).ToList());

    [Fact]
    public void CollectPrunedSubtree_LinearChainNoDeletions_KeepsEveryNote_LeafIsTheTail()
    {
        var a = MakeNote("A", new DateTime(2026, 8, 1));
        var b = MakeNote("B", new DateTime(2026, 8, 2)) with { ParentNoteId = a.Id };
        var c = MakeNote("C", new DateTime(2026, 8, 3)) with { ParentNoteId = b.Id };

        var result = NoteThreadOrganizer.CollectPrunedSubtree(a, BuildRepliesByParentId(a, b, c));

        Assert.Equal(new[] { a.Id, b.Id, c.Id }, result.Notes.Select(n => n.Id));
        Assert.Empty(result.TombstoneNoteIds);
        Assert.Equal(new HashSet<Guid> { c.Id }, result.LeafNoteIds);
    }

    [Fact]
    public void CollectPrunedSubtree_MiddleNoteDeletedWithSurvivingDescendant_KeepsItAsATombstone()
    {
        var a = MakeNote("A", new DateTime(2026, 8, 1));
        var b = MakeNote("B", new DateTime(2026, 8, 2)) with { ParentNoteId = a.Id, IsDeleted = true };
        var c = MakeNote("C", new DateTime(2026, 8, 3)) with { ParentNoteId = b.Id };

        var result = NoteThreadOrganizer.CollectPrunedSubtree(a, BuildRepliesByParentId(a, b, c));

        Assert.Equal(new[] { a.Id, b.Id, c.Id }, result.Notes.Select(n => n.Id));
        Assert.Equal(new HashSet<Guid> { b.Id }, result.TombstoneNoteIds);
        Assert.Equal(new HashSet<Guid> { c.Id }, result.LeafNoteIds);
    }

    /// <summary>Regression guard for the cascading-removal requirement: when the trailing deletions
    /// leave nothing surviving below A, both B and C's tombstones must disappear entirely and A must
    /// become a leaf again (able to receive a new reply) - not just have C's tombstone dropped while
    /// B's lingers.</summary>
    [Fact]
    public void CollectPrunedSubtree_TrailingConsecutiveDeletions_CascadesAwayEntirely_LeavingRootAsLeaf()
    {
        var a = MakeNote("A", new DateTime(2026, 8, 1));
        var b = MakeNote("B", new DateTime(2026, 8, 2)) with { ParentNoteId = a.Id, IsDeleted = true };
        var c = MakeNote("C", new DateTime(2026, 8, 3)) with { ParentNoteId = b.Id, IsDeleted = true };

        var result = NoteThreadOrganizer.CollectPrunedSubtree(a, BuildRepliesByParentId(a, b, c));

        Assert.Equal(new[] { a.Id }, result.Notes.Select(n => n.Id));
        Assert.Empty(result.TombstoneNoteIds);
        Assert.Equal(new HashSet<Guid> { a.Id }, result.LeafNoteIds);
    }

    [Fact]
    public void CollectPrunedSubtree_MultipleConsecutiveDeletionsThenALiveReply_KeepsAllAsTombstonesExceptTheLiveTail()
    {
        var a = MakeNote("A", new DateTime(2026, 8, 1));
        var b = MakeNote("B", new DateTime(2026, 8, 2)) with { ParentNoteId = a.Id, IsDeleted = true };
        var c = MakeNote("C", new DateTime(2026, 8, 3)) with { ParentNoteId = b.Id, IsDeleted = true };
        var d = MakeNote("D", new DateTime(2026, 8, 4)) with { ParentNoteId = c.Id };

        var result = NoteThreadOrganizer.CollectPrunedSubtree(a, BuildRepliesByParentId(a, b, c, d));

        Assert.Equal(new[] { a.Id, b.Id, c.Id, d.Id }, result.Notes.Select(n => n.Id));
        Assert.Equal(new HashSet<Guid> { b.Id, c.Id }, result.TombstoneNoteIds);
        Assert.Equal(new HashSet<Guid> { d.Id }, result.LeafNoteIds);
    }

    /// <summary>The root itself is never pruned by this method even when soft-deleted and childless -
    /// per its documented contract, callers own the decision of whether a deleted root is worth
    /// keeping (e.g. the main timeline's per-root loop deciding whether to treat it as a displayed
    /// thread at all).</summary>
    [Fact]
    public void CollectPrunedSubtree_DeletedChildlessRoot_IsNeverPrunedByThisMethod()
    {
        var a = MakeNote("A", new DateTime(2026, 8, 1)) with { IsDeleted = true };

        var result = NoteThreadOrganizer.CollectPrunedSubtree(a, BuildRepliesByParentId(a));

        Assert.Equal(new[] { a.Id }, result.Notes.Select(n => n.Id));
        Assert.Equal(new HashSet<Guid> { a.Id }, result.TombstoneNoteIds);
        Assert.Equal(new HashSet<Guid> { a.Id }, result.LeafNoteIds);
    }
}
