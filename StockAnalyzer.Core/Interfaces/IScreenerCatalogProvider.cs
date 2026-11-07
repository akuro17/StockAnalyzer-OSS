using System.Collections.Generic;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Interfaces;

/// <summary>
/// Provider interface for supplying catalog metadata for Indicators, Columns, and Screening Criteria.
/// Extracted to adhere to Single Source of Truth (SSoT) and Single Responsibility Principle (SRP).
/// </summary>
public interface IScreenerCatalogProvider
{
    /// <summary>
    /// Gets all catalog items across Indicators, Columns, and Criteria.
    /// </summary>
    /// <param name="indicatorFactory">Optional indicator factory for dynamic settings generation.</param>
    /// <returns>A read-only list of screener catalog items.</returns>
    IReadOnlyList<ScreenerCatalogItem> GetCatalogItems(IIndicatorFactory? indicatorFactory = null);

    /// <summary>
    /// The catalog of the Screener itself (the Screener tab and the MCP screener): <see cref="GetCatalogItems"/> plus the indicators that compare two symbols and that the
    /// Screener evaluates although the Backtest refuses them (<c>ScreenerIndicatorEligibility</c>). The Backtest selection and the Scatter registration keep using
    /// <see cref="GetCatalogItems"/>, whose result is unchanged. A provider written before this member existed answers with <see cref="GetCatalogItems"/>.
    /// </summary>
    /// <param name="indicatorFactory">Optional indicator factory for dynamic settings generation.</param>
    IReadOnlyList<ScreenerCatalogItem> GetCatalogItemsForScreener(IIndicatorFactory? indicatorFactory = null) => GetCatalogItems(indicatorFactory);

    /// <summary>
    /// Gets default indicator settings for a specified indicator type.
    /// </summary>
    /// <param name="type">The indicator type.</param>
    /// <param name="indicatorFactory">Optional indicator factory instance.</param>
    /// <returns>CoreIndicatorSettings instance if available.</returns>
    CoreIndicatorSettings? GetDefaultSettings(IndicatorType type, IIndicatorFactory? indicatorFactory = null);

    /// <summary>
    /// Gets available output series names for a specified indicator type (e.g. ["Main", "Signal", "Histogram"]).
    /// </summary>
    /// <param name="type">The indicator type.</param>
    /// <param name="indicatorFactory">Optional indicator factory instance.</param>
    /// <returns>A list of output series names.</returns>
    IReadOnlyList<string> GetOutputSeriesNames(IndicatorType type, IIndicatorFactory? indicatorFactory = null);

    /// <summary>Maps a display alias to the canonical series name used by indicator results.</summary>
    string NormalizeOutputSeriesName(
        IndicatorType type,
        string? outputName,
        PriceType? priceSource = null,
        IIndicatorFactory? indicatorFactory = null) =>
        IndicatorOutputSeriesResolver.Normalize(type, outputName, priceSource, indicatorFactory);
}
