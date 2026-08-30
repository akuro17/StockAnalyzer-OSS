using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
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

    public NoteCardView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
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
        var bodyTextBlock = this.FindControl<TextBlock>("BodyTextBlock");
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

        foreach (var segment in _viewModel.BodySegments)
        {
            Control? inlineControl = segment switch
            {
                { ClickableHashtag: { } hashtag } => CreateHashtagButton(segment.Text, hashtag),
                { ClickableUrl: { } url } => CreateUrlButton(segment.Text, url),
                { AttachedImageId: { } imageId } => CreateInlineImage(imageId),
                _ => null,
            };

            bodyTextBlock.Inlines.Add(inlineControl is not null
                ? new InlineUIContainer(inlineControl)
                : new Run(segment.Text));
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
            Content = new TextBlock { Text = displayText, TextDecorations = TextDecorations.Underline },
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
}
