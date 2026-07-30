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
        if (e.Key == Key.Enter && sender is AutoCompleteBox box)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                string text = box.Text ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    vm.SelectedTicker = text.Trim().ToUpperInvariant();
                }
            }
            box.IsDropDownOpen = false;
        }
    }

    private void SymbolSearchBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is AutoCompleteBox box && box.SelectedItem is string selectedSymbol)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                if (!string.IsNullOrWhiteSpace(selectedSymbol) && !string.Equals(vm.SelectedTicker, selectedSymbol, System.StringComparison.OrdinalIgnoreCase))
                {
                    vm.SelectedTicker = selectedSymbol.Trim().ToUpperInvariant();
                }
            }
        }
    }
}