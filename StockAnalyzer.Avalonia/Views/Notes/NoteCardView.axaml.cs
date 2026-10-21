using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Notes;
using StockAnalyzer.Core.Models.Notes;

namespace StockAnalyzer.Avalonia.Views.Notes;

public partial class NoteCardView : UserControl
{
    private NoteTimelineItemViewModel? _viewModel;

    /// <summary>The Inlines <see cref="RebuildBody"/> built, in selection-layout coordinates (Run = its text length, inline button =
    /// 1). The single source <see cref="SelectionOffsetMapper"/> reads, recorded at the single construction point so it can never
    /// diverge from what is drawn.</summary>
    private readonly List<BodyLayoutPiece> _bodyLayoutPieces = new();

    /// <summary>The body selection frozen when the context flyout last opened; the three menu items act on it, never on a fresh read,
    /// because the selection can be gone by the time a click arrives.</summary>
    private NoteSelectionSnapshot _selectionSnapshot = NoteSelectionSnapshot.Empty;

    /// <summary>x:Name of the body block in <c>NoteCardView.axaml</c>.</summary>
    private const string BodyTextBlockName = "BodyTextBlock";
    private const string CopyMenuItemName = "BodyCopyMenuItem";
    private const string ApplyToFilterMenuItemName = "BodyApplyToFilterMenuItem";
    private const string ApplyToSearchMenuItemName = "BodyApplyToSearchMenuItem";

    private SelectableTextBlock? _selectableBody;
    private MenuItem? _copyMenuItem;
    private MenuItem? _applyToFilterMenuItem;
    private MenuItem? _applyToSearchMenuItem;

    public NoteCardView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        WireBodySelection();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as NoteTimelineItemViewModel;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        RebuildBody();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NoteTimelineItemViewModel.BodySegments))
        {
            RebuildBody();
        }
    }

    /// <summary>
    /// Rebuilds BodyTextBlock.Inlines from the current ViewModel's BodySegments: plain segments
    /// become a Run; a clickable-hashtag segment becomes an InlineUIContainer wrapping a flattened
    /// Button whose Flyout offers "Apply to Filter"/"Apply to Search" (fix request: clicking a
    /// "#tag" inside the posted body); a clickable-URL segment becomes a flattened Button that opens
    /// the link directly via OpenUrlCommand, no Flyout (fix request: a URL inside the body should
    /// itself be the single clickable representation - scheme-stripped/length-capped display text
    /// already computed by BodySegments - replacing the old separate LinkUrls button row).
    /// Avalonia's TextBlock.Inlines has no XAML-bindable per-segment click support, so this
    /// construction has to happen here in code-behind rather than in the DataTemplate.
    /// </summary>
    private void RebuildBody()
    {
        _bodyLayoutPieces.Clear();
        _selectionSnapshot = NoteSelectionSnapshot.Empty;

        var bodyTextBlock = this.FindControl<TextBlock>(BodyTextBlockName);
        if (bodyTextBlock is null)
        {
            return;
        }

        bodyTextBlock.Inlines ??= new InlineCollection();
        bodyTextBlock.Inlines.Clear();

        if (_viewModel is null)
        {
            return;
        }

        var layoutOffset = 0;
        foreach (var segment in _viewModel.BodySegments)
        {
            Control? inlineControl = segment switch
            {
                { ClickableHashtag: { } hashtag } => CreateHashtagButton(segment.Text, hashtag),
                { ClickableUrl: { } url } => CreateUrlButton(segment.Text, url),
                { AttachedImageId: { } imageId } => CreateInlineImage(imageId),
                _ => null,
            };

            if (inlineControl is not null)
            {
                bodyTextBlock.Inlines.Add(new InlineUIContainer(inlineControl));

                // An embedded control occupies exactly one selection position; an inline image draws no text.
                var drawnText = segment.AttachedImageId is null ? segment.Text : string.Empty;
                _bodyLayoutPieces.Add(new BodyLayoutPiece(layoutOffset, 1, drawnText, true));
                layoutOffset += 1;
            }
            else
            {
                bodyTextBlock.Inlines.Add(new Run(segment.Text));
                _bodyLayoutPieces.Add(new BodyLayoutPiece(layoutOffset, segment.Text.Length, segment.Text, false));
                layoutOffset += segment.Text.Length;
            }
        }
    }

    /// <summary>Renders an inline attached image at its original text-cursor position (Task E, Note
    /// Tab Enhancements: Image Display Mode = Inline). Returns null - contributing nothing to the
    /// body, same as a dropped defensive-only unresolved image token - unless the mode is actually
    /// Inline AND the Bitmap already decoded into <see cref="NoteTimelineItemViewModel.ThumbnailsByAttachmentId"/>
    /// (Attachment List mode, or an attachment beyond the bottom row's own render limit, both
    /// silently degrade to "nothing here" rather than a broken image icon).
    /// sa_minimal_fix (Note Tab Polish round, bug #5 - "Inline mode's image can't be clicked at
    /// all"): wrapped in the same flattened "BodyInlineLink" Button + <see cref="ThumbnailButton_Click"/>
    /// pattern as the Bottom row's thumbnails, instead of a bare unwrapped Image - a plain Image
    /// control has no click affordance whatsoever, which is why the inline image never responded to
    /// clicks (a distinct root cause from the Bottom row's own click-wiring bug fixed earlier in this
    /// round). DataContext is set directly to the resolved NoteThumbnailItem so
    /// ThumbnailButton_Click's existing `sender is Button { DataContext: NoteThumbnailItem }` check
    /// works unchanged, without needing an ItemsControl/DataTemplate.</summary>
    private Button? CreateInlineImage(Guid imageId)
    {
        if (_viewModel is not { } viewModel ||
            viewModel.ImageDisplayMode != NoteImageDisplayMode.Inline ||
            !viewModel.ThumbnailsByAttachmentId.TryGetValue(imageId, out var thumbnail))
        {
            return null;
        }

        var button = new Button
        {
            Content = new Image
            {
                Source = thumbnail.Bitmap,
                Width = viewModel.ThumbnailSizePixels,
                Height = viewModel.ThumbnailSizePixels,
                Stretch = Stretch.UniformToFill,
            },
            DataContext = thumbnail,
        };
        button.Classes.Add("BodyInlineLink");
        button.Click += ThumbnailButton_Click;
        return button;
    }

    /// <summary>sa_minimal_fix (Note Tab Polish round, "clicking a thumbnail does nothing"): reads
    /// the clicked Button's own DataContext (the NoteThumbnailItem - set by the Bottom row's
    /// ItemsControl.ItemTemplate, or directly by <see cref="CreateInlineImage"/> for an inline image)
    /// directly, rather than routing through a Command="{Binding $parent[ItemsControl]...}" XAML
    /// binding - see the comment on the thumbnail row in NoteCardView.axaml for why. Shared by both
    /// the Bottom row and the Inline-mode body image so there is exactly one "open the original file"
    /// click handler.</summary>
    private void ThumbnailButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: NoteThumbnailItem thumbnail })
        {
            _viewModel?.OpenAttachmentOriginalCommand.Execute(thumbnail);
        }
    }

    private static Button CreateFlattenedInlineButton(string displayText)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = displayText, FontWeight = FontWeight.Bold },
        };
        button.Classes.Add("BodyInlineLink");
        return button;
    }

    private Button CreateHashtagButton(string displayText, string hashtag)
    {
        var button = CreateFlattenedInlineButton(displayText);
        if (_viewModel is { } viewModel)
        {
            var ic = viewModel.HashtagColor;
            button.Foreground = new SolidColorBrush(Color.FromArgb(ic.A, ic.R, ic.G, ic.B));
        }

        var filterItem = new MenuItem { Header = LocalizationManager.Instance["Note_Hashtag_ApplyToFilter"] };
        filterItem.Click += (_, _) => _viewModel?.RequestHashtagFilter(hashtag);

        var searchItem = new MenuItem { Header = LocalizationManager.Instance["Note_Hashtag_ApplyToSearch"] };
        searchItem.Click += (_, _) => _viewModel?.RequestHashtagSearch(hashtag);

        button.Flyout = new MenuFlyout { ItemsSource = new[] { filterItem, searchItem } };

        return button;
    }

    private Button CreateUrlButton(string displayText, string url)
    {
        var button = CreateFlattenedInlineButton(displayText);
        if (_viewModel is { } viewModel)
        {
            var ic = viewModel.UrlColor;
            button.Foreground = new SolidColorBrush(Color.FromArgb(ic.A, ic.R, ic.G, ic.B));
        }
        button.Click += (_, _) => _viewModel?.OpenUrlCommand.Execute(url);
        return button;
    }

    // ---- body text selection (Notes text selection feature) ----

    /// <summary>Hooks the body's selection behaviour: the three context-menu items, the flyout-opening snapshot and the tunnelling
    /// Ctrl+C / Ctrl+A handler (tunnelling so it runs before SelectableTextBlock's own key handling, which would copy the skewed
    /// SelectedText - see SelectionOffsetMapper).</summary>
    private void WireBodySelection()
    {
        if (this.FindControl<SelectableTextBlock>(BodyTextBlockName) is not { } bodyTextBlock)
        {
            return;
        }

        _selectableBody = bodyTextBlock;
        bodyTextBlock.AddHandler(KeyDownEvent, BodyTextBlock_KeyDown, RoutingStrategies.Tunnel);

        if (bodyTextBlock.ContextFlyout is not MenuFlyout flyout)
        {
            return;
        }

        _copyMenuItem = FindFlyoutItem(flyout, CopyMenuItemName);
        _applyToFilterMenuItem = FindFlyoutItem(flyout, ApplyToFilterMenuItemName);
        _applyToSearchMenuItem = FindFlyoutItem(flyout, ApplyToSearchMenuItemName);
        if (_copyMenuItem is null || _applyToFilterMenuItem is null || _applyToSearchMenuItem is null)
        {
            // A markup edit that renames or drops an item must not silently switch the feature off.
            Serilog.Log.Error("{View}: the body context menu lacks one of {Copy}, {Filter}, {Search}; text selection actions are unavailable",
                nameof(NoteCardView), CopyMenuItemName, ApplyToFilterMenuItemName, ApplyToSearchMenuItemName);
            return;
        }

        _copyMenuItem.Click += (_, _) =>
        {
            if (_selectionSnapshot.CanCopy)
            {
                CopyTextToClipboard(_selectionSnapshot.RawText);
            }
        };
        _applyToFilterMenuItem.Click += (_, _) =>
        {
            if (_selectionSnapshot.CanApplyToFilter)
            {
                _viewModel?.ApplySelectionToFilter(_selectionSnapshot);
            }
        };
        _applyToSearchMenuItem.Click += (_, _) =>
        {
            if (_selectionSnapshot.CanApplyToSearch)
            {
                _viewModel?.ApplySelectionToSearch(_selectionSnapshot);
            }
        };

        flyout.Opening += BodyContextFlyout_Opening;
    }

    /// <summary>The text DRAWN inside the current body selection (hashtag / URL button text included), read through the layout-offset
    /// map instead of the control's SelectedText.</summary>
    private string ReadSelectedBodyText()
    {
        return _selectableBody is { } block
            ? SelectionOffsetMapper.Build(_bodyLayoutPieces, block.SelectionStart, block.SelectionEnd)
            : string.Empty;
    }

    private static MenuItem? FindFlyoutItem(MenuFlyout flyout, string name) =>
        flyout.Items.OfType<MenuItem>().FirstOrDefault(item => item.Name == name);

    /// <summary>Freezes the selection into <see cref="_selectionSnapshot"/> and sets each item's enabled state and disabled reason
    /// (null while enabled - SA_UI_INTERACTION section 29; ToolTip.ShowOnDisabled is declared in the XAML). The snapshot is not
    /// reset when the flyout closes: an item can only be clicked while the flyout is open, the next Opening replaces it, and a
    /// close-before-click ordering could otherwise lose it.</summary>
    private void BodyContextFlyout_Opening(object? sender, EventArgs e)
    {
        var raw = ReadSelectedBodyText();
        _selectionSnapshot = _viewModel?.EvaluateSelection(raw) ?? NoteSelectionTextRules.EvaluateCopyOnly(raw);

        ApplyMenuState(_copyMenuItem, _selectionSnapshot.CanCopy, _selectionSnapshot.CopyDisabledReason);
        ApplyMenuState(_applyToFilterMenuItem, _selectionSnapshot.CanApplyToFilter, _selectionSnapshot.FilterDisabledReason);
        ApplyMenuState(_applyToSearchMenuItem, _selectionSnapshot.CanApplyToSearch, _selectionSnapshot.SearchDisabledReason);
    }

    private static void ApplyMenuState(MenuItem? item, bool isEnabled, string? disabledReason)
    {
        if (item is null)
        {
            return;
        }

        item.IsEnabled = isEnabled;
        ToolTip.SetTip(item, disabledReason);
    }

    /// <summary>Ctrl+C (the platform's Copy gesture) writes the drawn selection text and Ctrl+A selects the whole layout, both
    /// replacing the control's own handling, which is wrong for a body containing inline buttons: Copy would write the skewed
    /// SelectedText (button text missing, nothing at all once the range ends past the text) and SelectAll ends one position short
    /// per button. A selection with no drawn text is left to the control (it does nothing, so the clipboard stays unchanged).</summary>
    private void BodyTextBlock_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || sender is not SelectableTextBlock block)
        {
            return;
        }

        var hotkeys = Application.Current?.PlatformSettings?.HotkeyConfiguration;

        if (MatchesHotkey(hotkeys?.Copy, e, Key.C))
        {
            // Handled only when something is written: a selection that draws no text (just an inline image) is left to the
            // control, which copies nothing, so the clipboard is never replaced by an empty string.
            var text = block.SelectionStart != block.SelectionEnd ? ReadSelectedBodyText() : string.Empty;
            if (text.Length > 0)
            {
                CopyTextToClipboard(text);
                e.Handled = true;
            }
        }
        else if (MatchesHotkey(hotkeys?.SelectAll, e, Key.A))
        {
            var total = SelectionOffsetMapper.TotalLength(_bodyLayoutPieces);
            if (total > 0)
            {
                block.SelectionStart = 0;
                block.SelectionEnd = total;
                e.Handled = true;
            }
        }
    }

    /// <summary>True when <paramref name="e"/> matches one of the platform gestures; without a platform configuration falls back to
    /// Ctrl + <paramref name="fallbackKey"/>.</summary>
    private static bool MatchesHotkey(IReadOnlyList<KeyGesture>? gestures, KeyEventArgs e, Key fallbackKey) =>
        gestures is { Count: > 0 }
            ? gestures.Any(gesture => gesture.Matches(e))
            : e.Key == fallbackKey && e.KeyModifiers == KeyModifiers.Control;

    /// <summary>async void: an exception leaving this method would be raised on the UI synchronization context and end the process, so
    /// everything is inside try/catch (AsyncVoidHandlerGuardTests enforces this).</summary>
    private async void CopyTextToClipboard(string text)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                return;
            }

            await clipboard.SetTextAsync(text);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "{Handler} failed", nameof(CopyTextToClipboard));
        }
    }
}
