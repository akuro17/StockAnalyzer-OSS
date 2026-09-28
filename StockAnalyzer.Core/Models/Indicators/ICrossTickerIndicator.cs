using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.Indicators;

/// <summary>
/// Defines a contract for indicators that consume secondary symbol candles for cross-ticker analysis.
/// </summary>
public interface ICrossTickerIndicator
{
    string ComparisonSymbol { get; set; }
    PriceType ComparisonPriceSource { get; set; }
    void SetSecondaryCandles(IReadOnlyList<CoreCandleData?>? candles);
}
