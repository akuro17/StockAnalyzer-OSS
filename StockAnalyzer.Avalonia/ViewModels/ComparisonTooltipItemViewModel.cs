using CommunityToolkit.Mvvm.ComponentModel;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.ViewModels;

/// <summary>
/// ViewModel for a single row in the comparison chart floating tooltip.
/// Uses mutable properties with change notification for ZeroAllocation updates.
/// </summary>
public partial class ComparisonTooltipItemViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _stockName = string.Empty;

    [ObservableProperty]
    private string _actualValueText = string.Empty;

    [ObservableProperty]
    private string _indexPercentText = string.Empty;

    [ObservableProperty]
    private IndicatorColor _iconColor;
}
