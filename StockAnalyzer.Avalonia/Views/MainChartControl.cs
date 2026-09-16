using StockAnalyzer.Avalonia.Views.Chart;

namespace StockAnalyzer.Avalonia.Views;

/// <summary>
/// Main chart control.
/// Delegates actual drawing logic to ChartBaseControl.
/// This class functions as a layer for styling and additional
/// features specific to a particular view (MainWindow).
/// </summary>
public class MainChartControl : ChartBaseControl
{
    // Currently, MainChartControl uses ChartBaseControl functionality as-is.
    // In the future, if main chart specific UI logic is needed
    // (e.g., toggling specific indicator rendering),
    // implementation will be added to this class.
}
