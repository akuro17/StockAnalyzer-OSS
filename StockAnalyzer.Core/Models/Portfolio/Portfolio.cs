using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.Portfolio;

public sealed record Portfolio
{
    public decimal CashBalance { get; init; }
    public IReadOnlyDictionary<CurrencyCode, decimal> CashBalances { get; init; }
    public IReadOnlyDictionary<string, Position> Positions { get; init; }
    public IReadOnlyList<Transaction> History { get; init; }
    public IReadOnlyList<ClosedPosition> ClosedPositions { get; init; }
    public decimal TotalRealizedPnL { get; init; }

    public Portfolio(
        decimal cashBalance,
        IReadOnlyDictionary<string, Position>? positions = null,
        IReadOnlyList<Transaction>? history = null,
        IReadOnlyList<ClosedPosition>? closedPositions = null,
        decimal totalRealizedPnL = 0m,
        IReadOnlyDictionary<CurrencyCode, decimal>? cashBalances = null)
    {
        if (cashBalance < 0) throw new ArgumentOutOfRangeException(nameof(cashBalance), "Cash balance cannot be negative.");
        
        CashBalances = cashBalances ?? new Dictionary<CurrencyCode, decimal> { { CurrencyCode.JPY, cashBalance } };
        CashBalance = cashBalances != null && cashBalances.TryGetValue(CurrencyCode.JPY, out var jpyBal) ? jpyBal : cashBalance;
        Positions = positions ?? new Dictionary<string, Position>();
        History = history ?? new List<Transaction>();
        ClosedPositions = closedPositions ?? new List<ClosedPosition>();
        TotalRealizedPnL = totalRealizedPnL;
    }
}
