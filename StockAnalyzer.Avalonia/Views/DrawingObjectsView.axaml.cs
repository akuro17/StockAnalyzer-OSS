using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using StockAnalyzer.Avalonia.ViewModels;

namespace StockAnalyzer.Avalonia.Views;

public partial class DrawingObjectsView : UserControl
{
    public DrawingObjectsView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Wires the per-row rename TextBox (realized once per item container) to auto-focus itself
    /// whenever it becomes visible, regardless of what triggered edit mode (currently the
    /// "Rename" context menu item, via <see cref="DrawingObjectItemViewModel.StartRenameCommand"/>).
    /// </summary>
    private void OnRenameTextBoxLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox textBox) return;
        textBox.PropertyChanged += OnRenameTextBoxPropertyChanged;
    }

    private void OnRenameTextBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Visual.IsVisibleProperty) return;
        if (sender is not TextBox { IsVisible: true } textBox) return;

        Dispatcher.UIThread.Post(() =>
        {
            textBox.Focus();
            textBox.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    private void OnRenameTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not StyledElement { DataContext: DrawingObjectItemViewModel item }) return;

        if (e.Key == Key.Enter)
        {
            item.CommitRenameCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            item.CancelRenameCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnRenameTextBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is StyledElement { DataContext: DrawingObjectItemViewModel { IsEditingName: true } item })
        {
            item.CommitRenameCommand.Execute(null);
        }
    }
}
