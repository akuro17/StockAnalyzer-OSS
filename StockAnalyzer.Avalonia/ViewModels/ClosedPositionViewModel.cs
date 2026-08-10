using System;
using CommunityToolkit.Mvvm.ComponentModel;
using StockAnalyzer.Core.Models.Portfolio;

namespace StockAnalyzer.Avalonia.ViewModels;

/// <summary>
/// ViewModel for wrapping ClosedPosition data to be bound to UI with styling helpers.
/// </summary>
public partial class ClosedPositionViewModel : ViewModelBase
{
    [ObservableProperty] private string _ticker = string.Empty;
    [ObservableProperty] private TransactionType _type;
    [ObservableProperty] private decimal _quantity;
    [ObservableProperty] private decimal _entryPrice;
    [ObservableProperty] private decimal _exitPrice;
    [ObservableProperty] private DateTimeOffset _entryTime;
    [ObservableProperty] private DateTimeOffset _exitTime;
    [ObservableProperty] private decimal _realizedPnL;
    [ObservableProperty] private decimal _totalFees;
    [ObservableProperty] private decimal _pnLRate;

    public bool IsProfit => RealizedPnL > 0;
    public bool IsLoss => RealizedPnL < 0;
    public bool IsNeutral => RealizedPnL == 0;

    public void UpdateFrom(ClosedPosition model)
    {
        Ticker = model.Ticker;
        Type = model.Type;
        Quantity = model.Quantity;
        EntryPrice = model.EntryPrice;
        ExitPrice = model.ExitPrice;
        EntryTime = model.EntryTime;
        ExitTime = model.ExitTime;
        RealizedPnL = model.RealizedPnL;
        TotalFees = model.TotalFees;

        PnLRate = EntryPrice == 0 ? 0 : 
            (Type == TransactionType.Short ? (EntryPrice - ExitPrice) / EntryPrice * 100 : (ExitPrice - EntryPrice) / EntryPrice * 100);

        OnPropertyChanged(nameof(IsProfit));
        OnPropertyChanged(nameof(IsLoss));
        OnPropertyChanged(nameof(IsNeutral));
    }
}
