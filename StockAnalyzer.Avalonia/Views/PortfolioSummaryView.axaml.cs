using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Controls.Presenters;
using Avalonia.Markup.Xaml;
using Avalonia.Interactivity;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Models.Portfolio;

namespace StockAnalyzer.Avalonia.Views;

public partial class PortfolioSummaryView : UserControl
{
    public PortfolioSummaryView()
    {
        InitializeComponent();
        var treeView = this.FindControl<TreeView>("GroupsTreeView");
        if (treeView != null)
        {
            treeView.AddHandler(PointerPressedEvent, OnTreeViewPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);

            // TreeViewItem has a built-in header double-tap handler (Avalonia.Controls.TreeViewItem.
            // OnHeaderDoubleTapped) that ALSO toggles IsExpanded, subscribed directly on PART_HeaderPresenter.
            // Because Gestures.DoubleTappedEvent only routes via Bubble (no Tunnel), that built-in handler
            // always runs before any handler we attach on an ancestor, so it cannot be pre-empted - only
            // compensated for after the fact. Without this, rapid clicks that Avalonia recognizes as a
            // double-tap get toggled twice (once by our single-press handler above, once by the built-in),
            // cancelling out roughly every other click.
            treeView.AddHandler(Gestures.DoubleTappedEvent, OnTreeViewDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        }
    }

    private void OnAddTransactionMenuClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PortfolioSummaryViewModel vm)
        {
            string? prefilledTicker = null;

            var posGrid = this.FindControl<DataGrid>("PositionsDataGrid");
            if (posGrid?.SelectedItem is PortfolioPositionViewModel selectedPos)
            {
                prefilledTicker = selectedPos.Symbol;
            }
            else
            {
                var transGrid = this.FindControl<DataGrid>("TransactionsDataGrid");
                if (transGrid?.SelectedItem is Transaction selectedTrans)
                {
                    prefilledTicker = selectedTrans.Ticker;
                }
            }

            vm.PrefillTicker = prefilledTicker;

            if (vm.AddTransactionCommand.CanExecute("Long"))
            {
                vm.AddTransactionCommand.Execute("Long");
            }
        }
    }

    private void OnExitPositionMenuClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PortfolioSummaryViewModel vm)
        {
            var dataGrid = this.FindControl<DataGrid>("PositionsDataGrid");
            var selected = dataGrid?.SelectedItem as PortfolioPositionViewModel;
            if (selected != null && vm.ExitPositionCommand.CanExecute(selected))
            {
                vm.ExitPositionCommand.Execute(selected);
            }
        }
    }

    private void OnEditTransactionMenuClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PortfolioSummaryViewModel vm)
        {
            var dataGrid = this.FindControl<DataGrid>("TransactionsDataGrid");
            var selected = dataGrid?.SelectedItem as Transaction;
            if (selected != null && vm.EditTransactionCommand.CanExecute(selected))
            {
                vm.EditTransactionCommand.Execute(selected);
            }
        }
    }

    private void OnDeleteTransactionMenuClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PortfolioSummaryViewModel vm)
        {
            var dataGrid = this.FindControl<DataGrid>("TransactionsDataGrid");
            if (dataGrid?.SelectedItems != null)
            {
                var selected = dataGrid.SelectedItems.Cast<Transaction>().ToList();
                if (selected.Count > 0 && vm.DeleteTransactionsCommand.CanExecute(selected))
                {
                    vm.DeleteTransactionsCommand.Execute(selected);
                }
            }
        }
    }

    private void OnTreeViewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var treeViewItem = ResolveExpandableTreeViewItem(e.Source as Visual);
        if (treeViewItem != null)
        {
            treeViewItem.IsExpanded = !treeViewItem.IsExpanded;
        }
    }

    private void OnTreeViewDoubleTapped(object? sender, TappedEventArgs e)
    {
        var treeViewItem = ResolveExpandableTreeViewItem(e.Source as Visual);
        if (treeViewItem == null) return;

        // Undo the built-in TreeViewItem.OnHeaderDoubleTapped toggle that already ran for this
        // same gesture (see comment on the AddHandler call in the constructor).
        treeViewItem.IsExpanded = !treeViewItem.IsExpanded;
    }

    // Shared by the single-press toggle and the double-tap compensation handler above: resolves
    // the TreeViewItem under the pointer only when the click landed on its header and it represents
    // an aggregate row or has children to expand/collapse.
    private static TreeViewItem? ResolveExpandableTreeViewItem(Visual? source)
    {
        var treeViewItem = source?.FindAncestorOfType<TreeViewItem>();
        if (treeViewItem == null) return null;

        var header = source?.FindAncestorOfType<ContentPresenter>();
        if (header == null || header.Name != "PART_HeaderPresenter") return null;

        var dataContext = treeViewItem.DataContext as TreeNode;
        if (dataContext == null || !(dataContext.IsAggregate || dataContext.Children.Count > 0)) return null;

        return treeViewItem;
    }

    private void OnTabControlContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is Control control)
        {
            Visual? current = control;
            while (current != null && current != sender)
            {
                if (current is DataGrid || (current is Control c && c.ContextMenu != null))
                {
                    return; // Allow the context menu to show
                }
                current = ((current as Control)?.Parent as Visual) ?? current.GetVisualParent();
            }

            // Fallback: If clicked on an overlay or empty area within the active tab,
            // direct the context menu request to the corresponding DataGrid if it has a ContextMenu.
            if (sender is TabControl tabControl)
            {
                DataGrid? targetGrid = tabControl.SelectedIndex switch
                {
                    1 => this.FindControl<DataGrid>("PositionsDataGrid"),
                    2 => this.FindControl<DataGrid>("TransactionsDataGrid"),
                    3 => this.FindControl<DataGrid>("ClosedPositionsDataGrid"),
                    _ => null
                };

                if (targetGrid != null && targetGrid.ContextMenu != null)
                {
                    targetGrid.ContextMenu.Open(targetGrid);
                    e.Handled = true;
                    return;
                }
            }
        }
        e.Handled = true;
    }
}

public class SymbolCellBorderConverter : global::Avalonia.Data.Converters.IMultiValueConverter
{
    public static readonly SymbolCellBorderConverter Instance = new();

    public object? Convert(System.Collections.Generic.IList<object?> values, System.Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (values.Count >= 2 &&
            values[0] is PortfolioPositionViewModel vm &&
            values[1] is int displayIndex)
        {
            if (displayIndex == 0)
            {
                return vm.SymbolBorderThickness;
            }
        }
        return global::Avalonia.AvaloniaProperty.UnsetValue;
    }
}

