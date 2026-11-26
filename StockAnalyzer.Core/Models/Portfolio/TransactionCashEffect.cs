using System;

namespace StockAnalyzer.Core.Models.Portfolio;

/// <summary>
/// The cash a transaction moves, as the portfolio engine (<see cref="StockAnalyzer.Core.Services.PortfolioManager"/>) books it, for display.
/// A trade is stored with <see cref="Transaction.CashAmount"/> = 0, so the amount of a trade is calculated here instead of being stored.
/// </summary>
public static class TransactionCashEffect
{
    /// <summary>
    /// The signed cash effect in <see cref="Transaction.Price"/>'s currency (positive = cash in, negative = cash out), mirroring the engine:
    /// Deposit +CashAmount; Withdrawal -CashAmount; Long and ExitShort -(Quantity x Price.Amount + Commission.Amount);
    /// ExitLong and Short +(Quantity x Price.Amount - Commission.Amount). The commission amount is applied as stored, whatever its currency (the engine does the same).
    /// Returns null for a type that is no member of <see cref="TransactionType"/>; never throws for a transaction.
    /// </summary>
    public static decimal? GetSettlementAmount(Transaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        return transaction.Type switch
        {
            TransactionType.Deposit => transaction.CashAmount,
            TransactionType.Withdrawal => -transaction.CashAmount,
            TransactionType.Long or TransactionType.ExitShort => -((transaction.Quantity * transaction.Price.Amount) + transaction.Commission.Amount),
            TransactionType.ExitLong or TransactionType.Short => (transaction.Quantity * transaction.Price.Amount) - transaction.Commission.Amount,
            _ => null,
        };
    }
}
