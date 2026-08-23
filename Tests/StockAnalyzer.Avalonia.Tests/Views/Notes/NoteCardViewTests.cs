using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StockAnalyzer.Avalonia.ViewModels.Notes;
using StockAnalyzer.Avalonia.Views.Notes;
using StockAnalyzer.Core.Models.Notes;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Notes;

/// <summary>
/// Real-UI regression coverage for the "click a hashtag inside a posted Note's body" fix request
/// (Y:\Temp\sa_implementation_plan_note_hashtag_click.md Task 2): mounts the actual NoteCardView.axaml
/// so the real code-behind-built TextBlock.Inlines (BodyTextBlock) and the inline hashtag Button's
/// MenuFlyout are exercised end to end, not just NoteTimelineItemViewModel.BodySegments in isolation.
/// </summary>
public class NoteCardViewTests
{
    private static readonly Func<NoteTimelineItemViewModel, Task> NoOpAsyncCallback = _ => Task.CompletedTask;
    private static readonly Action<NoteTimelineItemViewModel> NoOpItemCallback = _ => { };
    private static readonly Action<string> NoOpUrlCallback = _ => { };

    private static (NoteTimelineItemViewModel Item, Window Window, NoteCardView View) MountCard(
        Note note, Action<string>? onHashtagFilterRequested = null, Action<string>? onHashtagSearchRequested = null,
        Action<string>? onOpenUrlRequested = null, Action<NoteTimelineItemViewModel>? onOpenDetailRequested = null,
        Action<NoteTimelineItemViewModel>? onQuoteRequested = null, Action<NoteTimelineItemViewModel>? onReplyRequested = null,
        Note? quotedNotePreview = null, bool connectsDownToReplyCard = false,
        int collapsedReplyCount = 0, double connectorLineLength = 60.0, double dashLength = 8.0,
        bool isTombstone = false, bool canReply = true)
    {
        var item = new NoteTimelineItemViewModel(
            note, false,
            NoOpAsyncCallback, NoOpAsyncCallback, NoOpItemCallback, onOpenUrlRequested ?? NoOpUrlCallback, NoOpItemCallback,
            onOpenDetailRequested ?? NoOpItemCallback,
            onHashtagFilterRequested ?? NoOpUrlCallback,
            onHashtagSearchRequested ?? NoOpUrlCallback,
            onQuoteRequested: onQuoteRequested,
            onReplyRequested: onReplyRequested,
            quotedNotePreview: quotedNotePreview,
            connectsDownToReplyCard: connectsDownToReplyCard,
            hasCollapsedRepliesBelow: collapsedReplyCount > 0,
            collapsedReplyCount: collapsedReplyCount,
            connectorLineLength: connectorLineLength,
            dashLength: dashLength,
            isTombstone: isTombstone,
            canReply: canReply);

        var view = new NoteCardView { DataContext = item };
        var window = new Window { Content = view };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        return (item, window, view);
    }

    [AvaloniaFact]
    public void BodyTextBlock_RendersClickableHashtagAsInlineButton_AndPlainTextAsRun()
    {
        var note = new Note(Guid.NewGuid(), "見て #AI 良い", DateTime.Now, DateTime.Now)
        {
            Hashtags = ImmutableArray.Create("ai"),
        };
        var (_, window, view) = MountCard(note);
        try
        {
            var bodyTextBlock = view.FindControl<TextBlock>("BodyTextBlock");
            Assert.NotNull(bodyTextBlock);
            Assert.NotNull(bodyTextBlock!.Inlines);

            var hashtagButtons = bodyTextBlock.Inlines!.OfType<InlineUIContainer>().Select(c => c.Child).OfType<Button>().ToList();
            Assert.Single(hashtagButtons);

            var plainRuns = bodyTextBlock.Inlines!.OfType<Run>().ToList();
            Assert.Contains(plainRuns, r => r.Text == "見て ");
            Assert.Contains(plainRuns, r => r.Text == " 良い");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ClickingHashtagButtonsFlyout_ApplyToFilter_InvokesRequestHashtagFilter()
    {
        var note = new Note(Guid.NewGuid(), "body #earnings text", DateTime.Now, DateTime.Now)
        {
            Hashtags = ImmutableArray.Create("earnings"),
        };
        string? filterRequested = null;
        var (_, window, view) = MountCard(note, onHashtagFilterRequested: tag => filterRequested = tag);
        try
        {
            var bodyTextBlock = view.FindControl<TextBlock>("BodyTextBlock");
            var hashtagButton = bodyTextBlock!.Inlines!.OfType<InlineUIContainer>().Select(c => c.Child).OfType<Button>().Single();

            var flyout = Assert.IsType<MenuFlyout>(hashtagButton.Flyout);
            var menuItems = flyout.ItemsSource!.Cast<MenuItem>().ToList();
            Assert.Equal(2, menuItems.Count);

            menuItems[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

            Assert.Equal("earnings", filterRequested);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ClickingHashtagButtonsFlyout_ApplyToSearch_InvokesRequestHashtagSearch()
    {
        var note = new Note(Guid.NewGuid(), "body #earnings text", DateTime.Now, DateTime.Now)
        {
            Hashtags = ImmutableArray.Create("earnings"),
        };
        string? searchRequested = null;
        var (_, window, view) = MountCard(note, onHashtagSearchRequested: tag => searchRequested = tag);
        try
        {
            var bodyTextBlock = view.FindControl<TextBlock>("BodyTextBlock");
            var hashtagButton = bodyTextBlock!.Inlines!.OfType<InlineUIContainer>().Select(c => c.Child).OfType<Button>().Single();

            var flyout = Assert.IsType<MenuFlyout>(hashtagButton.Flyout);
            var menuItems = flyout.ItemsSource!.Cast<MenuItem>().ToList();

            menuItems[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

            Assert.Equal("earnings", searchRequested);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>sa_implement (Note tab UI polish Task 2, Y:\Temp\sa_implementation_plan_note_ui_polish.md):
    /// View Chart must stay always-visible but icon-only (a PathIcon child, no text Content), while
    /// Edit/Pin/Delete move into the "…" overflow Button's MenuFlyout as three MenuItems bound to
    /// the existing EditCommand/TogglePinnedCommand/DeleteCommand unchanged.</summary>
    [AvaloniaFact]
    public void ActionRow_ViewChartIsIconOnly_AndOverflowMenuListsEditPinDelete()
    {
        var note = new Note(Guid.NewGuid(), "body", DateTime.Now, DateTime.Now);
        var (item, window, view) = MountCard(note);
        try
        {
            var buttons = view.GetVisualDescendants().OfType<Button>().ToList();

            var viewChartButton = Assert.Single(buttons, b => ReferenceEquals(b.Command, item.ViewChartCommand));
            Assert.IsType<PathIcon>(viewChartButton.Content);

            var overflowButton = Assert.Single(buttons, b => b.Flyout is MenuFlyout);
            Assert.IsType<PathIcon>(overflowButton.Content);

            // MenuItem bindings (Command="{Binding EditCommand}" etc.) only resolve once the Flyout
            // is actually shown - its content isn't attached to the DataContext-propagating tree
            // beforehand - so the Flyout must be opened before inspecting bound properties.
            overflowButton.Flyout!.ShowAt(overflowButton);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var menuItems = ((MenuFlyout)overflowButton.Flyout!).Items.OfType<MenuItem>().ToList();

            Assert.Equal(3, menuItems.Count);
            Assert.Contains(menuItems, m => ReferenceEquals(m.Command, item.EditCommand));
            Assert.Contains(menuItems, m => ReferenceEquals(m.Command, item.TogglePinnedCommand));
            Assert.Contains(menuItems, m => ReferenceEquals(m.Command, item.DeleteCommand));

            overflowButton.Flyout!.Hide();
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>sa_implement (Quote/Reply Chain, Task B5): Quote and Reply stay always-visible in the
    /// action row alongside View Chart, since they are the feature's primary engagement actions.</summary>
    [AvaloniaFact]
    public void ActionRow_ContainsAlwaysVisibleQuoteAndReplyButtons()
    {
        var note = new Note(Guid.NewGuid(), "body", DateTime.Now, DateTime.Now);
        var (item, window, view) = MountCard(note);
        try
        {
            var buttons = view.GetVisualDescendants().OfType<Button>().ToList();

            var quoteButton = Assert.Single(buttons, b => ReferenceEquals(b.Command, item.QuoteCommand));
            var replyButton = Assert.Single(buttons, b => ReferenceEquals(b.Command, item.ReplyCommand));
            Assert.IsType<PathIcon>(quoteButton.Content);
            Assert.IsType<PathIcon>(replyButton.Content);
        }
        finally
        {
            window.Close();
        }
    }

    private static string FindSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("StockAnalyzer.sln").Length > 0)
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate StockAnalyzer.sln from " + AppContext.BaseDirectory);
    }

    /// <summary>Extracts the single &lt;path d="..."&gt; attribute from a Material Symbols SVG file
    /// under Assets\Icons\ - same one-shape-per-file source format the icons were originally copied
    /// from (fonts.google.com/icons).</summary>
    private static string ExtractSvgPathData(string svgFileName)
    {
        var svgPath = Path.Combine(FindSolutionRoot(), "StockAnalyzer.Avalonia", "Assets", "Icons", svgFileName);
        var svgContent = File.ReadAllText(svgPath);
        var match = System.Text.RegularExpressions.Regex.Match(svgContent, "<path\\s+d=\"([^\"]+)\"");
        Assert.True(match.Success, $"Could not find a <path d=\"...\"> attribute in {svgPath}");
        return match.Groups[1].Value;
    }

    /// <summary>Extracts the literal PathIcon Data="..." string that immediately follows a
    /// "&lt;!-- iconCommentName --&gt;" marker comment in NoteCardView.axaml's own source text -
    /// reads the raw XAML rather than the mounted PathIcon.Data (an Avalonia Geometry, whose
    /// ToString() returns its runtime type name, not the original path string), so this compares
    /// the exact text a developer would paste in, matching what ExtractSvgPathData reads too.</summary>
    private static string ExtractXamlIconData(string iconCommentName)
    {
        var xamlPath = Path.Combine(FindSolutionRoot(), "StockAnalyzer.Avalonia", "Views", "Notes", "NoteCardView.axaml");
        var xamlContent = File.ReadAllText(xamlPath);
        var match = System.Text.RegularExpressions.Regex.Match(
            xamlContent,
            $"<!--\\s*{System.Text.RegularExpressions.Regex.Escape(iconCommentName)}\\s*-->\\s*<PathIcon Data=\"([^\"]+)\"");
        Assert.True(match.Success, $"Could not find a PathIcon Data=\"...\" right after <!-- {iconCommentName} --> in {xamlPath}");
        return match.Groups[1].Value;
    }

    /// <summary>sa_minimal_fix (icon mismatch round): Assets\Icons\*.svg are reference-only (never
    /// loaded at runtime - NoteCardView.axaml hardcodes each icon's "d" attribute as a literal
    /// PathIcon.Data string), so overwriting a source SVG has no effect on the app unless the
    /// matching Data string is manually re-synced. This locks the two in step: if either the Quote or
    /// Reply icon's source SVG changes without updating NoteCardView.axaml's PathIcon.Data, this test
    /// fails instead of the mismatch silently going unnoticed until a user reports the wrong icon.</summary>
    [Fact]
    public void QuoteAndReplyButtonIcons_MatchTheirSourceSvgFiles()
    {
        Assert.Equal(ExtractSvgPathData("format_quote.svg"), ExtractXamlIconData("format_quote"));
        Assert.Equal(ExtractSvgPathData("reply.svg"), ExtractXamlIconData("reply"));
    }

    [AvaloniaFact]
    public void ClickingQuoteButton_InvokesQuoteCallbackWithThisItem()
    {
        var note = new Note(Guid.NewGuid(), "body", DateTime.Now, DateTime.Now);
        NoteTimelineItemViewModel? quotedItem = null;
        var (item, window, view) = MountCard(note, onQuoteRequested: i => quotedItem = i);
        try
        {
            var quoteButton = Assert.Single(view.GetVisualDescendants().OfType<Button>(), b => ReferenceEquals(b.Command, item.QuoteCommand));

            Assert.True(quoteButton.Command!.CanExecute(null));
            quoteButton.Command.Execute(null);

            Assert.Same(item, quotedItem);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ClickingReplyButton_InvokesReplyCallbackWithThisItem()
    {
        var note = new Note(Guid.NewGuid(), "body", DateTime.Now, DateTime.Now);
        NoteTimelineItemViewModel? repliedItem = null;
        var (item, window, view) = MountCard(note, onReplyRequested: i => repliedItem = i);
        try
        {
            var replyButton = Assert.Single(view.GetVisualDescendants().OfType<Button>(), b => ReferenceEquals(b.Command, item.ReplyCommand));

            Assert.True(replyButton.Command!.CanExecute(null));
            replyButton.Command.Execute(null);

            Assert.Same(item, repliedItem);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>sa_implement (Quote/Reply Chain, Task B6): the mini-preview Border is hidden for an
    /// ordinary Note (Note.QuotedNoteId unset).</summary>
    [AvaloniaFact]
    public void QuotedNotePreviewBorder_HiddenForOrdinaryNote()
    {
        var note = new Note(Guid.NewGuid(), "just a regular post", DateTime.Now, DateTime.Now);
        var (_, window, view) = MountCard(note);
        try
        {
            var previewBorder = view.FindControl<Border>("QuotedNotePreviewBorder");
            Assert.NotNull(previewBorder);
            Assert.False(previewBorder!.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Task B6: a resolved quoted Note renders its excerpt inside the mini-preview and hides the "deleted" placeholder.</summary>
    [AvaloniaFact]
    public void QuotedNotePreviewBorder_ShowsExcerpt_WhenQuotedNoteResolves()
    {
        var quotedNote = new Note(Guid.NewGuid(), "the original post", new DateTime(2026, 8, 1, 9, 0, 0), new DateTime(2026, 8, 1, 9, 0, 0));
        var note = new Note(Guid.NewGuid(), "my reaction", DateTime.Now, DateTime.Now) { QuotedNoteId = quotedNote.Id };
        var (_, window, view) = MountCard(note, quotedNotePreview: quotedNote);
        try
        {
            var previewBorder = view.FindControl<Border>("QuotedNotePreviewBorder");
            Assert.True(previewBorder!.IsVisible);

            var excerptTextBlock = view.FindControl<TextBlock>("QuotedNotePreviewExcerptTextBlock");
            Assert.True(excerptTextBlock!.IsVisible);
            Assert.Equal("the original post", excerptTextBlock.Text);

            var placeholderTextBlock = view.FindControl<TextBlock>("QuotedNoteDeletedPlaceholderTextBlock");
            Assert.False(placeholderTextBlock!.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>sa_minimal_fix (Quote/Reply Chain fix request #2, 2nd round): the reply thread
    /// connector line is visible when the owning ViewModel flagged this card as having its reply
    /// directly below it, hidden otherwise.</summary>
    [AvaloniaFact]
    public void ReplyThreadConnectorLine_VisibilityMirrorsConnectsDownToReplyCard()
    {
        var note = new Note(Guid.NewGuid(), "body", DateTime.Now, DateTime.Now);

        var (_, adjacentWindow, adjacentView) = MountCard(note, connectsDownToReplyCard: true, connectorLineLength: 24.0);
        try
        {
            var line = adjacentView.FindControl<Border>("ReplyThreadConnectorLine")!;
            Assert.True(line.IsVisible);

            // Regression guard (fix request #2 follow-up): a Height + Margin combination that nets to
            // zero (e.g. Height=12 with Margin.Top=-4/Margin.Bottom=-8) collapses DesiredSize.Height to
            // 0, so the StackPanel measures/arranges this card as if the line occupied no space at all.
            // That went unnoticed here (Bounds still reported 12px in this isolated single-card
            // Window) but caused the line to be clipped/misplaced once real cards sit inside the
            // scrolling ItemsControl - assert on DesiredSize, not just Bounds, to catch it here too.
            Assert.True(line.DesiredSize.Height > 0, $"Connector line's DesiredSize.Height must be > 0 (was {line.DesiredSize.Height}) or it gets clipped once mounted inside the real scrolling list.");

            // Regression guard (sa_minimal_fix, "ConnectorLineLength not reflected for non-collapsed
            // threads"): Height used to be hardcoded to 8 regardless of the ConnectorLineLength setting.
            // A bare "> 0" check above would not catch that regression, since 8 is still > 0 - assert
            // the configured 24.0 explicitly reaches the Border's Height property.
            Assert.Equal(24.0, line.Height);
        }
        finally
        {
            adjacentWindow.Close();
        }

        var (_, nonAdjacentWindow, nonAdjacentView) = MountCard(note, connectsDownToReplyCard: false);
        try
        {
            Assert.False(nonAdjacentView.FindControl<Border>("ReplyThreadConnectorLine")!.IsVisible);
        }
        finally
        {
            nonAdjacentWindow.Close();
        }
    }

    /// <summary>sa_minimal_fix ("LineLength/DashLength appear reversed" fix request): reproduces the
    /// reported bug directly on the real mounted Line control. The original implementation
    /// (StartPoint="0,0" EndPoint="0,1" Stretch="Fill" + Height binding) rendered EndPoint as the
    /// literal unit point (0,1) regardless of ConnectorLineLength - Stretch scaled the already-drawn
    /// geometry (dash pattern included) rather than the EndPoint value itself, so asserting
    /// EndPoint.Y == ConnectorLineLength here would have failed before the fix (it would have been
    /// 1, not 40). The fix binds EndPoint directly to a Point in real pixel coordinates.
    ///
    /// Also covers a follow-up sa_minimal_fix ("ConnectorLineLength not reflected in the gap between
    /// cards"): fixing the above by dropping Stretch also dropped the Line's explicit Width/Height,
    /// on the assumption a Stretch=None Line's DesiredSize would naturally follow its EndPoint
    /// geometry. It does not, for a degenerate (zero-width) vertical line - DesiredSize.Height
    /// collapsed to roughly just the 4px Margin regardless of ConnectorLineLength, so the StackPanel
    /// never actually reserved the configured gap (the stroke itself still painted the full length,
    /// unclipped, silently overlapping the next card instead) - same pitfall as the
    /// "Height/Margin nets to a too-small DesiredSize.Height" lesson already documented for
    /// ReplyThreadConnectorLine's own regression test above. Asserting DesiredSize.Height is
    /// approximately ConnectorLineLength (not just "> 0") is what actually catches this: a Line with
    /// no Height binding still has SOME nonzero measured size from its Margin alone, so a bare "> 0"
    /// check would not have caught the regression.</summary>
    [AvaloniaFact]
    public void CollapsedThreadIndicatorLine_EndPointAndDashArray_MatchConfiguredPixelLengths()
    {
        var note = new Note(Guid.NewGuid(), "body", DateTime.Now, DateTime.Now);
        var (_, window, view) = MountCard(note, collapsedReplyCount: 1, connectorLineLength: 40.0, dashLength: 4.0);
        try
        {
            var line = view.FindControl<global::Avalonia.Controls.Shapes.Line>("CollapsedThreadIndicatorLine")!;
            Assert.True(line.IsVisible);
            Assert.Equal(new global::Avalonia.Point(0, 0), line.StartPoint);
            Assert.Equal(new global::Avalonia.Point(0, 40), line.EndPoint);

            // StrokeThickness="2" in XAML: DashLengthToStrokeDashArrayConverter divides the configured
            // 4px DashLength by that same 2px to produce a dash array of [2, 2] dash-array units,
            // which Avalonia then renders as 2*2=4px dashes/gaps - matching the configured pixel value.
            Assert.Equal(new[] { 2.0, 2.0 }, line.StrokeDashArray!);

            // The StackPanel must reserve ~ConnectorLineLength (40px) of layout space for the gap
            // between the two visible cards, not just the 4px Margin - see summary above.
            Assert.True(line.DesiredSize.Height >= 40, $"CollapsedThreadIndicatorLine's DesiredSize.Height must reflect the configured ConnectorLineLength (was {line.DesiredSize.Height}), or the gap between cards silently stays pinned to the Margin regardless of the setting.");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>sa_minimal_fix (Quote/Reply Chain fix request #1): the posted Note's own body (and its
    /// Read-more toggle) must render above the quoted-source mini-preview, not below it - the article
    /// being read takes visual precedence over the thing it references.</summary>
    [AvaloniaFact]
    public void QuotedNotePreviewBorder_RendersBelowBodyTextBlock()
    {
        var quotedNote = new Note(Guid.NewGuid(), "the original post", DateTime.Now, DateTime.Now);
        var note = new Note(Guid.NewGuid(), "my reaction", DateTime.Now, DateTime.Now) { QuotedNoteId = quotedNote.Id };
        var (_, window, view) = MountCard(note, quotedNotePreview: quotedNote);
        try
        {
            var descendants = view.GetVisualDescendants().ToList();
            var bodyIndex = descendants.FindIndex(d => ReferenceEquals(d, view.FindControl<TextBlock>("BodyTextBlock")));
            var previewBorderIndex = descendants.FindIndex(d => ReferenceEquals(d, view.FindControl<Border>("QuotedNotePreviewBorder")));

            Assert.True(bodyIndex >= 0);
            Assert.True(previewBorderIndex >= 0);
            Assert.True(bodyIndex < previewBorderIndex, "BodyTextBlock must appear before QuotedNotePreviewBorder in the visual tree.");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Task B6: when the quoted Note was permanently deleted (no preview resolved), the
    /// mini-preview still shows (IsQuote stays true) but with the "deleted" placeholder instead of an excerpt.</summary>
    [AvaloniaFact]
    public void QuotedNotePreviewBorder_ShowsDeletedPlaceholder_WhenQuotedNoteCouldNotBeResolved()
    {
        var note = new Note(Guid.NewGuid(), "quoting something now gone", DateTime.Now, DateTime.Now) { QuotedNoteId = Guid.NewGuid() };
        var (_, window, view) = MountCard(note, quotedNotePreview: null);
        try
        {
            var previewBorder = view.FindControl<Border>("QuotedNotePreviewBorder");
            Assert.True(previewBorder!.IsVisible);

            var excerptTextBlock = view.FindControl<TextBlock>("QuotedNotePreviewExcerptTextBlock");
            Assert.False(excerptTextBlock!.IsVisible);

            var placeholderTextBlock = view.FindControl<TextBlock>("QuotedNoteDeletedPlaceholderTextBlock");
            Assert.True(placeholderTextBlock!.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>sa_implement (Reply-Leaf Restriction & Deletion Tombstone, Task 6): a tombstone card
    /// (IsTombstone=true) must show the deletion placeholder Border instead of the real PremiumCard -
    /// the two are mutually exclusive via !IsTombstone/IsTombstone.</summary>
    [AvaloniaFact]
    public void TombstoneBorder_VisibleInsteadOfPremiumCard_WhenIsTombstone()
    {
        var note = new Note(Guid.NewGuid(), "deleted body that must not render", DateTime.Now, DateTime.Now);
        var (_, window, view) = MountCard(note, isTombstone: true);
        try
        {
            var tombstoneBorder = view.FindControl<Border>("TombstoneBorder");
            var premiumCardBorder = view.FindControl<Border>("PremiumCardBorder");
            Assert.True(tombstoneBorder!.IsVisible);
            Assert.False(premiumCardBorder!.IsVisible);

            var placeholder = view.FindControl<TextBlock>("TombstonePlaceholderTextBlock");
            Assert.True(placeholder!.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Companion to the above: an ordinary (non-tombstone) card must show the real
    /// PremiumCard and never the tombstone placeholder.</summary>
    [AvaloniaFact]
    public void PremiumCardBorder_VisibleInsteadOfTombstone_ForOrdinaryNote()
    {
        var note = new Note(Guid.NewGuid(), "ordinary body", DateTime.Now, DateTime.Now);
        var (_, window, view) = MountCard(note, isTombstone: false);
        try
        {
            var tombstoneBorder = view.FindControl<Border>("TombstoneBorder");
            var premiumCardBorder = view.FindControl<Border>("PremiumCardBorder");
            Assert.False(tombstoneBorder!.IsVisible);
            Assert.True(premiumCardBorder!.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Regression guard: a tombstone's Reply/Quote commands must be disabled (spec: reply/quote
    /// on a deletion trace is always disallowed), proving the two gating flags combine correctly on
    /// the real ICommand instances the View binds to (not just a hand-constructed VM in isolation).
    /// Not asserted via the mounted Button's IsEnabled here, since those buttons sit inside the
    /// PremiumCard Border which is itself IsVisible=False for a tombstone (see
    /// TombstoneBorder_VisibleInsteadOfPremiumCard_WhenIsTombstone) - Avalonia does not fully realize
    /// template/binding state for controls inside a collapsed subtree in the headless test host, so
    /// IsEnabled there is not a meaningful signal; CanExecute is the strongest true claim for a command
    /// that governs a currently-invisible control.</summary>
    [AvaloniaFact]
    public void ReplyAndQuoteCommands_AreDisabled_WhenIsTombstone()
    {
        var note = new Note(Guid.NewGuid(), "deleted", DateTime.Now, DateTime.Now);
        var (item, window, _) = MountCard(note, isTombstone: true, canReply: true);
        try
        {
            Assert.False(item.ReplyCommand.CanExecute(null));
            Assert.False(item.QuoteCommand.CanExecute(null));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Companion, on a visible (non-tombstone) card: a live Note that already has its one
    /// reply (canReply=false) must have its ReplyCommand disabled while QuoteCommand stays enabled,
    /// since only Reply is leaf-restricted. Asserted via CanExecute() rather than the mounted Button's
    /// IsEnabled - see the note on ReplyAndQuoteCommands_AreDisabled_WhenIsTombstone above for why
    /// IsEnabled is not a reliable signal for this command-gating pattern in the headless test host.</summary>
    [AvaloniaFact]
    public void ReplyCommand_IsDisabled_WhenCanReplyIsFalse_QuoteCommandStaysEnabled()
    {
        var note = new Note(Guid.NewGuid(), "already has a reply", DateTime.Now, DateTime.Now);
        var (item, window, _) = MountCard(note, isTombstone: false, canReply: false);
        try
        {
            Assert.False(item.ReplyCommand.CanExecute(null));
            Assert.True(item.QuoteCommand.CanExecute(null));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>sa_implement (individual Note detail page, Y:\Temp\sa_implementation_plan.md Task
    /// A2): the card's posted-time timestamp is rendered as a Button bound to OpenDetailCommand
    /// (found here by Command reference-equality against the real mounted visual tree, rather than
    /// via a simulated Button.ClickEvent raise - unlike the code-behind-built inline
    /// hashtag/URL buttons elsewhere in this file, this Button's Command is a plain XAML
    /// {Binding}, and Avalonia's Button only runs its bound Command from its own internal
    /// pointer-release handling, not merely in response to the routed Click event being raised),
    /// which relays to the owning NoteTimelineViewModel via the same constructor-callback pattern
    /// as pin/delete/edit/openUrl/viewChart.</summary>
    [AvaloniaFact]
    public void CreatedAtTimestamp_IsClickable_AndInvokesOpenDetailCallback()
    {
        var note = new Note(Guid.NewGuid(), "body", DateTime.Now, DateTime.Now);
        NoteTimelineItemViewModel? openedItem = null;
        var (item, window, view) = MountCard(note, onOpenDetailRequested: i => openedItem = i);
        try
        {
            var timestampButton = Assert.Single(
                view.GetVisualDescendants().OfType<Button>(), b => ReferenceEquals(b.Command, item.OpenDetailCommand));

            Assert.True(timestampButton.Command!.CanExecute(null));
            timestampButton.Command.Execute(null);

            Assert.Same(item, openedItem);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>sa_implement (Note body URL inline linking, Y:\Temp\sa_implementation_plan_note_url_inline.md
    /// Task 3): a URL actually in Note.LinkUrls renders as a Flyout-less inline Button showing the
    /// scheme-stripped display text, and clicking it invokes OpenUrlCommand with the full original
    /// URL - replacing the old separate LinkUrls button row entirely (only 2 buttons - View Chart
    /// and the "…" overflow - exist outside the body's inline links).</summary>
    [AvaloniaFact]
    public void BodyTextBlock_RendersClickableUrlAsInlineButton_AndOpensItViaOpenUrlCommand()
    {
        var note = new Note(Guid.NewGuid(), "見て https://example.com/a 良い", DateTime.Now, DateTime.Now)
        {
            LinkUrls = ImmutableArray.Create("https://example.com/a"),
        };
        string? openedUrl = null;
        var (_, window, view) = MountCard(note, onOpenUrlRequested: url => openedUrl = url);
        try
        {
            var bodyTextBlock = view.FindControl<TextBlock>("BodyTextBlock");
            var urlButton = bodyTextBlock!.Inlines!.OfType<InlineUIContainer>().Select(c => c.Child).OfType<Button>().Single();

            Assert.Null(urlButton.Flyout);
            var content = Assert.IsType<TextBlock>(urlButton.Content);
            Assert.Equal("example.com/a", content.Text);

            urlButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("https://example.com/a", openedUrl);

            // The old separate "Note.LinkUrls" button row is gone: only the (hidden, since this
            // short body never requires collapsing) "Read more" toggle, the clickable CreatedAt
            // timestamp (Feature A, sa_implementation_plan.md Task A2), View Chart, Quote, Reply
            // (Quote/Reply Chain, Task B5), and the "…" overflow button remain outside the body's
            // own inline links - not a second copy of the URL as a button.
            var outsideBodyButtons = view.GetVisualDescendants().OfType<Button>().Except(new[] { urlButton }).ToList();
            Assert.Equal(6, outsideBodyButtons.Count);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>sa_implement Task 3 (fix request): both the hashtag and URL inline links must use
    /// the shared "BodyInlineLink" style class, whose Foreground now matches the body text color
    /// instead of a separate accent color.</summary>
    [AvaloniaFact]
    public void InlineHashtagAndUrlButtons_UseSharedBodyInlineLinkStyleClass()
    {
        var note = new Note(Guid.NewGuid(), "#earnings と https://example.com/a", DateTime.Now, DateTime.Now)
        {
            Hashtags = ImmutableArray.Create("earnings"),
            LinkUrls = ImmutableArray.Create("https://example.com/a"),
        };
        var (_, window, view) = MountCard(note);
        try
        {
            var bodyTextBlock = view.FindControl<TextBlock>("BodyTextBlock");
            var inlineButtons = bodyTextBlock!.Inlines!.OfType<InlineUIContainer>().Select(c => c.Child).OfType<Button>().ToList();

            Assert.Equal(2, inlineButtons.Count);
            Assert.All(inlineButtons, b => Assert.Contains("BodyInlineLink", b.Classes));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void BodyTextBlock_HashtagNotInNoteHashtags_RendersAsPlainTextOnly()
    {
        // A "#word" that never made it into Note.Hashtags (e.g. lost to the 30-tag/50-char
        // extraction cap at save time) must degrade to plain, non-interactive text (fix request:
        // AI-Expected Requirements Inverse case).
        var note = new Note(Guid.NewGuid(), "見て #未保存", DateTime.Now, DateTime.Now);
        var (_, window, view) = MountCard(note);
        try
        {
            var bodyTextBlock = view.FindControl<TextBlock>("BodyTextBlock");
            var hashtagButtons = bodyTextBlock!.Inlines!.OfType<InlineUIContainer>().Select(c => c.Child).OfType<Button>().ToList();

            Assert.Empty(hashtagButtons);
            var renderedText = string.Concat(bodyTextBlock.Inlines!.OfType<Run>().Select(r => r.Text));
            Assert.Equal("見て #未保存", renderedText);
        }
        finally
        {
            window.Close();
        }
    }
}
