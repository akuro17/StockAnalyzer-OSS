using Avalonia.Controls;
using Avalonia.Input;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Core.Models.Screener;

namespace StockAnalyzer.Avalonia.Views.Backtest;

public partial class BacktestIndicatorSelectionView : UserControl
{
    public BacktestIndicatorSelectionView()
    {
        InitializeComponent();
    }

    private void OnLeftConditionContainerPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is BacktestIndicatorSelectionViewModel vm)
        {
            vm.SelectLeftConditionTargetCommand.Execute(null);
        }
    }

    private void OnRightConditionContainerPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is BacktestIndicatorSelectionViewModel vm && vm.ConditionTargetMode == RightHandTargetMode.Indicator)
        {
            vm.SelectRightConditionTargetCommand.Execute(null);
        }
    }
}
