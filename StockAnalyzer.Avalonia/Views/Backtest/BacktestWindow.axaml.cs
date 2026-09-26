using Avalonia.Controls;
using Avalonia.Input;
using StockAnalyzer.Avalonia.ViewModels.Backtest;

namespace StockAnalyzer.Avalonia.Views.Backtest
{
    public partial class BacktestWindow : Window
    {
        private BacktestWindowViewModel? _subscribedViewModel;

        public BacktestWindow()
        {
            InitializeComponent();

            DataContextChanged += (_, _) =>
            {
                if (_subscribedViewModel != null) _subscribedViewModel.RequestClose -= Close;
                _subscribedViewModel = DataContext as BacktestWindowViewModel;
                if (_subscribedViewModel != null) _subscribedViewModel.RequestClose += Close;
            };
        }

        private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(e);
            }
        }
    }
}
