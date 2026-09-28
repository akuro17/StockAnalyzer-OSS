using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using StockAnalyzer.Avalonia.ViewModels;

namespace StockAnalyzer.Avalonia.Views;

public partial class SeasonalityChartView : UserControl
{
    public SeasonalityChartView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // Clicking into the plot collapses the left settings flyout so it never sits over the chart.
    private void OnPlotPointerPressed(object? sender, PointerPressedEventArgs e)
        => (DataContext as SeasonalityChartViewModel)?.CollapseSettingsFlyout();
}
