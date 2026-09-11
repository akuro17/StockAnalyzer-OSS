using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using StockAnalyzer.Avalonia.ViewModels.Watchlist;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Views.Controls
{
    public class TagCellControl : UserControl
    {
        private readonly DockPanel _container;
        private readonly WrapPanel _wrapPanel;
        private readonly Button _addBtn;
        private System.Collections.Specialized.INotifyCollectionChanged? _activeCollection;
        private readonly System.Collections.Specialized.NotifyCollectionChangedEventHandler? _collectionChangedHandler;
        private readonly StockAnalyzer.Avalonia.Services.IFontSettingsManager? _fontSettingsManager;
        private readonly System.ComponentModel.PropertyChangedEventHandler? _fontPropertyChangedHandler;

        public TagCellControl()
        {
            _container = new DockPanel
            {
                LastChildFill = true,
                VerticalAlignment = VerticalAlignment.Center
            };

            // Custom ToolTip with Chips
            var tooltipPanel = new ItemsControl
            {
                Padding = new Thickness(4)
            };
            tooltipPanel.Bind(ItemsControl.ItemsSourceProperty, 
                new global::Avalonia.Data.Binding(nameof(WatchlistItemViewModel.TagsList)));
            tooltipPanel.ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel { Orientation = Orientation.Horizontal });
            tooltipPanel.ItemTemplate = new FuncDataTemplate<string>((tag, _) =>
            {
                var border = new Border
                {
                    CornerRadius = new CornerRadius(10),
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(2),
                    Padding = new Thickness(6, 1, 6, 1)
                };
                border.Bind(Border.BackgroundProperty, border.GetResourceObservable("Brush.Background.Primary"));
                border.Bind(Border.BorderBrushProperty, border.GetResourceObservable("Brush.Border.Primary"));

                var text = new TextBlock { Text = tag, VerticalAlignment = VerticalAlignment.Center };
                text.Bind(TextBlock.FontSizeProperty, text.GetResourceObservable("HelperFontSize"));
                text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable("Brush.Text.Primary"));
                border.Child = text;
                return border;
            });

            _container.SetValue(ToolTip.TipProperty, tooltipPanel);
            _container.Bind(ToolTip.BackgroundProperty, _container.GetResourceObservable("Brush.Background.Primary"));
            _container.Bind(ToolTip.BorderBrushProperty, _container.GetResourceObservable("Brush.Border.Primary"));
            _container.SetValue(ToolTip.BorderThicknessProperty, new Thickness(1));

            _addBtn = new Button
            {
                Content = "+",
                Padding = new Thickness(0),
                MinWidth = 20,
                MinHeight = 20,
                CornerRadius = new CornerRadius(10),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center
            };
            _addBtn.Bind(Button.FontSizeProperty, _addBtn.GetResourceObservable("HelperFontSize"));
            _addBtn.Bind(Button.ForegroundProperty, _addBtn.GetResourceObservable("Brush.Text.Secondary"));
            DockPanel.SetDock(_addBtn, Dock.Right);

            _wrapPanel = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            var flyout = new Flyout
            {
                Placement = PlacementMode.BottomEdgeAlignedRight
            };

            var flyoutPanel = new StackPanel
            {
                Spacing = 6,
                MinWidth = 200,
                MaxWidth = 300
            };

            var editItemsControl = new ItemsControl
            {
                VerticalAlignment = VerticalAlignment.Center
            };
            editItemsControl.Bind(ItemsControl.ItemsSourceProperty, 
                new global::Avalonia.Data.Binding(nameof(WatchlistItemViewModel.TagsList)));

            editItemsControl.ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel 
            { 
                Orientation = Orientation.Horizontal 
            });

            editItemsControl.ItemTemplate = new FuncDataTemplate<string>((tag, _) =>
            {
                var border = new Border
                {
                    CornerRadius = new CornerRadius(10),
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(2),
                    Padding = new Thickness(6, 1, 6, 1)
                };
                border.Bind(Border.BackgroundProperty, border.GetResourceObservable("Brush.Background.Primary"));
                border.Bind(Border.BorderBrushProperty, border.GetResourceObservable("Brush.Border.Primary"));

                var stack = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4
                };

                var text = new TextBlock
                {
                    Text = tag,
                    VerticalAlignment = VerticalAlignment.Center
                };
                text.Bind(TextBlock.FontSizeProperty, text.GetResourceObservable("HelperFontSize"));
                text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable("Brush.Text.Primary"));

                var deleteBtn = new Button
                {
                    Content = "×",
                    Padding = new Thickness(0),
                    MinWidth = 14,
                    MinHeight = 14,
                    CornerRadius = new CornerRadius(7),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 9
                };
                deleteBtn.Bind(Button.ForegroundProperty, deleteBtn.GetResourceObservable("Brush.Text.Secondary"));

                deleteBtn.Click += (s, e) =>
                {
                    if (DataContext is WatchlistItemViewModel vm)
                    {
                        vm.TagsList.Remove(tag);
                    }
                };

                stack.Children.Add(text);
                stack.Children.Add(deleteBtn);
                border.Child = stack;
                return border;
            });

            var inputTb = new TextBox
            {
                MinWidth = 120,
                Margin = new Thickness(2,0,2,0),
                VerticalAlignment = VerticalAlignment.Center,
                Watermark = global::StockAnalyzer.Avalonia.Services.LocalizationManager.Instance["Settings_Tag_Watermark"]
            };

            inputTb.KeyDown += (sender, args) =>
            {
                if (sender is TextBox textBox && DataContext is WatchlistItemViewModel vm)
                {
                    if (args.Key == global::Avalonia.Input.Key.Enter || args.Key == global::Avalonia.Input.Key.OemComma)
                    {
                        var newTag = textBox.Text?.Trim();
                        if (!string.IsNullOrEmpty(newTag))
                        {
                            newTag = newTag.Replace(",", "");
                            if (!string.IsNullOrEmpty(newTag) && !vm.TagsList.Contains(newTag))
                            {
                                vm.TagsList.Add(newTag);
                            }
                        }
                        textBox.Text = string.Empty;
                        args.Handled = true;

                        if (args.Key == global::Avalonia.Input.Key.Enter)
                        {
                            flyout.Hide();
                        }
                    }
                }
            };

            inputTb.LostFocus += (sender, args) =>
            {
                if (sender is TextBox textBox && DataContext is WatchlistItemViewModel vm)
                {
                    var newTag = textBox.Text?.Trim();
                    if (!string.IsNullOrEmpty(newTag))
                    {
                        var parts = newTag.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var part in parts)
                        {
                            var trimmed = part.Trim();
                            if (!string.IsNullOrEmpty(trimmed) && !vm.TagsList.Contains(trimmed))
                            {
                                vm.TagsList.Add(trimmed);
                            }
                        }
                    }
                    textBox.Text = string.Empty;
                }
            };

            flyoutPanel.Children.Add(editItemsControl);
            flyoutPanel.Children.Add(inputTb);
            flyout.Content = flyoutPanel;
            _addBtn.Flyout = flyout;

            _container.Children.Add(_addBtn);
            _container.Children.Add(_wrapPanel);
            Content = _container;

            _collectionChangedHandler = (s, e) =>
            {
                var vm = DataContext as WatchlistItemViewModel;
                double w = _wrapPanel.Bounds.Width;
                if (double.IsNaN(w) || w <= 0) w = 100;
                RebuildChips(vm, w);
            };

            _wrapPanel.SizeChanged += (s, e) =>
            {
                var vm = DataContext as WatchlistItemViewModel;
                RebuildChips(vm, e.NewSize.Width);
            };

            if (Application.Current is App app && app.Services != null)
            {
                _fontSettingsManager = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetService<StockAnalyzer.Avalonia.Services.IFontSettingsManager>(app.Services);
                if (_fontSettingsManager != null)
                {
                    _fontPropertyChangedHandler = (s, e) =>
                    {
                        if (e.PropertyName == "DetailFontSize")
                        {
                            var vm = DataContext as WatchlistItemViewModel;
                            double w = _wrapPanel.Bounds.Width;
                            if (double.IsNaN(w) || w <= 0) w = Bounds.Width;
                            if (double.IsNaN(w) || w <= 0) w = 100;
                            RebuildChips(vm, w);
                        }
                    };
                    _fontSettingsManager.PropertyChanged += _fontPropertyChangedHandler;
                }
            }
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);

            if (_activeCollection != null && _collectionChangedHandler != null)
            {
                _activeCollection.CollectionChanged -= _collectionChangedHandler;
            }

            var vm = DataContext as WatchlistItemViewModel;
            _activeCollection = vm?.TagsList;

            if (_activeCollection != null && _collectionChangedHandler != null)
            {
                _activeCollection.CollectionChanged += _collectionChangedHandler;
            }

            double w = _wrapPanel.Bounds.Width;
            if (double.IsNaN(w) || w <= 0) w = 100;
            RebuildChips(vm, w);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            if (_activeCollection != null && _collectionChangedHandler != null)
            {
                _activeCollection.CollectionChanged -= _collectionChangedHandler;
                _activeCollection = null;
            }
            if (_fontSettingsManager != null && _fontPropertyChangedHandler != null)
            {
                _fontSettingsManager.PropertyChanged -= _fontPropertyChangedHandler;
            }
        }

        private void RebuildChips(WatchlistItemViewModel? vm, double cellWidth)
        {
            if (vm == null || vm.TagsList == null || vm.TagsList.Count == 0)
            {
                foreach (var child in _wrapPanel.Children)
                {
                    child.IsVisible = false;
                }
                return;
            }

            var tags = vm.TagsList;

            double currentFontSize = 12.0;
            if (Application.Current!.Resources.TryGetResource("DetailFontSize", Application.Current.ActualThemeVariant, out var resObj) && resObj is double dVal)
            {
                currentFontSize = dVal;
            }
            double fontSizeScale = currentFontSize / 12.0;

            double availableWidth = cellWidth - 25;
            if (availableWidth <= 0) availableWidth = 50;

            int fitCount = 0;
            double currentWidth = 0;
            double badgeWidth = 28 * fontSizeScale;

            for (int i = 0; i < tags.Count; i++)
            {
                double chipWidth = 10 * fontSizeScale;
                string tag = tags[i];
                for (int charIdx = 0; charIdx < tag.Length; charIdx++)
                {
                    chipWidth += ((tag[charIdx] < 256) ? 7.0 : 14.0) * fontSizeScale;
                }
                
                double neededWidth = currentWidth + chipWidth;
                
                if (i < tags.Count - 1)
                {
                    neededWidth += badgeWidth;
                }

                if (neededWidth <= availableWidth)
                {
                    currentWidth += chipWidth;
                    fitCount++;
                }
                else
                {
                    break;
                }
            }

            if (fitCount == 0) fitCount = 1;
            if (fitCount > tags.Count) fitCount = tags.Count;

            int childIdx = 0;

            for (int i = 0; i < fitCount; i++)
            {
                Border border;
                TextBlock text;
                if (childIdx < _wrapPanel.Children.Count)
                {
                    border = (Border)_wrapPanel.Children[childIdx];
                    text = (TextBlock)border.Child;
                    border.IsVisible = true;
                    text.Text = tags[i];
                    border.Classes.Set("TagChip", true);
                    border.Classes.Set("TagBadge", false);
                    text.Classes.Set("TagText", true);
                    text.Classes.Set("TagBadgeText", false);
                }
                else
                {
                    border = new Border();
                    border.Classes.Add("TagChip");

                    text = new TextBlock
                    {
                        Text = tags[i],
                    };
                    text.Classes.Add("TagText");
                    text.Bind(TextBlock.FontSizeProperty, text.GetResourceObservable("DetailFontSize"));

                    border.Child = text;
                    _wrapPanel.Children.Add(border);
                }
                childIdx++;
            }

            if (fitCount < tags.Count)
            {
                Border badgeBorder;
                TextBlock badgeText;
                if (childIdx < _wrapPanel.Children.Count)
                {
                    badgeBorder = (Border)_wrapPanel.Children[childIdx];
                    badgeText = (TextBlock)badgeBorder.Child;
                    badgeBorder.IsVisible = true;
                    badgeText.Text = $"+{tags.Count - fitCount}";
                    badgeBorder.Classes.Set("TagChip", false);
                    badgeBorder.Classes.Set("TagBadge", true);
                    badgeText.Classes.Set("TagText", false);
                    badgeText.Classes.Set("TagBadgeText", true);
                }
                else
                {
                    badgeBorder = new Border();
                    badgeBorder.Classes.Add("TagBadge");

                    badgeText = new TextBlock
                    {
                        Text = $"+{tags.Count - fitCount}",
                    };
                    badgeText.Classes.Add("TagBadgeText");
                    badgeText.Bind(TextBlock.FontSizeProperty, badgeText.GetResourceObservable("DetailFontSize"));

                    badgeBorder.Child = badgeText;
                    _wrapPanel.Children.Add(badgeBorder);
                }
                childIdx++;
            }

            for (int i = childIdx; i < _wrapPanel.Children.Count; i++)
            {
                _wrapPanel.Children[i].IsVisible = false;
            }
            _wrapPanel.InvalidateMeasure();
            _wrapPanel.InvalidateArrange();
            _container.InvalidateMeasure();
            _container.InvalidateArrange();
        }
    }
}
