using Avalonia.Controls;
using Avalonia.Input;

namespace StockAnalyzer.Avalonia.Views
{
    public partial class ScreenerWindow : Window
    {
        public ScreenerWindow()
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
    }
}
