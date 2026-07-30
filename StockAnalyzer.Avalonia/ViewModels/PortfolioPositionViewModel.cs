using CommunityToolkit.Mvvm.ComponentModel;
using StockAnalyzer.Core.Models.Portfolio;

namespace StockAnalyzer.Avalonia.ViewModels;

/// <summary>
/// ViewModel for an individual portfolio position.
/// Implements INotifyPropertyChanged for in-place updates.
/// </summary>
public partial class PortfolioPositionViewModel : ViewModelBase
{
    [ObservableProperty] private string _symbol = string.Empty;
    [ObservableProperty] private string _displaySymbol = string.Empty;
    [ObservableProperty] private decimal _quantity;
    [ObservableProperty] private decimal _averagePrice;
    [ObservableProperty] private decimal _currentPrice;
    [ObservableProperty] private decimal _marketValue;
    [ObservableProperty] private decimal _unrealizedPnL;
    [ObservableProperty] private decimal _pnLRate;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Type))] [NotifyPropertyChangedFor(nameof(ShowSymbol))] [NotifyPropertyChangedFor(nameof(SymbolMargin))] [NotifyPropertyChangedFor(nameof(SymbolVerticalAlignment))] [NotifyPropertyChangedFor(nameof(ZIndex))] [NotifyPropertyChangedFor(nameof(SymbolBorderThickness))] private bool _isShort;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowSymbol))] [NotifyPropertyChangedFor(nameof(SymbolMargin))] [NotifyPropertyChangedFor(nameof(SymbolVerticalAlignment))] [NotifyPropertyChangedFor(nameof(ZIndex))] [NotifyPropertyChangedFor(nameof(SymbolBorderThickness))] private bool _isHedged;

    public string Type => IsShort ? "Short" : "Long";

    public bool ShowSymbol => !IsShort || !IsHedged;
    public global::Avalonia.Layout.VerticalAlignment SymbolVerticalAlignment => (IsHedged && !IsShort) ? global::Avalonia.Layout.VerticalAlignment.Bottom : global::Avalonia.Layout.VerticalAlignment.Center;
    public global::Avalonia.Thickness SymbolMargin => (IsHedged && !IsShort) ? new global::Avalonia.Thickness(0, 0, 0, -11) : new global::Avalonia.Thickness(0);
    public int ZIndex => (IsHedged && !IsShort) ? 10 : 0;
    public global::Avalonia.Thickness SymbolBorderThickness => (IsHedged && !IsShort) ? new global::Avalonia.Thickness(0, 0, 1, 0) : new global::Avalonia.Thickness(0, 0, 1, 1);

    // Zero-Allocation Styling Properties (calculated for binding)
    public bool IsProfit => UnrealizedPnL > 0;
    public bool IsLoss => UnrealizedPnL < 0;
    public bool IsNeutral => UnrealizedPnL == 0;

    /// <summary>
    /// Updates the ViewModel properties in-place from an evaluation result.
    /// This avoids re-allocating the VM instance in the DataGrid.
    /// </summary>
    public void UpdateFrom(PositionEvaluation eval)
    {
        Symbol = eval.Symbol;
        DisplaySymbol = eval.DisplaySymbol;
        Quantity = eval.Quantity;
        AveragePrice = eval.AveragePrice;
        CurrentPrice = eval.CurrentPrice;
        MarketValue = eval.MarketValue;
        UnrealizedPnL = eval.UnrealizedPnL;
        PnLRate = eval.PnLRate;
        IsShort = eval.IsShort;
        
        OnPropertyChanged(nameof(IsProfit));
        OnPropertyChanged(nameof(IsLoss));
        OnPropertyChanged(nameof(IsNeutral));
        OnPropertyChanged(nameof(Type));
        OnPropertyChanged(nameof(ShowSymbol));
        OnPropertyChanged(nameof(SymbolVerticalAlignment));
        OnPropertyChanged(nameof(SymbolMargin));
        OnPropertyChanged(nameof(ZIndex));
        OnPropertyChanged(nameof(SymbolBorderThickness));
    }
}
