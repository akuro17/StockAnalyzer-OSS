using Avalonia.Controls;
using Avalonia.Input;
using StockAnalyzer.Avalonia.ViewModels;

namespace StockAnalyzer.Avalonia.Views;

public partial class IndicatorRegistrationView : UserControl
{
    public IndicatorRegistrationView()
    {
        InitializeComponent();
    }

    private void OnLeftContainerPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is IndicatorRegistrationViewModel vm)
        {
            vm.SelectLeftTargetCommand.Execute(null);
        }
    }

    private void OnRightContainerPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is IndicatorRegistrationViewModel vm && vm.RightTargetMode == StockAnalyzer.Core.Models.Screener.RightHandTargetMode.Indicator)
        {
            vm.SelectRightTargetCommand.Execute(null);
        }
    }
}
