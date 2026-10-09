using Avalonia.Controls;
using Avalonia.Input;

namespace StockAnalyzer.Avalonia.Views;

public partial class PortfolioRlWindow : Window
{
    public PortfolioRlWindow()
    {
        InitializeComponent();
        SubWindowKeys.CloseOnEscape(this);
    }

    /// <summary>The custom header replaces the native title bar, so it moves the window.</summary>
    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        BeginMoveDrag(e);
    }
}
