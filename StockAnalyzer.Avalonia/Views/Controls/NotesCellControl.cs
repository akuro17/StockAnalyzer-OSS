using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using StockAnalyzer.Avalonia.ViewModels.Watchlist;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using Microsoft.Extensions.DependencyInjection;

namespace StockAnalyzer.Avalonia.Views.Controls
{
    public class NotesCellControl : UserControl
    {
        private readonly DockPanel _container;
        private readonly TextBlock _textBlock;
        private readonly Button _addBtn;

        public NotesCellControl()
        {
            _container = new DockPanel
            {
                LastChildFill = true,
                VerticalAlignment = VerticalAlignment.Center
            };

            _addBtn = new Button
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
            _addBtn.Bind(Button.FontSizeProperty, _addBtn.GetResourceObservable("HelperFontSize"));
            _addBtn.Bind(Button.ForegroundProperty, _addBtn.GetResourceObservable("Brush.Text.Secondary"));
            DockPanel.SetDock(_addBtn, Dock.Right);

            _addBtn.Click += async (sender, args) =>
            {
                if (DataContext is WatchlistItemViewModel vm)
                {
                    var dialogService = App.Current?.Services?.GetService<IDialogService>();
                    if (dialogService != null)
                    {
                        await dialogService.ShowEditTickerNotesDialogAsync(
                            vm.Symbol,
                            vm.Long,
                            vm.ExitLong,
                            vm.StopLossLong,
                            vm.Short,
                            vm.ExitShort,
                            vm.StopLossShort,
                            vm.Notes,
                            (longVal, exitLong, stopLossLong, shortVal, exitShort, stopLossShort, notes) =>
                            {
                                vm.UpdateStrategyMetadata(longVal, exitLong, stopLossLong, shortVal, exitShort, stopLossShort, notes);
                            });
                    }
                }
            };

            _textBlock = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(4, 0)
            };
            _textBlock.Bind(TextBlock.TextProperty, new global::Avalonia.Data.Binding(nameof(WatchlistItemViewModel.DisplayNotes)));
            _textBlock.Bind(TextBlock.ForegroundProperty, _textBlock.GetResourceObservable("Brush.Text.Primary"));

            _container.Children.Add(_addBtn);
            _container.Children.Add(_textBlock);

            Content = _container;
        }
    }
}
