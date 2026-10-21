using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels.Notes;
using StockAnalyzer.Avalonia.Views.Notes;
using StockAnalyzer.Core.Models.Notes;
using Xunit;
using static StockAnalyzer.Avalonia.Tests.TestHelpers.NoteTimelineTestFixture;

namespace StockAnalyzer.Avalonia.Tests.Views.Notes;

/// <summary>
/// The real <see cref="NoteCardView"/> with its body <see cref="SelectableTextBlock"/>: mouse selection, Ctrl+C / Ctrl+A, and the
/// three-item context menu (Copy / Apply to Filter / Apply to Search) acting on the selection frozen when the menu opens
/// (docs/sa_ai_context_NotesTextSelection section 5.5). Body "ab #tag cd" lays out as a=0 b=1 space=2 [#tag button]=3
/// space=4 c=5 d=6, end=7 (pinned by NoteCardView_SelectionProbeTests).
/// </summary>
public class NoteCardView_SelectionTests
{
    private const string HashtagBody = "ab #tag cd";

    private static string CreateIsolatedTempDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "sa_note_selection_view_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    private static void Settle()
    {
        for (var i = 0; i < 3; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static Note MakeNote(string body, ImmutableArray<string>? hashtags = null)
    {
        var now = DateTime.Now;
        return new Note(Guid.NewGuid(), body, now, now) { Hashtags = hashtags ?? ImmutableArray<string>.Empty };
    }

    /// <summary>A card whose item has NO selection target (only Copy can be available).</summary>
    private static (Window Window, NoteCardView View, SelectableTextBlock Block) MountDetachedCard(string body, ImmutableArray<string>? hashtags = null)
    {
        var item = new NoteTimelineItemViewModel(
            MakeNote(body, hashtags), false, _ => Task.CompletedTask, _ => Task.CompletedTask, _ => { }, _ => { }, _ => { }, _ => { }, _ => { }, _ => { });
        return Mount(item);
    }

    private static (Window Window, NoteCardView View, SelectableTextBlock Block) Mount(NoteTimelineItemViewModel item)
    {
        var view = new NoteCardView { DataContext = item };
        var window = new Window { Content = view, Width = 600, Height = 400 };
        window.Show();
        Settle();
        return (window, view, view.FindControl<SelectableTextBlock>("BodyTextBlock")!);
    }

    /// <summary>A card of a real timeline that knows tickers 7203.T / 9984.T and the hashtag "tag".</summary>
    private static async Task<(NoteTimelineViewModel Timeline, Window Window, NoteCardView View, SelectableTextBlock Block)> MountTimelineCardAsync(
        string tempDir, string body, ImmutableArray<string>? hashtags = null)
    {
        var marketDataProvider = new FakeMarketDataProvider(availableTickers: new[] { "7203.T", "9984.T" });
        var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir, marketDataProvider: marketDataProvider);
        await noteRepository.CreateAsync(MakeNote(body, hashtags));
        await timeline.LoadAvailableFilterOptionsAsync();
        await timeline.RefreshAsync();
        var (window, view, block) = Mount(timeline.DisplayedNotes.Single());
        return (timeline, window, view, block);
    }

    private static MenuFlyout Flyout(SelectableTextBlock block) => Assert.IsType<MenuFlyout>(block.ContextFlyout);

    private static List<MenuItem> Items(SelectableTextBlock block) => Flyout(block).Items.OfType<MenuItem>().ToList();

    private static Point BodyPoint(Window window, SelectableTextBlock block, double x, double y) =>
        block.TranslatePoint(new Point(x, y), window)!.Value;

    private static void RightClickBody(Window window, SelectableTextBlock block)
    {
        var point = BodyPoint(window, block, 4, 8);
        window.MouseMove(point);
        Settle();
        window.MouseDown(point, MouseButton.Right);
        try
        {
            window.MouseUp(point, MouseButton.Right);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("job loop"))
        {
            // The release has been processed; the headless platform's frozen clock cannot finish the flyout transition.
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    private static async Task<string?> ReadClipboardAsync(Window window)
    {
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();
        return await TopLevel.GetTopLevel(window)!.Clipboard!.TryGetTextAsync();
    }

    // ---- structure ----

    [AvaloniaFact]
    public void BodyBlock_IsASelectableTextBlockWithThreeContextMenuItemsInFixedOrder()
    {
        var (window, _, block) = MountDetachedCard(HashtagBody);
        try
        {
            Assert.Equal(global::Avalonia.Media.Colors.Transparent, ((global::Avalonia.Media.ISolidColorBrush)block.Background!).Color);
            var items = Items(block);
            Assert.Equal(3, items.Count);
            Assert.Equal(LocalizationManager.Instance["Note_Body_ContextMenu_Copy"], items[0].Header);
            Assert.Equal(LocalizationManager.Instance["Note_Hashtag_ApplyToFilter"], items[1].Header);
            Assert.Equal(LocalizationManager.Instance["Note_Hashtag_ApplyToSearch"], items[2].Header);
            Assert.All(items, item => Assert.True(ToolTip.GetShowOnDisabled(item)));
        }
        finally
        {
            window.Close();
        }
    }

    // ---- real mouse selection + keyboard ----

    [AvaloniaFact]
    public async Task MouseDragAcrossTheBody_ThenCtrlC_CopiesTheDrawnTextIncludingTheHashtagButton()
    {
        var (window, _, block) = MountDetachedCard(HashtagBody, ImmutableArray.Create("tag"));
        try
        {
            var clipboard = TopLevel.GetTopLevel(window)!.Clipboard!;
            await clipboard.SetTextAsync("sentinel");

            var from = BodyPoint(window, block, 1, 8);
            var to = BodyPoint(window, block, block.Bounds.Width - 2, 8);
            window.MouseMove(from);
            Settle();
            window.MouseDown(from, MouseButton.Left);
            window.MouseMove(to, RawInputModifiers.LeftMouseButton);
            window.MouseUp(to, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(0, block.SelectionStart);
            Assert.Equal(7, block.SelectionEnd);

            window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control);

            Assert.Equal(HashtagBody, await ReadClipboardAsync(window));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task CtrlA_SelectsTheWholeLayoutIncludingTheLastCharacter_ThenCtrlCCopiesEverything()
    {
        var (window, _, block) = MountDetachedCard(HashtagBody, ImmutableArray.Create("tag"));
        try
        {
            block.Focus();
            Dispatcher.UIThread.RunJobs();

            window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control);

            Assert.Equal(0, block.SelectionStart);
            Assert.Equal(7, block.SelectionEnd);

            var clipboard = TopLevel.GetTopLevel(window)!.Clipboard!;
            await clipboard.SetTextAsync("sentinel");
            window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control);

            Assert.Equal(HashtagBody, await ReadClipboardAsync(window));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task CtrlC_WithPartialSelectionAfterTheButton_CopiesExactlyThoseCharacters()
    {
        var (window, _, block) = MountDetachedCard(HashtagBody, ImmutableArray.Create("tag"));
        try
        {
            var clipboard = TopLevel.GetTopLevel(window)!.Clipboard!;
            await clipboard.SetTextAsync("sentinel");
            block.Focus();
            Dispatcher.UIThread.RunJobs();
            block.SelectionStart = 5;
            block.SelectionEnd = 7;

            window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control);

            Assert.Equal("cd", await ReadClipboardAsync(window));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task CtrlC_WithEmptySelection_LeavesTheClipboardUnchanged()
    {
        var (window, _, block) = MountDetachedCard(HashtagBody, ImmutableArray.Create("tag"));
        try
        {
            var clipboard = TopLevel.GetTopLevel(window)!.Clipboard!;
            await clipboard.SetTextAsync("sentinel");
            block.Focus();
            Dispatcher.UIThread.RunJobs();

            window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control);

            Assert.Equal("sentinel", await ReadClipboardAsync(window));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task CtrlC_PlainBodyWithoutButtons_CopiesTheSelection()
    {
        var (window, _, block) = MountDetachedCard("hello world");
        try
        {
            var clipboard = TopLevel.GetTopLevel(window)!.Clipboard!;
            await clipboard.SetTextAsync("sentinel");
            block.Focus();
            Dispatcher.UIThread.RunJobs();
            block.SelectionStart = 6;
            block.SelectionEnd = 11;

            window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control);

            Assert.Equal("world", await ReadClipboardAsync(window));
        }
        finally
        {
            window.Close();
        }
    }

    // ---- context menu state (snapshot taken when the flyout opens) ----

    [AvaloniaFact]
    public async Task ContextMenu_WithoutSelection_EverythingIsDisabledWithTheEmptySelectionReason()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (_, window, _, block) = await MountTimelineCardAsync(tempDir, HashtagBody, ImmutableArray.Create("tag"));
            try
            {
                RightClickBody(window, block);

                var expected = LocalizationManager.Instance["Note_Selection_Reason_Empty"];
                Assert.All(Items(block), item =>
                {
                    Assert.False(item.IsEnabled);
                    Assert.Equal(expected, ToolTip.GetTip(item));
                });
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

    [AvaloniaFact]
    public void ContextMenu_ItemWithoutTarget_CopyEnabledAndApplyExplainedAsUnavailable()
    {
        var (window, _, block) = MountDetachedCard(HashtagBody, ImmutableArray.Create("tag"));
        try
        {
            block.SelectionStart = 0;
            block.SelectionEnd = 3;

            RightClickBody(window, block);

            var items = Items(block);
            Assert.True(items[0].IsEnabled);
            Assert.Null(ToolTip.GetTip(items[0]));
            Assert.False(items[1].IsEnabled);
            Assert.False(items[2].IsEnabled);
            Assert.Equal(LocalizationManager.Instance["Note_Selection_Reason_Unavailable"], ToolTip.GetTip(items[1]));
            Assert.Equal(LocalizationManager.Instance["Note_Selection_Reason_Unavailable"], ToolTip.GetTip(items[2]));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ContextMenu_MultiLineSelection_CopyEnabledApplyDisabledWithTheLineBreakReason()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (_, window, _, block) = await MountTimelineCardAsync(tempDir, "line1\nline2");
            try
            {
                block.SelectionStart = 0;
                block.SelectionEnd = 11;

                RightClickBody(window, block);

                var items = Items(block);
                var expected = LocalizationManager.Instance["Note_Selection_Reason_MultiLine"];
                Assert.True(items[0].IsEnabled);
                Assert.False(items[1].IsEnabled);
                Assert.False(items[2].IsEnabled);
                Assert.Equal(expected, ToolTip.GetTip(items[1]));
                Assert.Equal(expected, ToolTip.GetTip(items[2]));
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

    [AvaloniaFact]
    public async Task ContextMenu_SelectionWithoutACandidate_SearchEnabledFilterDisabledWithTheNoMatchReason()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (_, window, _, block) = await MountTimelineCardAsync(tempDir, "nothing special here");
            try
            {
                block.SelectionStart = 0;
                block.SelectionEnd = 7;

                RightClickBody(window, block);

                var items = Items(block);
                Assert.True(items[0].IsEnabled);
                Assert.False(items[1].IsEnabled);
                Assert.Equal(LocalizationManager.Instance["Note_Selection_Reason_NoMatch"], ToolTip.GetTip(items[1]));
                Assert.True(items[2].IsEnabled);
                Assert.Null(ToolTip.GetTip(items[2]));
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

    // ---- actions ----

    [AvaloniaFact]
    public async Task ApplyToSearch_FromASelectionAcrossTheHashtagButton_PutsTheDrawnTextIntoTheSearchBox()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, window, _, block) = await MountTimelineCardAsync(tempDir, HashtagBody, ImmutableArray.Create("tag"));
            try
            {
                timeline.SearchText = "old";
                block.SelectionStart = 0;
                block.SelectionEnd = 7;

                RightClickBody(window, block);
                Click(Items(block)[2]);
                await timeline.RefreshAsync();

                Assert.Equal(HashtagBody, timeline.SearchText);
                Assert.Equal(HashtagBody, timeline.FilterCriteria.SearchText);
                Assert.Single(timeline.DisplayedNotes);
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

    [AvaloniaFact]
    public async Task ApplyToFilter_FromTheHashtagButtonAlone_FiltersByThatHashtag()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, window, _, block) = await MountTimelineCardAsync(tempDir, HashtagBody, ImmutableArray.Create("tag"));
            try
            {
                block.SelectionStart = 3;
                block.SelectionEnd = 4;

                RightClickBody(window, block);
                var items = Items(block);
                Assert.True(items[1].IsEnabled);
                Click(items[1]);

                Assert.Equal("tag", timeline.FilterCriteria.SelectedHashtag);
                Assert.Equal("#tag", timeline.SearchBoxDisplayText);
                Assert.Equal(string.Empty, timeline.SearchText);
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

    [AvaloniaFact]
    public async Task ApplyToFilter_FromASelectedTickerInPlainText_AddsTheTickerChip()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, window, _, block) = await MountTimelineCardAsync(tempDir, "watch 7203.T today");
            try
            {
                block.SelectionStart = 6;
                block.SelectionEnd = 12;

                RightClickBody(window, block);
                Click(Items(block)[1]);

                Assert.Equal(new[] { "7203.T" }, timeline.SelectedTickerCodes);
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

    [AvaloniaFact]
    public async Task CopyItem_WritesTheRawSelectionUntrimmed()
    {
        var (window, _, block) = MountDetachedCard(HashtagBody, ImmutableArray.Create("tag"));
        try
        {
            var clipboard = TopLevel.GetTopLevel(window)!.Clipboard!;
            await clipboard.SetTextAsync("sentinel");
            block.SelectionStart = 2;
            block.SelectionEnd = 5;

            RightClickBody(window, block);
            Click(Items(block)[0]);

            Assert.Equal(" #tag ", await ReadClipboardAsync(window));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task DisabledItems_DoNothingWhenClicked()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, window, _, block) = await MountTimelineCardAsync(tempDir, "nothing special here");
            try
            {
                var clipboard = TopLevel.GetTopLevel(window)!.Clipboard!;
                await clipboard.SetTextAsync("sentinel");
                timeline.SearchText = "keep";
                var criteriaBefore = timeline.FilterCriteria;

                RightClickBody(window, block); // no selection: all three disabled
                foreach (var item in Items(block))
                {
                    Click(item);
                }

                Assert.Equal("sentinel", await ReadClipboardAsync(window));
                Assert.Equal("keep", timeline.SearchText);
                Assert.Same(criteriaBefore, timeline.FilterCriteria);
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

    [AvaloniaFact]
    public async Task ActionsUseTheSnapshotOfTheMomentTheMenuOpened_NotALaterSelection()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var (timeline, window, _, block) = await MountTimelineCardAsync(tempDir, "alpha beta gamma");
            try
            {
                block.SelectionStart = 0;
                block.SelectionEnd = 5;
                RightClickBody(window, block);

                // The selection moves (or is cleared) while the menu is open; the click must still act on "alpha".
                block.SelectionStart = 6;
                block.SelectionEnd = 10;
                Click(Items(block)[2]);

                Assert.Equal("alpha", timeline.SearchText);
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

    [AvaloniaFact]
    public void Rebuilding_TheBody_DropsTheSnapshot()
    {
        var (window, view, block) = MountDetachedCard("alpha beta");
        try
        {
            block.SelectionStart = 0;
            block.SelectionEnd = 5;
            RightClickBody(window, block);
            Assert.True(Items(block)[0].IsEnabled);
            try { Flyout(block).Hide(); } catch (Exception) { /* headless flyout transition */ }
            Settle();

            var item = new NoteTimelineItemViewModel(
                MakeNote("other"), false, _ => Task.CompletedTask, _ => Task.CompletedTask, _ => { }, _ => { }, _ => { }, _ => { }, _ => { }, _ => { });
            view.DataContext = item;
            Dispatcher.UIThread.RunJobs();

            // Reopening reads the NEW (empty) selection of the new body, not the old snapshot.
            block.SelectionStart = 0;
            block.SelectionEnd = 0;
            RightClickBody(window, block);
            Assert.False(Items(block)[0].IsEnabled);
        }
        finally
        {
            window.Close();
        }
    }

    // ---- highlight colour (Settings > Notes > Appearance) ----

    private static global::Avalonia.Media.Color BrushColor(SelectableTextBlock block) =>
        Assert.IsAssignableFrom<global::Avalonia.Media.ISolidColorBrush>(block.SelectionBrush).Color;

    [AvaloniaFact]
    public void SelectionBrush_IsTheItemsHighlightColor()
    {
        var item = new NoteTimelineItemViewModel(
            MakeNote(HashtagBody), false, _ => Task.CompletedTask, _ => Task.CompletedTask, _ => { }, _ => { }, _ => { }, _ => { }, _ => { }, _ => { },
            highlightColor: StockAnalyzer.Core.Models.IndicatorColor.FromUInt(0xFF336699));
        var (window, _, block) = Mount(item);
        try
        {
            Assert.Equal(global::Avalonia.Media.Color.FromArgb(0xFF, 0x33, 0x66, 0x99), BrushColor(block));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SelectionBrush_WithoutAnExplicitColor_IsTheDarkBlueDefault()
    {
        var (window, _, block) = MountDetachedCard(HashtagBody);
        try
        {
            Assert.Equal(global::Avalonia.Media.Color.FromArgb(0xFF, 0x26, 0x4F, 0x78), BrushColor(block));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ChangingTheHighlightSetting_ReachesTheRebuiltCards()
    {
        var tempDir = CreateIsolatedTempDirectory();
        try
        {
            var settings = new FakeNotesSettingsManager();
            var marketDataProvider = new FakeMarketDataProvider(availableTickers: new[] { "7203.T" });
            var (timeline, noteRepository, _) = await CreateTimelineAsync(tempDir, marketDataProvider: marketDataProvider, notesSettingsManager: settings);
            await noteRepository.CreateAsync(MakeNote(HashtagBody));
            await timeline.RefreshAsync();

            settings.SetHighlightColor(StockAnalyzer.Core.Models.IndicatorColor.FromRgb(0x10, 0x80, 0xF0));
            await timeline.RefreshAsync();

            var (window, _, block) = Mount(timeline.DisplayedNotes.Single());
            try
            {
                Assert.Equal(global::Avalonia.Media.Color.FromRgb(0x10, 0x80, 0xF0), BrushColor(block));
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
