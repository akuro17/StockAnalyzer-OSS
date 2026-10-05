using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public partial class BacktestTradeHistoryColumnChooserWindow : Window
{
    private ListBoxItem? _lastDragOverItem;

    public BacktestTradeHistoryColumnChooserWindow()
    {
        InitializeComponent();
        var listBox = this.FindControl<ListBox>("ColumnsListBox");
        if (listBox != null)
        {
            listBox.AddHandler(PointerPressedEvent, OnListBoxPointerPressed, RoutingStrategies.Bubble, true);
        }
    }

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        BeginMoveDrag(e);
    }

    private void OnCategorySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox listBox && listBox.SelectedIndex >= 0 && DataContext is BacktestTradeHistoryColumnChooserViewModel vm)
        {
            vm.SelectedCategory = (BacktestTradeColumnCategory)listBox.SelectedIndex;
        }
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private void OnApplyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is BacktestTradeHistoryColumnChooserViewModel vm)
        {
            vm.Apply();
        }
    }

    private void OnListBoxPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not BacktestTradeHistoryColumnChooserViewModel vm || vm.SelectedCategory != BacktestTradeColumnCategory.Active) return;

        var visual = e.Source as Visual;
        var grip = visual as PathIcon ?? visual?.FindAncestorOfType<PathIcon>();

        // Ensure we are dragging using the Grip handle
        if (grip != null && grip.Classes.Contains("grip"))
        {
            var listBoxItem = visual?.FindAncestorOfType<ListBoxItem>();
            if (listBoxItem != null && listBoxItem.DataContext is BacktestTradeColumnItemViewModel item)
            {
#pragma warning disable CS0618
                var data = new DataObject();
                data.Set("TradeColumnItem", item);
                DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
#pragma warning restore CS0618
            }
        }
    }

    private void ClearDragHover()
    {
        if (_lastDragOverItem != null)
        {
            _lastDragOverItem.Classes.Remove("drag-hover");
            _lastDragOverItem = null;
        }
    }

    private void OnListBoxDragLeave(object? sender, DragEventArgs e)
    {
        ClearDragHover();
    }

    private void OnListBoxDragOver(object? sender, DragEventArgs e)
    {
        if (DataContext is not BacktestTradeHistoryColumnChooserViewModel vm || vm.SelectedCategory != BacktestTradeColumnCategory.Active)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        if (e.Data.Contains("TradeColumnItem"))
        {
            e.DragEffects = DragDropEffects.Move;
            var visual = e.Source as Visual;
            var targetItem = visual?.FindAncestorOfType<ListBoxItem>();
            if (targetItem != _lastDragOverItem)
            {
                if (_lastDragOverItem != null)
                {
                    _lastDragOverItem.Classes.Remove("drag-hover");
                }
                _lastDragOverItem = targetItem;
                if (_lastDragOverItem != null)
                {
                    _lastDragOverItem.Classes.Add("drag-hover");
                }
            }
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
            ClearDragHover();
        }
    }

    private void OnListBoxDrop(object? sender, DragEventArgs e)
    {
        ClearDragHover();
#pragma warning disable CS0618
        var data = e.Data;
        if (!data.Contains("TradeColumnItem") || DataContext is not BacktestTradeHistoryColumnChooserViewModel vm) return;

        if (vm.SelectedCategory != BacktestTradeColumnCategory.Active) return;

        var sourceItem = data.Get("TradeColumnItem") as BacktestTradeColumnItemViewModel;
        if (sourceItem == null) return;
#pragma warning restore CS0618

        var visual = e.Source as Visual;
        var targetItem = visual?.FindAncestorOfType<ListBoxItem>()?.DataContext as BacktestTradeColumnItemViewModel;
        if (targetItem == null || sourceItem == targetItem) return;

        // Move item in VM AllItems collection
        int oldIndex = vm.AllItems.IndexOf(sourceItem);
        int newIndex = vm.AllItems.IndexOf(targetItem);
        if (oldIndex >= 0 && newIndex >= 0)
        {
            vm.AllItems.Move(oldIndex, newIndex);
            vm.Apply();
        }
    }
}
