using System;

namespace StockAnalyzer.Core.Models.Portfolio;

public sealed record ClosedPosition
{
    public Guid Id { get; init; }
    public string Ticker { get; init; }
    public TransactionType Type { get; init; } // Long or Short
    public decimal Quantity { get; init; }
    public decimal EntryPrice { get; init; }
    public decimal ExitPrice { get; init; }
    public DateTimeOffset EntryTime { get; init; }
    public DateTimeOffset ExitTime { get; init; }
    public decimal RealizedPnL { get; init; }
    public decimal TotalFees { get; init; }

    public ClosedPosition(
        Guid id,
        string ticker,
        TransactionType type,
        decimal quantity,
        decimal entryPrice,
        decimal exitPrice,
        DateTimeOffset entryTime,
        DateTimeOffset exitTime,
        decimal realizedPnL,
        decimal totalFees)
    {
        if (string.IsNullOrWhiteSpace(ticker)) throw new ArgumentException("Ticker cannot be empty.", nameof(ticker));
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be positive.");
        
        Id = id;
        Ticker = ticker;
        Type = type;
        Quantity = quantity;
        EntryPrice = entryPrice;
        ExitPrice = exitPrice;
        EntryTime = entryTime;
        ExitTime = exitTime;
        RealizedPnL = realizedPnL;
        TotalFees = totalFees;
    }
}
