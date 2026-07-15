using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using System;
using Avalonia.Interactivity;
using Avalonia.Input;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

/// <summary>
/// Code-behind for the Modern Add Ticker Dialog.
/// </summary>
public partial class AddTickerWindow : Window
{
    public AddTickerWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Event handler for the Add button click.
    /// Closes the window and returns the selected symbol.
    /// </summary>
    public void OnAddClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AddTickerViewModel vm && vm.AddCommand.CanExecute(null))
        {
            vm.AddCommand.Execute(null);
            Close(vm.ResultSymbol);
        }
    }

    /// <summary>
    /// Event handler for the Cancel button click.
    /// Closes the window without a result.
    /// </summary>
    public void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    /// <summary>
    /// Event handler for the Bulk Import button click.
    /// Closes the window and signals a bulk request.
    /// </summary>
    public void OnBulkImportClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AddTickerViewModel vm)
        {
            vm.ImportBulkCommand.Execute(null);
            Close(null); // Result is handled via IsBulkRequestRequested property
        }
    }

    private void SearchTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down)
        {
            if (DataContext is AddTickerViewModel vm && vm.Suggestions.Count > 0)
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
            if (DataContext is AddTickerViewModel vm && vm.SelectedSuggestion != null)
            {
                vm.SearchText = vm.SelectedSuggestion;
                vm.Suggestions.Clear();
                vm.SelectedSuggestion = null;

                var textBox = this.FindControl<TextBox>("SearchTextBox");
                textBox?.Focus();
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Escape)
        {
            if (DataContext is AddTickerViewModel vm)
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
        if (DataContext is AddTickerViewModel vm && vm.SelectedSuggestion != null)
        {
            vm.SearchText = vm.SelectedSuggestion;
            vm.Suggestions.Clear();
            vm.SelectedSuggestion = null;

            var textBox = this.FindControl<TextBox>("SearchTextBox");
            textBox?.Focus();
            e.Handled = true;
        }
    }
}
