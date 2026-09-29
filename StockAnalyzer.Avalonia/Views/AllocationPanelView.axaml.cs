using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace StockAnalyzer.Avalonia.Views;

public partial class AllocationPanelView : UserControl
{
    public AllocationPanelView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
