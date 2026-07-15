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
        }
    }

    private void OnTreeViewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var visual = e.Source as Visual;
        var treeViewItem = visual?.FindAncestorOfType<TreeViewItem>();
        if (treeViewItem != null)
        {
            var header = visual?.FindAncestorOfType<ContentPresenter>();
            if (header != null && header.Name == "PART_HeaderPresenter")
            {
                var dataContext = treeViewItem.DataContext as TickerGroupNode;
                if (dataContext != null && dataContext.Children != null && dataContext.Children.Count > 0)
                {
                    treeViewItem.IsExpanded = !treeViewItem.IsExpanded;
                }
            }
        }
    }
}
