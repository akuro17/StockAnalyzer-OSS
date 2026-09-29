using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Views;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public partial class EditTickerNotesDialog : Window
{
    public EditTickerNotesDialog()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Closed += OnClosed;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is EditTickerNotesDialogViewModel vm)
        {
            vm.CloseAction = result => Close(result);
        }
    }

    private void OnClosed(object? sender, System.EventArgs e)
    {
        if (DataContext is EditTickerNotesDialogViewModel vm)
        {
            vm.Dispose();
        }
    }

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    /// <summary>
    /// Enter key pressed in the Symbol AutoCompleteBox: confirm the typed symbol.
    /// </summary>
    private void DashboardSymbolBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not AutoCompleteBox box) return;
        if (e.Key == Key.Enter)
        {
            if (DataContext is EditTickerNotesDialogViewModel vm)
            {
                SymbolAutoCompleteInputHelper.ApplyTypedSymbol(box.Text, symbol => vm.Symbol = symbol);
            }
            box.IsDropDownOpen = false;
        }
    }

    /// <summary>
    /// Item selected from the Symbol AutoCompleteBox dropdown.
    /// </summary>
    private void DashboardSymbolBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is AutoCompleteBox box && box.SelectedItem is string selectedSymbol && DataContext is EditTickerNotesDialogViewModel vm)
        {
            SymbolAutoCompleteInputHelper.ApplySelectedSymbol(selectedSymbol, vm.Symbol, symbol => vm.Symbol = symbol);
        }
    }
}
