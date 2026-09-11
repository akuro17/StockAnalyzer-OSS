using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace StockAnalyzer.Avalonia.Views;

public partial class SpiralAnalysisView : UserControl
{
    public SpiralAnalysisView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
