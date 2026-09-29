using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Unified calculation context supplied to drawing tools during calculation and rendering updates.
/// Wraps historical candle data, active technical indicator outputs, and indicator settings (ZeroAllocation).
/// </summary>
public readonly record struct DrawingCalculationContext(
    IReadOnlyList<CoreCandleData>? Candles,
    IReadOnlyDictionary<string, IIndicatorResult>? IndicatorResults = null,
    IReadOnlyList<CoreIndicatorSettings>? IndicatorSettings = null,
    string Symbol = "")
{
    public static readonly DrawingCalculationContext Empty = new(null, null, null, string.Empty);

    public bool HasCandles => Candles != null && Candles.Count > 0;
    public bool HasIndicators => IndicatorResults != null && IndicatorResults.Count > 0;

    /// <summary>
    /// Attempts to retrieve an indicator result by its unique setting ID.
    /// </summary>
    public bool TryGetIndicatorResult(string indicatorId, out IIndicatorResult? result)
    {
        if (IndicatorResults != null && !string.IsNullOrEmpty(indicatorId))
        {
            return IndicatorResults.TryGetValue(indicatorId, out result);
        }

        result = null;
        return false;
    }

    /// <summary>
    /// Attempts to find the first active indicator result matching the specified indicator type name (e.g. "ATR", "SMA", "EMA", "RSI").
    /// </summary>
    public bool TryGetFirstIndicatorResultByType(string typeName, out IIndicatorResult? result, out CoreIndicatorSettings? settings)
    {
        if (IndicatorSettings != null && IndicatorResults != null)
        {
            for (int i = 0; i < IndicatorSettings.Count; i++)
            {
                var setting = IndicatorSettings[i];
                if (setting.IsEnabled && setting.TypeEnum.HasValue && string.Equals(setting.TypeEnum.Value.ToString(), typeName, StringComparison.OrdinalIgnoreCase))
                {
                    if (IndicatorResults.TryGetValue(setting.Id, out result))
                    {
                        settings = setting;
                        return true;
                    }
                }
            }
        }

        result = null;
        settings = null;
        return false;
    }
}
