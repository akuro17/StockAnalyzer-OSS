using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Controls.Presenters;
using Avalonia.Interactivity;
using StockAnalyzer.Avalonia.ViewModels.TickerList;

namespace StockAnalyzer.Avalonia.Views;

public partial class TickerListView : UserControl
{
    public TickerListView()
    {
        InitializeComponent();
        var treeView = this.FindControl<TreeView>("TickersTreeView");
        if (treeView != null)
        {
            treeView.AddHandler(PointerPressedEvent, OnTreeViewPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);

            // TreeViewItem has a built-in header double-tap handler (Avalonia.Controls.TreeViewItem.
            // OnHeaderDoubleTapped) that ALSO toggles IsExpanded, subscribed directly on PART_HeaderPresenter.
            // Because Gestures.DoubleTappedEvent only routes via Bubble (no Tunnel), that built-in handler
            // always runs before any handler we attach on an ancestor, so it cannot be pre-empted - only
            // compensated for after the fact. Without this, rapid clicks that Avalonia recognizes as a
            // double-tap get toggled twice (once by our single-press handler below, once by the built-in),
            // cancelling out roughly every other click.
            treeView.AddHandler(Gestures.DoubleTappedEvent, OnTreeViewDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
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
    // the TreeViewItem under the pointer only when the click landed on its header and it has
    // children to expand/collapse.
    private static TreeViewItem? ResolveExpandableTreeViewItem(Visual? source)
    {
        var treeViewItem = source?.FindAncestorOfType<TreeViewItem>();
        if (treeViewItem == null) return null;

        var header = source?.FindAncestorOfType<ContentPresenter>();
        if (header == null || header.Name != "PART_HeaderPresenter") return null;

        var dataContext = treeViewItem.DataContext as TickerGroupNode;
        if (dataContext == null || dataContext.Children == null || dataContext.Children.Count == 0) return null;

        return treeViewItem;
    }
}
