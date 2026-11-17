using System.Collections.Generic;
using StockAnalyzer.Core.Models.Portfolio;

namespace StockAnalyzer.Core.Services;

/// <summary>The one definition of which exchange rates a portfolio valuation uses: the rates the user's own transactions applied, newest first.</summary>
public static class PortfolioExchangeRates
{
    /// <summary>
    /// For every currency the newest rate that names it as its base: each applied rate and its inverse, scanning <paramref name="history"/> from newest to oldest
    /// (the first rate found for a currency wins). Nothing is fetched; a currency without a rate has no entry.
    /// </summary>
    public static IReadOnlyDictionary<CurrencyCode, ExchangeRate> ExtractLatest(IReadOnlyList<Transaction> history)
    {
        var rates = new Dictionary<CurrencyCode, ExchangeRate>();
        for (int i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].AppliedRate is not { } rate)
            {
                continue;
            }

            rates.TryAdd(rate.BaseCurrency, rate);
            ExchangeRate inverse = rate.Inverse();
            rates.TryAdd(inverse.BaseCurrency, inverse);
        }

        return rates;
    }
}
