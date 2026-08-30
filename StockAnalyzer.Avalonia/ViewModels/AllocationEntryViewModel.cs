using CommunityToolkit.Mvvm.ComponentModel;

namespace StockAnalyzer.Avalonia.ViewModels;

public partial class AllocationEntryViewModel : ViewModelBase
{
    [ObservableProperty] private string _category = string.Empty;
    [ObservableProperty] private decimal _marketValue;
    [ObservableProperty] private decimal _percentage;
    [ObservableProperty] private uint _color;

    public string DisplayPercentage => $"{Percentage:F2}%";
}
