using Avalonia.Controls;
using Avalonia.Input;
using StockAnalyzer.Avalonia.ViewModels;

namespace StockAnalyzer.Avalonia.Views;

public partial class RlTrainingWindow : Window
{
    public RlTrainingWindow()
    {
        InitializeComponent();
        SubWindowKeys.CloseOnEscape(this);
    }

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        BeginMoveDrag(e);
    }

    private void SymbolSearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not AutoCompleteBox box) return;
        if (e.Key == Key.Enter)
        {
            if (DataContext is RlTrainingDialogViewModel vm)
            {
                SymbolAutoCompleteInputHelper.ApplyTypedSymbol(box.Text, symbol => vm.Symbol = symbol);
            }
            box.IsDropDownOpen = false;
        }
    }

    private void SymbolSearchBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is AutoCompleteBox box && box.SelectedItem is string selectedSymbol && DataContext is RlTrainingDialogViewModel vm)
        {
            SymbolAutoCompleteInputHelper.ApplySelectedSymbol(selectedSymbol, vm.Symbol, symbol => vm.Symbol = symbol);
        }
    }
}
