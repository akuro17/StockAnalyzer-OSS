using System;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Notes;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Notes;

namespace StockAnalyzer.Avalonia.ViewModels.Notes;

/// <summary>An image the user picked in this session but has not yet been validated/saved to disk.
/// <paramref name="Preview"/> is null when decoding fails - staging never blocks on a bad preview,
/// real validation still happens in <see cref="AttachmentRepository.SaveAsync"/> at Save.</summary>
public sealed record PendingImageAttachment(byte[] Bytes, string OriginalFileName, Guid LocalId, Bitmap? Preview);

/// <summary>
/// Edits and saves a single Note: Body, related ticker, chart anchor, pending image attachments,
/// and link URLs (spec sections 3, 6.1, 8). Constructed fresh for "create" and via
/// <see cref="LoadForEdit"/> for "edit" (spec section 9.2's Note editing = minor corrections only).
/// </summary>
public partial class NoteEditorViewModel : ViewModelBase
{
    private readonly NoteRepository _noteRepository;
    private readonly AttachmentRepository _attachmentRepository;
    private readonly TickerMetadataNotesCacheSynchronizer _cacheSynchronizer;
    private readonly IMarketDataProvider _marketDataProvider;
    private readonly IDispatcherService _dispatcherService;
    private readonly INotesSettingsManager _notesSettingsManager;
    private readonly ILogger<NoteEditorViewModel>? _logger;

    private Guid? _editingNoteId;
    private DateTime _originalCreatedAt;
    private string? _originalRelatedTicker;
    private ImmutableArray<Guid> _existingAttachmentIds = ImmutableArray<Guid>.Empty;
    private Guid? _quotedNoteId;
    private Guid? _parentNoteId;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _body = string.Empty;

    [ObservableProperty]
    private string? _relatedTicker;

    [ObservableProperty]
    private DateTime? _chartAnchorDate;

    [ObservableProperty]
    private TimeFrame? _chartTimeframe;

    [ObservableProperty]
    private bool _isPinned;

    /// <summary>Images picked but not yet passed through <see cref="AttachmentRepository.SaveAsync"/>.</summary>
    public ObservableCollection<PendingImageAttachment> PendingAttachments { get; } = new();

    /// <summary>Human-readable failure reasons for attachments skipped during the last Save (spec section 8: one bad image never blocks the rest).</summary>
    public ObservableCollection<string> AttachmentErrors { get; } = new();

    /// <summary>The Note persisted by the most recent successful Save.</summary>
    public Note? SavedNote { get; private set; }

    public bool IsEditingExistingNote => _editingNoteId.HasValue;

    /// <summary>Every known ticker symbol, for the "Related ticker" AutoCompleteBox - same source
    /// and suggestion UX as MainWindow's own Symbol AutoCompleteBox (fix request #3).</summary>
    public ObservableCollection<string> AvailableTickers { get; } = new();

    /// <summary>Live pass-through of the configured thumbnail size (Settings &gt; Notes), so pending-attachment
    /// previews in the compose panel match the size used for posted Notes' thumbnail row.</summary>
    public double ThumbnailSizePixels => _notesSettingsManager.ThumbnailSizePixels;

    public NoteEditorViewModel(
        NoteRepository noteRepository,
        AttachmentRepository attachmentRepository,
        TickerMetadataNotesCacheSynchronizer cacheSynchronizer,
        IMarketDataProvider marketDataProvider,
        IDispatcherService dispatcherService,
        INotesSettingsManager notesSettingsManager,
        ILogger<NoteEditorViewModel>? logger = null)
    {
        _noteRepository = noteRepository;
        _attachmentRepository = attachmentRepository;
        _cacheSynchronizer = cacheSynchronizer;
        _marketDataProvider = marketDataProvider;
        _dispatcherService = dispatcherService;
        _notesSettingsManager = notesSettingsManager;
        _logger = logger ?? NullLogger<NoteEditorViewModel>.Instance;

        _ = LoadAvailableTickersAsync();
    }

    /// <summary>
    /// Populates <see cref="AvailableTickers"/> (bound to the "Related ticker" AutoCompleteBox's
    /// ItemsSource) on the UI thread, same pattern as ChartViewModel's own ticker-list load - the
    /// prior implementation mutated this ObservableCollection straight off the background thread
    /// that GetAvailableTickersAsync().ConfigureAwait(false) resumed on, so the suggestion list
    /// never reliably reached the UI (fix request #1, 2nd round).
    /// </summary>
    private async Task LoadAvailableTickersAsync()
    {
        var tickers = await _marketDataProvider.GetAvailableTickersAsync().ConfigureAwait(false);
        _dispatcherService.Post(() =>
        {
            AvailableTickers.Clear();
            foreach (var ticker in tickers)
            {
                AvailableTickers.Add(ticker);
            }
        });
    }

    /// <summary>Populates every field from an existing Note so Save performs an update instead of a create.</summary>
    public void LoadForEdit(Note note)
    {
        _editingNoteId = note.Id;
        _originalCreatedAt = note.CreatedAt;
        _originalRelatedTicker = note.RelatedTicker;
        _existingAttachmentIds = note.AttachmentIds;
        _quotedNoteId = note.QuotedNoteId;
        _parentNoteId = note.ParentNoteId;

        Body = note.Body;
        RelatedTicker = note.RelatedTicker;
        ChartAnchorDate = note.ChartAnchorDate;
        ChartTimeframe = note.ChartTimeframe;
        IsPinned = note.IsPinned;

        PendingAttachments.Clear();
        AttachmentErrors.Clear();
    }

    /// <summary>Resets the editor to a fresh "create" state pre-linked as a quote of <paramref name="quotedNote"/> (spec section 6: Quote). Save will create a brand-new Note whose <see cref="Note.QuotedNoteId"/> points back to it; this is never treated as editing <paramref name="quotedNote"/> itself.</summary>
    public void LoadForQuote(Note quotedNote) => BeginThreadedNote(quotedNoteId: quotedNote.Id, parentNoteId: null);

    /// <summary>Resets the editor to a fresh "create" state pre-linked as a reply to <paramref name="parentNote"/> (spec section 6: Reply Chain). Save will create a brand-new Note whose <see cref="Note.ParentNoteId"/> points back to it, forming a thread; this is never treated as editing <paramref name="parentNote"/> itself.</summary>
    public void LoadForReply(Note parentNote) => BeginThreadedNote(quotedNoteId: null, parentNoteId: parentNote.Id);

    private void BeginThreadedNote(Guid? quotedNoteId, Guid? parentNoteId)
    {
        _editingNoteId = null;
        _originalRelatedTicker = null;
        _existingAttachmentIds = ImmutableArray<Guid>.Empty;
        _quotedNoteId = quotedNoteId;
        _parentNoteId = parentNoteId;

        Body = string.Empty;
        RelatedTicker = null;
        ChartAnchorDate = null;
        ChartTimeframe = null;
        IsPinned = false;

        PendingAttachments.Clear();
        AttachmentErrors.Clear();
    }

    /// <summary>Stages a picked image for saving on the next Save. Actual validation happens in
    /// <see cref="AttachmentRepository.SaveAsync"/> during Save; a preview decode failure here never
    /// blocks staging, it just leaves <see cref="PendingImageAttachment.Preview"/> null.
    /// <paramref name="localId"/> defaults to a freshly generated Guid when the caller has not
    /// already embedded a matching <see cref="NoteImageTokenExtractor"/> placeholder token via
    /// <see cref="InsertImagePlaceholder"/> (Image Display Mode = Attachment List: no token needed).</summary>
    public void AddPendingAttachment(byte[] bytes, string originalFileName, Guid? localId = null)
    {
        Bitmap? preview = null;
        try
        {
            using var stream = new MemoryStream(bytes);
            preview = new Bitmap(stream);
        }
        catch
        {
            // Not a decodable image (or a transient decode failure) - staging still proceeds without a preview.
        }

        PendingAttachments.Add(new PendingImageAttachment(bytes, originalFileName, localId ?? Guid.NewGuid(), preview));
    }

    /// <summary>Embeds a <see cref="NoteImageTokenExtractor"/> placeholder token for
    /// <paramref name="localId"/> at <paramref name="caretIndex"/> in Body (Image Display Mode =
    /// Inline: an attached image renders at its original text-cursor position). Called before
    /// <see cref="AddPendingAttachment"/> stages the same <paramref name="localId"/>, so the token
    /// and the staged attachment share an identity until <see cref="SavePendingAttachmentsAsync"/>
    /// replaces it with the real AttachmentId (or strips it on a failed Save).</summary>
    public void InsertImagePlaceholder(int caretIndex, Guid localId)
    {
        var token = NoteImageTokenExtractor.Build(localId);
        var index = Math.Clamp(caretIndex, 0, Body.Length);
        Body = Body.Insert(index, token);
    }

    [RelayCommand]
    private void RemovePendingAttachment(PendingImageAttachment? attachment)
    {
        if (attachment is not null)
        {
            PendingAttachments.Remove(attachment);
        }
    }

    private bool CanSave() => !string.IsNullOrWhiteSpace(Body);

    /// <summary>
    /// Extracts hashtags and URLs directly from Body (both are auto-derived, spec sections 3.1 and
    /// 8 - there is no separate manual URL input), saves any pending image attachments (skipping -
    /// not failing on - any that don't pass validation), then creates or updates the Note, and
    /// finally recalculates the TickerMetadata.Notes cache for the affected ticker(s) (spec section 4.4).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSave), AllowConcurrentExecutions = false)]
    private async Task SaveAsync()
    {
        if (!CanSave())
        {
            return;
        }

        var hashtags = HashtagExtractor.Extract(Body);
        var linkUrls = UrlExtractor.Extract(Body);
        var newlySavedAttachmentIds = await SavePendingAttachmentsAsync();
        var now = DateTime.Now;
        var previousRelatedTicker = _originalRelatedTicker;

        Note noteToSave;
        if (_editingNoteId is { } id)
        {
            noteToSave = new Note(id, Body, _originalCreatedAt, now)
            {
                ChartAnchorDate = ChartAnchorDate,
                ChartTimeframe = ChartTimeframe,
                RelatedTicker = RelatedTicker,
                Hashtags = hashtags,
                AttachmentIds = _existingAttachmentIds.AddRange(newlySavedAttachmentIds),
                LinkUrls = linkUrls,
                IsPinned = IsPinned,
                QuotedNoteId = _quotedNoteId,
                ParentNoteId = _parentNoteId,
            };
            await _noteRepository.UpdateAsync(noteToSave);
        }
        else
        {
            noteToSave = new Note(Guid.NewGuid(), Body, now, now)
            {
                ChartAnchorDate = ChartAnchorDate,
                ChartTimeframe = ChartTimeframe,
                RelatedTicker = RelatedTicker,
                Hashtags = hashtags,
                AttachmentIds = newlySavedAttachmentIds,
                LinkUrls = linkUrls,
                IsPinned = IsPinned,
                QuotedNoteId = _quotedNoteId,
                ParentNoteId = _parentNoteId,
            };
            await _noteRepository.CreateAsync(noteToSave);
        }

        await RecalculateCacheForAffectedTickersAsync(previousRelatedTicker, noteToSave.RelatedTicker);

        SavedNote = noteToSave;
        _editingNoteId = noteToSave.Id;
        _originalCreatedAt = noteToSave.CreatedAt;
        _originalRelatedTicker = noteToSave.RelatedTicker;
        _existingAttachmentIds = noteToSave.AttachmentIds;
        _quotedNoteId = noteToSave.QuotedNoteId;
        _parentNoteId = noteToSave.ParentNoteId;
        PendingAttachments.Clear();

        _logger?.LogDebug("Saved Note {NoteId} (ticker: {Ticker}).", noteToSave.Id, noteToSave.RelatedTicker ?? "(none)");
    }

    private async Task<ImmutableArray<Guid>> SavePendingAttachmentsAsync()
    {
        AttachmentErrors.Clear();
        var savedIds = ImmutableArray.CreateBuilder<Guid>();

        foreach (var pending in PendingAttachments.ToList())
        {
            var result = await _attachmentRepository.SaveAsync(pending.Bytes, pending.OriginalFileName);
            if (result.Success && result.Attachment is not null)
            {
                savedIds.Add(result.Attachment.Id);
                Body = NoteImageTokenExtractor.ReplaceToken(Body, pending.LocalId, result.Attachment.Id);
            }
            else
            {
                AttachmentErrors.Add($"{pending.OriginalFileName}: {result.FailureReason}");
                Body = NoteImageTokenExtractor.RemoveToken(Body, pending.LocalId);
            }
        }

        return savedIds.ToImmutable();
    }

    private async Task RecalculateCacheForAffectedTickersAsync(string? previousTicker, string? currentTicker)
    {
        if (!string.IsNullOrWhiteSpace(currentTicker))
        {
            await _cacheSynchronizer.RecalculateNotesCacheAsync(currentTicker);
        }

        if (!string.IsNullOrWhiteSpace(previousTicker) &&
            !string.Equals(previousTicker, currentTicker, StringComparison.OrdinalIgnoreCase))
        {
            await _cacheSynchronizer.RecalculateNotesCacheAsync(previousTicker);
        }
    }
}
