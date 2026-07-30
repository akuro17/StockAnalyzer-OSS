using System;

namespace StockAnalyzer.Core.Models.Portfolio;

public readonly record struct CurrencyCode
{
    public string Value { get; }

    public CurrencyCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length != 3)
        {
            throw new ArgumentException("Currency code must be a non-empty 3-character string conforming to ISO 4217.", nameof(value));
        }
        Value = value.Trim().ToUpperInvariant();
    }

    public static readonly CurrencyCode JPY = new("JPY");
    public static readonly CurrencyCode USD = new("USD");
    public static readonly CurrencyCode EUR = new("EUR");

    public override string ToString() => Value;
}
