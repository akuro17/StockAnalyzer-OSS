using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.Services.Analysis;

/// <summary>
/// Service for coordinating various analysis and indicator calculations.
/// decoupling calculation logic from data coordination view models.
/// </summary>
public interface IAnalysisPipelineService
{
    Dictionary<string, IIndicatorResult> CalculateIndicators(IReadOnlyList<CoreCandleData> candles, IEnumerable<CoreIndicatorSettings> settings);

    /// <summary>
    /// Calculates all enabled indicators asynchronously based on the provided settings.
    /// This prevents blocking the UI thread for long-running indicators (like Python ML models).
    /// </summary>
    /// <param name="candles">The source candle data.</param>
    /// <param name="settings">The indicator settings.</param>
    /// <returns>A dictionary of indicator values keyed by setting ID.</returns>
    System.Threading.Tasks.Task<Dictionary<string, IIndicatorResult>> CalculateIndicatorsAsync(IReadOnlyList<CoreCandleData> candles, IEnumerable<CoreIndicatorSettings> settings);

    /// <summary>
    /// Calculates Reverse Watch curve data.
    /// </summary>
    /// <param name="candles">The source candle data.</param>
    /// <param name="period">The rolling window period.</param>
    /// <param name="symbol">The stock symbol (for metadata).</param>
    /// <returns>Calculated Reverse Watch data or null if calculation fails/invalid.</returns>
    ReverseWatchCurveData? CalculateReverseWatch(IReadOnlyList<CoreCandleData> candles, int period, string symbol, bool isMaBased = true, bool isLogScaleVolume = false, int dataCount = 0);

    /// <summary>
    /// Calculates the Average True Range (ATR) for the given period.
    /// </summary>
    /// <param name="candles">The source candle data.</param>
    /// <param name="period">The ATR period.</param>
    /// <returns>The latest ATR value, or 0 if insufficient data.</returns>
    decimal CalculateAtr(IReadOnlyList<CoreCandleData> candles, int period);
}
