using Avalonia.Controls;
using Avalonia.Input;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

/// <summary>
/// The scatter metric registration window. Every way of closing it (Cancel, Esc, the window's close button, Alt+F4) goes through the view model's
/// Cancel command, which asks before it throws away a modified draft; the window closes for real only when the view model asks for it
/// (<see cref="CloseApproved"/>).
/// </summary>
public partial class ScatterMetricRegistrationWindow : Window
{
    private bool _closeApproved;

    public ScatterMetricRegistrationWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    /// <summary>Closes the window without asking again (the view model has decided).</summary>
    public void CloseApproved()
    {
        _closeApproved = true;
        Close();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeApproved || DataContext is not ScatterMetricRegistrationViewModel viewModel)
        {
            return;
        }

        e.Cancel = true;
        if (viewModel.CancelCommand.CanExecute(null))
        {
            viewModel.CancelCommand.Execute(null);
        }
    }

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnCatalogItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is ScatterMetricRegistrationViewModel viewModel && viewModel.AddCommand.CanExecute(null))
        {
            viewModel.AddCommand.Execute(null);
        }
    }

    /// <summary>Enter in the search box is not the default button's Enter: it must not confirm the window.</summary>
    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
        }
    }
}
