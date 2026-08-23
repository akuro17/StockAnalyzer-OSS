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
                // TextBox (body), Grid (ticker + attach/cancel/save row), PendingAttachments list,
                // AttachmentErrors list - the old separate bottom Cancel/Save row no longer exists.
                Assert.Equal(4, composeStack.Children.Count);
                var buttonsRowGrid = Assert.IsType<Grid>(composeStack.Children[1]);

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
}
