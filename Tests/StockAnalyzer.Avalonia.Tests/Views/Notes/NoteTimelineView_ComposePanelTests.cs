using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using static StockAnalyzer.Avalonia.Tests.TestHelpers.NoteTimelineTestFixture;
using StockAnalyzer.Avalonia.ViewModels.Notes;
using StockAnalyzer.Avalonia.Views.Notes;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Notes;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Notes;

/// <summary>sa_minimal_fix (Quote/Reply Chain thread-display round, fix request #3): mounts the real
/// NoteTimelineView.axaml and opens the inline compose panel (BeginCreateCommand) to prove the body
/// TextBox is height-capped (internal scrollbar instead of pushing Save off-screen) and that Attach
/// image/Cancel/Save now sit on the same row as the Ticker input, directly below the body - not in a
/// separate row after the pending-attachments/error lists as before.</summary>
public class NoteTimelineView_ComposePanelTests
{
    private static async Task<(NoteTimelineViewModel Timeline, Window Window, NoteTimelineView View)> MountViewAsync(string tempDir)
    {
        var (timeline, _, _) = await CreateTimelineAsync(tempDir);

        var view = new NoteTimelineView { DataContext = timeline };
        var window = new Window { Content = view, Width = 480, Height = 600 };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        return (timeline, window, view);
    }

    [AvaloniaFact]
    public async Task ComposePanel_BodyTextBoxHasMaxHeight_AndTickerButtonsRowSitsDirectlyBelowIt()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "sa_note_compose_panel_ui_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var (timeline, window, view) = await MountViewAsync(tempDir);
            try
            {
                timeline.BeginCreateCommand.Execute(null);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();

                // The body TextBox is the only multiline (AcceptsReturn) TextBox in the whole view -
                // the unified search box's TextBox is single-line - so this uniquely identifies it
                // without needing an x:Name.
                var bodyTextBox = view.GetVisualDescendants().OfType<TextBox>().Single(t => t.AcceptsReturn);
                Assert.Equal(160d, bodyTextBox.MaxHeight);

                var composeStack = Assert.IsType<StackPanel>(bodyTextBox.Parent);
                // TextBox (body), Grid (ticker + attach/cancel/save row), ScrollViewer (wrapping the
                // PendingAttachments/AttachmentErrors lists - sa_minimal_fix, New Note Polish: height-
                // capped with an internal scroll, same MaxHeight pattern as the body TextBox above) -
                // the old separate bottom Cancel/Save row no longer exists.
                Assert.Equal(3, composeStack.Children.Count);
                var buttonsRowGrid = Assert.IsType<Grid>(composeStack.Children[1]);
                Assert.IsType<ScrollViewer>(composeStack.Children[2]);

                var tickerBox = buttonsRowGrid.GetVisualDescendants().OfType<AutoCompleteBox>().Single();
                var actionButtons = buttonsRowGrid.GetVisualDescendants().OfType<Button>().ToList();
                Assert.Equal(3, actionButtons.Count); // Attach image, Cancel, Save

                // Both controls are children of the same single-row Grid (asserted above via
                // buttonsRowGrid), which is the structural guarantee of "same row" - their Y positions
                // may still differ by a few px from template-internal padding/vertical-centering
                // differences between AutoCompleteBox and Button, so allow a small tolerance rather
                // than asserting exact pixel equality.
                var tickerTop = tickerBox.TranslatePoint(new Point(0, 0), view)!.Value.Y;
                var buttonsTop = actionButtons[0].TranslatePoint(new Point(0, 0), view)!.Value.Y;
                Assert.True(Math.Abs(tickerTop - buttonsTop) < 10, $"ticker box (Y={tickerTop}) and action buttons (Y={buttonsTop}) must sit in the same row");

                var bodyBottom = bodyTextBox.TranslatePoint(new Point(0, bodyTextBox.Bounds.Height), view)!.Value.Y;
                Assert.True(buttonsTop >= bodyBottom - 1, $"buttons row (Y={buttonsTop}) must sit at/below the body TextBox's bottom edge (Y={bodyBottom})");
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

    private static byte[] CreateTestPngBytes(int width = 40, int height = 40)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    /// <summary>sa_minimal_fix (New Note Polish, "no scrollbar when compose panel content overflows"):
    /// P (fix request #3, 2nd round comment) => the body TextBox's own MaxHeight="160" prevents ITS
    /// growth from pushing the Ticker/action-buttons row off screen, but nothing capped the
    /// PendingAttachments list added later (Task D, Note Tab Enhancements) - with enough staged
    /// images the WrapPanel grows without bound and nothing below it (errors, or the rest of the
    /// window) stays reachable. Root cause: PendingAttachments/AttachmentErrors sat as bare
    /// ItemsControls directly in the compose StackPanel with no ScrollViewer/height cap of their own.
    /// Reproduces by staging far more attachments than fit in the 480x600 test window and asserting
    /// the attachments area itself never grows past a bounded height - which requires an ancestor
    /// ScrollViewer to exist at all.</summary>
    [AvaloniaFact]
    public async Task ComposePanel_ManyPendingAttachments_StaysHeightCapped_ViaInternalScroll()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "sa_note_compose_panel_ui_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var (timeline, window, view) = await MountViewAsync(tempDir);
            try
            {
                timeline.BeginCreateCommand.Execute(null);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();

                for (int i = 0; i < 20; i++)
                {
                    timeline.EditingNote!.AddPendingAttachment(CreateTestPngBytes(), $"chart{i}.png");
                }
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();

                var pendingAttachmentsItemsControl = view.GetVisualDescendants().OfType<ItemsControl>()
                    .Single(ic => ReferenceEquals(ic.ItemsSource, timeline.EditingNote!.PendingAttachments));

                var scrollViewer = pendingAttachmentsItemsControl.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
                Assert.NotNull(scrollViewer);
                Assert.True(scrollViewer!.Bounds.Height <= 170,
                    $"the pending-attachments area must stay height-capped via an internal scroll, but was {scrollViewer.Bounds.Height}px tall");

                // sa_minimal_fix (follow-up, common UI spec): SidebarScrollViewerTheme's ControlTemplate
                // Grid is ColumnDefinitions="Auto,Auto" - if applying it here made the content column
                // measure with unconstrained width, the WrapPanel would never wrap and would instead lay
                // every attachment out on one very wide row, overflowing the 480px test window instead
                // of wrapping into multiple rows and scrolling vertically. Asserts wrapping still occurs.
                Assert.True(pendingAttachmentsItemsControl.Bounds.Width <= 480,
                    $"the attachments area must still wrap within the window's width, but was {pendingAttachmentsItemsControl.Bounds.Width}px wide");

                // sa_minimal_fix (common UI spec "UIレイアウト幅の最適化およびスクロールバーの表示制御仕様"):
                // the original plain ScrollViewer capped height/scrolled correctly but rendered no
                // visible scrollbar chrome at all - Avalonia FluentTheme's default ScrollBar is an
                // overlay style that stays collapsed/near-invisible outside of hover. Confirms the
                // SidebarScrollViewerTheme's PART_VerticalScrollBar is both present and pinned
                // permanently visible (AllowAutoHide=False) rather than only appearing on hover.
                var verticalScrollBar = scrollViewer.GetVisualDescendants().OfType<ScrollBar>()
                    .SingleOrDefault(sb => sb.Orientation == Orientation.Vertical);
                Assert.NotNull(verticalScrollBar);
                Assert.False(verticalScrollBar!.AllowAutoHide,
                    "the pending-attachments area's vertical scrollbar must stay permanently visible (AllowAutoHide=False), not only on hover");
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

    /// <summary>sa_minimal_fix (New Note Polish, 3rd round - "when the panel showing the Note tab is
    /// short, no scrollbar appears"): P => the two ScrollViewers added in earlier rounds (body TextBox's
    /// built-in one, and the attachments/errors ScrollViewer) each only cap THEIR OWN sub-section - they
    /// let the compose panel avoid growing unbounded internally, but nothing bounds the compose Border
    /// (Grid.Row="2", row height "Auto") itself against the actual available height of whatever hosts
    /// this UserControl. Avalonia Grid's "Auto" row sizing measures its content with an effectively
    /// unconstrained height (by design - "Auto" means "give me your true desired size"), so when the
    /// hosting panel (e.g. a resized dockable bottom-panel tab) is shorter than the compose panel's own
    /// (already internally capped) natural height - Ticker row + up to 160px body + up to 160px
    /// attachments + padding, easily 300px+ - the excess is silently clipped by the host with nothing to
    /// scroll to reach it. Reproduces by hosting the view in a deliberately short (150px) Window and
    /// asserting an outer ScrollViewer exists wrapping the whole compose Border, sized to the actual
    /// available height rather than clipped.</summary>
    [AvaloniaFact]
    public async Task ComposePanel_HostPanelIsShort_StillReachableViaOuterScroll()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "sa_note_compose_panel_ui_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var (timeline, _, _) = await CreateTimelineAsync(tempDir);

            var view = new NoteTimelineView { DataContext = timeline };
            var window = new Window { Content = view, Width = 480, Height = 150 };
            window.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            try
            {
                timeline.BeginCreateCommand.Execute(null);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();

                var composeBorder = view.GetVisualDescendants().OfType<Border>()
                    .Single(b => b.Classes.Contains("PremiumCard") && Equals(Grid.GetRow(b), 2));

                // The Border's own immediate Child must be a ScrollViewer wrapping the WHOLE compose
                // panel (Ticker row + body + attachments together) - not merely an inner ScrollViewer
                // around one sub-section (the attachments area's own ScrollViewer, several levels
                // deeper, would otherwise satisfy a looser "any ScrollViewer exists" query without
                // actually proving the Save/Cancel/Attach row itself stays reachable when the whole
                // panel doesn't fit).
                var outerScrollViewer = Assert.IsType<ScrollViewer>(composeBorder.Child);
                Assert.True(outerScrollViewer.Bounds.Height <= 150,
                    $"the whole compose panel must stay reachable via an outer scroll bounded by the host window's actual height (150px), but was {outerScrollViewer.Bounds.Height}px tall");
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

    /// <summary>sa_minimal_fix (SAで制約確認 remediation, Finding 4): a prior version of
    /// AttachImageButton_Click's multi-file loop (the file picker is AllowMultiple=true) re-read
    /// ComposeBodyTextBox.CaretIndex from the live control on every iteration, assuming it would
    /// advance to track each prior iteration's InsertImagePlaceholder call (a programmatic Body
    /// mutation made from the ViewModel side, not from user typing). A diagnostic repro proved
    /// Avalonia's bound TextBox does NOT advance CaretIndex for such a change - it stays at its
    /// pre-mutation value - so every image beyond the first landed at the same stale position,
    /// inserting BEFORE the previous one and reversing the selected order. The fix (tracking a local
    /// running offset advanced by each inserted token's own length, independent of the live control)
    /// sidesteps that Avalonia characteristic rather than changing it, so this test proves the actual
    /// observable guarantee - multiple images attached in one action land in Body in selection order -
    /// by exercising the identical algorithm AttachImageButton_Click now uses, since the method itself
    /// cannot be driven end-to-end in a headless test (it depends on the real OS file picker).</summary>
    [AvaloniaFact]
    public async Task MultipleImagePlaceholders_InsertedViaRunningOffset_LandInSelectionOrder()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "sa_note_compose_panel_ui_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var (timeline, window, view) = await MountViewAsync(tempDir);
            try
            {
                timeline.BeginCreateCommand.Execute(null);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();

                var composeBodyTextBox = view.FindControl<TextBox>("ComposeBodyTextBox");
                Assert.NotNull(composeBodyTextBox);

                var editingNote = timeline.EditingNote!;
                var firstLocalId = Guid.NewGuid();
                var secondLocalId = Guid.NewGuid();

                // Mirrors AttachImageButton_Click's fixed loop: read CaretIndex once, then advance a
                // local running offset by each inserted token's own length instead of re-reading the
                // live control (which, per the diagnostic above, would stay stale).
                var insertionIndex = composeBodyTextBox!.CaretIndex;
                editingNote.InsertImagePlaceholder(insertionIndex, firstLocalId);
                insertionIndex += NoteImageTokenExtractor.Build(firstLocalId).Length;
                editingNote.InsertImagePlaceholder(insertionIndex, secondLocalId);

                var expectedBody = NoteImageTokenExtractor.Build(firstLocalId) + NoteImageTokenExtractor.Build(secondLocalId);
                Assert.Equal(expectedBody, editingNote.Body);
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
