using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>
/// Represents a selectable Watchlist or Portfolio profile in the Add Ticker dialog.
/// </summary>
public partial class WatchlistProfileSelectItemViewModel : ObservableObject
{
    public Guid Id { get; }
    public string Name { get; }
    public bool IsPortfolio { get; }

    [ObservableProperty]
    private bool _isSelected;

    public WatchlistProfileSelectItemViewModel(Guid id, string name, bool isPortfolio, bool isSelected = false)
    {
        Id = id;
        Name = name;
        IsPortfolio = isPortfolio;
        _isSelected = isSelected;
    }
}
