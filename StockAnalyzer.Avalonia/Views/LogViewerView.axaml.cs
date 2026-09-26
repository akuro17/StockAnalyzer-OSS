using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using StockAnalyzer.Avalonia.ViewModels;

namespace StockAnalyzer.Avalonia.Views;

public partial class LogViewerView : Window
{
    public LogViewerView()
    {
        InitializeComponent();
    }

    public void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        BeginMoveDrag(e);
    }

    public void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
