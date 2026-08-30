using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.TickerList;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Notes;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Notes;

namespace StockAnalyzer.Avalonia.ViewModels.Notes;

/// <summary>Decoded thumbnail plus its original file's full path on disk (fix request: confirm
/// original size via the OS's own image viewer - opening it needs the path, not just the decoded
/// Bitmap).</summary>
public sealed record NoteThumbnailItem(global::Avalonia.Media.Imaging.Bitmap Bitmap, string FilePath);

/// <summary>
/// A single Note as displayed in the timeline, carrying its own session-only expand/collapse state
/// (spec section 5.3). Card-level actions (pin, delete, edit, open a link, apply a body hashtag to
/// the Filter/Search field) are relayed back to the owning <see cref="NoteTimelineViewModel"/> via
/// constructor-supplied callbacks rather than this item reaching into repositories directly, keeping
/// it a thin, easily-templated view wrapper.
/// </summary>
public sealed partial class NoteTimelineItemViewModel : ObservableObject
{
    public Note Note { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayBody))]
    [NotifyPropertyChangedFor(nameof(ToggleLabel))]
    [NotifyPropertyChangedFor(nameof(BodySegments))]
    private bool _isExpanded;

    private readonly Func<NoteTimelineItemViewModel, Task> _onTogglePinnedRequested;
    private readonly Func<NoteTimelineItemViewModel, Task> _onDeleteRequested;
    private readonly Action<NoteTimelineItemViewModel> _onEditRequested;
    private readonly Action<string> _onOpenUrlRequested;
    private readonly Action<NoteTimelineItemViewModel> _onViewChartRequested;
    private readonly Action<NoteTimelineItemViewModel> _onOpenDetailRequested;
    private readonly Action<string> _onHashtagFilterRequested;
    private readonly Action<string> _onHashtagSearchRequested;
    private readonly AttachmentRepository? _attachmentRepository;
    private readonly Action<string>? _onOpenAttachmentRequested;
    private readonly Action<NoteTimelineItemViewModel>? _onQuoteRequested;
    private readonly Action<NoteTimelineItemViewModel>? _onReplyRequested;

    /// <summary>Character/newline "Read more" collapse thresholds (Settings &gt; Notes, default 150/5),
    /// captured at construction time from <see cref="NoteTimelineViewModel"/>'s injected
    /// <see cref="Services.INotesSettingsManager"/> rather than read live, matching how
    /// <see cref="ConnectsDownToReplyCard"/> is likewise a constructor-time snapshot.</summary>
    private readonly int _readMoreMaxCharacters;
    private readonly int _readMoreMaxLines;

    /// <summary>Font size applied to every piece of text this card renders (Settings &gt; Notes,
    /// independent of Settings &gt; Fonts), captured at construction time from
    /// <see cref="Services.INotesSettingsManager.BodyFontSize"/> - same constructor-time-snapshot
    /// rationale as <see cref="ConnectorLineLength"/>.</summary>
    public double BodyFontSize { get; }

    /// <summary>Side length, in pixels, of a rendered attachment thumbnail (Settings &gt; Notes,
    /// "Images" section), captured at construction time from
    /// <see cref="Services.INotesSettingsManager.ThumbnailSizePixels"/> - same constructor-time-snapshot
    /// rationale as <see cref="BodyFontSize"/>.</summary>
    public double ThumbnailSizePixels { get; }

    /// <summary>Whether an attached image renders inline at its original text-cursor position
    /// (<see cref="NoteImageDisplayMode.Inline"/>) or only in the bottom <see cref="Thumbnails"/> row
    /// (<see cref="NoteImageDisplayMode.AttachmentList"/>), captured at construction time from
    /// <see cref="Services.INotesSettingsManager.ImageDisplayMode"/> - same constructor-time-snapshot
    /// rationale as <see cref="ThumbnailSizePixels"/>.</summary>
    public NoteImageDisplayMode ImageDisplayMode { get; }

    /// <summary>Foreground color applied to every piece of text this card renders (Settings &gt; Notes,
    /// independent of Settings &gt; Theme), captured at construction time from
    /// <see cref="Services.INotesSettingsManager.BodyTextColor"/>. A platform-agnostic
    /// <see cref="IndicatorColor"/> (SA_ARCHITECTURE_RULES.md §1 Platform Agnosticism) - bound directly
    /// in XAML via <see cref="StockAnalyzer.Avalonia.Converters.RawColorToBrushConverter"/>'s existing
    /// <c>IndicatorColor</c> case rather than built in code-behind.</summary>
    public IndicatorColor BodyTextColor { get; }

    /// <summary>Background color applied to this card (Settings &gt; Notes, independent of Settings &gt;
    /// Theme), captured at construction time from
    /// <see cref="Services.INotesSettingsManager.BodyBackgroundColor"/>.</summary>
    public IndicatorColor BodyBackgroundColor { get; }

    /// <summary>Foreground color for clickable URL segments (Settings &gt; Notes, part of the
    /// "Appearance" section; no on/off toggle - always an explicit value, defaulting to
    /// <see cref="BodyTextColor"/>), turned into a Brush by <see cref="Views.Notes.NoteCardView"/>'s
    /// code-behind (on the UI thread, where it's actually rendered) via
    /// <c>Avalonia.Media.Color.FromArgb</c>. A platform-agnostic <see cref="IndicatorColor"/> struct
    /// rather than an Avalonia <c>IBrush</c>/<c>Color</c> here for the same "ViewModels stay
    /// platform-agnostic, View converts" rationale as <see cref="BodyTextColor"/>; a raw Avalonia
    /// <c>SolidColorBrush</c> in particular would additionally be unsafe to construct here since it
    /// derives from Avalonia's <c>AvaloniaObject</c>/<c>Animatable</c>, which asserts UI-thread affinity
    /// in its constructor, and <see cref="CreateItemViewModel"/> runs inside
    /// <see cref="RefreshCoreAsync"/>'s dispatcher hop rather than guaranteed-real UI-thread code.</summary>
    public IndicatorColor UrlColor { get; }

    /// <summary>Foreground color for clickable hashtag segments (Settings &gt; Notes); same
    /// always-explicit contract and platform-agnostic-type rationale as <see cref="UrlColor"/>.</summary>
    public IndicatorColor HashtagColor { get; }

    /// <summary>Max thumbnails actually rendered on a card (spec 5.2 / UI spec FR-5 5.2 section):
    /// beyond this, ExtraAttachmentCountText reports the remainder as a "+N" count instead.
    /// sa_implement (Note Thumbnail Limit): configurable via Settings &gt; Notes, "Images" section -
    /// captured at construction time from <see cref="Services.INotesSettingsManager.MaxRenderedThumbnails"/>,
    /// same constructor-time-snapshot contract as <see cref="ImageDisplayMode"/> (a Note already on
    /// screen keeps its own render limit even if the user changes the setting mid-session; the next
    /// refresh picks up the new value).</summary>
    public int MaxRenderedThumbnails { get; }

    /// <summary>
    /// Decoded thumbnails for up to <see cref="MaxRenderedThumbnails"/> attachments, loaded
    /// fire-and-forget from the constructor. Empty (rather than partially filled) whenever
    /// <paramref name="attachmentRepository"/> is null or an attachment fails to resolve/decode -
    /// the View falls back to <see cref="AttachmentCountText"/> in that case (spec section 5.2:
    /// a broken attachment must never crash the card).
    /// </summary>
    public ObservableCollection<NoteThumbnailItem> Thumbnails { get; } = new();

    /// <summary>The same decoded <see cref="NoteThumbnailItem"/> (Bitmap + original FilePath) as
    /// <see cref="Thumbnails"/>, keyed by AttachmentId, so inline rendering
    /// (<see cref="ImageDisplayMode"/> = Inline) can resolve the identical Bitmap - and, critically,
    /// the FilePath an inline image needs to wire up its own "open original" click - instead of
    /// decoding the file a second time. Only ever contains the same up-to-
    /// <see cref="MaxRenderedThumbnails"/> attachments <see cref="Thumbnails"/> does - an inline
    /// token beyond that limit degrades to rendering nothing, same as the bottom row's own "+N"
    /// fallback for the overflow.</summary>
    private readonly Dictionary<Guid, NoteThumbnailItem> _thumbnailsByAttachmentId = new();

    internal IReadOnlyDictionary<Guid, NoteThumbnailItem> ThumbnailsByAttachmentId => _thumbnailsByAttachmentId;

    /// <summary>Non-null while the fire-and-forget thumbnail load kicked off by the constructor is
    /// pending/complete; exposed (internal, via InternalsVisibleTo) purely so tests can await
    /// deterministic completion instead of racing the background load.</summary>
    internal Task? ThumbnailLoadTask { get; }

    public NoteTimelineItemViewModel(
        Note note,
        bool isExpanded,
        Func<NoteTimelineItemViewModel, Task> onTogglePinnedRequested,
        Func<NoteTimelineItemViewModel, Task> onDeleteRequested,
        Action<NoteTimelineItemViewModel> onEditRequested,
        Action<string> onOpenUrlRequested,
        Action<NoteTimelineItemViewModel> onViewChartRequested,
        Action<NoteTimelineItemViewModel> onOpenDetailRequested,
        Action<string> onHashtagFilterRequested,
        Action<string> onHashtagSearchRequested,
        AttachmentRepository? attachmentRepository = null,
        Action<string>? onOpenAttachmentRequested = null,
        Action<NoteTimelineItemViewModel>? onQuoteRequested = null,
        Action<NoteTimelineItemViewModel>? onReplyRequested = null,
        Note? quotedNotePreview = null,
        bool connectsDownToReplyCard = false,
        int readMoreMaxCharacters = NotesSettingsConstants.DefaultReadMoreMaxCharacters,
        int readMoreMaxLines = NotesSettingsConstants.DefaultReadMoreMaxLines,
        IndicatorColor? urlColor = null,
        IndicatorColor? hashtagColor = null,
        bool hasCollapsedRepliesBelow = false,
        int collapsedReplyCount = 0,
        double connectorLineLength = NotesSettingsConstants.DefaultConnectorLineLength,
        double dashLength = NotesSettingsConstants.DefaultDashLength,
        bool isTombstone = false,
        bool canReply = true,
        double bodyFontSize = NotesSettingsConstants.DefaultBodyFontSize,
        IndicatorColor? bodyTextColor = null,
        IndicatorColor? bodyBackgroundColor = null,
        double thumbnailSizePixels = NotesSettingsConstants.DefaultThumbnailSizePixels,
        NoteImageDisplayMode imageDisplayMode = NotesSettingsConstants.DefaultImageDisplayMode,
        int maxRenderedThumbnails = NotesSettingsConstants.DefaultMaxRenderedThumbnails)
    {
        Note = note;
        _readMoreMaxCharacters = readMoreMaxCharacters;
        _readMoreMaxLines = readMoreMaxLines;
        BodyTextColor = bodyTextColor ?? IndicatorColor.FromUInt(NotesSettingsConstants.DefaultBodyTextColorArgb);
        UrlColor = urlColor ?? BodyTextColor;
        HashtagColor = hashtagColor ?? BodyTextColor;
        HasCollapsedRepliesBelow = hasCollapsedRepliesBelow;
        CollapsedReplyCount = collapsedReplyCount;
        ConnectorLineLength = connectorLineLength;
        DashLength = dashLength;
        BodyFontSize = bodyFontSize;
        ThumbnailSizePixels = thumbnailSizePixels;
        ImageDisplayMode = imageDisplayMode;
        MaxRenderedThumbnails = maxRenderedThumbnails;
        BodyBackgroundColor = bodyBackgroundColor ?? IndicatorColor.FromUInt(NotesSettingsConstants.DefaultBodyBackgroundColorArgb);
        IsTombstone = isTombstone;
        CanReply = canReply;
        _isExpanded = isExpanded;
        _onTogglePinnedRequested = onTogglePinnedRequested;
        _onDeleteRequested = onDeleteRequested;
        _onEditRequested = onEditRequested;
        _onOpenUrlRequested = onOpenUrlRequested;
        _onViewChartRequested = onViewChartRequested;
        _onOpenDetailRequested = onOpenDetailRequested;
        _onHashtagFilterRequested = onHashtagFilterRequested;
        _onHashtagSearchRequested = onHashtagSearchRequested;
        _attachmentRepository = attachmentRepository;
        _onOpenAttachmentRequested = onOpenAttachmentRequested;
        _onQuoteRequested = onQuoteRequested;
        _onReplyRequested = onReplyRequested;
        QuotedNotePreview = quotedNotePreview;
        ConnectsDownToReplyCard = connectsDownToReplyCard;

        ThumbnailLoadTask = (_attachmentRepository is not null && Note.AttachmentIds.Length > 0)
            ? LoadThumbnailsAsync()
            : null;
    }

    private async Task LoadThumbnailsAsync()
    {
        await LoadThumbnailsForAsync(Note.AttachmentIds.Take(MaxRenderedThumbnails), isOverflow: false).ConfigureAwait(false);
    }

    /// <summary>True once <see cref="ExpandRemainingThumbnailsAsync"/> has run (sa_implement, Note
    /// Thumbnail Limit Task 2: fix request - clicking the "+N" overflow count should reveal the rest).
    /// Sticky for the lifetime of this card instance - there is no "collapse back" affordance, matching
    /// the request as stated (only "show the rest on click").</summary>
    /// sa_minimal_fix (Inline Mode "+N" expand bug): also notifies BodySegments - NoteCardView's
    /// code-behind only calls RebuildBody() in response to a PropertyChanged(BodySegments) event
    /// (see NoteCardView.axaml.cs's OnViewModelPropertyChanged), so without this, an Inline-mode
    /// card's newly-decoded overflow images never actually redrew even though
    /// ThumbnailsByAttachmentId already had them ready.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExtraAttachmentCountText))]
    [NotifyPropertyChangedFor(nameof(BodySegments))]
    [NotifyCanExecuteChangedFor(nameof(ExpandRemainingThumbnailsCommand))]
    private bool _allThumbnailsExpanded;

    private bool CanExpandRemainingThumbnails() =>
        !AllThumbnailsExpanded && _attachmentRepository is not null && Note.AttachmentIds.Length > MaxRenderedThumbnails;

    /// <summary>Decodes and appends every attachment beyond <see cref="MaxRenderedThumbnails"/> into
    /// <see cref="Thumbnails"/> (same per-attachment decode/IO-failure tolerance as
    /// <see cref="LoadThumbnailsAsync"/>), then marks <see cref="AllThumbnailsExpanded"/> so
    /// <see cref="ExtraAttachmentCountText"/> hides the now-satisfied "+N" indicator - even if some of
    /// the remaining attachments individually failed to decode, matching <see cref="LoadThumbnailsAsync"/>'s
    /// existing "broken attachment must never crash the card" tolerance (spec 5.2) rather than leaving
    /// the button re-clickable to retry.</summary>
    [RelayCommand(CanExecute = nameof(CanExpandRemainingThumbnails))]
    private async Task ExpandRemainingThumbnailsAsync()
    {
        await LoadThumbnailsForAsync(Note.AttachmentIds.Skip(MaxRenderedThumbnails), isOverflow: true).ConfigureAwait(false);
        AllThumbnailsExpanded = true;
    }

    private async Task LoadThumbnailsForAsync(IEnumerable<Guid> attachmentIds, bool isOverflow)
    {
        var inlineAttachmentIds = ImageDisplayMode == NoteImageDisplayMode.Inline
            ? StockAnalyzer.Core.Services.Notes.NoteImageTokenExtractor.Tokenize(Note.Body)
                .Where(t => t.AttachmentId is not null)
                .Select(t => t.AttachmentId!.Value)
                .ToHashSet()
            : null;

        foreach (var attachmentId in attachmentIds)
        {
            try
            {
                var attachment = await _attachmentRepository!.GetByIdAsync(attachmentId).ConfigureAwait(false);
                if (attachment is null)
                {
                    continue;
                }

                var filePath = _attachmentRepository.GetFilePath(attachment);
                if (!System.IO.File.Exists(filePath))
                {
                    continue;
                }

                await using var stream = System.IO.File.OpenRead(filePath);
                var bitmap = new global::Avalonia.Media.Imaging.Bitmap(stream);
                var thumbnailItem = new NoteThumbnailItem(bitmap, filePath);
                _thumbnailsByAttachmentId[attachmentId] = thumbnailItem;

                // When Inline mode is active, images referenced in Body are shown inline and excluded
                // from the initial bottom thumbnail row (preventing duplicate display with body text).
                // When the user clicks "+N" (isOverflow == true), the remaining overflow images
                // are displayed in the bottom thumbnail row as requested.
                var shouldExcludeFromBottomRow = !isOverflow &&
                    inlineAttachmentIds is not null &&
                    inlineAttachmentIds.Contains(attachmentId);

                if (!shouldExcludeFromBottomRow)
                {
                    Thumbnails.Add(thumbnailItem);
                }
            }
            catch
            {
                // Decode/IO failure for this one attachment: skip it and keep the 📎 N fallback
                // rather than letting a corrupted file break the whole card (spec section 5.2).
            }
        }
    }

    /// <summary>
    /// True when the card needs a "Read more"/"Show less" toggle at all (spec section 5.3):
    /// Body exceeds <see cref="_readMoreMaxCharacters"/> characters OR contains more than
    /// <see cref="_readMoreMaxLines"/> newlines (Settings &gt; Notes, default 150/5).
    /// </summary>
    public bool RequiresCollapseToggle =>
        StockAnalyzer.Core.Services.Notes.NoteReadMorePreview.RequiresCollapse(Note.Body, _readMoreMaxCharacters, _readMoreMaxLines);

    /// <summary>The text to actually render: full Body when expanded (or never needed collapsing), else a capped preview.</summary>
    public string DisplayBody =>
        IsExpanded
            ? Note.Body
            : StockAnalyzer.Core.Services.Notes.NoteReadMorePreview.BuildCollapsedText(Note.Body, _readMoreMaxCharacters, _readMoreMaxLines);

    /// <summary>One run of <see cref="DisplayBody"/> as rendered by the View: plain text when all
    /// three are null; a clickable "#tag" occurrence when <see cref="ClickableHashtag"/> is set; a
    /// clickable URL (Text already scheme-stripped/length-capped for display) when
    /// <see cref="ClickableUrl"/> is set; an inline attached image when <see cref="AttachedImageId"/>
    /// is set (Text is empty in that case - the image itself is the content). The three are mutually
    /// exclusive.</summary>
    public sealed record NoteBodySegment(string Text, string? ClickableHashtag, string? ClickableUrl, Guid? AttachedImageId = null);

    /// <summary>
    /// <see cref="DisplayBody"/> split into plain-text, hashtag, URL and inline-image runs for
    /// rendering (fix request: a URL written in the body should itself become the single clickable
    /// representation - scheme-stripped/length-capped like the old separate LinkUrls button row,
    /// which this replaces - instead of being shown twice). <see cref="NoteImageTokenExtractor.Tokenize"/>
    /// runs first (Task E, Note Tab Enhancements) so a <c>[[note-image:{guid}]]</c> placeholder is
    /// carved out before URL/hashtag tokenization ever sees the text; URL tokenization
    /// (<see cref="UrlExtractor.Tokenize"/>) then runs on each remaining plain-text span so a URL's
    /// own '#' fragment (the UrlPattern character class includes '#') is consumed as part of the URL
    /// span; hashtag tokenization (<see cref="HashtagExtractor.Tokenize"/>) then only ever sees the
    /// non-URL remainder, so a literal "#section" inside a URL can never be mistaken for a separate
    /// clickable hashtag. Both the hashtag and URL dimensions apply the same real-vs-truncated guard:
    /// a token only becomes clickable when its normalized form is actually present in
    /// <see cref="Note.LinkUrls"/>/<see cref="Note.Hashtags"/> respectively - a collapsed-preview cut
    /// mid-token, or (for hashtags) the 30-tag/50-char extraction limits at save time, silently
    /// degrades to plain, non-interactive text instead of offering an action that would not actually
    /// match anything. An image token likewise only becomes an <see cref="NoteBodySegment.AttachedImageId"/>
    /// segment when its id is actually present in <see cref="Note.AttachmentIds"/> - a token whose
    /// attachment failed to save (defensive only; <see cref="NoteEditorViewModel"/>'s own Save-time
    /// cleanup already strips these) is dropped entirely rather than rendered.
    /// </summary>
    public IReadOnlyList<NoteBodySegment> BodySegments
    {
        get
        {
            var segments = new List<NoteBodySegment>();

            foreach (var imageToken in NoteImageTokenExtractor.Tokenize(DisplayBody))
            {
                if (imageToken.AttachmentId is { } imageId)
                {
                    if (Note.AttachmentIds.Contains(imageId))
                    {
                        segments.Add(new NoteBodySegment(string.Empty, null, null, imageId));
                    }

                    continue;
                }

                foreach (var urlToken in UrlExtractor.Tokenize(imageToken.Text))
                {
                    if (urlToken.NormalizedUrl is not null && Note.LinkUrls.Contains(urlToken.NormalizedUrl, StringComparer.OrdinalIgnoreCase))
                    {
                        segments.Add(new NoteBodySegment(UrlDisplayFormatter.Format(urlToken.NormalizedUrl), null, urlToken.NormalizedUrl));
                        continue;
                    }

                    foreach (var hashtagToken in HashtagExtractor.Tokenize(urlToken.Text))
                    {
                        segments.Add(new NoteBodySegment(
                            hashtagToken.Text,
                            hashtagToken.NormalizedHashtag is not null && Note.Hashtags.Contains(hashtagToken.NormalizedHashtag, StringComparer.Ordinal)
                                ? hashtagToken.NormalizedHashtag
                                : null,
                            null));
                    }
                }
            }

            return segments;
        }
    }

    /// <summary>Relays a body-hashtag click's "Apply to Filter" choice to the owning
    /// NoteTimelineViewModel (same constructor-callback pattern as pin/delete/edit/openUrl/viewChart).</summary>
    public void RequestHashtagFilter(string hashtag) => _onHashtagFilterRequested(hashtag);

    /// <summary>Relays a body-hashtag click's "Apply to Search" choice to the owning NoteTimelineViewModel.</summary>
    public void RequestHashtagSearch(string hashtag) => _onHashtagSearchRequested(hashtag);

    /// <summary>Empty when there are no attachments, so the View can hide the indicator via
    /// IsNotNullOrEmpty. Used as the fallback when no thumbnail could be decoded (spec 5.2).</summary>
    public string AttachmentCountText => Note.AttachmentIds.Length > 0 ? $"\U0001F4CE {Note.AttachmentIds.Length}" : string.Empty;

    /// <summary>"+N" for attachments beyond the first <see cref="MaxRenderedThumbnails"/>; empty
    /// otherwise (spec 5.2), and also empty once <see cref="AllThumbnailsExpanded"/> (sa_implement,
    /// Note Thumbnail Limit Task 2) - clicking it (<see cref="ExpandRemainingThumbnailsCommand"/>)
    /// loads the rest into <see cref="Thumbnails"/>, at which point the overflow count has nothing
    /// left to report.</summary>
    public string ExtraAttachmentCountText =>
        !AllThumbnailsExpanded && Note.AttachmentIds.Length > MaxRenderedThumbnails
            ? $"+{Note.AttachmentIds.Length - MaxRenderedThumbnails}"
            : string.Empty;

    /// <summary>Label for the "Read more"/"Show less" toggle button (spec section 5.3).</summary>
    public string ToggleLabel => IsExpanded ? "Show less ▲" : "Read more ▼";

    /// <summary>Each Refresh rebuilds this item from the current immutable Note, so no change notification is needed here.</summary>
    public string PinButtonLabel => Note.IsPinned
        ? LocalizationManager.Instance["Note_UnpinButton"]
        : LocalizationManager.Instance["Note_PinButton"];

    /// <summary>The quoted Note's full record, for the Quote mini-preview card (spec section 6).
    /// Resolved by the owning NoteTimelineViewModel's RefreshAsync via a single batched lookup per
    /// distinct QuotedNoteId (not one query per card). Null both when <see cref="Note.QuotedNoteId"/>
    /// is unset and when it references a Note that no longer exists (permanently deleted) - the two
    /// cases are told apart by <see cref="IsQuote"/> vs <see cref="QuotedNoteWasDeleted"/>.</summary>
    public Note? QuotedNotePreview { get; }

    /// <summary>True when this Note quotes another (drives the mini-preview container's visibility),
    /// regardless of whether the quoted Note could still be resolved.</summary>
    public bool IsQuote => Note.QuotedNoteId.HasValue;

    /// <summary>True when this is a quote but the quoted Note could not be resolved (permanently
    /// deleted) - drives a "original post was deleted" placeholder instead of the excerpt.</summary>
    public bool QuotedNoteWasDeleted => IsQuote && QuotedNotePreview is null;

    /// <summary>True when this is a quote AND the quoted Note was successfully resolved - drives the
    /// excerpt+timestamp portion of the mini-preview, as opposed to the "deleted" placeholder.</summary>
    public bool HasQuotedNotePreview => IsQuote && QuotedNotePreview is not null;

    /// <summary>Max characters of the quoted Note's Body shown in the mini-preview before an ellipsis cuts it off.</summary>
    private const int QuotedPreviewExcerptMaxCharacters = 120;

    /// <summary>First <see cref="QuotedPreviewExcerptMaxCharacters"/> characters of the resolved quoted Note's Body, ellipsis-suffixed when truncated. Empty when there is no resolved quoted Note.</summary>
    public string QuotedNotePreviewExcerpt
    {
        get
        {
            if (QuotedNotePreview is not { } quoted)
            {
                return string.Empty;
            }

            return quoted.Body.Length > QuotedPreviewExcerptMaxCharacters
                ? quoted.Body[..QuotedPreviewExcerptMaxCharacters] + "…"
                : quoted.Body;
        }
    }

    /// <summary>True when the very next card below this one in the same displayed list is a direct
    /// reply to this Note (spec section 6: Reply Chain, fix request #2 2nd round) - drives a short
    /// vertical connector line at the bottom of this card linking it down to that reply. The main
    /// timeline groups every reply directly under the Note it replies to (see
    /// <see cref="StockAnalyzer.Core.Services.Notes.NoteThreadOrganizer.BuildThreadedDisplayOrder"/>), so this is true for every
    /// Note that has at least one reply, and again for each of those replies that itself has a
    /// further reply, forming an unbroken chain of connector lines down the whole thread. Always
    /// false in the detail thread's ancestor/reply lists (see
    /// <see cref="NoteTimelineViewModel.LoadDetailThreadAsync"/>), which never show this line.</summary>
    public bool ConnectsDownToReplyCard { get; }

    /// <summary>True when the reply chain below this card was collapsed (Settings &gt; Notes: a
    /// thread exceeding <c>ThreadCollapseThreshold</c> total posts keeps only the first/last
    /// <c>TailVisibleCount</c> cards) - drives a dotted vertical line in place of the omitted
    /// replies, mutually exclusive in practice with <see cref="ConnectsDownToReplyCard"/> (the
    /// card immediately preceding a collapsed segment can't also be directly followed by its real
    /// next reply). By design there is no way to re-expand a collapsed segment from the timeline -
    /// the omitted Notes remain reachable via their own individual detail page (see
    /// <see cref="OpenDetail"/> on whichever ancestor/descendant card is still visible).</summary>
    public bool HasCollapsedRepliesBelow { get; }

    /// <summary>Number of replies omitted by the collapse represented by <see cref="HasCollapsedRepliesBelow"/>; 0 when that flag is false.</summary>
    public int CollapsedReplyCount { get; }

    /// <summary>Height, in pixels, of the dotted vertical line drawn when <see cref="HasCollapsedRepliesBelow"/> is true (Settings &gt; Notes).</summary>
    public double ConnectorLineLength { get; }

    /// <summary>Length, in pixels, of each dash in that vertical line (Settings &gt; Notes).</summary>
    public double DashLength { get; }

    /// <summary>True when this card represents a soft-deleted Note kept only to preserve a connected
    /// thread's continuity ("this article was deleted") rather than a real, readable post - set from
    /// <see cref="StockAnalyzer.Core.Services.Notes.NoteThreadOrganizer.CollectPrunedSubtree"/>'s <c>TombstoneNoteIds</c>. Forces
    /// Reply/Quote unavailable (<see cref="CanExecuteReply"/>/<see cref="CanExecuteQuote"/>)
    /// regardless of <see cref="CanReply"/>, since replying to or quoting a deletion placeholder has
    /// no product meaning.</summary>
    public bool IsTombstone { get; }

    /// <summary>True when this Note has no live (or surviving-tombstone) reply of its own - i.e. it's
    /// the current end of its branch - set from <see cref="StockAnalyzer.Core.Services.Notes.NoteThreadOrganizer.CollectPrunedSubtree"/>'s
    /// <c>LeafNoteIds</c>. This is a purely structural fact independent of <see cref="IsTombstone"/>;
    /// <see cref="CanExecuteReply"/> is what actually combines the two to gate <see cref="ReplyCommand"/>.
    /// Restricting Reply to the thread's leaf (spec: only the last article of the thread may be replied to) also prevents a
    /// second, branching reply from ever being created through the UI, since a Note stops being a leaf
    /// - and so stops being repliable - the moment it gains its one reply.</summary>
    public bool CanReply { get; }

    /// <summary>Tooltip text for <see cref="HasCollapsedRepliesBelow"/>'s dotted line, e.g. "3 replies omitted" (same LocalizationManager.Instance + string.Format pattern as CriteriaFlowItemViewModel.DisplayText).</summary>
    public string CollapsedReplyCountTooltip
    {
        get
        {
            var format = LocalizationManager.Instance["Note_CollapsedThreadIndicator_TooltipFormat"];
            if (string.IsNullOrEmpty(format) || format == "[Note_CollapsedThreadIndicator_TooltipFormat]")
            {
                format = "{0} replies omitted";
            }
            return string.Format(format, CollapsedReplyCount);
        }
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private Task TogglePinnedAsync() => _onTogglePinnedRequested(this);

    [RelayCommand]
    private Task DeleteAsync() => _onDeleteRequested(this);

    [RelayCommand]
    private void Edit() => _onEditRequested(this);

    [RelayCommand]
    private void OpenUrl(string? url)
    {
        if (!string.IsNullOrEmpty(url))
        {
            _onOpenUrlRequested(url);
        }
    }

    /// <summary>Opens one attachment's original file via the OS-associated app (fix request: confirm
    /// the original upload size - no in-app viewer is built, the OS's own viewer shows it instead).</summary>
    [RelayCommand]
    private void OpenAttachmentOriginal(NoteThumbnailItem? item)
    {
        if (item is not null)
        {
            _onOpenAttachmentRequested?.Invoke(item.FilePath);
        }
    }

    /// <summary>True only when this Note has a related ticker - spec section 5.2 disables "View Chart" for ticker-independent Notes.</summary>
    private bool CanViewChart() => !string.IsNullOrWhiteSpace(Note.RelatedTicker);

    [RelayCommand(CanExecute = nameof(CanViewChart))]
    private void ViewChart() => _onViewChartRequested(this);

    /// <summary>Opens this Note's individual detail page (spec: clicking the card's posted-time
    /// timestamp navigates to a single-Note view). Unlike <see cref="ViewChart"/>, always
    /// executable - every Note has a detail page regardless of whether it has a related ticker.</summary>
    [RelayCommand]
    private void OpenDetail() => _onOpenDetailRequested(this);

    /// <summary>Quoting a deletion tombstone has no product meaning (spec: reply/quote on a deletion trace is always disallowed) - the only restriction on Quote, unlike Reply, which is otherwise available on any live Note regardless of thread position.</summary>
    private bool CanExecuteQuote() => !IsTombstone;

    /// <summary>Opens the inline compose panel pre-linked to quote this Note (spec section 6: Quote). No-op when the owning ViewModel did not supply a handler.</summary>
    [RelayCommand(CanExecute = nameof(CanExecuteQuote))]
    private void Quote() => _onQuoteRequested?.Invoke(this);

    /// <summary>Reply is restricted to the leaf of its branch (<see cref="CanReply"/>) and, like Quote,
    /// never available on a deletion tombstone (spec: only the last article of the thread may be replied
    /// to; reply/quote on a deletion trace is always disallowed) - combined here rather than baked into <see cref="CanReply"/> itself, so
    /// <see cref="CanReply"/> stays the pure "is this the current end of its branch" structural fact.</summary>
    private bool CanExecuteReply() => CanReply && !IsTombstone;

    /// <summary>Opens the inline compose panel pre-linked to reply to this Note (spec section 6: Reply Chain). No-op when the owning ViewModel did not supply a handler.</summary>
    [RelayCommand(CanExecute = nameof(CanExecuteReply))]
    private void Reply() => _onReplyRequested?.Invoke(this);
}

/// <summary>
/// The kind of entity a single <see cref="NoteScopeSuggestion"/> represents: which icon to render
/// (matching the Tickers tab's own iconography, sa_analysis §8-10) and which filter-bar target the
/// selection is dispatched to (SelectedTickerCodes / SelectedScopeNode / the single selected tag).
/// </summary>
public enum NoteScopeSuggestionKind
{
    Ticker,
    Watchlist,
    Portfolio,
    Filter,
    Tag,
    Hashtag,

    /// <summary>Dedicated sentinel entry (spec: "未分類") for extracting Notes with no
    /// RelatedTicker at all - a distinct Kind rather than an empty-string TickerFilterText value,
    /// so it can never collide with "no filter selected" or an actual ticker code.</summary>
    NoTicker,
}

/// <summary>
/// A single entry in the unified Ticker/Watchlist/Portfolio/Filter/Tag/Hashtag search suggestion
/// list (replaces the separate scope-menu Flyout and "Tag ▼" menu). Wraps either a plain string
/// (Ticker/Tag/Hashtag) or a <see cref="TickerGroupNode"/> (Watchlist/Portfolio/Filter) behind one
/// common shape so all six kinds can share a single AutoCompleteBox ItemsSource.
/// </summary>
public sealed class NoteScopeSuggestion
{
    public NoteScopeSuggestionKind Kind { get; }
    public string DisplayName { get; }

    /// <summary>Set only for Watchlist/Portfolio/Filter kinds.</summary>
    public TickerGroupNode? Node { get; }

    /// <summary>Set only for Ticker/Tag/Hashtag kinds. For Hashtag this is the raw tag text
    /// (no leading '#', matching Note.Hashtags' own normalized form) even though DisplayName
    /// renders it with a '#' prefix.</summary>
    public string? Value { get; }

    private NoteScopeSuggestion(NoteScopeSuggestionKind kind, string displayName, TickerGroupNode? node, string? value)
    {
        Kind = kind;
        DisplayName = displayName;
        Node = node;
        Value = value;
    }

    public static NoteScopeSuggestion ForTicker(string ticker) => new(NoteScopeSuggestionKind.Ticker, ticker, null, ticker);

    public static NoteScopeSuggestion ForTag(string tag) => new(NoteScopeSuggestionKind.Tag, tag, null, tag);

    /// <summary>Note.Hashtags is already normalized by HashtagExtractor, so <paramref name="hashtag"/>
    /// is stored as-is in Value; only DisplayName gets the '#' prefix for readability, mirroring
    /// NoteCardView.axaml's own hashtag chip formatting.</summary>
    public static NoteScopeSuggestion ForHashtag(string hashtag) => new(NoteScopeSuggestionKind.Hashtag, $"#{hashtag}", null, hashtag);

    public static NoteScopeSuggestion ForNode(NoteScopeSuggestionKind kind, TickerGroupNode node) => new(kind, node.DisplayName, node, null);

    /// <summary>The single always-available "未分類" (no ticker) sentinel entry (fix request:
    /// extract Notes with no RelatedTicker). DisplayName is the localized sentinel word itself -
    /// distinct from any real ticker code by construction, not by string convention.</summary>
    public static NoteScopeSuggestion ForNoTickerSentinel() =>
        new(NoteScopeSuggestionKind.NoTicker, LocalizationManager.Instance["Note_SearchSuggestion_NoTicker"], null, null);

    /// <summary>Icon path data for the suggestion row. Watchlist/Portfolio/Filter reuse
    /// <see cref="TickerGroupIconGeometries"/> - the exact same shapes as TickerListView.axaml's own
    /// TreeDataTemplates. Tag reuses <see cref="SharedIconGeometries.Tag"/>, the same shape
    /// BulkTagEditWindow.axaml's own "TagIconData" resource uses (sa_constraint_check re-audit:
    /// this file previously defined its own visually-near-identical but textually different tag
    /// glyph). Ticker and Hashtag have no equivalent elsewhere in the app, so they keep their own
    /// literal here (SharedIconGeometries.cs: only consolidate once a second consumer needs it).</summary>
    public string IconGeometry => Kind switch
    {
        NoteScopeSuggestionKind.Watchlist => TickerGroupIconGeometries.Watchlist,
        NoteScopeSuggestionKind.Portfolio => TickerGroupIconGeometries.Portfolio,
        NoteScopeSuggestionKind.Filter => TickerGroupIconGeometries.Filter,
        NoteScopeSuggestionKind.Tag => SharedIconGeometries.Tag,
        NoteScopeSuggestionKind.Ticker => "M3.5,18.49L9.5,12.48L13.5,16.48L22,6.92L20.59,5.51L13.5,13.48L9.5,9.48L2,16.99L3.5,18.49Z",
        NoteScopeSuggestionKind.Hashtag => "M9.5,3L8.33,9H14.33L15.5,3H17.5L16.33,9H21V11H15.96L15.09,15.5H20V17.5H14.7L13.5,23.7H11.5L12.7,17.5H6.7L5.5,23.7H3.5L4.7,17.5H0V15.5H5.07L5.94,11H1V9H6.33L7.5,3H9.5M8.7,11L7.83,15.5H13.83L14.7,11H8.7Z",
        _ => string.Empty,
    };

    /// <summary>Short kind label shown after DisplayName in the suggestion row (fix request #2):
    /// the icon alone was reported as hard to tell apart at a glance. Watchlist/Portfolio reuse the
    /// Tickers tab's own type-name keys (sa_constraint_check High #1: same concept, must not be
    /// re-hardcoded); Ticker/Filter/Tag/Hashtag use dedicated Note_SearchSuggestion_TypeName_* keys.</summary>
    public string KindLabel => Kind switch
    {
        NoteScopeSuggestionKind.Ticker => LocalizationManager.Instance["Note_SearchSuggestion_TypeName_Ticker"],
        NoteScopeSuggestionKind.Watchlist => LocalizationManager.Instance["TickerList_TypeName_Watchlist"],
        NoteScopeSuggestionKind.Portfolio => LocalizationManager.Instance["TickerList_TypeName_Portfolio"],
        NoteScopeSuggestionKind.Filter => LocalizationManager.Instance["Note_SearchSuggestion_TypeName_Filter"],
        NoteScopeSuggestionKind.Tag => LocalizationManager.Instance["Note_SearchSuggestion_TypeName_Tag"],
        NoteScopeSuggestionKind.Hashtag => LocalizationManager.Instance["Note_SearchSuggestion_TypeName_Hashtag"],
        _ => string.Empty,
    };

    public override string ToString() => DisplayName;
}

/// <summary>
/// Drives the Ticker Note timeline: loads non-deleted Notes, applies the current filter-bar
/// selection (<see cref="NoteFilterEngine"/>), sorts pinned Notes first (spec section 5.4), tracks
/// each card's expand/collapse state in memory only (spec section 5.3), and hosts the inline
/// create/edit compose panel backed by <see cref="NoteEditorViewModel"/> (Step 90-1-12).
/// </summary>
public partial class NoteTimelineViewModel : ViewModelBase, IDisposable
{
    private bool _isDisposed;
    private readonly NoteRepository _noteRepository;
    private readonly NoteSchemaInitializer _schemaInitializer;
    private readonly TickerMetadataNotesCacheSynchronizer _cacheSynchronizer;
    private readonly Func<NoteEditorViewModel> _editorFactory;
    private readonly IDialogService _dialogService;
    private readonly IWatchlistManager _watchlistManager;
    private readonly IMarketDataProvider _marketDataProvider;
    private readonly AttachmentRepository _attachmentRepository;
    private readonly IDispatcherService _dispatcherService;
    private readonly ITickerStateStore _tickerStateStore;
    private readonly OrphanedAttachmentScanResultHolder _orphanedAttachmentHolder;
    private readonly INotesSettingsManager _notesSettingsManager;
    private readonly IExternalFileOpener _fileOpener;
    private readonly ILogger<NoteTimelineViewModel> _logger;
    private readonly Dictionary<Guid, bool> _expandedState = new();
    private bool _schemaInitialized;

    // Infinite-scroll pagination state (sa_implement, Notes メインタイムライン インフィニットスクロール
    // Task 4). _visibleNoteSource and its lookup tables are the full result of the most recent
    // RefreshCoreAsync - i.e. NoteThreadOrganizer.BuildThreadedDisplayOrder's complete output for the
    // current filter, already excluding thread-collapse-hidden replies - cached so LoadMoreVisibleNotesAsync
    // can reveal more of it without a further DB round trip. All are reset together on every refresh
    // (filter change, keystroke, Settings > Notes change, card action all funnel through
    // RefreshCoreAsync) and are only ever mutated from the UI thread (inside RefreshCoreAsync's
    // dispatcher-posted callback, or from LoadMoreVisibleNotesAsync itself), so no additional
    // synchronization is needed beyond the existing single-threaded UI dispatcher.
    private List<Note> _visibleNoteSource = new();
    private Dictionary<Guid, int> _visibleCollapsedReplyCountAfterNoteId = new();
    private HashSet<Guid> _visibleTombstoneNoteIds = new();
    private HashSet<Guid> _visibleLeafNoteIds = new();
    private Dictionary<Guid, Note> _visibleQuotedNotesById = new();

    /// <summary>How many of <see cref="_visibleNoteSource"/>'s notes are currently materialized into
    /// <see cref="DisplayedNotes"/>. Always a "safe" (non-thread-splitting) boundary - see
    /// <see cref="ComputeSafeRevealCount"/>.</summary>
    private int _loadedVisibleCount;

    // Resolved by RebuildFilterCriteria from the current selections (scope-menu node, registered
    // tag) into ticker sets for NoteFilterEngine. Null = dimension inactive (spec section 5.1: no
    // constraint) - see ResolveTaggedTickers. Single-value, not a set: sa_analysis_report.md §9
    // confirms the unified suggestion box (sa_implement Task 2/3) drops the old multi-select OR
    // behavior in favor of "pick one" like Ticker/Scope already work.
    private string? _selectedRegisteredTag;

    /// <summary>The single hashtag (raw, no leading '#') selected from the unified search
    /// suggestion box - same single-value, mutually-exclusive-with-the-other-three-dimensions
    /// contract as <see cref="_selectedRegisteredTag"/>.</summary>
    private string? _selectedHashtag;

    /// <summary>True when the user picked the "未分類" (NoTicker) sentinel suggestion - same
    /// single-value, mutually-exclusive-with-the-other-dimensions contract as
    /// <see cref="_selectedRegisteredTag"/>/<see cref="_selectedHashtag"/>.</summary>
    private bool _noTickerOnlySelected;

    /// <summary>Registered-tag -&gt; tickers carrying that tag (case-insensitive), built once by
    /// <see cref="LoadAvailableFilterOptionsAsync"/> from IMarketDataProvider metadata (mirrors
    /// ScreenerViewModel.LoadTargetSourcesAsync's tag-scanning approach).</summary>
    private Dictionary<string, ImmutableHashSet<string>> _tagToTickers = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty]
    private NoteFilterCriteria _filterCriteria = NoteFilterCriteria.None;

    /// <summary>Non-null while the inline create/edit compose panel should be shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTimelineListVisible))]
    private NoteEditorViewModel? _editingNote;

    /// <summary>Non-null while the individual Note detail page (opened by clicking a card's posted-time
    /// timestamp, closed by <see cref="BackToListCommand"/>) should be shown in place of the timeline
    /// list. Same null-as-"hidden" contract as <see cref="EditingNote"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTimelineListVisible))]
    private NoteTimelineItemViewModel? _selectedDetailItem;

    /// <summary>True when the posted-Notes timeline list itself should be shown (sa_improve, Note Tab
    /// Polish, Task 4: fix request - don't show the posted timeline while actively composing a New
    /// Note, since it's a distraction from the compose panel). False while either the inline
    /// compose/edit panel (<see cref="EditingNote"/>) or the individual detail page
    /// (<see cref="SelectedDetailItem"/>) is showing instead - the timeline list is mutually exclusive
    /// with both, one at a time.</summary>
    public bool IsTimelineListVisible => EditingNote is null && SelectedDetailItem is null;

    /// <summary>The detail page's thread ancestors (spec section 6: Reply Chain), root-first (oldest
    /// ancestor first, immediate parent last) so the View can render them above <see cref="SelectedDetailItem"/>
    /// in the order they were actually posted. Empty when <see cref="SelectedDetailItem"/> has no
    /// <see cref="Note.ParentNoteId"/>, or while <see cref="SelectedDetailItem"/> is null. Populated
    /// asynchronously by <see cref="LoadDetailThreadAsync"/> after <see cref="SelectedDetailItem"/>
    /// changes - see that method for why this can't simply be resolved inline.</summary>
    public BulkObservableCollection<NoteTimelineItemViewModel> DetailAncestorChain { get; } = new();

    /// <summary>The detail page's direct replies (spec section 6: Reply Chain), oldest-first (same
    /// ordering rationale as <see cref="NoteRepository.GetRepliesAsync"/>) so the View can render them
    /// below <see cref="SelectedDetailItem"/> as a conversation read top-to-bottom.</summary>
    public BulkObservableCollection<NoteTimelineItemViewModel> DetailReplies { get; } = new();

    /// <summary>Count badge for the toolbar's "Trash" icon (fix request: split from the old single
    /// "Trash" button into two icon+count buttons). Recomputed on every <see cref="RefreshCoreAsync"/>
    /// from <c>_noteRepository.CountDeletedAsync()</c>, same dispatcher-posted pattern as
    /// <see cref="AvailableHashtags"/>.</summary>
    [ObservableProperty]
    private int _deletedNotesCount;

    /// <summary>Count badge for the toolbar's "Orphaned Files" icon (fix request). Read from
    /// <see cref="OrphanedAttachmentScanResultHolder"/> (the app-startup scan result; this never
    /// triggers a re-scan itself, same policy as NoteTrashViewModel's own Orphaned tab) on every
    /// <see cref="RefreshCoreAsync"/> so the badge picks up the scan's result even if it completes
    /// after this ViewModel was constructed.</summary>
    [ObservableProperty]
    private int _orphanedFilesCount;

    // Bindable filter-bar inputs. Each setter rebuilds FilterCriteria, which (via
    // OnFilterCriteriaChanged) triggers a reactive refresh.
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Tickers accumulated by repeatedly picking Ticker suggestions from the unified
    /// search box (fix request: multi-Ticker OR filter, chip UI) - replaces the old single-value
    /// TickerFilterText. Cleared whenever any other dimension (Watchlist/Portfolio/Filter/Tag/
    /// Hashtag/NoTicker) becomes active, same mutual-exclusivity contract as before.</summary>
    public ObservableCollection<string> SelectedTickerCodes { get; } = new();

    [ObservableProperty]
    private DateTimeOffset? _periodStart;

    [ObservableProperty]
    private DateTimeOffset? _periodEnd;

    /// <summary>The unified scope-menu selection (replaces the old separate All/Watchlist/Portfolio
    /// buttons): a single Watchlist/Portfolio/Filter node, or null/AllTickersNode for "All Tickers"
    /// (no ticker-scope constraint). Mirrors the Tickers tab's own tree via <see cref="AvailableScopeNodes"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchBoxDisplayText))]
    private TickerGroupNode? _selectedScopeNode;

    /// <summary>The same node tree (All Tickers / Watchlists / Portfolios / Filters, same order) shown
    /// by the Tickers tab's left column, reused read-only via <see cref="ITickerStateStore"/> so the
    /// unified scope menu never duplicates that structure or its Watchlist/Portfolio/Filter data.</summary>
    public IEnumerable<TickerGroupNode> AvailableScopeNodes => _tickerStateStore.Groups;

    /// <summary>Distinct registered-tag values (TickerMetadata.Tag, comma-split) offered by the
    /// "Registered Tag" dropdown (spec 5.1). BulkObservableCollection (sa_constraint_check Phase 2:
    /// CODE_REVIEW_GUIDELINES.md "Bulk List Optimization") since this can span every distinct tag
    /// across the whole ticker universe; populated via ReplaceRange for a single Reset notification
    /// instead of one CollectionChanged per tag.</summary>
    public BulkObservableCollection<string> AvailableRegisteredTags { get; } = new();

    /// <summary>Distinct values of <see cref="Note.Hashtags"/> across every currently active
    /// (non-deleted) Note, offered by the unified suggestion box's "Hashtag" entries. Unlike
    /// <see cref="AvailableRegisteredTags"/> (scanned once from ticker metadata), this is recomputed
    /// on every <see cref="RefreshCoreAsync"/> from the same activeNotes list already fetched for
    /// display - new hashtags become selectable as soon as a Note is saved, with no extra I/O.
    /// BulkObservableCollection (sa_constraint_check Phase 2) for the same bulk-list reason as
    /// <see cref="AvailableRegisteredTags"/>.</summary>
    public BulkObservableCollection<string> AvailableHashtags { get; } = new();

    /// <summary>Every known ticker symbol, for the "Ticker" filter AutoCompleteBox - same source
    /// (IMarketDataProvider.GetAvailableTickersAsync) and suggestion UX as MainWindow's own Symbol
    /// AutoCompleteBox (fix request #3). BulkObservableCollection (sa_constraint_check Phase 2): a
    /// full market's ticker universe can be thousands of symbols, and CODE_REVIEW_GUIDELINES.md
    /// explicitly names "Ticker Lists" as the canonical case requiring ReplaceRange over individual
    /// Add() calls.</summary>
    public BulkObservableCollection<string> AvailableTickers { get; } = new();

    /// <summary>Composite of <see cref="AvailableTickers"/>, the Watchlist/Portfolio/Filter nodes
    /// flattened out of <see cref="AvailableScopeNodes"/> (CategoryNode/AllTickersNode/ActionNode
    /// excluded - they are not individually selectable scopes), <see cref="AvailableRegisteredTags"/>
    /// and <see cref="AvailableHashtags"/>, for the unified search suggestion box that replaces the
    /// separate scope-menu and Tag ▼ menu. Rebuilt whenever any source collection finishes loading or
    /// changes (<see cref="RebuildAvailableSearchSuggestions"/>). BulkObservableCollection
    /// (sa_constraint_check Phase 2) since it inherits AvailableTickers' full size plus every scope
    /// node, registered tag and hashtag.</summary>
    public BulkObservableCollection<NoteScopeSuggestion> AvailableSearchSuggestions { get; } = new();

    /// <summary>What the unified search box's Text should show for the currently active selection:
    /// the active Watchlist/Portfolio/Filter's name, else the active tag, else the NoTicker
    /// sentinel label, else empty when nothing is selected ("All Tickers") - selected Tickers no
    /// longer show here (fix request: multi-Ticker chip UI, Task F) since they render as removable
    /// chips below the box instead, leaving the box itself free for the next pick. Recomputed via
    /// <see cref="NotifyPropertyChangedFor"/> on SelectedScopeNode and an explicit raise from
    /// <see cref="SelectSuggestion"/>/<see cref="ClearFilters"/> for the other dimensions, which have
    /// no backing ObservableProperty of their own.</summary>
    public string SearchBoxDisplayText =>
        SelectedScopeNode is not null and not AllTickersNode ? SelectedScopeNode.DisplayName
        : !string.IsNullOrEmpty(_selectedHashtag) ? $"#{_selectedHashtag}"
        : _selectedRegisteredTag is not null ? _selectedRegisteredTag
        : _noTickerOnlySelected ? LocalizationManager.Instance["Note_SearchSuggestion_NoTicker"]
        : string.Empty;

    /// <summary>The currently visible Notes, pinned-first then by CreatedAt descending within each
    /// group. BulkObservableCollection (sa_constraint_check Phase 2) since a full refresh rebuilds
    /// every visible card at once.</summary>
    public BulkObservableCollection<NoteTimelineItemViewModel> DisplayedNotes { get; } = new();

    /// <summary>True while <see cref="LoadMoreVisibleNotesAsync"/> is actively appending the next
    /// infinite-scroll page - guards against a re-entrant scroll trigger starting a second, overlapping
    /// load before the first has updated <see cref="_loadedVisibleCount"/>.</summary>
    [ObservableProperty]
    private bool _isLoadingMoreNotes;

    /// <summary>True when <see cref="_visibleNoteSource"/> has more notes beyond what's currently in
    /// <see cref="DisplayedNotes"/> - i.e. whether an infinite-scroll trigger should call
    /// <see cref="LoadMoreVisibleNotesAsync"/> at all. Recomputed every time <see cref="_loadedVisibleCount"/>
    /// changes (initial load in <see cref="RefreshCoreAsync"/>, or a subsequent page in
    /// <see cref="LoadMoreVisibleNotesAsync"/>).</summary>
    [ObservableProperty]
    private bool _hasMoreVisibleNotes;

    public NoteTimelineViewModel(
        NoteRepository noteRepository,
        NoteSchemaInitializer schemaInitializer,
        TickerMetadataNotesCacheSynchronizer cacheSynchronizer,
        Func<NoteEditorViewModel> editorFactory,
        IDialogService dialogService,
        IWatchlistManager watchlistManager,
        IMarketDataProvider marketDataProvider,
        AttachmentRepository attachmentRepository,
        IDispatcherService dispatcherService,
        ITickerStateStore tickerStateStore,
        OrphanedAttachmentScanResultHolder orphanedAttachmentHolder,
        INotesSettingsManager notesSettingsManager,
        IExternalFileOpener? fileOpener = null,
        ILogger<NoteTimelineViewModel>? logger = null)
    {
        _noteRepository = noteRepository;
        _schemaInitializer = schemaInitializer;
        _cacheSynchronizer = cacheSynchronizer;
        _editorFactory = editorFactory;
        _dialogService = dialogService;
        _watchlistManager = watchlistManager;
        _marketDataProvider = marketDataProvider;
        _attachmentRepository = attachmentRepository;
        _dispatcherService = dispatcherService;
        _tickerStateStore = tickerStateStore;
        _orphanedAttachmentHolder = orphanedAttachmentHolder;
        _notesSettingsManager = notesSettingsManager;
        _fileOpener = fileOpener ?? new ExternalFileOpener();
        _logger = logger ?? NullLogger<NoteTimelineViewModel>.Instance;

        // sa_minimal_fix (Notes-only Settings, ReadMoreThreshold fix request #1): CreateItemViewModel
        // only ever reads _notesSettingsManager's current values when it builds a NEW
        // NoteTimelineItemViewModel, and each item then caches them for its own lifetime (see
        // NoteTimelineItemViewModel's _readMoreMaxCharacters/_readMoreMaxLines fields) - so without
        // this subscription, changing a Settings > Notes value never reached the already-rendered
        // timeline until some unrelated action (search text, editing a Note) happened to trigger
        // another RefreshAsync.
        _notesSettingsManager.PropertyChanged += OnNotesSettingsManagerPropertyChanged;

        // Default selection = "All Tickers" (fix request #2): no ticker-scope constraint.
        _selectedScopeNode = _tickerStateStore.Groups.FirstOrDefault(n => n is AllTickersNode);

        _ = RefreshAsync();
        _ = LoadAvailableFilterOptionsAsync();
    }

    /// <summary>
    /// Populates the registered-tag dropdown option list and the Ticker AutoCompleteBox's suggestion
    /// source (spec section 5.1). Registered tags require scanning every candidate ticker's
    /// TickerMetadata via IMarketDataProvider (same approach as
    /// ScreenerViewModel.LoadTargetSourcesAsync), so this runs fire-and-forget from the constructor
    /// rather than blocking the initial Note load. The unified scope menu's node tree
    /// (<see cref="AvailableScopeNodes"/>) is owned by <see cref="ITickerStateStore"/>, not this
    /// method - the Tickers tab keeps it in sync independently.
    /// </summary>
    public async Task LoadAvailableFilterOptionsAsync(CancellationToken ct = default)
    {
        var profiles = _watchlistManager.GetAllProfiles();

        var availableTickers = await _marketDataProvider.GetAvailableTickersAsync().ConfigureAwait(false);

        // ConfigureAwait(false) above resumed this method on a background thread, so the
        // ObservableCollection bound to the filter bar's Ticker AutoCompleteBox must be mutated via
        // the dispatcher (same pattern as ChartViewModel's ticker-list load) rather than directly -
        // mutating it here left the suggestion list never reliably reaching the UI (fix request #1,
        // 2nd round). Static lambda + state tuple (sa_constraint_check Phase 2, SA_RENDERING_PERFORMANCE.md
        // "Dispatcher Synchronization"): avoids the closure allocation a `() => AvailableTickers.ReplaceRange(availableTickers)`
        // capture of `this`/`availableTickers` would create.
        _dispatcherService.Post(static state => state.Self.AvailableTickers.ReplaceRange(state.Tickers), (Self: this, Tickers: availableTickers));

        var candidateTickers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ticker in availableTickers)
        {
            candidateTickers.Add(ticker);
        }
        foreach (var profile in profiles)
        {
            foreach (var item in profile.Items)
            {
                candidateTickers.Add(item.Ticker);
            }
        }

        var tagToTickers = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var ticker in candidateTickers)
        {
            ct.ThrowIfCancellationRequested();
            var meta = await _marketDataProvider.GetMetadataAsync(ticker).ConfigureAwait(false);
            if (string.IsNullOrEmpty(meta.Tag))
            {
                continue;
            }

            foreach (var rawTag in meta.Tag.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var tag = rawTag.Trim();
                if (tag.Length == 0)
                {
                    continue;
                }

                if (!tagToTickers.TryGetValue(tag, out var tickers))
                {
                    tickers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    tagToTickers[tag] = tickers;
                }

                tickers.Add(ticker);
            }
        }

        _tagToTickers = tagToTickers.ToDictionary(kv => kv.Key, kv => kv.Value.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

        AvailableRegisteredTags.ReplaceRange(_tagToTickers.Keys.OrderBy(t => t, StringComparer.OrdinalIgnoreCase));

        _dispatcherService.Post(static self => self.RebuildAvailableSearchSuggestions(), this);
    }

    private static NoteScopeSuggestionKind? ClassifyScopeNodeKind(TickerGroupNode node) => node switch
    {
        WatchlistNode => NoteScopeSuggestionKind.Watchlist,
        PortfolioNode => NoteScopeSuggestionKind.Portfolio,
        FilterNode => NoteScopeSuggestionKind.Filter,
        _ => null,
    };

    /// <summary>Walks the Tickers-tab node tree (CategoryNode/AllTickersNode wrap children, FilterNode
    /// may itself be nested under a Watchlist/Portfolio/CategoryNode) and yields one suggestion per
    /// Watchlist/Portfolio/Filter node encountered, in tree order. ActionNode ("Create New...") and
    /// AllTickersNode/CategoryNode themselves are never individually selectable scopes and are skipped.</summary>
    private static IEnumerable<NoteScopeSuggestion> FlattenScopeNodeSuggestions(IEnumerable<TickerGroupNode> nodes)
    {
        foreach (var node in nodes)
        {
            var kind = ClassifyScopeNodeKind(node);
            if (kind is not null)
            {
                yield return NoteScopeSuggestion.ForNode(kind.Value, node);
            }

            if (node.Children is { } children)
            {
                foreach (var childSuggestion in FlattenScopeNodeSuggestions(children))
                {
                    yield return childSuggestion;
                }
            }
        }
    }

    /// <summary>Rebuilds <see cref="AvailableSearchSuggestions"/> from the current contents of
    /// <see cref="AvailableTickers"/>, <see cref="AvailableScopeNodes"/>,
    /// <see cref="AvailableRegisteredTags"/> and <see cref="AvailableHashtags"/>. Must run on the UI thread (mutates a UI-bound
    /// ObservableCollection) - callers invoke this via <see cref="_dispatcherService"/>. Builds the
    /// full list first and applies it via a single <c>ReplaceRange</c> (sa_constraint_check Phase 2)
    /// rather than one CollectionChanged notification per ticker/node/tag.</summary>
    private void RebuildAvailableSearchSuggestions()
    {
        var suggestions = new List<NoteScopeSuggestion> { NoteScopeSuggestion.ForNoTickerSentinel() };

        foreach (var ticker in AvailableTickers)
        {
            suggestions.Add(NoteScopeSuggestion.ForTicker(ticker));
        }

        // Watchlist/Portfolio/Filter profiles are not required to have unique names (fix request
        // #1): two Watchlists both named "List" must appear as a single suggestion, not two
        // independent ones. Kind+DisplayName is the merge key; ResolveScopeTickers resolves the
        // union of every node sharing that key regardless of which one this representative wraps.
        suggestions.AddRange(FlattenScopeNodeSuggestions(AvailableScopeNodes)
            .GroupBy(s => (s.Kind, s.DisplayName))
            .Select(g => g.First()));

        foreach (var tag in AvailableRegisteredTags)
        {
            suggestions.Add(NoteScopeSuggestion.ForTag(tag));
        }

        foreach (var hashtag in AvailableHashtags)
        {
            suggestions.Add(NoteScopeSuggestion.ForHashtag(hashtag));
        }

        AvailableSearchSuggestions.ReplaceRange(suggestions);
    }

    /// <summary>Restores the Scope/Tag/Hashtag/NoTicker dimensions to their inactive ("All Tickers")
    /// state without touching SearchText/period/SelectedTickerCodes (fix request #6): the unified
    /// search box's Text is OneWay-bound to <see cref="SearchBoxDisplayText"/>, so when the user
    /// manually deletes the box's typed text back to empty, nothing otherwise pushes that back into
    /// the ViewModel - the last applied filter stayed active even though the box looked empty.
    /// Ticker chips (<see cref="SelectedTickerCodes"/>) are a separate UI element from the box's own
    /// text (fix request: multi-Ticker chip UI, Task F) and are intentionally left untouched here -
    /// only <see cref="RemoveTickerFilter"/> or <see cref="ClearFilters"/> removes a chip.</summary>
    public void ClearActiveSuggestionFilter()
    {
        var allTickersNode = _tickerStateStore.Groups.FirstOrDefault(n => n is AllTickersNode);
        if (ReferenceEquals(SelectedScopeNode, allTickersNode) &&
            _selectedRegisteredTag is null && _selectedHashtag is null && !_noTickerOnlySelected)
        {
            return;
        }

        SelectedScopeNode = allTickersNode;
        _selectedRegisteredTag = null;
        _selectedHashtag = null;
        _noTickerOnlySelected = false;
        OnPropertyChanged(nameof(SearchBoxDisplayText));
        RebuildFilterCriteria();
    }

    /// <summary>
    /// Applies a suggestion picked from the unified search box: routes the selection to whichever of
    /// SelectedTickerCodes/SelectedScopeNode/selected-registered-tag/selected-hashtag/NoTicker
    /// matches its Kind, and resets the other dimensions to their inactive state so only one
    /// dimension is ever active from a suggestion pick at a time (sa_analysis_report.md §6 Converse
    /// requirement) - except Ticker, which accumulates into <see cref="SelectedTickerCodes"/> as a
    /// removable chip instead of replacing a single value (fix request: multi-Ticker OR filter).
    /// </summary>
    public void SelectSuggestion(NoteScopeSuggestion suggestion)
    {
        var allTickersNode = _tickerStateStore.Groups.FirstOrDefault(n => n is AllTickersNode);

        switch (suggestion.Kind)
        {
            case NoteScopeSuggestionKind.Ticker:
                if (suggestion.Value is { } tickerCode && !SelectedTickerCodes.Contains(tickerCode, StringComparer.OrdinalIgnoreCase))
                {
                    SelectedTickerCodes.Add(tickerCode);
                }
                SelectedScopeNode = allTickersNode;
                _selectedRegisteredTag = null;
                _selectedHashtag = null;
                _noTickerOnlySelected = false;
                break;

            case NoteScopeSuggestionKind.Watchlist:
            case NoteScopeSuggestionKind.Portfolio:
            case NoteScopeSuggestionKind.Filter:
                SelectedScopeNode = suggestion.Node;
                SelectedTickerCodes.Clear();
                _selectedRegisteredTag = null;
                _selectedHashtag = null;
                _noTickerOnlySelected = false;
                break;

            case NoteScopeSuggestionKind.Tag:
                _selectedRegisteredTag = suggestion.Value;
                SelectedTickerCodes.Clear();
                SelectedScopeNode = allTickersNode;
                _selectedHashtag = null;
                _noTickerOnlySelected = false;
                break;

            case NoteScopeSuggestionKind.Hashtag:
                _selectedHashtag = suggestion.Value;
                SelectedTickerCodes.Clear();
                SelectedScopeNode = allTickersNode;
                _selectedRegisteredTag = null;
                _noTickerOnlySelected = false;
                break;

            case NoteScopeSuggestionKind.NoTicker:
                _noTickerOnlySelected = true;
                SelectedTickerCodes.Clear();
                SelectedScopeNode = allTickersNode;
                _selectedRegisteredTag = null;
                _selectedHashtag = null;
                break;

            default:
                // Anti-Ambiguity (sa_constraint_check Low #1): fail loudly rather than silently
                // no-op if NoteScopeSuggestionKind ever grows another value without this switch
                // being updated to handle it.
                throw new ArgumentOutOfRangeException(nameof(suggestion), suggestion.Kind, $"Unhandled {nameof(NoteScopeSuggestionKind)} value.");
        }

        OnPropertyChanged(nameof(SearchBoxDisplayText));
        RebuildFilterCriteria();
    }

    /// <summary>Removes one chip from <see cref="SelectedTickerCodes"/> (fix request: multi-Ticker
    /// chip UI, Task F) - bound to each chip's own remove button in NoteTimelineView.axaml.</summary>
    [RelayCommand]
    private void RemoveTickerFilter(string? ticker)
    {
        if (ticker is not null && SelectedTickerCodes.Remove(ticker))
        {
            RebuildFilterCriteria();
        }
    }

    partial void OnSelectedScopeNodeChanged(TickerGroupNode? value) => RebuildFilterCriteria();

    /// <summary>Applies a hashtag clicked inside a Note's body to the Filter dimension (fix request):
    /// routes through the exact same <see cref="SelectSuggestion"/> path a Hashtag suggestion picked
    /// from the unified search box would take, so both entry points share one behavior.</summary>
    private void ApplyHashtagToFilter(string hashtag) => SelectSuggestion(NoteScopeSuggestion.ForHashtag(hashtag));

    /// <summary>Applies a hashtag clicked inside a Note's body to the free-text Search field (fix
    /// request): mirrors the "#tag" formatting <see cref="SearchBoxDisplayText"/> and the removed
    /// card chip used, so the box's content matches what the user just clicked.</summary>
    private void ApplyHashtagToSearch(string hashtag) => SearchText = $"#{hashtag}";

    /// <summary>
    /// Resolves the unified scope menu's current selection into a ticker set (fix request #2 of the
    /// scope-menu round; fix request #1 of the unified-suggestion round): null/AllTickersNode means
    /// "All Tickers" - no ticker-scope constraint. Any other node resolves as the union of
    /// ITickerStateStore.GetTickersForNode across every node sharing the selected node's (Kind,
    /// DisplayName) - Watchlist/Portfolio/Filter profiles are not required to have unique names, so a
    /// single-node lookup would silently ignore same-named sibling profiles.
    /// </summary>
    private ImmutableHashSet<string>? ResolveScopeTickers()
    {
        if (SelectedScopeNode is null || SelectedScopeNode is AllTickersNode)
        {
            return null;
        }

        var kind = ClassifyScopeNodeKind(SelectedScopeNode);
        if (kind is null)
        {
            // Defensive fallback (sa_constraint_check Educational #1): SelectSuggestion only ever
            // assigns SelectedScopeNode a Watchlist/Portfolio/Filter node or AllTickersNode (handled
            // above), so this branch is unreachable via the current unified search box UI - the
            // scope-menu TreeView that could once set it to a CategoryNode/ActionNode was removed in
            // sa_implement Task 3. It stays because SelectedScopeNode remains a public settable
            // ObservableProperty, so any other caller (tests, future code) could still assign an
            // unclassified node kind here; single-node lookup is the correct behavior for that case.
            return _tickerStateStore.GetTickersForNode(SelectedScopeNode).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var suggestion in FlattenScopeNodeSuggestions(AvailableScopeNodes))
        {
            if (suggestion.Kind == kind && string.Equals(suggestion.DisplayName, SelectedScopeNode.DisplayName, StringComparison.Ordinal))
            {
                result.UnionWith(_tickerStateStore.GetTickersForNode(suggestion.Node));
            }
        }

        return result.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private ImmutableHashSet<string>? ResolveTaggedTickers(string? selectedTag)
    {
        if (string.IsNullOrEmpty(selectedTag))
        {
            return null;
        }

        return _tagToTickers.TryGetValue(selectedTag, out var tickers) ? tickers : ImmutableHashSet<string>.Empty;
    }

    /// <summary>Opens the trash dialog to its "Deleted" tab (spec section 9.3, fix request: this and
    /// <see cref="OpenOrphanedFilesAsync"/> are now the toolbar's two separate icon+count entry
    /// points, replacing the old single "Trash" button) and refreshes the timeline afterward, in
    /// case a restore happened while it was open.</summary>
    [RelayCommand]
    private async Task OpenTrashAsync()
    {
        await _dialogService.ShowNoteTrashDialogAsync(NoteTrashInitialTab.Deleted);
        await RefreshAsync();
    }

    /// <summary>Opens the trash dialog to its "Orphaned Files" tab (fix request).</summary>
    [RelayCommand]
    private async Task OpenOrphanedFilesAsync()
    {
        await _dialogService.ShowNoteTrashDialogAsync(NoteTrashInitialTab.Orphaned);
        await RefreshAsync();
    }

    partial void OnFilterCriteriaChanged(NoteFilterCriteria value)
    {
        _ = RefreshAsync();
    }

    partial void OnSearchTextChanged(string value) => RebuildFilterCriteria();
    partial void OnPeriodStartChanged(DateTimeOffset? value) => RebuildFilterCriteria();
    partial void OnPeriodEndChanged(DateTimeOffset? value) => RebuildFilterCriteria();

    /// <summary>Clears every filter-bar input, restoring the "All" (show everything) state (spec
    /// section 5.1). Note: this resets the underlying selection state (so filtering reverts to
    /// showing everything) but cannot reach into NoteTimelineView's ListBox controls to visually
    /// deselect their highlighted items - a cosmetic gap accepted under the "functional verification
    /// takes priority" scope decision for this pass (UI spec FR-4 section 4.3).</summary>
    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = string.Empty;
        SelectedTickerCodes.Clear();
        PeriodStart = null;
        PeriodEnd = null;
        _selectedRegisteredTag = null;
        _selectedHashtag = null;
        _noTickerOnlySelected = false;
        SelectedScopeNode = _tickerStateStore.Groups.FirstOrDefault(n => n is AllTickersNode);
        OnPropertyChanged(nameof(SearchBoxDisplayText));
        RebuildFilterCriteria();
    }

    private void RebuildFilterCriteria()
    {
        FilterCriteria = new NoteFilterCriteria
        {
            SearchText = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
            SelectedTickers = SelectedTickerCodes.Count > 0 ? SelectedTickerCodes.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase) : null,
            NoTickerOnly = _noTickerOnlySelected,
            PeriodStartInclusive = PeriodStart?.DateTime,
            PeriodEndInclusive = PeriodEnd?.DateTime,
            ScopeTickers = ResolveScopeTickers(),
            RegisteredTagTickers = ResolveTaggedTickers(_selectedRegisteredTag),
            SelectedHashtag = _selectedHashtag,
        };
    }

    private Task _refreshChain = Task.CompletedTask;
    private readonly object _refreshChainLock = new();

    /// <summary>
    /// Ensures the notes.db schema exists (idempotent - cheap no-op after the first call, spec
    /// section 4.2), reloads active Notes, applies <see cref="FilterCriteria"/>, sorts them, and
    /// rebuilds <see cref="DisplayedNotes"/>, restoring each card's previously recorded
    /// expand/collapse state from the in-memory dictionary. Also refreshes <see cref="AvailableHashtags"/>
    /// from the same reloaded active-Notes set, so a hashtag typed into a just-saved Note becomes
    /// selectable in the suggestion box without a separate load pass.
    /// </summary>
    /// <remarks>
    /// Calls are serialized onto a single chain: the constructor kicks off an initial load
    /// fire-and-forget, and callers (filter-bar changes, card actions, tests) may call this again
    /// before that completes. Without serialization, two overlapping calls could interleave their
    /// <see cref="DisplayedNotes"/> Clear/Add mutations. Chaining guarantees each call waits for
    /// every earlier one to finish and always reflects the latest FilterCriteria by the time it
    /// resolves.
    /// </remarks>
    public Task RefreshAsync(CancellationToken ct = default)
    {
        lock (_refreshChainLock)
        {
            _refreshChain = _refreshChain.ContinueWith(_ => RefreshCoreAsync(ct), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            return _refreshChain;
        }
    }

    private async Task RefreshCoreAsync(CancellationToken ct)
    {
        if (!_schemaInitialized)
        {
            await _schemaInitializer.InitializeAsync(ct).ConfigureAwait(false);
            _schemaInitialized = true;
        }

        var activeNotes = await _noteRepository.GetAllActiveAsync(ct).ConfigureAwait(false);

        // sa_implement (Reply-Leaf Restriction & Deletion Tombstone): soft-deleted Notes that are
        // still referenced as someone's ParentNoteId must stay resolvable in BuildThreadedDisplayOrder's
        // tree (as deletion tombstones) rather than vanishing and orphan-promoting their children to
        // root. Deliberately concatenated AFTER NoteFilterEngine.Filter, not before - a tombstone has
        // no meaningful body/hashtags/ticker to search, so the active search/scope filter must never
        // include or exclude it; it's structural chrome, not filterable content.
        var deletedReferencedNotes = await _noteRepository.GetDeletedNotesReferencedAsParentAsync(ct).ConfigureAwait(false);
        var threadedOrder = NoteThreadOrganizer.BuildThreadedDisplayOrder(
            NoteFilterEngine.Filter(activeNotes, FilterCriteria).Concat(deletedReferencedNotes),
            _notesSettingsManager.ThreadCollapseThreshold,
            _notesSettingsManager.TailVisibleCount);
        var visible = threadedOrder.Notes;

        // Toolbar count badges (fix request: split "Trash" button into Trash/Orphaned icon+count
        // pair). Deleted count is a fresh DB read each refresh (same recency guarantee as
        // AvailableHashtags), but via CountDeletedAsync's COUNT(*) rather than GetAllDeletedAsync's
        // full row fetch (sa_constraint_check Phase 2: this runs on every keystroke-driven refresh,
        // so only the number - not the deleted Notes' full row data - should be paid for). Orphaned
        // count only ever reflects the last app-startup scan (no re-scan triggered here), matching
        // NoteTrashViewModel's own Orphaned-tab policy.
        var deletedNotesCount = await _noteRepository.CountDeletedAsync(ct).ConfigureAwait(false);
        var orphanedFilesCount = _orphanedAttachmentHolder.LatestReport?.OrphanedFileNames.Count ?? 0;

        // Sourced from activeNotes (the full active set, not the filtered `visible` subset) so the
        // Hashtag dropdown always offers every tag currently in use, independent of the active
        // filter - same "universe, not filtered view" contract AvailableRegisteredTags follows.
        var availableHashtags = activeNotes
            .SelectMany(note => note.Hashtags)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(tag => tag, StringComparer.Ordinal)
            .ToList();

        var quotedNotesById = await ResolveQuotedNotesAsync(visible, ct).ConfigureAwait(false);

        // RefreshAsync's _refreshChain.ContinueWith(..., TaskScheduler.Default) deliberately keeps
        // this whole method off the UI thread for the DB read/filter/sort work, but DisplayedNotes
        // is bound to the timeline's ItemsControl - mutating it from a background thread (as the
        // previous implementation did directly here) leaves Avalonia's collection-changed handling
        // undefined: stale cards can survive a Clear(), which is what made a single new Note look
        // like a duplicate post, and made deleting either stale card remove the one real underlying
        // Note (fix request #1/#4, 3rd round). Route the actual rebuild through the dispatcher, same
        // pattern as AvailableTickers' load. Static lambda + state tuple (sa_constraint_check Phase 2,
        // SA_RENDERING_PERFORMANCE.md "Dispatcher Synchronization") instead of a capturing closure.
        await _dispatcherService.PostAsync(static state =>
        {
            var (self, notes, hashtags, deletedCount, orphanedCount, quotedNotes, collapsedCounts, tombstoneIds, leafIds) = state;
            self.DeletedNotesCount = deletedCount;
            self.OrphanedFilesCount = orphanedCount;

            // Infinite-scroll pagination (sa_implement Task 4): cache the full visible-note source and
            // its lookup tables so LoadMoreVisibleNotesAsync can reveal more of it later without a
            // further DB round trip, then materialize only the first "page" (Settings > Notes'
            // TimelinePageSize, extended to the next safe boundary so a page break never splits a
            // reply chain - see ComputeSafeRevealCount) into DisplayedNotes.
            self._visibleNoteSource = notes;
            self._visibleCollapsedReplyCountAfterNoteId = collapsedCounts;
            self._visibleTombstoneNoteIds = tombstoneIds;
            self._visibleLeafNoteIds = leafIds;
            self._visibleQuotedNotesById = quotedNotes;

            var revealCount = ComputeSafeRevealCount(notes, Math.Min(self._notesSettingsManager.TimelinePageSize, notes.Count));
            self._loadedVisibleCount = revealCount;
            self.HasMoreVisibleNotes = revealCount < notes.Count;

            var items = self.BuildItemViewModels(notes, 0, revealCount, quotedNotes, collapsedCounts, tombstoneIds, leafIds);
            self.DisplayedNotes.ReplaceRange(items);

            // Skip-if-equal (same optimization as WatchlistManager.Initialize): avoids rebuilding
            // AvailableSearchSuggestions - and thus flashing the suggestion dropdown - on every
            // keystroke-driven RefreshAsync call when the active hashtag universe hasn't changed.
            if (!self.AvailableHashtags.SequenceEqual(hashtags, StringComparer.Ordinal))
            {
                self.AvailableHashtags.ReplaceRange(hashtags);
                self.RebuildAvailableSearchSuggestions();
            }

            return Task.CompletedTask;
        }, (Self: this, Notes: visible, Hashtags: availableHashtags, DeletedCount: deletedNotesCount, OrphanedCount: orphanedFilesCount, QuotedNotes: quotedNotesById, CollapsedCounts: threadedOrder.CollapsedReplyCountAfterNoteId, TombstoneIds: threadedOrder.TombstoneNoteIds, LeafIds: threadedOrder.LeafNoteIds)).ConfigureAwait(false);
    }

    /// <summary>Reveals the next <see cref="INotesSettingsManager.TimelinePageSize"/> notes of the most
    /// recently computed <see cref="_visibleNoteSource"/> onto the end of <see cref="DisplayedNotes"/>
    /// (infinite scroll) without a further DB round trip or disturbing already-rendered cards/scroll
    /// position (<see cref="BulkObservableCollection{T}.AddRange"/>). No-ops if a load is already in
    /// progress or every visible note is already displayed (<see cref="IsLoadingMoreNotes"/>/
    /// <see cref="HasMoreVisibleNotes"/>) - callers (the timeline View's scroll-threshold trigger,
    /// sa_implement Task 5) are not expected to guard against redundant calls themselves. Must be
    /// called on the UI thread, same requirement as this ViewModel's other card-construction methods.</summary>
    public Task LoadMoreVisibleNotesAsync()
    {
        if (IsLoadingMoreNotes || !HasMoreVisibleNotes)
        {
            return Task.CompletedTask;
        }

        IsLoadingMoreNotes = true;
        try
        {
            var targetCount = Math.Min(_loadedVisibleCount + _notesSettingsManager.TimelinePageSize, _visibleNoteSource.Count);
            var revealCount = ComputeSafeRevealCount(_visibleNoteSource, targetCount);

            var newItems = BuildItemViewModels(_visibleNoteSource, _loadedVisibleCount, revealCount, _visibleQuotedNotesById, _visibleCollapsedReplyCountAfterNoteId, _visibleTombstoneNoteIds, _visibleLeafNoteIds);
            DisplayedNotes.AddRange(newItems);
            _loadedVisibleCount = revealCount;
            HasMoreVisibleNotes = _loadedVisibleCount < _visibleNoteSource.Count;
        }
        finally
        {
            IsLoadingMoreNotes = false;
        }

        return Task.CompletedTask;
    }

    /// <summary>Builds timeline card ViewModels for <paramref name="notes"/>[<paramref name="startIndex"/>,
    /// <paramref name="endIndexExclusive"/>) - a sub-range of the full visible-note source - using
    /// global list indices (not range-relative ones) for <c>connectsDownToReplyCard</c>, so a page
    /// boundary never miscomputes the connector line for the range's own last card: <see cref="ComputeSafeRevealCount"/>
    /// already guarantees <paramref name="endIndexExclusive"/> never lands in the middle of a reply
    /// chain, so if <c>notes[endIndexExclusive]</c> exists it is never a reply to the last revealed
    /// card. Must be called on the UI thread (mutates/reads UI-bound state via <c>this</c>, same
    /// requirement as <see cref="CreateItemViewModel"/>).</summary>
    private List<NoteTimelineItemViewModel> BuildItemViewModels(
        List<Note> notes,
        int startIndex,
        int endIndexExclusive,
        Dictionary<Guid, Note> quotedNotesById,
        Dictionary<Guid, int> collapsedReplyCountAfterNoteId,
        HashSet<Guid> tombstoneNoteIds,
        HashSet<Guid> leafNoteIds)
    {
        var items = new List<NoteTimelineItemViewModel>(endIndexExclusive - startIndex);
        for (var i = startIndex; i < endIndexExclusive; i++)
        {
            var note = notes[i];
            // Fix request #2 (2nd round): BuildThreadedDisplayOrder already places every reply
            // directly after the Note it replies to, so "connects down" now simply means "the
            // next card is my direct reply" - no adjacency coincidence involved. A collapsed
            // segment's last-visible-head Note never satisfies this (its real next reply was
            // omitted from `notes`), so ConnectsDownToReplyCard/HasCollapsedRepliesBelow end up
            // mutually exclusive without extra guard code (sa_implement Task 5).
            var connectsDownToReply = i < notes.Count - 1 && IsReplyOf(notes[i + 1], note);
            var collapsedReplyCount = collapsedReplyCountAfterNoteId.TryGetValue(note.Id, out var hiddenCount) ? hiddenCount : 0;
            var isTombstone = tombstoneNoteIds.Contains(note.Id);
            var canReply = leafNoteIds.Contains(note.Id);
            items.Add(CreateItemViewModel(note, GetExpandedState(note.Id), ResolveQuotedPreview(note, quotedNotesById), connectsDownToReply, collapsedReplyCount, isTombstone, canReply));
        }
        return items;
    }

    /// <summary>Extends <paramref name="minimumCount"/> forward, if needed, to the next index in
    /// <paramref name="visibleNotes"/> that isn't itself a reply to the note immediately before it -
    /// the same adjacency check <see cref="BuildItemViewModels"/> uses for <c>connectsDownToReplyCard</c>.
    /// Without this, an arbitrary flat-index page cut could reveal a thread's parent card with its
    /// connector line drawn toward a reply that hasn't been revealed yet, since
    /// <see cref="NoteThreadOrganizer.BuildThreadedDisplayOrder"/> already lays every reply immediately
    /// after the note it replies to - a page boundary must land between threads, never inside one.</summary>
    private static int ComputeSafeRevealCount(List<Note> visibleNotes, int minimumCount)
    {
        var count = Math.Min(minimumCount, visibleNotes.Count);
        while (count > 0 && count < visibleNotes.Count && IsReplyOf(visibleNotes[count], visibleNotes[count - 1]))
        {
            count++;
        }
        return count;
    }

    /// <summary>The single SSoT definition of "connects down" adjacency (sa_minimal_fix,
    /// sa_constraint_check remediation): whether <paramref name="child"/> is a direct reply to
    /// <paramref name="parent"/>. Shared by <see cref="BuildItemViewModels"/> (deciding whether to draw
    /// a card's connector line to the next card) and <see cref="ComputeSafeRevealCount"/> (deciding
    /// whether a page boundary would split a reply chain) - both ask the exact same structural
    /// question about two adjacent notes in <see cref="_visibleNoteSource"/>, so a single definition
    /// keeps them from silently drifting apart if this adjacency rule ever changes.</summary>
    private static bool IsReplyOf(Note child, Note parent) => child.ParentNoteId == parent.Id;

    /// <summary>Resolves the quoted Note (spec section 6) for each distinct <see cref="Note.QuotedNoteId"/>
    /// found among <paramref name="notes"/> with a single batched query (<see cref="NoteRepository.GetByIdsAsync"/>)
    /// rather than one DB round trip per distinct id - shared by both the main timeline
    /// (<see cref="RefreshCoreAsync"/>, which runs on every keystroke-driven refresh) and the detail
    /// thread (<see cref="LoadDetailThreadAsync"/>). A missing entry (permanently deleted quoted Note)
    /// is simply absent from the result; <see cref="NoteTimelineItemViewModel.QuotedNoteWasDeleted"/>
    /// degrades gracefully from that, it is never treated as an error here.</summary>
    private async Task<Dictionary<Guid, Note>> ResolveQuotedNotesAsync(IEnumerable<Note> notes, CancellationToken ct)
    {
        var quotedNoteIds = notes.Where(n => n.QuotedNoteId.HasValue).Select(n => n.QuotedNoteId!.Value).Distinct();
        var quotedNotes = await _noteRepository.GetByIdsAsync(quotedNoteIds, ct).ConfigureAwait(false);
        return quotedNotes.ToDictionary(n => n.Id);
    }

    /// <summary>The resolved quote-preview for <paramref name="note"/> from a batch produced by
    /// <see cref="ResolveQuotedNotesAsync"/>, or null when <paramref name="note"/> isn't a quote or
    /// its quoted Note couldn't be resolved.</summary>
    private static Note? ResolveQuotedPreview(Note note, Dictionary<Guid, Note> quotedNotesById) =>
        note.QuotedNoteId is { } quotedId && quotedNotesById.TryGetValue(quotedId, out var quoted) ? quoted : null;

    private bool GetExpandedState(Guid noteId) => _expandedState.TryGetValue(noteId, out var expanded) && expanded;

    /// <summary>Builds one timeline card's ViewModel with every constructor callback wired the same
    /// way regardless of whether it ends up in <see cref="DisplayedNotes"/> (the main list) or
    /// <see cref="DetailAncestorChain"/>/<see cref="DetailReplies"/> (the detail thread, Task B7) -
    /// a reply shown inside the detail thread must be just as interactive (Quote/Reply/Edit/Pin/
    /// Delete, and clicking its own timestamp to navigate the detail page to it) as one in the main
    /// list. <paramref name="connectsDownToReplyCard"/> defaults to false so detail-thread
    /// callers (which never want the connector line - fix request #2) don't need to pass it.
    /// <paramref name="collapsedReplyCount"/> likewise defaults to 0 (no collapse indicator) so
    /// detail-thread callers - which always show a thread in full, sa_implement Task 5 - don't need
    /// to pass it either. <paramref name="isTombstone"/>/<paramref name="canReply"/> default to
    /// false/true (an ordinary, repliable live Note) for the same reason. Must be called on the UI
    /// thread (mutates/reads UI-bound state via <c>this</c>).</summary>
    private NoteTimelineItemViewModel CreateItemViewModel(Note note, bool isExpanded, Note? quotedNotePreview, bool connectsDownToReplyCard = false, int collapsedReplyCount = 0, bool isTombstone = false, bool canReply = true)
    {
        var item = new NoteTimelineItemViewModel(
            note,
            isExpanded,
            HandleTogglePinnedRequestedAsync,
            HandleDeleteRequestedAsync,
            HandleEditRequested,
            HandleOpenUrlRequested,
            HandleViewChartRequested,
            HandleOpenDetailRequested,
            ApplyHashtagToFilter,
            ApplyHashtagToSearch,
            _attachmentRepository,
            HandleOpenAttachmentRequested,
            onQuoteRequested: HandleQuoteRequested,
            onReplyRequested: HandleReplyRequested,
            quotedNotePreview: quotedNotePreview,
            connectsDownToReplyCard: connectsDownToReplyCard,
            readMoreMaxCharacters: _notesSettingsManager.ReadMoreMaxCharacters,
            readMoreMaxLines: _notesSettingsManager.ReadMoreMaxLines,
            urlColor: _notesSettingsManager.UrlColor,
            hashtagColor: _notesSettingsManager.HashtagColor,
            hasCollapsedRepliesBelow: collapsedReplyCount > 0,
            collapsedReplyCount: collapsedReplyCount,
            connectorLineLength: _notesSettingsManager.ConnectorLineLength,
            dashLength: _notesSettingsManager.DashLength,
            isTombstone: isTombstone,
            canReply: canReply,
            bodyFontSize: _notesSettingsManager.BodyFontSize,
            bodyTextColor: _notesSettingsManager.BodyTextColor,
            bodyBackgroundColor: _notesSettingsManager.BodyBackgroundColor,
            thumbnailSizePixels: _notesSettingsManager.ThumbnailSizePixels,
            imageDisplayMode: _notesSettingsManager.ImageDisplayMode,
            maxRenderedThumbnails: _notesSettingsManager.MaxRenderedThumbnails);
        item.PropertyChanged += OnItemPropertyChanged;
        return item;
    }

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NoteTimelineItemViewModel.IsExpanded) && sender is NoteTimelineItemViewModel item)
        {
            _expandedState[item.Note.Id] = item.IsExpanded;
        }
    }

    /// <summary>Opens the inline compose panel for a brand-new Note (spec section 4.7's dialog-based flow is a separate entry point; this is the timeline's own "+" affordance).</summary>
    [RelayCommand]
    private void BeginCreate() => EditingNote = _editorFactory();

    [RelayCommand]
    private void CancelEdit() => EditingNote = null;

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task SaveEditAsync()
    {
        if (EditingNote is null)
        {
            return;
        }

        await EditingNote.SaveCommand.ExecuteAsync(null);
        if (EditingNote.SavedNote is not null)
        {
            EditingNote = null;
            await RefreshAsync();
        }
    }

    private async Task HandleTogglePinnedRequestedAsync(NoteTimelineItemViewModel item)
    {
        var updated = item.Note with { IsPinned = !item.Note.IsPinned };
        await _noteRepository.UpdateAsync(updated).ConfigureAwait(false);
        await RefreshAsync().ConfigureAwait(false);
    }

    private async Task HandleDeleteRequestedAsync(NoteTimelineItemViewModel item)
    {
        await _noteRepository.SoftDeleteAsync(item.Note.Id).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(item.Note.RelatedTicker))
        {
            await _cacheSynchronizer.RecalculateNotesCacheAsync(item.Note.RelatedTicker).ConfigureAwait(false);
        }

        await RefreshAsync().ConfigureAwait(false);
    }

    private void HandleEditRequested(NoteTimelineItemViewModel item)
    {
        var editor = _editorFactory();
        editor.LoadForEdit(item.Note);
        EditingNote = editor;
    }

    /// <summary>Non-null while the fire-and-forget <see cref="LoadDetailThreadAsync"/> kicked off by
    /// the most recent <see cref="HandleOpenDetailRequested"/> is pending/complete; exposed
    /// (internal, via InternalsVisibleTo) purely so tests can await deterministic completion instead
    /// of racing the background load - same pattern as <see cref="NoteTimelineItemViewModel.ThumbnailLoadTask"/>.</summary>
    internal Task? DetailThreadLoadTask { get; private set; }

    private void HandleOpenDetailRequested(NoteTimelineItemViewModel item)
    {
        // sa_minimal_fix (bug-list item #3c): item is the exact same NoteTimelineItemViewModel
        // instance rendered in the main timeline list, so its ConnectsDownToReplyCard/
        // HasCollapsedRepliesBelow flags reflect that list's own layout (e.g. true for the head card
        // of a collapsed thread, or a card directly above its reply). Assigning it to
        // SelectedDetailItem unchanged carried those main-list-only flags into the detail page too,
        // drawing a stray connector/dotted line under the focal card there. Build a fresh,
        // detail-page-scoped view model for the same Note instead - mirroring how
        // DetailAncestorChain/DetailReplies below are always built fresh via CreateItemViewModel's
        // connectsDownToReplyCard/collapsedReplyCount defaults of false/0 - reusing item's
        // already-resolved QuotedNotePreview since that part is independent of list-vs-detail context.
        // IsTombstone/CanReply are reused the same way (sa_implement, Reply-Leaf Restriction &
        // Deletion Tombstone): whichever list `item` came from (main timeline, or another detail
        // page's own ancestor/reply chain) computed them from the same CollectPrunedSubtree-backed
        // logic moments earlier against the same underlying data, so they're already correct for this
        // Note - no need to block the synchronous SelectedDetailItem assignment on a fresh DB query.
        SelectedDetailItem = CreateItemViewModel(item.Note, GetExpandedState(item.Note.Id), item.QuotedNotePreview, isTombstone: item.IsTombstone, canReply: item.CanReply);

        // Clear synchronously so a previous Note's thread never flashes underneath the newly
        // selected one while LoadDetailThreadAsync's DB reads are still in flight (most visible when
        // navigating between nested replies inside the detail page itself, spec section 6's "in-thread navigation").
        DetailAncestorChain.Clear();
        DetailReplies.Clear();
        DetailThreadLoadTask = LoadDetailThreadAsync(item.Note);
    }

    /// <summary>
    /// Populates <see cref="DetailAncestorChain"/> and <see cref="DetailReplies"/> for the Note the
    /// detail page is now showing (spec section 6: Reply Chain thread display, Task B7). Runs
    /// fire-and-forget from <see cref="HandleOpenDetailRequested"/> rather than making
    /// OpenDetailCommand itself async - SelectedDetailItem's assignment, and thus the page swap,
    /// must stay instant; the ancestor/reply cards simply populate a beat later (same UX as each
    /// card's own attachment-thumbnail load).
    /// </summary>
    private async Task LoadDetailThreadAsync(Note note)
    {
        // sa_minimal_fix (bug-list item #3a/#3b) established that a detail page must show the whole
        // connected thread, not just one level; sa_implement (Reply-Leaf Restriction & Deletion
        // Tombstone) extends that to soft-deleted Notes: GetAncestorChainAsync already never
        // filtered IsDeleted (a pre-existing gap - a deleted ancestor used to leak its real Body here
        // unfiltered, fixed below by always rendering it as a tombstone instead), and
        // GetDescendantRepliesIncludingDeletedAsync walks past a deleted reply instead of
        // GetRepliesAsync's old behavior of dropping everything below it - in a single batched query
        // (sa_constraint_check: the previous per-node recursive repository-call walk opened a new DB
        // connection per descendant, an N+1 pattern).
        var ancestors = await _noteRepository.GetAncestorChainAsync(note.Id).ConfigureAwait(false);
        var descendants = await _noteRepository.GetDescendantRepliesIncludingDeletedAsync(note.Id).ConfigureAwait(false);
        var repliesByParentId = descendants
            .Where(n => n.ParentNoteId.HasValue)
            .GroupBy(n => n.ParentNoteId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(n => n.CreatedAt).ToList());

        // CollectPrunedSubtree treats `note` as its own "root" purely to reuse the same
        // survives()/leaf computation the main timeline uses (Task 4) - its own tombstone/leaf status
        // isn't used from here (SelectedDetailItem already carries the correct values, reused from
        // whichever card was clicked to get here - see HandleOpenDetailRequested), only its pruned
        // descendants are. The detail page never applies thread-collapse windowing (existing design:
        // it's the "escape hatch" for a collapsed thread), so every surviving descendant renders.
        var pruned = NoteThreadOrganizer.CollectPrunedSubtree(note, repliesByParentId);
        var replies = pruned.Notes.Skip(1).ToList();

        var quotedNotesById = await ResolveQuotedNotesAsync(ancestors.Concat(replies), CancellationToken.None).ConfigureAwait(false);

        // Static lambda + state tuple (sa_constraint_check, SA_RENDERING_PERFORMANCE.md "Dispatcher
        // Synchronization") instead of a capturing closure - same pattern as RefreshCoreAsync's post above.
        await _dispatcherService.PostAsync(static state =>
        {
            var (self, note, ancestors, replies, quotedNotesById, pruned) = state;

            // Stale-response guard: if the user navigated to a different Note's detail page (or back
            // to the list) while the DB reads above were in flight, SelectedDetailItem no longer
            // matches this call's Note - applying the result now would flash the wrong thread onto
            // whatever page is actually showing.
            if (self.SelectedDetailItem?.Note.Id != note.Id)
            {
                return Task.CompletedTask;
            }

            // Every ancestor in the chain always has exactly one child - the next ancestor, or
            // ultimately `note` itself - so none of them can ever be the leaf of their branch;
            // canReply is unconditionally false rather than computed, unlike replies below.
            self.DetailAncestorChain.ReplaceRange(ancestors.Select(a => self.CreateItemViewModel(a, self.GetExpandedState(a.Id), ResolveQuotedPreview(a, quotedNotesById), isTombstone: a.IsDeleted, canReply: false)));
            self.DetailReplies.ReplaceRange(replies.Select(r => self.CreateItemViewModel(r, self.GetExpandedState(r.Id), ResolveQuotedPreview(r, quotedNotesById), isTombstone: pruned.TombstoneNoteIds.Contains(r.Id), canReply: pruned.LeafNoteIds.Contains(r.Id))));
            return Task.CompletedTask;
        }, (Self: this, Note: note, Ancestors: ancestors, Replies: replies, QuotedNotesById: quotedNotesById, Pruned: pruned)).ConfigureAwait(false);
    }

    private void HandleQuoteRequested(NoteTimelineItemViewModel item)
    {
        var editor = _editorFactory();
        editor.LoadForQuote(item.Note);
        EditingNote = editor;
    }

    private void HandleReplyRequested(NoteTimelineItemViewModel item)
    {
        var editor = _editorFactory();
        editor.LoadForReply(item.Note);
        EditingNote = editor;
    }

    /// <summary>Closes the individual Note detail page and returns to the timeline list.</summary>
    [RelayCommand]
    private void BackToList()
    {
        SelectedDetailItem = null;
        DetailAncestorChain.Clear();
        DetailReplies.Clear();
    }

    /// <summary>
    /// Broadcasts a chart-jump request for a Note's ticker/anchor date/timeframe (spec section
    /// 6.2). Missing ChartAnchorDate/ChartTimeframe fall back to the Note's CreatedAt date and
    /// Daily, respectively, per spec section 3 ("use the CreatedAt date when unspecified"). Delivered via
    /// WeakReferenceMessenger rather than a direct ChartViewModel reference because this timeline
    /// has no reference to any specific chart tab/window instance - MainWindowViewModel owns and
    /// routes to the primary chart (see its Receive(NoteChartJumpRequestedMessage) handler).
    /// </summary>
    private static void HandleViewChartRequested(NoteTimelineItemViewModel item)
    {
        var note = item.Note;
        if (string.IsNullOrWhiteSpace(note.RelatedTicker))
        {
            return;
        }

        var anchorDate = note.ChartAnchorDate ?? note.CreatedAt.Date;
        var timeframe = note.ChartTimeframe ?? TimeFrame.D1;
        WeakReferenceMessenger.Default.Send(new NoteChartJumpRequestedMessage(note.RelatedTicker, anchorDate, timeframe));
    }

    /// <summary>Opens the URL in the OS default browser via <see cref="IExternalFileOpener"/>.</summary>
    private void HandleOpenUrlRequested(string url)
    {
        _fileOpener.OpenUrl(url);
    }

    /// <summary>Opens an attachment's original file via <see cref="IExternalFileOpener"/> using the OS-associated default application.</summary>
    private void HandleOpenAttachmentRequested(string filePath)
    {
        _fileOpener.OpenFile(filePath);
    }

    private void OnNotesSettingsManagerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_isDisposed) return;
        _ = RefreshAsync();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (_notesSettingsManager != null)
        {
            _notesSettingsManager.PropertyChanged -= OnNotesSettingsManagerPropertyChanged;
        }

        GC.SuppressFinalize(this);
    }
}
