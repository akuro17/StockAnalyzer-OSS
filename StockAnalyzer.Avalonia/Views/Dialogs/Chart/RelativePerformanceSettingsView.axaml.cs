using Avalonia.Controls;
using Avalonia.Input;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;

namespace StockAnalyzer.Avalonia.Views.Dialogs.Chart;

public partial class RelativePerformanceSettingsView : UserControl
{
    public RelativePerformanceSettingsView()
    {
        InitializeComponent();
    }

    private void NewSymbolTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down)
        {
            if (DataContext is RelativePerformanceSettingsViewModel vm && vm.Suggestions.Count > 0)
            {
                var listBox = this.FindControl<ListBox>("SuggestionsListBox");
                if (listBox != null)
                {
                    if (listBox.SelectedIndex < 0)
                    {
                        listBox.SelectedIndex = 0;
                    }
                    e.Handled = true;
                    global::Avalonia.Threading.Dispatcher.UIThread.Post(static state =>
                    {
                        if (state is ListBox listBox)
                        {
                            var container = listBox.ContainerFromIndex(listBox.SelectedIndex) as Control;
                            if (container != null)
                                container.Focus();
                            else
                                listBox.Focus();
                        }
                    }, listBox);
                }
            }
        }
    }

    private void SuggestionsListBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (DataContext is RelativePerformanceSettingsViewModel vm && vm.SelectedSuggestion != null)
            {
                vm.NewSymbol = vm.SelectedSuggestion;
                vm.Suggestions.Clear();
                vm.SelectedSuggestion = null;

                var textBox = this.FindControl<TextBox>("NewSymbolTextBox");
                textBox?.Focus();
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Escape)
        {
            if (DataContext is RelativePerformanceSettingsViewModel vm)
            {
                vm.Suggestions.Clear();
                vm.SelectedSuggestion = null;
                var textBox = this.FindControl<TextBox>("NewSymbolTextBox");
                textBox?.Focus();
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Up)
        {
            var listBox = sender as ListBox;
            if (listBox != null && listBox.SelectedIndex == 0)
            {
                var textBox = this.FindControl<TextBox>("NewSymbolTextBox");
                textBox?.Focus();
                listBox.SelectedIndex = -1;
                e.Handled = true;
            }
        }
    }

    private void SuggestionsListBox_Tapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is RelativePerformanceSettingsViewModel vm && vm.SelectedSuggestion != null)
        {
            vm.NewSymbol = vm.SelectedSuggestion;
            vm.Suggestions.Clear();
            vm.SelectedSuggestion = null;

            var textBox = this.FindControl<TextBox>("NewSymbolTextBox");
            textBox?.Focus();
            e.Handled = true;
        }
    }
}
