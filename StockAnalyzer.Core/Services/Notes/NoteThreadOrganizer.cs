using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.Notes;

namespace StockAnalyzer.Core.Services.Notes;

/// <summary>Pure, DB-independent domain logic (sa_minimal_fix, constraint-check remediation Phase 3)
/// for turning a flat set of <see cref="Note"/> rows into the threaded, collapsed, tombstone-aware
/// display order used by both StockAnalyzer.Avalonia's main timeline (NoteTimelineViewModel.
/// RefreshCoreAsync/BuildThreadedDisplayOrder) and its individual detail page
/// (NoteTimelineViewModel.LoadDetailThreadAsync). Moved out of the ViewModel layer into
/// StockAnalyzer.Core so tree-traversal/collapse/tombstone business rules live in the Domain layer
/// per the project's Dependency Direction rule (Views -&gt; ViewModels -&gt; Application -&gt; Domain),
/// rather than inside a ViewModel.</summary>
public static class NoteThreadOrganizer
{
    /// <summary>Result of <see cref="CollectPrunedSubtree"/>: <paramref name="Notes"/> is
    /// <paramref name="root"/> plus every descendant reply, depth-first, oldest-first per level -
    /// except that a soft-deleted Note with no surviving (live, or itself-surviving-tombstone)
    /// descendant is dropped together with its entire (necessarily all-deleted) subtree, per the
    /// cascading tombstone-removal requirement ("once a deletion trace ends up at the very tail of
    /// the chain, remove the trace itself entirely instead of displaying it"). Every Id still present
    /// in <paramref name="Notes"/> that <em>is</em> soft-deleted belongs to
    /// <see cref="TombstoneNoteIds"/>. <see cref="LeafNoteIds"/> is a purely structural fact - the set
    /// of Notes with zero children remaining after pruning - independent of deletion status; callers
    /// combine it with <see cref="TombstoneNoteIds"/> to decide reply-eligibility (a tombstone must
    /// never be repliable regardless of its leaf status).</summary>
    public readonly record struct PrunedSubtree(List<Note> Notes, HashSet<Guid> TombstoneNoteIds, HashSet<Guid> LeafNoteIds);

    /// <summary>Shared domain logic behind both the main timeline's per-root thread collapsing
    /// (<see cref="BuildThreadedDisplayOrder"/>) and the detail page's reply-chain listing: given
    /// <paramref name="root"/> and a parent→children lookup that includes soft-deleted Notes (so a
    /// chain doesn't lose everything below a deletion), walks the whole subtree, determines - bottom
    /// up - whether each soft-deleted Note "survives" (has at least one live or surviving-tombstone
    /// descendant), and prunes any soft-deleted Note that doesn't (which, since a non-surviving
    /// deleted Note can only have non-surviving deleted descendants itself, safely drops its entire
    /// remaining subtree too). <paramref name="root"/> itself is never pruned by this method even if
    /// soft-deleted (callers that don't want the root considered are responsible for checking/handling
    /// it themselves) - this mirrors <see cref="NoteRepository.GetAncestorChainAsync"/>'s "the
    /// starting note itself is not included" contract by simply not making any assumption about the
    /// root's own deletion state.</summary>
    public static PrunedSubtree CollectPrunedSubtree(Note root, IReadOnlyDictionary<Guid, List<Note>> repliesByParentId)
    {
        var visited = new HashSet<Guid>();
        var allNodes = new List<Note>();

        void Collect(Note note)
        {
            if (!visited.Add(note.Id))
            {
                return;
            }

            allNodes.Add(note);
            if (repliesByParentId.TryGetValue(note.Id, out var children))
            {
                foreach (var child in children)
                {
                    Collect(child);
                }
            }
        }

        Collect(root);

        var survives = new Dictionary<Guid, bool>();
        bool Survives(Note note)
        {
            if (survives.TryGetValue(note.Id, out var cached))
            {
                return cached;
            }

            // Guard against the recursion below re-entering a node that's still being computed (only
            // reachable via a data cycle, which GetAncestorChainAsync's own doc comment already notes
            // is "structurally impossible today since ParentNoteId is immutable after creation, but
            // defensive") - treat a not-yet-resolved node as non-surviving so the recursion terminates.
            survives[note.Id] = false;

            var result = !note.IsDeleted || (repliesByParentId.TryGetValue(note.Id, out var children) && children.Any(Survives));
            survives[note.Id] = result;
            return result;
        }

        foreach (var note in allNodes)
        {
            Survives(note);
        }

        var prunedNotes = new List<Note>();
        var tombstoneNoteIds = new HashSet<Guid>();

        void CollectPruned(Note note, bool isRoot)
        {
            if (!isRoot && note.IsDeleted && !survives[note.Id])
            {
                return;
            }

            prunedNotes.Add(note);
            if (note.IsDeleted)
            {
                tombstoneNoteIds.Add(note.Id);
            }

            if (repliesByParentId.TryGetValue(note.Id, out var children))
            {
                foreach (var child in children)
                {
                    CollectPruned(child, isRoot: false);
                }
            }
        }

        CollectPruned(root, isRoot: true);

        var childCounts = new Dictionary<Guid, int>();
        foreach (var note in prunedNotes)
        {
            if (note.ParentNoteId is { } parentId)
            {
                childCounts[parentId] = childCounts.GetValueOrDefault(parentId) + 1;
            }
        }

        var leafNoteIds = prunedNotes.Where(note => !childCounts.ContainsKey(note.Id)).Select(note => note.Id).ToHashSet();

        return new PrunedSubtree(prunedNotes, tombstoneNoteIds, leafNoteIds);
    }

    /// <summary>Result of <see cref="BuildThreadedDisplayOrder"/>: the Notes to actually render, in
    /// order, plus - for a thread that was collapsed - how many replies were omitted after a given
    /// Note's card. A card whose <see cref="Note.Id"/> is a key in
    /// <see cref="CollapsedReplyCountAfterNoteId"/> is the last visible card before the collapsed
    /// segment. <see cref="TombstoneNoteIds"/>/<see cref="LeafNoteIds"/> are the union, across every
    /// root's <see cref="CollectPrunedSubtree"/> call, of which rendered Notes are deletion
    /// placeholders and which have no reply of their own - callers look each rendered Note's Id up in
    /// both when constructing its card.</summary>
    public readonly record struct ThreadedDisplayOrder(List<Note> Notes, Dictionary<Guid, int> CollapsedReplyCountAfterNoteId, HashSet<Guid> TombstoneNoteIds, HashSet<Guid> LeafNoteIds);

    /// <summary>Builds the main timeline's actual display order (spec section 6: Reply Chain): every
    /// reply is grouped directly under the Note it replies to, instead of sorting purely by
    /// <see cref="Note.CreatedAt"/> - a reply must always read as connected to its parent, not only
    /// when it happens to land next to it chronologically. Root-level Notes (no
    /// <see cref="Note.ParentNoteId"/>, or whose parent isn't present in <paramref name="notes"/> -
    /// e.g. filtered out by the active search/scope) keep the existing pinned-first/CreatedAt-
    /// descending order; each root is immediately followed by its own replies, oldest-first (same
    /// reading order as <see cref="NoteRepository.GetRepliesAsync"/>), and each of those replies' own
    /// replies recursively, so a whole reply chain reads top-to-bottom as it was actually posted.
    ///
    /// <para>Deletion tombstones: <paramref name="notes"/> is expected to already include not just
    /// active Notes but also every soft-deleted Note that's still referenced as someone's
    /// <see cref="Note.ParentNoteId"/> (see <see cref="NoteRepository.GetDeletedNotesReferencedAsParentAsync"/>)
    /// - that's what lets a deleted parent stay resolvable in the internal id lookup so its children
    /// are no longer orphan-promoted to root here. Each root's subtree is collected via
    /// <see cref="CollectPrunedSubtree"/>, which already drops any trailing deleted Note with nothing
    /// surviving below it; a root itself that turns out to be exactly that (soft-deleted, nothing else
    /// survived) is skipped entirely here - it isn't a real post and preserves no continuity for
    /// anything still visible, so it must not become its own displayed "thread".</para>
    ///
    /// <para>Thread collapsing, per-root, not applied to nested sub-branches individually: a root's
    /// own <em>entire</em> (pruned) subtree is one "thread" for collapsing purposes. When that
    /// subtree's total Note count exceeds both <paramref name="threadCollapseThreshold"/> AND
    /// <c>1 + effective tailVisibleCount</c> (the second guard skips collapsing when the configured
    /// head+tail window would already cover every Note, which would otherwise produce a
    /// zero-hidden-count indicator), only the thread-starting root Note plus the last
    /// <paramref name="tailVisibleCount"/> Notes of the subtree are kept, and the omitted count is
    /// recorded against the root itself - the head is always exactly the root (the DashLine always
    /// starts immediately below the post that opened the thread), and
    /// <paramref name="tailVisibleCount"/> governs only the tail.
    /// <paramref name="tailVisibleCount"/> is floored to 1 (not 0) internally, specifically so there
    /// is always at least one visible tail card to attach alongside the root - the setting itself
    /// still allows 0, which this floor reinterprets as "show only 1" rather than "show none", since a
    /// floating indicator with no card of its own isn't representable in the caller's card-list UI
    /// model.</para></summary>
    public static ThreadedDisplayOrder BuildThreadedDisplayOrder(IEnumerable<Note> notes, int threadCollapseThreshold, int tailVisibleCount)
    {
        var noteList = notes.ToList();
        var byId = noteList.ToDictionary(note => note.Id);
        var repliesByParentId = noteList
            .Where(note => note.ParentNoteId is { } parentId && byId.ContainsKey(parentId))
            .GroupBy(note => note.ParentNoteId!.Value)
            .ToDictionary(group => group.Key, group => group.OrderBy(note => note.CreatedAt).ToList());

        var roots = SortForDisplay(noteList.Where(note => note.ParentNoteId is not { } parentId || !byId.ContainsKey(parentId)));

        var ordered = new List<Note>(noteList.Count);
        var collapsedReplyCountAfterNoteId = new Dictionary<Guid, int>();
        var tombstoneNoteIds = new HashSet<Guid>();
        var leafNoteIds = new HashSet<Guid>();

        var effectiveTailVisibleCount = Math.Max(tailVisibleCount, 1);

        // The head window used to be sized by tailVisibleCount too (subtree[0..tailVisibleCount)), so
        // a tailVisibleCount > 1 pulled one or more reply cards into the head alongside the root, and
        // the collapse indicator ended up on that last head *reply* instead of on the thread-starting
        // post itself. The head is fixed to the single root Note; tailVisibleCount governs only how
        // many of the most recent replies stay visible at the tail.
        const int HeadVisibleCount = 1;

        foreach (var root in roots)
        {
            var pruned = CollectPrunedSubtree(root, repliesByParentId);

            // A deleted root whose entire subtree failed to survive (CollectPrunedSubtree never
            // prunes the root itself, so this is the only way to detect it here) has nothing left to
            // preserve continuity for and isn't a real post - skip it rather than rendering an
            // isolated tombstone with no bearing on anything else still visible.
            if (root.IsDeleted && pruned.Notes.Count == 1)
            {
                continue;
            }

            var subtree = pruned.Notes;
            tombstoneNoteIds.UnionWith(pruned.TombstoneNoteIds);
            leafNoteIds.UnionWith(pruned.LeafNoteIds);

            var hiddenCount = subtree.Count - (HeadVisibleCount + effectiveTailVisibleCount);
            if (subtree.Count > threadCollapseThreshold && hiddenCount > 0)
            {
                ordered.Add(subtree[0]);
                collapsedReplyCountAfterNoteId[subtree[0].Id] = hiddenCount;
                for (var i = subtree.Count - effectiveTailVisibleCount; i < subtree.Count; i++)
                {
                    ordered.Add(subtree[i]);
                }
            }
            else
            {
                ordered.AddRange(subtree);
            }
        }

        return new ThreadedDisplayOrder(ordered, collapsedReplyCountAfterNoteId, tombstoneNoteIds, leafNoteIds);
    }

    /// <summary>Pinned Notes first (spec section 5.4), CreatedAt descending within each group.</summary>
    private static IEnumerable<Note> SortForDisplay(IEnumerable<Note> notes) =>
        notes.OrderByDescending(note => note.IsPinned).ThenByDescending(note => note.CreatedAt);
}
