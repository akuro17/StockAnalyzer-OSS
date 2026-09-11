namespace StockAnalyzer.Core.Models.Portfolio;

public enum TransactionType
{
    Long = 0,
    Buy = 0,
    ExitLong = 1,
    Sell = 1,
    Deposit = 2,
    Withdrawal = 3,
    Short = 4,
    ExitShort = 5,
    Cover = 5
}
