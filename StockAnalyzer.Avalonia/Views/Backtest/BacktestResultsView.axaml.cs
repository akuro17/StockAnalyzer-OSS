using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;

namespace StockAnalyzer.Avalonia.Views.Backtest;

public partial class BacktestResultsView : UserControl
{
    private BacktestResultsViewModel? _subscribedVm;

    public BacktestResultsView()
    {
        InitializeComponent();
        RebuildTradesHeader(null);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_subscribedVm != null)
        {
            _subscribedVm.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedVm = null;
        }

        if (DataContext is BacktestResultsViewModel vm)
        {
            _subscribedVm = vm;
            _subscribedVm.PropertyChanged += OnViewModelPropertyChanged;
            RebuildTradesHeader(vm.ActiveTradeColumns);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        LocalizationManager.Instance.LocaleChanged += OnLocaleChanged;
        if (DataContext is BacktestResultsViewModel vm)
        {
            if (_subscribedVm != vm)
            {
                if (_subscribedVm != null)
                {
                    _subscribedVm.PropertyChanged -= OnViewModelPropertyChanged;
                }
                _subscribedVm = vm;
                _subscribedVm.PropertyChanged += OnViewModelPropertyChanged;
            }
            RebuildTradesHeader(vm.ActiveTradeColumns);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        LocalizationManager.Instance.LocaleChanged -= OnLocaleChanged;
        if (_subscribedVm != null)
        {
            _subscribedVm.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedVm = null;
        }
        base.OnDetachedFromVisualTree(e);
    }

    private void OnLocaleChanged(object? sender, EventArgs e)
    {
        RebuildTradesHeader(_subscribedVm?.ActiveTradeColumns);
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BacktestResultsViewModel.ActiveTradeColumns) && sender is BacktestResultsViewModel vm)
        {
            RebuildTradesHeader(vm.ActiveTradeColumns);
        }
        else if ((e.PropertyName == nameof(BacktestResultsViewModel.ActiveSortColumnId) ||
                  e.PropertyName == nameof(BacktestResultsViewModel.ActiveSortOrder)) && sender is BacktestResultsViewModel)
        {
            UpdateTradeHeaderSortIndicators();
        }
    }

    public void RebuildTradesHeader(IReadOnlyList<string>? activeColumns)
    {
        var headerGrid = this.FindControl<Grid>("TradesHeaderGrid");
        if (headerGrid == null) return;

        headerGrid.ColumnDefinitions.Clear();
        headerGrid.Children.Clear();

        var cols = activeColumns != null && activeColumns.Count > 0
            ? activeColumns
            : BacktestTradeHistoryColumnChooserViewModel.KnownColumns.Select(c => c.Id).ToList();

        for (int i = 0; i < cols.Count; i++)
        {
            string colId = cols[i];
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto)
            {
                SharedSizeGroup = colId,
                MinWidth = GetTradeColumnMinWidth(colId)
            });

            var headerCell = CreateTradeHeaderCell(colId, i);
            Grid.SetColumn(headerCell, i);
            headerGrid.Children.Add(headerCell);
        }

        UpdateTradeHeaderSortIndicators();
    }

    private static double GetTradeColumnMinWidth(string colId) =>
        BacktestTradeHistoryColumnChooserViewModel.GetColumnMinWidth(colId);

    private Border CreateTradeHeaderCell(string colId, int colIndex)
    {
        var border = new Border();
        border.Classes.Add("TableHeaderCell");
        border.Classes.Add($"Col{colId}");
        border.MinWidth = GetTradeColumnMinWidth(colId);

        var cellGrid = new Grid();
        cellGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        cellGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        string headerKey = BacktestTradeHistoryColumnChooserViewModel.GetColumnHeaderKey(colId);
        string headerText = LocalizationManager.Instance[headerKey] ?? colId;

        var clickArea = new Grid
        {
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        clickArea.Tapped += (s, e) =>
        {
            if (DataContext is BacktestResultsViewModel vm)
            {
                vm.SortTradesCommand.Execute(colId);
            }
        };

        var align = (colId == "Side" || colId == "ForcedLiquidation")
            ? HorizontalAlignment.Center
            : (colId == "EntrySignal" || colId == "ExitReason")
                ? HorizontalAlignment.Left
                : HorizontalAlignment.Right;

        var contentStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = align,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 4
        };

        var tb = new TextBlock
        {
            Text = headerText,
            VerticalAlignment = VerticalAlignment.Center
        };
        tb.Classes.Add("ColumnHeader");
        contentStack.Children.Add(tb);

        var sortTb = new TextBlock
        {
            Tag = colId
        };
        sortTb.Classes.Add("SortIndicator");
        contentStack.Children.Add(sortTb);

        clickArea.Children.Add(contentStack);
        Grid.SetColumn(clickArea, 0);
        cellGrid.Children.Add(clickArea);

        var thumb = new Thumb
        {
            Tag = colIndex.ToString(CultureInfo.InvariantCulture)
        };
        thumb.Classes.Add("ColumnResizer");
        thumb.DragDelta += OnTradeColumnDragDelta;
        thumb.DoubleTapped += OnTradeHeaderDoubleTapped;
        Grid.SetColumn(thumb, 1);
        cellGrid.Children.Add(thumb);

        border.Child = cellGrid;
        return border;
    }

    private void UpdateTradeHeaderSortIndicators()
    {
        var headerGrid = this.FindControl<Grid>("TradesHeaderGrid");
        if (headerGrid == null || DataContext is not BacktestResultsViewModel vm) return;

        foreach (var child in headerGrid.Children)
        {
            if (child is Border cellBorder && cellBorder.Child is Grid cellGrid)
            {
                foreach (var cellChild in cellGrid.Children)
                {
                    if (cellChild is Grid clickArea && clickArea.Children.FirstOrDefault() is StackPanel stack)
                    {
                        var sortTb = stack.Children.OfType<TextBlock>().FirstOrDefault(t => t.Classes.Contains("SortIndicator"));
                        if (sortTb != null && sortTb.Tag is string colId)
                        {
                            if (string.Equals(vm.ActiveSortColumnId, colId, StringComparison.OrdinalIgnoreCase))
                            {
                                sortTb.Text = vm.ActiveSortOrder switch
                                {
                                    TradeSortOrder.Ascending => "▲",
                                    TradeSortOrder.Descending => "▼",
                                    _ => string.Empty
                                };
                            }
                            else
                            {
                                sortTb.Text = string.Empty;
                            }
                        }
                    }
                }
            }
        }
    }

    private void OnTradeColumnDragDelta(object? sender, VectorEventArgs e)
    {
        if (sender is Thumb thumb && thumb.Tag is string tagStr && int.TryParse(tagStr, out int colIndex))
        {
            var headerGrid = this.FindControl<Grid>("TradesHeaderGrid");
            if (headerGrid != null && colIndex >= 0 && colIndex < headerGrid.ColumnDefinitions.Count)
            {
                var colDef = headerGrid.ColumnDefinitions[colIndex];
                double current = colDef.ActualWidth;
                if (current <= 0 && colIndex < headerGrid.Children.Count && headerGrid.Children[colIndex] is Control cellControl && cellControl.Bounds.Width > 0)
                {
                    current = cellControl.Bounds.Width;
                }
                double minWidth = colDef.MinWidth > 0 ? colDef.MinWidth : 40;
                if (current <= 0) current = minWidth;
                double newWidth = Math.Max(minWidth, current + e.Vector.X);
                colDef.Width = new GridLength(newWidth);
            }
        }
    }

    private void OnTradeHeaderDoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (sender is Control control && control.Tag is string tagStr && int.TryParse(tagStr, out int colIndex))
        {
            var headerGrid = this.FindControl<Grid>("TradesHeaderGrid");
            if (headerGrid != null && colIndex >= 0 && colIndex < headerGrid.ColumnDefinitions.Count)
            {
                headerGrid.ColumnDefinitions[colIndex].Width = GridLength.Auto;
            }
        }
    }

    private void OnMetricColumnDragDelta(object? sender, VectorEventArgs e)
    {
        int colIndex = 0;
        if (sender is Thumb thumb && thumb.Tag is string tagStr && int.TryParse(tagStr, out int idx))
        {
            colIndex = idx;
        }
        var headerGrid = this.FindControl<Grid>("MetricsHeaderGrid");
        if (headerGrid != null && colIndex >= 0 && colIndex < headerGrid.ColumnDefinitions.Count)
        {
            var colDef = headerGrid.ColumnDefinitions[colIndex];
            double current = colDef.ActualWidth;
            if (current <= 0 && sender is Visual visual && visual.FindAncestorOfType<Grid>() is { } rowGrid && colIndex < rowGrid.ColumnDefinitions.Count)
            {
                current = rowGrid.ColumnDefinitions[colIndex].ActualWidth;
            }
            double minWidth = colDef.MinWidth > 0 ? colDef.MinWidth : (colIndex == 0 ? 260 : 120);
            if (current <= 0) current = minWidth;
            double newWidth = Math.Max(minWidth, current + e.Vector.X);
            colDef.Width = new GridLength(newWidth);
        }
    }

    private void OnMetricHeaderDoubleTapped(object? sender, RoutedEventArgs e)
    {
        int colIndex = 0;
        if (sender is Control control && control.Tag is string tagStr && int.TryParse(tagStr, out int idx))
        {
            colIndex = idx;
        }
        var headerGrid = this.FindControl<Grid>("MetricsHeaderGrid");
        if (headerGrid != null && colIndex >= 0 && colIndex < headerGrid.ColumnDefinitions.Count)
        {
            headerGrid.ColumnDefinitions[colIndex].Width = GridLength.Auto;
        }
    }
}
