using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Notes;
using StockAnalyzer.Core.Services.Notes;

namespace StockAnalyzer.Avalonia.ViewModels.Notes;

/// <summary>Which tab of the trash dialog should be selected when it opens (fix request: the
/// "Trash" and "Orphaned Files" toolbar icons in NoteTimelineView now each open this same dialog,
/// pre-selected to their own tab, instead of always opening to "Deleted").</summary>
public enum NoteTrashInitialTab
{
    Deleted,
    Orphaned,
}

/// <summary>
/// A single logically-deleted Note in the trash view, with Restore/Permanently-delete commands
/// relayed back to the owning <see cref="NoteTrashViewModel"/> (same callback pattern as
/// <c>NoteTimelineItemViewModel</c>, Step 90-1-14). No mutable/observable state of its own, so
/// this is a plain class rather than an <c>ObservableObject</c>.
/// </summary>
public sealed class NoteTrashItemViewModel
{
    public Note Note { get; }

    public IAsyncRelayCommand RestoreCommand { get; }

    public IAsyncRelayCommand PermanentlyDeleteCommand { get; }

    /// <summary>Font size/foreground color applied to this card's text (Settings &gt; Notes,
    /// independent of Settings &gt; Theme/Fonts), captured at construction time from
    /// <see cref="INotesSettingsManager"/> - same constructor-time-snapshot rationale as
    /// <c>NoteTimelineItemViewModel.BodyFontSize</c>/<c>BodyTextColor</c>.</summary>
    public double BodyFontSize { get; }
    public IndicatorColor BodyTextColor { get; }
    public IndicatorColor BodyBackgroundColor { get; }

    public NoteTrashItemViewModel(
        Note note,
        Func<NoteTrashItemViewModel, Task> onRestoreRequested,
        Func<NoteTrashItemViewModel, Task> onPermanentlyDeleteRequested,
        double bodyFontSize,
        IndicatorColor bodyTextColor,
        IndicatorColor bodyBackgroundColor)
    {
        Note = note;
        RestoreCommand = new AsyncRelayCommand(() => onRestoreRequested(this));
        PermanentlyDeleteCommand = new AsyncRelayCommand(() => onPermanentlyDeleteRequested(this));
        BodyFontSize = bodyFontSize;
        BodyTextColor = bodyTextColor;
        BodyBackgroundColor = bodyBackgroundColor;
    }
}

/// <summary>
/// Drives the Ticker Note trash view (spec section 9.3): lists logically-deleted Notes, and
/// supports individual restore, individual permanent deletion, and emptying the trash entirely.
/// </summary>
public partial class NoteTrashViewModel : ViewModelBase, IDisposable
{
    private readonly NoteRepository _noteRepository;
    private readonly TickerMetadataNotesCacheSynchronizer _cacheSynchronizer;
    private readonly INotesSettingsManager _notesSettingsManager;
    private readonly PropertyChangedEventHandler _notesSettingsChangedHandler;
    private bool _isDisposed;

    /// <summary>Font size/foreground color for every piece of text this dialog renders (Settings &gt;
    /// Notes, independent of Settings &gt; Theme/Fonts). Exposed here (not just on
    /// <see cref="NoteTrashItemViewModel"/>) because the "Orphaned Files" tab's list items are plain
    /// strings with no ViewModel of their own - NoteTrashView.axaml binds those TextBlocks to these
    /// properties via an ancestor binding back to this ViewModel instead.</summary>
    [ObservableProperty]
    private double _bodyFontSize;

    [ObservableProperty]
    private IndicatorColor _bodyTextColor;

    [ObservableProperty]
    private IndicatorColor _bodyBackgroundColor;

    /// <summary>Which TabItem the dialog's TabControl should show first (fix request), set by
    /// DialogService right after resolving this Transient-registered view model, before the window
    /// is shown. Defaults to the "Deleted" tab (index 0).</summary>
    [ObservableProperty]
    private int _selectedTabIndex;

    public ObservableCollection<NoteTrashItemViewModel> DeletedNotes { get; } = new();

    /// <summary>
    /// File names detected by the most recent app-startup orphaned-attachment scan (spec section
    /// 4.5). Populated once, from <see cref="OrphanedAttachmentScanResultHolder"/>, when the dialog
    /// opens - this tab only ever presents the last scan's result; it does not re-scan (a manual
    /// "re-scan" affordance is a candidate for a future pass, not required by Step 90-1-20's scope).
    /// Detection only: nothing in this view model ever deletes a file (spec section 4.5).
    /// </summary>
    public ObservableCollection<string> OrphanedAttachmentFileNames { get; } = new();

    public NoteTrashViewModel(
        NoteRepository noteRepository,
        TickerMetadataNotesCacheSynchronizer cacheSynchronizer,
        OrphanedAttachmentScanResultHolder orphanedAttachmentHolder,
        INotesSettingsManager notesSettingsManager)
    {
        _noteRepository = noteRepository;
        _cacheSynchronizer = cacheSynchronizer;
        _notesSettingsManager = notesSettingsManager;
        _bodyFontSize = _notesSettingsManager.BodyFontSize;
        _bodyTextColor = _notesSettingsManager.BodyTextColor;
        _bodyBackgroundColor = _notesSettingsManager.BodyBackgroundColor;

        var report = orphanedAttachmentHolder.LatestReport;
        if (report is not null)
        {
            foreach (var fileName in report.OrphanedFileNames)
            {
                OrphanedAttachmentFileNames.Add(fileName);
            }
        }

        // Settings > Notes can change while this dialog is open, same as NoteTimelineViewModel's own
        // subscription - re-snapshot the appearance properties and rebuild DeletedNotes so its cards
        // pick up the new values too. Named delegate (not an inline lambda) so Dispose() can unsubscribe
        // it: INotesSettingsManager is a DI Singleton but this ViewModel is Transient (a new instance is
        // resolved every time the Trash/Orphaned Files dialog opens, per DialogService), so an
        // unsubscribed handler would otherwise accumulate on the Singleton for the app's entire lifetime.
        _notesSettingsChangedHandler = (_, _) =>
        {
            BodyFontSize = _notesSettingsManager.BodyFontSize;
            BodyTextColor = _notesSettingsManager.BodyTextColor;
            BodyBackgroundColor = _notesSettingsManager.BodyBackgroundColor;
            _ = RefreshAsync();
        };
        _notesSettingsManager.PropertyChanged += _notesSettingsChangedHandler;

        _ = RefreshAsync();
    }

    /// <summary>Unsubscribes from the Singleton <see cref="INotesSettingsManager"/> so this
    /// Transient-registered instance can be garbage-collected once its dialog closes. MUST be called
    /// by the dialog's owner (<c>DialogService.ShowNoteTrashDialogAsync</c>) after the dialog closes.</summary>
    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _notesSettingsManager.PropertyChanged -= _notesSettingsChangedHandler;

        GC.SuppressFinalize(this);
    }

    private Task _refreshChain = Task.CompletedTask;
    private readonly object _refreshChainLock = new();

    /// <summary>
    /// Reloads the trash list. Calls are serialized onto a single chain (same rationale as
    /// <c>NoteTimelineViewModel.RefreshAsync</c>, Step 90-1-14): the constructor's fire-and-forget
    /// initial load and any later caller (a restore/delete action, a test) must not interleave
    /// their <see cref="DeletedNotes"/> Clear/Add mutations.
    /// </summary>
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
        var deleted = await _noteRepository.GetAllDeletedAsync(ct).ConfigureAwait(false);

        DeletedNotes.Clear();
        foreach (var note in deleted)
        {
            DeletedNotes.Add(new NoteTrashItemViewModel(
                note,
                HandleRestoreRequestedAsync,
                HandlePermanentlyDeleteRequestedAsync,
                _notesSettingsManager.BodyFontSize,
                _notesSettingsManager.BodyTextColor,
                _notesSettingsManager.BodyBackgroundColor));
        }
    }

    /// <summary>Physically removes every Note currently in the trash, along with their attachment files (spec section 4.6).</summary>
    [RelayCommand]
    private async Task EmptyTrashAsync()
    {
        await _noteRepository.EmptyTrashAsync().ConfigureAwait(false);
        await RefreshAsync().ConfigureAwait(false);
    }

    private async Task HandleRestoreRequestedAsync(NoteTrashItemViewModel item)
    {
        await _noteRepository.RestoreAsync(item.Note.Id).ConfigureAwait(false);

        // spec section 4.4: restoring a Note is one of the five triggers that recalculates the
        // TickerMetadata.Notes preview cache for its ticker.
        if (!string.IsNullOrWhiteSpace(item.Note.RelatedTicker))
        {
            await _cacheSynchronizer.RecalculateNotesCacheAsync(item.Note.RelatedTicker).ConfigureAwait(false);
        }

        await RefreshAsync().ConfigureAwait(false);
    }

    private async Task HandlePermanentlyDeleteRequestedAsync(NoteTrashItemViewModel item)
    {
        // Permanent deletion of an already soft-deleted Note does not change which Note is the
        // "latest active" one for its ticker (it was already excluded from that calculation when
        // it was soft-deleted), so no cache recalculation trigger applies here (spec section 4.4
        // lists only create/edit/soft-delete/restore as triggers).
        await _noteRepository.PermanentlyDeleteAsync(item.Note.Id).ConfigureAwait(false);
        await RefreshAsync().ConfigureAwait(false);
    }
}
