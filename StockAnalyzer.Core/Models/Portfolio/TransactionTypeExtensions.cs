namespace StockAnalyzer.Core.Models.Portfolio;

/// <summary>
/// Classification of <see cref="TransactionType"/> shared by the portfolio screens (the one definition of "is a cash flow").
/// </summary>
public static class TransactionTypeExtensions
{
    /// <summary>
    /// True for a Deposit or a Withdrawal: cash moves in or out of the account, no ticker is traded.
    /// False for every trade type and for a value that is no member of <see cref="TransactionType"/>.
    /// </summary>
    public static bool IsCashFlow(this TransactionType type) => type is TransactionType.Deposit or TransactionType.Withdrawal;
}
