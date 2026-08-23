using Avalonia.Controls;
using Avalonia.Input;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views;

namespace StockAnalyzer.Avalonia.Views.Chart;

public partial class ChartView : UserControl
{
    public ChartView()
    {
        InitializeComponent();
    }

    private void SymbolSearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not AutoCompleteBox box) return;
        if (e.Key == Key.Enter)
        {
            if (DataContext is ChartViewModel vm)
            {
                SymbolAutoCompleteInputHelper.ApplyTypedSymbol(box.Text, symbol => vm.Symbol = symbol);
            }
            box.IsDropDownOpen = false;
        }
    }

    private void SymbolSearchBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is AutoCompleteBox box && box.SelectedItem is string selectedSymbol && DataContext is ChartViewModel vm)
        {
            SymbolAutoCompleteInputHelper.ApplySelectedSymbol(selectedSymbol, vm.Symbol, symbol => vm.Symbol = symbol);
        }
    }
}
