using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Messaging;
using StockAnalyzer.Avalonia.ViewModels.Watchlist;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Models.Watchlist;

namespace StockAnalyzer.Avalonia.Views.Controls
{
    /// <summary>
    /// Reminder cell: an editable free-text field backed by TickerMetadata.Reminder, opened via
    /// the Ticker Dashboard. Reuses the button/dialog-launch wiring formerly on
    /// <see cref="NotesCellControl"/>, adapted to Reminder now that Dashboard editing no longer
    /// touches Notes (which is an auto-derived Notes-tab preview instead).
    /// </summary>
    public class ReminderCellControl : UserControl
    {
        private readonly DockPanel _container;
        private readonly TextBlock _textBlock;
        private readonly Button _editBtn;

        public ReminderCellControl()
        {
            _container = new DockPanel
            {
                LastChildFill = true,
                VerticalAlignment = VerticalAlignment.Center
            };

            _editBtn = new Button
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
            _editBtn.Bind(Button.FontSizeProperty, _editBtn.GetResourceObservable("HelperFontSize"));
            _editBtn.Bind(Button.ForegroundProperty, _editBtn.GetResourceObservable("Brush.Text.Secondary"));
            DockPanel.SetDock(_editBtn, Dock.Right);

            _editBtn.Click += (sender, args) =>
            {
                if (DataContext is WatchlistItemViewModel vm && !string.IsNullOrEmpty(vm.Symbol))
                {
                    WeakReferenceMessenger.Default.Send(new OpenReminderDialogRequestedMessage(vm.Symbol));
                }
            };

            _textBlock = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(4, 0)
            };
            _textBlock.Bind(TextBlock.TextProperty, new global::Avalonia.Data.Binding(nameof(WatchlistItemViewModel.DisplayReminder)));
            _textBlock.Bind(TextBlock.ForegroundProperty, _textBlock.GetResourceObservable("Brush.Text.Primary"));
            // Tooltip shows the full, unconverted preview text, unlike the possibly-trimmed
            // DisplayReminder shown in the cell itself (mirrors NotesCellControl's convention).
            // Built as an explicit TextBlock (rather than binding ToolTip.FontSizeProperty on the
            // owner) because the popup is rooted at the TopLevel, not the owner's visual tree, so a
            // FontSize set on the owner never reaches it - only FontSize set on the Tip content itself does.
            var reminderTip = new TextBlock { Margin = new Thickness(WatchlistConstants.TooltipContentMargin) };
            reminderTip.Bind(TextBlock.TextProperty, new global::Avalonia.Data.Binding(nameof(WatchlistItemViewModel.Reminder)));
            reminderTip.Bind(TextBlock.FontSizeProperty, reminderTip.GetResourceObservable("TooltipFontSize"));
            _textBlock.SetValue(ToolTip.TipProperty, reminderTip);

            _container.Children.Add(_editBtn);
            _container.Children.Add(_textBlock);

            Content = _container;
        }
    }
}
