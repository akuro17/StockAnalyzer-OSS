using System;

namespace StockAnalyzer.Core.Models.Portfolio;

public sealed record Position
{
    public string Ticker { get; init; }
    public decimal Quantity { get; init; }
    public decimal AverageCostPerUnit { get; init; }
    public bool IsShort { get; init; }
    public Money AverageCost { get; init; }

    public Position(string ticker, decimal quantity, decimal averageCostPerUnit, bool isShort = false, Money? averageCost = null)
    {
        if (string.IsNullOrWhiteSpace(ticker)) throw new ArgumentException("Ticker cannot be empty.", nameof(ticker));
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity), "Position quantity must be positive.");
        if (averageCostPerUnit < 0) throw new ArgumentOutOfRangeException(nameof(averageCostPerUnit), "Average cost cannot be negative.");

        Ticker = ticker;
        Quantity = quantity;
        AverageCostPerUnit = averageCostPerUnit;
        IsShort = isShort;
        AverageCost = averageCost ?? new Money(averageCostPerUnit, CurrencyCode.JPY);
    }
}
