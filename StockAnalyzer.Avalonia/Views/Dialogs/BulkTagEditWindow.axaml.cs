using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using StockAnalyzer.Avalonia.Models;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using Avalonia.Interactivity;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

/// <summary>
/// Code-behind for BulkTagEditWindow (Add/Remove Tags Dialog).
/// </summary>
public partial class BulkTagEditWindow : Window
{
    public BulkTagEditWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public void OnOkClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is BulkTagEditViewModel vm)
        {
            Tag = new BulkTagEditResult(BulkTagEditAction.Add, vm.BulkTagInput);
            Close();
        }
        else
        {
            Tag = null;
            Close();
        }
    }

    public void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Tag = null;
        Close();
    }

    public void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is BulkTagEditViewModel vm)
        {
            Tag = new BulkTagEditResult(BulkTagEditAction.Delete, vm.BulkTagInput);
            Close();
        }
        else
        {
            Tag = null;
            Close();
        }
    }

    public void OnHeaderPointerPressed(object? sender, global::Avalonia.Input.PointerPressedEventArgs e)
    {
        BeginMoveDrag(e);
    }
}
