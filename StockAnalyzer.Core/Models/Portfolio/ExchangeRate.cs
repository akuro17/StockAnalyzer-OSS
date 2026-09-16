using System;

namespace StockAnalyzer.Core.Models.Portfolio;

public readonly record struct ExchangeRate
{
    public CurrencyCode BaseCurrency { get; }
    public CurrencyCode QuoteCurrency { get; }
    public decimal Rate { get; }
    public DateTime AsOf { get; }

    public ExchangeRate(CurrencyCode baseCurrency, CurrencyCode quoteCurrency, decimal rate, DateTime asOf)
    {
        if (rate <= 0)
        {
            throw new ArgumentException("Exchange rate must be positive.", nameof(rate));
        }
        BaseCurrency = baseCurrency;
        QuoteCurrency = quoteCurrency;
        Rate = rate;
        AsOf = asOf;
    }

    public Money Convert(Money amount)
    {
        if (amount.Currency != BaseCurrency)
        {
            throw new InvalidOperationException($"Cannot convert money of currency {amount.Currency} using rate defined for {BaseCurrency}.");
        }
        return new Money(amount.Amount * Rate, QuoteCurrency);
    }

    public ExchangeRate Inverse()
    {
        return new ExchangeRate(QuoteCurrency, BaseCurrency, 1m / Rate, AsOf);
    }

    public override string ToString() => $"1 {BaseCurrency} = {Rate} {QuoteCurrency} (As of {AsOf:yyyy-MM-dd HH:mm:ss})";
}
