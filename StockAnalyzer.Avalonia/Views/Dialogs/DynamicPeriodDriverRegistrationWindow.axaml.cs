using Avalonia.Controls;
using Avalonia.Input;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;

namespace StockAnalyzer.Avalonia.Views.Dialogs;

public partial class DynamicPeriodDriverRegistrationWindow : Window
{
    public DynamicPeriodDriverRegistrationWindow()
    {
        InitializeComponent();
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
        if (DataContext is DynamicPeriodDriverRegistrationViewModel vm && vm.SelectedCatalogItem != null)
        {
            if (vm.RegisterIndicatorCommand.CanExecute(null))
            {
                vm.RegisterIndicatorCommand.Execute(null);
            }
        }
    }
}
