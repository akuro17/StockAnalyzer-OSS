using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Avalonia.Input;
using Avalonia.Interactivity;
using System;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();

        DataContextChanged += (s, e) =>
        {
            if (DataContext is SettingsViewModel vm)
            {
                vm.RequestClose += () => Close();
            }
        };

        Closing += (s, e) => (DataContext as SettingsViewModel)?.OnClosing();
        Closed += (s, e) => (DataContext as IDisposable)?.Dispose();

        var treeView = this.FindControl<TreeView>("CategoryTreeView");
        if (treeView != null)
        {
            // Subscribed here (not via the XAML "Tapped=" attribute, which defaults to
            // handledEventsToo: false) for the same reason as the DoubleTapped handler below:
            // TreeViewItem/SelectingItemsControl's own internal pointer handling (selection,
            // focus) marks the underlying gesture Handled before it would otherwise bubble to
            // a plain XAML-declared handler on the ancestor TreeView, so a single click
            // intermittently never reached OnCategoryTapped at all -- clicking silently did
            // nothing. handledEventsToo: true ensures our handler always sees every tap.
            treeView.AddHandler(Gestures.TappedEvent, OnCategoryTapped, RoutingStrategies.Bubble, handledEventsToo: true);

            // TreeViewItem has a built-in header double-tap handler (Avalonia.Controls.TreeViewItem.
            // OnHeaderDoubleTapped) that ALSO toggles IsExpanded, subscribed directly on PART_HeaderPresenter.
            // Because Gestures.DoubleTappedEvent only routes via Bubble (no Tunnel), that built-in handler
            // always runs before any handler we attach on an ancestor, so it cannot be pre-empted - only
            // compensated for after the fact. Without this, rapid clicks that Avalonia recognizes as a
            // double-tap get toggled twice (once by OnCategoryTapped below, once by the built-in),
            // cancelling out roughly every other click.
            treeView.AddHandler(Gestures.DoubleTappedEvent, OnCategoryDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnHeaderPointerPressed(object? sender, global::Avalonia.Input.PointerPressedEventArgs e)
    {
        BeginMoveDrag(e);
    }

    private void OnCategoryTapped(object? sender, RoutedEventArgs e)
    {
        var item = ResolveExpandableTreeViewItem(e.Source as global::Avalonia.Visual);
        if (item != null)
        {
            item.IsExpanded = !item.IsExpanded;
        }
    }

    private void OnCategoryDoubleTapped(object? sender, TappedEventArgs e)
    {
        var item = ResolveExpandableTreeViewItem(e.Source as global::Avalonia.Visual);
        if (item == null) return;

        // Undo the built-in TreeViewItem.OnHeaderDoubleTapped toggle that already ran for
        // this same gesture (see comment on the AddHandler call in the constructor).
        item.IsExpanded = !item.IsExpanded;
    }

    // Shared by OnCategoryTapped and OnCategoryDoubleTapped above: resolves the TreeViewItem
    // under the pointer only when it has children (like the "Chart" category) to expand/collapse.
    // In Avalonia 11, TreeViewItem has an ItemCount property.
    private static TreeViewItem? ResolveExpandableTreeViewItem(global::Avalonia.Visual? source)
    {
        var item = source?.FindAncestorOfType<TreeViewItem>();
        return item != null && item.ItemCount > 0 ? item : null;
    }
}
