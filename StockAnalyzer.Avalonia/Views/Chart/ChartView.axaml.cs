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

    private void InPlaceTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ChartViewModel vm) return;
        if (e.Key == Key.Enter && (e.KeyModifiers == KeyModifiers.None || e.KeyModifiers == KeyModifiers.Control))
        {
            vm.InPlaceTextEdit.Commit(vm.DrawingEditCoordinator, vm.ObjectManager, vm.LayerService);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.InPlaceTextEdit.Cancel(vm.ObjectManager);
            e.Handled = true;
        }
    }

    private void InPlaceTextBox_LostFocus(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not ChartViewModel vm) return;
        if (vm.InPlaceTextEdit.IsEditing)
        {
            vm.InPlaceTextEdit.Commit(vm.DrawingEditCoordinator, vm.ObjectManager, vm.LayerService);
        }
    }

    protected override void OnAttachedToVisualTree(global::Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is ChartViewModel vm)
        {
            vm.InPlaceTextEdit.PropertyChanged += InPlaceTextEdit_PropertyChanged;
        }
    }

    protected override void OnDetachedFromVisualTree(global::Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (DataContext is ChartViewModel vm)
        {
            vm.InPlaceTextEdit.PropertyChanged -= InPlaceTextEdit_PropertyChanged;
        }
    }

    private void InPlaceTextEdit_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModels.Chart.InPlaceTextEditViewModel.IsEditing))
        {
            if (DataContext is ChartViewModel vm && vm.InPlaceTextEdit.IsEditing)
            {
                global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    var tb = this.FindControl<TextBox>("InPlaceTextBox");
                    if (tb != null)
                    {
                        tb.Focus();
                        tb.SelectAll();
                    }
                });
            }
        }
    }
}
