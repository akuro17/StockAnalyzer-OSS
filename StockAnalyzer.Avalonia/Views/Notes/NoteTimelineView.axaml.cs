using System;
using System.ComponentModel;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using StockAnalyzer.Avalonia.ViewModels.Notes;

namespace StockAnalyzer.Avalonia.Views.Notes;

public partial class NoteTimelineView : UserControl
{
    /// <summary>Infinite scroll (sa_implement, Notes メインタイムライン インフィニットスクロール Task 5)
    /// fires <see cref="NoteTimelineViewModel.LoadMoreVisibleNotesAsync"/> once the unfilled space
    /// below the last currently-loaded card drops to this many pixels, so the next page is already
    /// appended just before the user would otherwise reach the bottom - rather than only after they
    /// hit it and see an empty gap.</summary>
    private const double LoadMoreScrollThreshold = 300.0;

    private NoteTimelineViewModel? _viewModel;
    private Vector _savedListScrollOffset;
    private bool _scrollRestorePending;

    public NoteTimelineView()
    {
        InitializeComponent();

        // AutoCompleteBox.ItemFilter/ItemSelector are delegate-typed CLR properties, not simple
        // Avalonia styled properties - there is no XAML markup for them, so the unified search box's
        // custom Kind-agnostic filter/select-text behavior (sa_implement Task 3) is wired here.
        if (this.FindControl<AutoCompleteBox>("SearchSuggestionBox") is { } searchBox)
        {
            searchBox.ItemFilter = FilterSearchSuggestion;
            searchBox.ItemSelector = SelectSearchSuggestionText;
            searchBox.TextChanged += SearchSuggestionBox_TextChanged;
        }

        // Infinite scroll (Task 5): TimelineScrollViewer is also used by OnViewModelPropertyChanged
        // below for the unrelated "preserve scroll position across a detail-page visit" feature; both
        // subscriptions are independent and never interfere with each other. No matching
        // detach/unsubscribe is needed here (unlike _viewModel.PropertyChanged below) - the
        // ScrollViewer is a child control owned by this View, not a DI Singleton, so both are
        // garbage-collected together when a torn-off tab discards this View instance.
        if (this.FindControl<ScrollViewer>("TimelineScrollViewer") is { } timelineScrollViewer)
        {
            timelineScrollViewer.ScrollChanged += TimelineScrollViewer_ScrollChanged;
        }

        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as NoteTimelineViewModel;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    /// <summary>
    /// NoteTimelineViewModel is registered Singleton (ServiceCollectionExtensions.cs) and resolved
    /// again by ITabRegistry every time the "NoteTimeline" tab is materialized - including
    /// ITearOffService.TearOff, which removes this View's container from MainWindow's tab
    /// collection and builds a brand-new NoteTimelineView for the DetachedWindow around the same
    /// Singleton instance. Without unsubscribing here, the old (now-discarded) View stays
    /// permanently referenced by the Singleton's PropertyChanged handler list and leaks every time
    /// the tab is torn off - same pattern TagCellControl/HeatmapTileControl/ViewSwitcher already
    /// guard against for their own long-lived subscriptions.
    /// </summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
    }

    /// <summary>
    /// Preserves the timeline list's scroll position across a visit to an individual Note's detail
    /// page (Feature A, sa_implementation_plan.md Task A3). NoteTimelineViewModel only tracks
    /// whether a detail page is open (SelectedDetailItem, a plain nullable reference) - it cannot
    /// hold Avalonia types such as Vector/ScrollViewer itself (Primitive Barrier,
    /// SA_ARCHITECTURE_RULES.md), so the actual ScrollViewer.Offset capture/restore is this View's
    /// own concern, done by watching that one property from here instead.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(NoteTimelineViewModel.SelectedDetailItem))
        {
            return;
        }

        if (this.FindControl<ScrollViewer>("TimelineScrollViewer") is not { } scrollViewer)
        {
            return;
        }

        if (_viewModel?.SelectedDetailItem is not null)
        {
            // Detail page just opened: remember where the list was scrolled to - unless a restore
            // from a previous "Back" click is still in flight (sa_constraint_report.md Low [Nit]).
            // In that window scrollViewer.Offset does not yet reflect the real list position (the
            // restore below is deliberately deferred a dispatcher tick past the ScrollViewer
            // becoming visible again), so capturing it now would overwrite the correct saved value
            // with a stale, not-yet-restored one. The already-saved value is still the right one to
            // keep, since the user has not visually seen the list settle since that earlier "Back".
            if (!_scrollRestorePending)
            {
                _savedListScrollOffset = scrollViewer.Offset;
            }
        }
        else
        {
            // Back to list: the ScrollViewer's IsVisible binding flips to true in this same
            // notification, but its Extent/Viewport only reflect that after the next layout pass -
            // setting Offset synchronously here would clamp against the still-stale (hidden) size.
            // Posting defers the restore until after that pass; _scrollRestorePending marks the
            // window during which a re-opened detail page must not treat Offset as trustworthy.
            _scrollRestorePending = true;
            var savedOffset = _savedListScrollOffset;
            Dispatcher.UIThread.Post(() =>
            {
                scrollViewer.Offset = savedOffset;
                _scrollRestorePending = false;
            });
        }
    }

    /// <summary>Infinite scroll (sa_implement Task 5): once the space below the last loaded card
    /// shrinks to <see cref="LoadMoreScrollThreshold"/> pixels, requests the next page from the
    /// ViewModel. Guarded by <see cref="NoteTimelineViewModel.HasMoreVisibleNotes"/>/
    /// <see cref="NoteTimelineViewModel.IsLoadingMoreNotes"/> here purely to skip the distance
    /// calculation on every irrelevant scroll event once there's nothing left to load or a load is
    /// already in flight - LoadMoreVisibleNotesAsync itself re-checks both and is safe to call
    /// unconditionally. Also skipped while the detail page is open (SelectedDetailItem non-null):
    /// TimelineScrollViewer's IsVisible binding hides it then, but a stale ScrollChanged from just
    /// before that binding takes effect must not trigger a load for a list the user cannot see.</summary>
    private void TimelineScrollViewer_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_viewModel is not { SelectedDetailItem: null, HasMoreVisibleNotes: true, IsLoadingMoreNotes: false } viewModel)
        {
            return;
        }

        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        var distanceToBottom = scrollViewer.Extent.Height - scrollViewer.Viewport.Height - scrollViewer.Offset.Y;
        if (distanceToBottom <= LoadMoreScrollThreshold)
        {
            _ = viewModel.LoadMoreVisibleNotesAsync();
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>Matches the unified suggestion box's items by DisplayName regardless of Kind
    /// (Ticker/Watchlist/Portfolio/Filter/Tag all filter the same way), case-insensitive prefix match
    /// (fix request #2, 7th round) - the same behavior FilterMode="StartsWith" gives every other
    /// AutoCompleteBox in the app (MainWindow's Symbol box, the compose panel's Related-ticker box).
    /// A substring/Contains match was used here initially, but that let unrelated mid-string
    /// characters pull in noisy suggestions inconsistent with the rest of the app's search boxes.</summary>
    private static bool FilterSearchSuggestion(string? search, object? item) =>
        item is NoteScopeSuggestion suggestion &&
        (string.IsNullOrEmpty(search) || suggestion.DisplayName.StartsWith(search, StringComparison.OrdinalIgnoreCase));

    private static string SelectSearchSuggestionText(string? search, object? item) =>
        item is NoteScopeSuggestion suggestion ? suggestion.DisplayName : search ?? string.Empty;

    /// <summary>Fired whenever the unified search box's text changes, including when the user
    /// manually deletes it back to empty. Text is only OneWay-bound (View reflects ViewModel state,
    /// not the reverse), so without this, clearing the box by hand left the last applied Ticker/
    /// Scope/Tag filter active even though the box looked empty (fix request #6, 5th round).</summary>
    private void SearchSuggestionBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is AutoCompleteBox { Text: null or "" } && DataContext is NoteTimelineViewModel vm)
        {
            vm.ClearActiveSuggestionFilter();
        }
    }

    /// <summary>Enter key pressed in the unified search box with free-typed text and no suggestion
    /// picked: treated as a Ticker filter, same normalization as the old Ticker-only AutoCompleteBox
    /// (fix request #3) - there is no other kind information available from raw typed text.</summary>
    private void SearchSuggestionBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not AutoCompleteBox box) return;
        if (e.Key == Key.Enter)
        {
            if (DataContext is NoteTimelineViewModel vm)
            {
                SymbolAutoCompleteInputHelper.ApplyTypedSymbol(box.Text, symbol => vm.SelectSuggestion(NoteScopeSuggestion.ForTicker(symbol)));
            }
            box.IsDropDownOpen = false;
        }
    }

    /// <summary>Fires when a suggestion is picked from the dropdown (mouse click or keyboard
    /// confirm) - dispatches the Kind-specific apply/clear logic (sa_implement Task 2).</summary>
    private void SearchSuggestionBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is AutoCompleteBox { SelectedItem: NoteScopeSuggestion suggestion } && DataContext is NoteTimelineViewModel vm)
        {
            vm.SelectSuggestion(suggestion);
        }
    }

    /// <summary>
    /// Enter key pressed in the compose panel's Related-ticker AutoCompleteBox (fix request #3).
    /// Uses the box's own DataContext (NoteEditorViewModel, via the panel's
    /// DataContext="{Binding EditingNote}" override) rather than this.DataContext, which is the
    /// UserControl's NoteTimelineViewModel.
    /// </summary>
    private void RelatedTickerBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not AutoCompleteBox box) return;
        if (e.Key == Key.Enter)
        {
            if (box.DataContext is NoteEditorViewModel vm)
            {
                SymbolAutoCompleteInputHelper.ApplyTypedSymbol(box.Text, symbol => vm.RelatedTicker = symbol);
            }
            box.IsDropDownOpen = false;
        }
    }

    private void RelatedTickerBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is AutoCompleteBox box && box.SelectedItem is string selectedSymbol && box.DataContext is NoteEditorViewModel vm)
        {
            SymbolAutoCompleteInputHelper.ApplySelectedSymbol(selectedSymbol, vm.RelatedTicker, symbol => vm.RelatedTicker = symbol);
        }
    }

    /// <summary>
    /// "Attach Image" button in the compose panel (fix request #3, 2nd round): opens the OS file
    /// picker, reads each selected file's bytes, and stages them via
    /// NoteEditorViewModel.AddPendingAttachment - the same staging step SaveAsync already turns
    /// into real Attachment rows, so this is purely the missing UI entry point rather than new
    /// backend behavior.
    /// </summary>
    private async void AttachImageButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not NoteEditorViewModel vm) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Attach Image",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Images") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.webp" } }
            }
        });

        foreach (var file in files)
        {
            await using var stream = await file.OpenReadAsync();
            using var memoryStream = new MemoryStream();
            await stream.CopyToAsync(memoryStream);
            vm.AddPendingAttachment(memoryStream.ToArray(), file.Name);
        }
    }
}
