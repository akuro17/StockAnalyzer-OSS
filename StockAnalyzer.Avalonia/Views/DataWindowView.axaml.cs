using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace StockAnalyzer.Avalonia.Views;

public partial class DataWindowView : UserControl
{
    public DataWindowView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
