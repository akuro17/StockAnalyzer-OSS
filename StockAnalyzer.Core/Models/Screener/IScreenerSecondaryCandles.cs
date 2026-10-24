using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.Screener;

/// <summary>
/// The candles of the comparison symbols an evaluation may read, for the indicators that compare two tickers (<c>ICrossTickerIndicator</c>).
/// Shared by the evaluation of every symbol of one scan and only read. Passing none (null) keeps the Screener's original rule: a two-ticker
/// indicator has no secondary data and gives no value.
/// </summary>
public interface IScreenerSecondaryCandles
{
    /// <summary>The bars of <paramref name="comparisonSymbol"/>, or null when they are not available.</summary>
    IReadOnlyList<CoreCandleData?>? Get(string comparisonSymbol);
}
