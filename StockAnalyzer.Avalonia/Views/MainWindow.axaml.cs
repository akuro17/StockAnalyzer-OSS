using Avalonia.Controls;
using Avalonia.Input;
using StockAnalyzer.Avalonia.ViewModels;

namespace StockAnalyzer.Avalonia.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.PrepareExit();
            // Force a final save synchronously to ensure DetachedTabs state is persisted
            vm.ForceSaveOnShutdown();
        }
        base.OnClosing(e);
    }

    private void OnSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.SaveLayoutCommand.Execute(null);
        }
    }

    private void SymbolSearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not AutoCompleteBox box) return;
        if (e.Key == Key.Enter)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                SymbolAutoCompleteInputHelper.ApplyTypedSymbol(box.Text, symbol => vm.SelectedTicker = symbol);
            }
            box.IsDropDownOpen = false;
        }
    }

    private void SymbolSearchBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is AutoCompleteBox box && box.SelectedItem is string selectedSymbol && DataContext is MainWindowViewModel vm)
        {
            SymbolAutoCompleteInputHelper.ApplySelectedSymbol(selectedSymbol, vm.SelectedTicker, symbol => vm.SelectedTicker = symbol);
        }
    }
}