using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Avalonia.Media;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public partial class ColumnChooserWindow : Window
{
    public ColumnChooserWindow()
    {
        InitializeComponent();
#if DEBUG
        this.AttachDevTools();
#endif
        var listBox = this.FindControl<ListBox>("ColumnsListBox");
        if (listBox != null)
        {
            listBox.AddHandler(PointerPressedEvent, OnListBoxPointerPressed, RoutingStrategies.Bubble, true);
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnHeaderPointerPressed(object sender, PointerPressedEventArgs e)
    {
        BeginMoveDrag(e);
    }

    private void OnCategorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox listBox && listBox.SelectedIndex >= 0 && DataContext is ColumnChooserViewModel vm)
        {
            vm.SelectedCategory = (ColumnCategory)listBox.SelectedIndex;
        }
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        Close(true);
    }

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is ColumnChooserViewModel vm)
        {
            vm.ApplyCommand.Execute(null);
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private void OnListBoxPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not ColumnChooserViewModel vm || vm.SelectedCategory != ColumnCategory.Active) return;

        var visual = e.Source as Visual;
        var grip = visual as PathIcon ?? visual?.FindAncestorOfType<PathIcon>();
        
        // Ensure we are dragging using the Grip handle
        if (grip != null && grip.Classes.Contains("grip"))
        {
            var listBoxItem = visual?.FindAncestorOfType<ListBoxItem>();
            if (listBoxItem != null && listBoxItem.DataContext is ColumnItemViewModel item)
            {
#pragma warning disable CS0618
                var data = new DataObject();
                data.Set("ColumnItem", item);
                DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
#pragma warning restore CS0618
            }
        }
    }

    private ListBoxItem? _lastDragOverItem;

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
        if (DataContext is not ColumnChooserViewModel vm || vm.SelectedCategory != ColumnCategory.Active)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        if (e.Data.Contains("ColumnItem"))
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
        if (!data.Contains("ColumnItem") || DataContext is not ColumnChooserViewModel vm) return;

        if (vm.SelectedCategory != ColumnCategory.Active) return;

        var sourceItem = data.Get("ColumnItem") as ColumnItemViewModel;
        if (sourceItem == null) return;
#pragma warning restore CS0618

        var visual = e.Source as Visual;
        var targetItem = visual?.FindAncestorOfType<ListBoxItem>()?.DataContext as ColumnItemViewModel;
        if (targetItem == null || sourceItem == targetItem) return;

        // Move item in VM AllItems collection
        int oldIndex = vm.AllItems.IndexOf(sourceItem);
        int newIndex = vm.AllItems.IndexOf(targetItem);
        if (oldIndex >= 0 && newIndex >= 0)
        {
            vm.AllItems.Move(oldIndex, newIndex);
        }
    }
}
