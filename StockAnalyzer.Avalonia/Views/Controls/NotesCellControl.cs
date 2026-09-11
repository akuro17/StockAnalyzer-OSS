using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Messaging;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.ViewModels.Watchlist;

namespace StockAnalyzer.Avalonia.Views.Controls
{
    /// <summary>
    /// Read-only preview of the Notes tab's latest article for this ticker (auto-derived by
    /// TickerMetadataNotesCacheSynchronizer). The "+" button navigates to the Notes tab filtered to
    /// this ticker (<see cref="NavigateToNoteTimelineRequestedMessage"/>), replacing the column's
    /// former behavior of opening the Ticker Dashboard's Note editor - that editing capability has
    /// moved to <see cref="ReminderCellControl"/> as the independent Reminder feature.
    /// </summary>
    public class NotesCellControl : UserControl
    {
        private readonly DockPanel _container;
        private readonly TextBlock _textBlock;
        private readonly Button _openBtn;

        public NotesCellControl()
        {
            _container = new DockPanel
            {
                LastChildFill = true,
                VerticalAlignment = VerticalAlignment.Center
            };

            _openBtn = new Button
            {
                Content = "+",
                Padding = new Thickness(0),
                MinWidth = 20,
                MinHeight = 20,
                CornerRadius = new CornerRadius(10),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0)
            };
            _openBtn.Bind(Button.FontSizeProperty, _openBtn.GetResourceObservable("HelperFontSize"));
            _openBtn.Bind(Button.ForegroundProperty, _openBtn.GetResourceObservable("Brush.Text.Secondary"));
            DockPanel.SetDock(_openBtn, Dock.Right);

            _openBtn.Click += (sender, args) =>
            {
                if (DataContext is WatchlistItemViewModel vm && !string.IsNullOrEmpty(vm.Symbol))
                {
                    WeakReferenceMessenger.Default.Send(new NavigateToNoteTimelineRequestedMessage(vm.Symbol));
                }
            };

            _textBlock = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(4, 0)
            };
            _textBlock.Bind(TextBlock.TextProperty, new global::Avalonia.Data.Binding(nameof(WatchlistItemViewModel.DisplayNotes)));
            _textBlock.Bind(TextBlock.ForegroundProperty, _textBlock.GetResourceObservable("Brush.Text.Primary"));
            // Tooltip shows the full, unconverted preview text (real newlines preserved), unlike the
            // single-line DisplayNotes shown in the cell itself.
            _textBlock.Bind(ToolTip.TipProperty, new global::Avalonia.Data.Binding(nameof(WatchlistItemViewModel.Notes)));

            _container.Children.Add(_openBtn);
            _container.Children.Add(_textBlock);

            Content = _container;
        }
    }
}
