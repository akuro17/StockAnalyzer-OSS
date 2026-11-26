using CommunityToolkit.Mvvm.ComponentModel;

namespace StockAnalyzer.Avalonia.ViewModels;

/// <summary>One entry of the per-currency cash strip of the portfolio Summary tab: a currency code and its cash balance in that currency.</summary>
public partial class CurrencyBalanceViewModel : ViewModelBase
{
    public CurrencyBalanceViewModel(string currency, decimal balance)
    {
        Currency = currency;
        _balance = balance;
    }

    public string Currency { get; }

    [ObservableProperty]
    private decimal _balance;
}
