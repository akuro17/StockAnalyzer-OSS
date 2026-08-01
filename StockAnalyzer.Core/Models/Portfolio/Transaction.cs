using System;

namespace StockAnalyzer.Core.Models.Portfolio;

public sealed record Transaction
{
    public DateTimeOffset ExecutedAt { get; init; }
    public TransactionType Type { get; init; }
    public string? Ticker { get; init; }
    public decimal Quantity { get; init; }
    public decimal PricePerUnit { get; init; }
    public decimal CashAmount { get; init; }
    public decimal Fee { get; init; }
    public string? Notes { get; init; }
    public decimal? TargetPrice { get; init; }
    public decimal? StopLoss { get; init; }
    public Guid Id { get; init; }
    public Guid? RelatedTransactionId { get; init; }

    public Money Price { get; init; }
    public Money Commission { get; init; }
    public ExchangeRate? AppliedRate { get; init; }

    public Transaction(
        DateTimeOffset executedAt,
        TransactionType type,
        string? ticker,
        decimal quantity,
        decimal pricePerUnit,
        decimal cashAmount,
        decimal fee = 0m,
        string? notes = null,
        decimal? targetPrice = null,
        decimal? stopLoss = null,
        Guid? id = null,
        Guid? relatedTransactionId = null,
        Money? price = null,
        Money? commission = null,
        ExchangeRate? appliedRate = null)
    {
        if (quantity < 0) throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be non-negative.");
        if (pricePerUnit < 0) throw new ArgumentOutOfRangeException(nameof(pricePerUnit), "Price must be non-negative.");
        if (executedAt > DateTimeOffset.UtcNow) throw new ArgumentOutOfRangeException(nameof(executedAt), "Transaction time cannot be in the future.");
        
        if ((type == TransactionType.Long || type == TransactionType.ExitLong || type == TransactionType.ExitShort || type == TransactionType.Short) && string.IsNullOrWhiteSpace(ticker))
        {
            throw new ArgumentException("Ticker is required for trade transactions.", nameof(ticker));
        }

        ExecutedAt = executedAt;
        Type = type;
        Ticker = ticker;
        Quantity = quantity;
        PricePerUnit = pricePerUnit;
        CashAmount = cashAmount;
        Fee = fee;
        Notes = notes;
        TargetPrice = targetPrice;
        StopLoss = stopLoss;
        Id = id ?? Guid.NewGuid();
        RelatedTransactionId = relatedTransactionId;
        Price = price ?? new Money(pricePerUnit, CurrencyCode.JPY);
        Commission = commission ?? new Money(fee, CurrencyCode.JPY);
        AppliedRate = appliedRate;
    }
}
