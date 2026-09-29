using Avalonia.Controls;

namespace StockAnalyzer.Avalonia.Views.Controls;

/// <summary>
/// Fixed-size month-by-year returns table for the "Seasonality Clock" tab. Expects its
/// <see cref="Control.DataContext"/> to be a <see cref="SeasonalityMonthlyReturnsPresentation"/>
/// (or null). The frame never resizes; a shallower overlay leaves whitespace inside the clip.
/// </summary>
public partial class SeasonalityMonthlyReturnsTableView : UserControl
{
    public SeasonalityMonthlyReturnsTableView()
    {
        InitializeComponent();
    }
}
