using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using Avalonia.Interactivity;
using Avalonia.Input;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public partial class EditTransactionDialog : Window
{
    public EditTransactionDialog()
    {
        InitializeComponent();
        
        var datePicker = this.FindControl<CalendarDatePicker>("ExecutedAtDatePicker");
        if (datePicker != null)
        {
            datePicker.AddHandler(PointerPressedEvent, (sender, e) =>
            {
                if (sender is CalendarDatePicker cdp && !cdp.IsDropDownOpen)
                {
                    cdp.IsDropDownOpen = true;
                }
            }, RoutingStrategies.Bubble, handledEventsToo: true);
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is EditTransactionDialogViewModel vm && vm.SaveCommand.CanExecute(null))
        {
            vm.SaveCommand.Execute(null);
            Close(vm.Result);
        }
    }

    public void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    private void SearchTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down)
        {
            if (DataContext is EditTransactionDialogViewModel vm && vm.Suggestions.Count > 0)
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
            if (DataContext is EditTransactionDialogViewModel vm && vm.SelectedSuggestion != null)
            {
                vm.Ticker = vm.SelectedSuggestion;
                vm.Suggestions.Clear();
                vm.SelectedSuggestion = null;

                var textBox = this.FindControl<TextBox>("SearchTextBox");
                textBox?.Focus();
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Escape)
        {
            if (DataContext is EditTransactionDialogViewModel vm)
            {
                vm.Suggestions.Clear();
                vm.SelectedSuggestion = null;
                var textBox = this.FindControl<TextBox>("SearchTextBox");
                textBox?.Focus();
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Up)
        {
            var listBox = sender as ListBox;
            if (listBox != null && listBox.SelectedIndex == 0)
            {
                var textBox = this.FindControl<TextBox>("SearchTextBox");
                textBox?.Focus();
                listBox.SelectedIndex = -1;
                e.Handled = true;
            }
        }
    }

    private void SuggestionsListBox_Tapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is EditTransactionDialogViewModel vm && vm.SelectedSuggestion != null)
        {
            vm.Ticker = vm.SelectedSuggestion;
            vm.Suggestions.Clear();
            vm.SelectedSuggestion = null;

            var textBox = this.FindControl<TextBox>("SearchTextBox");
            textBox?.Focus();
            e.Handled = true;
        }
    }
}
