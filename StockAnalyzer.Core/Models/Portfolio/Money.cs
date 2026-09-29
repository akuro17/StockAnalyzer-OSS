using System;

namespace StockAnalyzer.Core.Models.Portfolio;

public readonly record struct Money
{
    public decimal Amount { get; }
    public CurrencyCode Currency { get; }

    public Money(decimal amount, CurrencyCode currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public static Money operator +(Money a, Money b)
    {
        if (a.Currency != b.Currency)
        {
            throw new InvalidOperationException($"Cannot add different currencies: {a.Currency} and {b.Currency}. Apply exchange rate first.");
        }
        return new Money(a.Amount + b.Amount, a.Currency);
    }

    public static Money operator -(Money a, Money b)
    {
        if (a.Currency != b.Currency)
        {
            throw new InvalidOperationException($"Cannot subtract different currencies: {a.Currency} and {b.Currency}. Apply exchange rate first.");
        }
        return new Money(a.Amount - b.Amount, a.Currency);
    }

    public override string ToString() => $"{Amount} {Currency}";
}
