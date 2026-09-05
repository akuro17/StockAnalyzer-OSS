using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using static StockAnalyzer.Avalonia.Tests.TestHelpers.NoteTimelineTestFixture;
using StockAnalyzer.Avalonia.ViewModels.Notes;
using StockAnalyzer.Avalonia.Views.Notes;
using StockAnalyzer.Core.Models.Notes;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Notes;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Notes;

/// <summary>sa_minimal_fix (Quote/Reply Chain, thread-display gap fix): mounts the real
/// NoteDetailView.axaml against a fully-wired NoteTimelineViewModel/NoteRepository, built via the
/// shared NoteTimelineTestFixture.CreateTimelineAsync (sa_constraint_check Phase 2), so the actual
/// rendered layout of the ancestor chain / selected Note / replies stack can be inspected end to
/// end.</summary>
public class NoteDetailViewTests
{
    private static string CreateIsolatedTempDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "sa_note_detail_view_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    private static Note MakeNote(string body, DateTime createdAt) => new(Guid.NewGuid(), body, createdAt, createdAt);

    /// <summary>sa_minimal_fix: the ancestor chain's last card, the selected Note's card, and the
    /// replies' first card must sit flush against each other (0px gap), exactly like the main
    /// timeline's single ItemsControl - not the StackPanel-level 8px Spacing that used to apply
    /// between every direct child of NoteDetailView's root StackPanel, including the two ItemsControl
    /// wrappers themselves.</summary>
    [AvaloniaFact]
    public async Task CardsAcrossAncestorChainSelectedNoteAndReplies_AreFlushWithNoGap()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir);
            var root = MakeNote("root", new DateTime(2026, 8, 1));
            await noteRepository.CreateAsync(root);
            var middle = MakeNote("middle", new DateTime(2026, 8, 2)) with { ParentNoteId = root.Id };
            await noteRepository.CreateAsync(middle);
            var reply = MakeNote("reply", new DateTime(2026, 8, 3)) with { ParentNoteId = middle.Id };
            await noteRepository.CreateAsync(reply);
            await timeline.RefreshAsync();

            timeline.DisplayedNotes.Single(n => n.Note.Id == middle.Id).OpenDetailCommand.Execute(null);
            await timeline.DetailThreadLoadTask!;

            var view = new NoteDetailView { DataContext = timeline };
            var window = new Window { Content = view, Width = 500, Height = 800 };
            window.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            try
            {
                var cards = view.GetVisualDescendants().OfType<NoteCardView>().ToList();
                Assert.Equal(3, cards.Count); // root (ancestor) + middle (selected) + reply

                // Control.Bounds is parent-relative, not window/view-relative - root/middle/reply each
                // sit under a different immediate parent (root under the ancestor-chain ItemsControl's
                // panel, middle directly under the outer StackPanel, reply under the replies
                // ItemsControl's panel), so raw Bounds.Y values aren't directly comparable across them.
                // TranslatePoint into the common `view` root first.
                var topsInViewSpace = cards
                    .Select(c => (Card: c, TopY: c.TranslatePoint(new Point(0, 0), view)!.Value.Y, c.Bounds.Height))
                    .OrderBy(t => t.TopY)
                    .ToList();

                var gapAncestorToSelected = topsInViewSpace[1].TopY - (topsInViewSpace[0].TopY + topsInViewSpace[0].Height);
                var gapSelectedToReply = topsInViewSpace[2].TopY - (topsInViewSpace[1].TopY + topsInViewSpace[1].Height);

                Assert.Equal(0, gapAncestorToSelected, precision: 1);
                Assert.Equal(0, gapSelectedToReply, precision: 1);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>sa_improve (共通UI仕様「UIレイアウト幅の最適化およびスクロールバーの表示制御仕様」準拠):
    /// the root ScrollViewer used to be a plain, themeless ScrollViewer - inheriting Avalonia
    /// FluentTheme's default Overlay/Auto-hide scrollbar (collapses to near-invisible outside of
    /// hover, floats over content instead of a dedicated non-overlapping column), unlike every other
    /// ScrollViewer in the Notes tab (NoteTimelineView.axaml's compose panel/timeline), which already
    /// shares the app-wide SidebarScrollViewerTheme. Asserted directly on the ScrollViewer element
    /// (Theme reference + AllowAutoHide property) rather than via rendered ScrollBar bounds, since the
    /// latter requires content to actually overflow first (see DrawingToolSidebarScrollSpecTests for
    /// the same rationale).</summary>
    [AvaloniaFact]
    public async Task ScrollViewer_UsesNonOverlayThemeAndStaysAlwaysVisible()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir);
            var note = MakeNote("body", DateTime.Now);
            await noteRepository.CreateAsync(note);
            await timeline.RefreshAsync();

            timeline.DisplayedNotes.Single(n => n.Note.Id == note.Id).OpenDetailCommand.Execute(null);
            await timeline.DetailThreadLoadTask!;

            var view = new NoteDetailView { DataContext = timeline };
            var window = new Window { Content = view, Width = 500, Height = 800 };
            window.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            try
            {
                var scrollViewer = view.GetVisualDescendants().OfType<ScrollViewer>().Single();

                var expectedTheme = (ControlTheme)Application.Current!.FindResource("SidebarScrollViewerTheme")!;
                Assert.Same(expectedTheme, scrollViewer.Theme);
                Assert.False(scrollViewer.AllowAutoHide,
                    "the detail page's scrollbar must stay permanently visible (AllowAutoHide=False), not only on hover");
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
        }
    }
}
