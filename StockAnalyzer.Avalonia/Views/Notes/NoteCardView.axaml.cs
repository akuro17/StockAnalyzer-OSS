using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Notes;

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
                _ => null,
            };

            bodyTextBlock.Inlines.Add(inlineControl is not null
                ? new InlineUIContainer(inlineControl)
                : new Run(segment.Text));
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
