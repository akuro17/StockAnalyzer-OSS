using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Screener;

namespace StockAnalyzer.Core.Interfaces;

/// <summary>
/// Provides high-performance, zero-allocation indicator value extraction for screening conditions.
/// </summary>
public interface IScreenerValueExtractor
{
    /// <summary>
    /// Extracts the indicator value for the specified side configuration using a pre-mapped CoreCandleData list.
    /// </summary>
    decimal ExtractValue(ScreenerIndicatorSideConfig? config, IReadOnlyList<CoreCandleData> candles);

    /// <summary>
    /// Extracts the indicator value directly using a zero-allocation CandleData list.
    /// </summary>
    decimal ExtractValue(ScreenerIndicatorSideConfig? config, IReadOnlyList<CandleData> candles, TickerMetadata metadata = default);

    /// <summary>
    /// Extracts the indicator value returning null if dataset is missing or unresolvable.
    /// </summary>
    decimal? ExtractValueNullable(ScreenerIndicatorSideConfig? config, IReadOnlyList<CandleData> candles, TickerMetadata metadata = default);

    /// <summary>
    /// Extracts the text a text Column (Sector, Industry, Region, Name, Tag) holds for the ticker; null when the side is not a text Column or the
    /// metadata has no value. Existing implementers need not change: the default returns null.
    /// </summary>
    string? ExtractStringValue(ScreenerIndicatorSideConfig? config, TickerMetadata metadata) => null;
}
