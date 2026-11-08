using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
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

/// <summary>sa_minimal_fix (Reply connector-line, 3rd/4th follow-up rounds): drives the exact real
/// user path - click Reply, type a Body, click Save (NoteTimelineViewModel.SaveEditCommand, not a
/// direct NoteRepository.CreateAsync call as NoteTimelineViewModelTests does) - and inspects the
/// real, fully mounted NoteTimelineView.axaml (its actual main-timeline ItemsControl with a real
/// sibling card above/below, not the single-card-in-isolation Window NoteCardViewTests uses). 4th
/// round: the timeline now groups a reply directly under its parent (NoteTimelineViewModel.
/// BuildThreadedDisplayOrder) rather than only connecting when the reply happens to land adjacent by
/// CreatedAt, so the parent card - not the reply - is the one that shows the connector line down.</summary>
public class NoteTimelineView_ReplyConnectorLineTests
{
    private static async Task<(NoteTimelineViewModel Timeline, Window Window, NoteTimelineView View, NoteRepository NoteRepository)> MountViewAsync(string tempDir)
    {
        var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir);

        var view = new NoteTimelineView { DataContext = timeline };
        var window = new Window { Content = view, Width = 480, Height = 800 };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        return (timeline, window, view, noteRepository);
    }

    [AvaloniaFact]
    public async Task ReplyingViaRealSaveFlow_ShowsConnectorLine_OnTheParentCardInsideTheRealTimeline()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "sa_note_reply_connector_ui_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var (timeline, window, view, noteRepository) = await MountViewAsync(tempDir);
            try
            {
                var parent = new Note(Guid.NewGuid(), "parent post", DateTime.Now.AddMinutes(-5), DateTime.Now.AddMinutes(-5));
                await noteRepository.CreateAsync(parent);
                await timeline.RefreshAsync();
                Assert.Single(timeline.DisplayedNotes);

                // Real user path: click Reply on the parent's card, type a Body, click Save - not a
                // direct NoteRepository.CreateAsync call.
                timeline.DisplayedNotes[0].ReplyCommand.Execute(null);
                Assert.NotNull(timeline.EditingNote);
                timeline.EditingNote!.Body = "reply post";
                await timeline.SaveEditCommand.ExecuteAsync(null);

                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();

                Assert.Equal(2, timeline.DisplayedNotes.Count);
                // The reply is grouped directly under its parent (BuildThreadedDisplayOrder), not
                // sorted above it despite being the newest post.
                var parentItem = timeline.DisplayedNotes[0];
                var replyItem = timeline.DisplayedNotes[1];
                Assert.Equal(parent.Id, parentItem.Note.Id);
                Assert.Equal(parent.Id, replyItem.Note.ParentNoteId);
                Assert.True(parentItem.ConnectsDownToReplyCard, "ViewModel flag: the parent's very next card is its own reply, so it must be flagged as connected.");
                Assert.False(replyItem.ConnectsDownToReplyCard);

                var cards = view.GetVisualDescendants().OfType<NoteCardView>().ToList();
                Assert.Equal(2, cards.Count);
                var parentCard = cards.Single(c => ReferenceEquals(c.DataContext, parentItem));
                var replyCard = cards.Single(c => ReferenceEquals(c.DataContext, replyItem));

                var line = parentCard.FindControl<Border>("ThreadBottomConnectorLine");
                Assert.NotNull(line);
                Assert.True(line!.IsVisible, "connector line must be visible on the real parent card inside the real timeline ItemsControl.");
                Assert.True(line.DesiredSize.Height > 0, $"connector line DesiredSize.Height must be > 0 (was {line.DesiredSize.Height}).");
                Assert.True(line.Bounds.Height > 0, $"connector line Bounds.Height must be > 0 (was {line.Bounds.Height}).");

                var badge = parentCard.FindControl<Border>("ThreadActionBadge");
                Assert.NotNull(badge);
                Assert.True(badge!.IsVisible, "thread action badge must be visible on the parent card.");

                var replyBadge = replyCard.FindControl<Border>("ThreadActionBadge");
                Assert.NotNull(replyBadge);
                Assert.True(replyBadge!.IsVisible, "thread action badge must be visible on the reply card.");

                var parentCardBorder = parentCard.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("PremiumCard"));
                var replyCardBorder = replyCard.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("PremiumCard"));
                var parentCardBottomY = parentCardBorder.TranslatePoint(new Point(0, parentCardBorder.Bounds.Height), view)!.Value.Y;
                var replyCardTopY = replyCardBorder.TranslatePoint(new Point(0, 0), view)!.Value.Y;
                var lineTopY = line.TranslatePoint(new Point(0, 0), view)!.Value.Y;
                var lineBottomY = line.TranslatePoint(new Point(0, line.Bounds.Height), view)!.Value.Y;

                // The bottom connector line starts below the action badge inside the parent card,
                // and extends past the parent card's bottom edge across the 12px margin gap to reach the reply card's top edge.
                Assert.True(lineBottomY >= replyCardTopY - 1, $"connector line bottom (Y={lineBottomY}) must reach or span to the reply card's top edge (Y={replyCardTopY}).");
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
