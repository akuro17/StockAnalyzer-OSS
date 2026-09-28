using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Extracts market data, indicator series, and drawing tool metrics at a specific timestamp or coordinate.
/// Conforms to DataWindow formatting standards while strictly omitting the ticker symbol.
/// Functions as a pure, side-effect-free provider adhering to the shared function protection rules.
/// </summary>
public static class ChartInformationDataProvider
{
    /// <summary>
    /// Extracts a ChartInformationSnapshot from individual data sources.
    /// </summary>
    public static ChartInformationSnapshot Extract(
        IReadOnlyList<CoreCandleData>? candles,
        IEnumerable<CoreIndicatorSettings>? indicators,
        IReadOnlyDictionary<string, IIndicatorResult>? indicatorResults,
        ChartObjectManager? objectManager,
        DateTime targetTime,
        decimal? targetPrice = null,
        ThemeColors? theme = null)
    {
        var effectiveTheme = theme ?? ThemeColors.Dark;
        int globalIndex = -1;
        CoreCandleData? candle = null;

        if (candles != null && candles.Count > 0)
        {
            // Binary search for exact or nearest timestamp in chronological candles
            int low = 0;
            int high = candles.Count - 1;
            while (low <= high)
            {
                int mid = low + (high - low) / 2;
                if (candles[mid].Timestamp == targetTime)
                {
                    globalIndex = mid;
                    break;
                }
                if (candles[mid].Timestamp < targetTime)
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            if (globalIndex < 0)
            {
                if (low >= candles.Count)
                {
                    globalIndex = candles.Count - 1;
                }
                else if (high < 0)
                {
                    globalIndex = 0;
                }
                else
                {
                    long d1 = Math.Abs((candles[high].Timestamp - targetTime).Ticks);
                    long d2 = Math.Abs((candles[low].Timestamp - targetTime).Ticks);
                    globalIndex = d1 <= d2 ? high : low;
                }
            }

            candle = candles[globalIndex];
        }

        // 1. Extract Candle Information (Symbol deliberately omitted)
        CandleInformation? candleInfo = null;
        if (candle != null)
        {
            var c = candle;
            string dateText = (c.Timestamp.Hour == 0 && c.Timestamp.Minute == 0)
                ? c.Timestamp.ToString("yyyy/MM/dd")
                : c.Timestamp.ToString("yyyy/MM/dd HH:mm");

            string openText = c.Open.ToString("0.000");
            string highText = c.High.ToString("0.000");
            string lowText = c.Low.ToString("0.000");
            string closeText = c.Close.ToString("0.000");
            string volumeText = c.Volume.ToString("N0");

            string changeText = string.Empty;
            string changeRatioText = string.Empty;
            IndicatorColor changeColor = effectiveTheme.SemanticNeutral;

            if (candles != null && globalIndex > 0 && globalIndex < candles.Count)
            {
                var prev = candles[globalIndex - 1];
                var diff = c.Close - prev.Close;
                var ratio = prev.Close > 0 ? (diff / prev.Close) * 100m : 0m;
                changeText = (diff > 0 ? "+" : "") + diff.ToString("0.000");
                changeRatioText = (ratio > 0 ? "+" : "") + ratio.ToString("0.00") + "%";
                changeColor = diff > 0 ? effectiveTheme.SemanticPlus : (diff < 0 ? effectiveTheme.SemanticMinus : effectiveTheme.SemanticNeutral);
            }

            candleInfo = new CandleInformation(
                DateText: dateText,
                OpenText: openText,
                HighText: highText,
                LowText: lowText,
                CloseText: closeText,
                VolumeText: volumeText,
                YesterdayChangeText: changeText,
                YesterdayChangeRatioText: changeRatioText,
                YesterdayChangeColor: changeColor
            );
        }

        // 2. Extract Technical Indicators
        var indicatorItems = new List<IndicatorInformationItem>();
        if (indicators != null && globalIndex >= 0)
        {
            foreach (var indicator in indicators)
            {
                if (!indicator.IsEnabled) continue;

                string indicatorName = indicator.ShortDisplayName;
                if (string.IsNullOrEmpty(indicatorName)) indicatorName = indicator.DisplayName;
                if (string.IsNullOrEmpty(indicatorName)) indicatorName = indicator.TypeEnum?.ToString() ?? "Indicator";

                if (indicatorResults == null || !indicatorResults.TryGetValue(indicator.Id, out var result) || result == null)
                {
                    continue;
                }

                var seriesNames = result.SeriesNamesList;
                if (seriesNames == null || seriesNames.Count == 0)
                {
                    var main = result.MainValues;
                    decimal? val = (main != null && globalIndex >= 0 && globalIndex < main.Count) ? main[globalIndex] : null;
                    string valStr = val.HasValue ? val.Value.ToString("0.000") : "N/A";
                    indicatorItems.Add(new IndicatorInformationItem(indicatorName, valStr, indicator.Color));
                    continue;
                }

                var validSeries = new List<string>();
                foreach (var n in seriesNames)
                {
                    if (n == "BullishSignals" || n == "BearishSignals" || n == "BuySignals" || n == "SellSignals")
                        continue;

                    bool isMainExplicitlyMapped = indicator.SeriesColors != null && indicator.SeriesColors.Any(sc => sc.TargetSeries.Contains("Main"));
                    if (n == "Main" && !isMainExplicitlyMapped && seriesNames.Any(other => other != "Main" && !other.Contains("Signal") && !other.Contains("Histogram") && !other.Contains("Trend")))
                        continue;

                    validSeries.Add(n);
                }

                if (validSeries.Count == 0)
                {
                    var main = result.MainValues;
                    decimal? val = (main != null && globalIndex >= 0 && globalIndex < main.Count) ? main[globalIndex] : null;
                    string valStr = val.HasValue ? val.Value.ToString("0.000") : "N/A";
                    indicatorItems.Add(new IndicatorInformationItem(indicatorName, valStr, indicator.Color));
                }
                else if (validSeries.Count == 1)
                {
                    var series = result.GetSeries(validSeries[0]);
                    decimal? val = (series != null && globalIndex >= 0 && globalIndex < series.Count) ? series[globalIndex] : null;
                    string valStr = val.HasValue ? val.Value.ToString("0.000") : "N/A";
                    indicatorItems.Add(new IndicatorInformationItem(indicatorName, valStr, indicator.Color));
                }
                else
                {
                    foreach (var seriesName in validSeries)
                    {
                        var series = result.GetSeries(seriesName);
                        decimal? val = (series != null && globalIndex >= 0 && globalIndex < series.Count) ? series[globalIndex] : null;
                        string valStr = val.HasValue ? val.Value.ToString("0.000") : "N/A";

                        var childColor = indicator.Color;
                        string label = seriesName;
                        var colorConfig = indicator.SeriesColors?.FirstOrDefault(sc => sc.TargetSeries.Contains(seriesName));
                        if (colorConfig != null)
                        {
                            childColor = colorConfig.Color;
                            if (!string.IsNullOrEmpty(colorConfig.DisplayName)) label = colorConfig.DisplayName;
                        }
                        else if (result.SeriesLabels.TryGetValue(seriesName, out var cachedLabel))
                        {
                            label = cachedLabel;
                        }

                        indicatorItems.Add(new IndicatorInformationItem($"{indicatorName} ({label})", valStr, childColor));
                    }
                }
            }
        }

        // 3. Extract Drawing Tools
        var drawingItems = new List<DrawingInformationItem>();
        if (objectManager != null && objectManager.Objects.Count > 0)
        {
            DateTime evalTime = candle?.Timestamp ?? targetTime;
            decimal? evalPrice = candle?.Close ?? targetPrice;

            foreach (var obj in objectManager.Objects)
            {
                if (!obj.IsVisible) continue;
                if (obj is not IDrawingCalculatedValuesProvider provider) continue;

                var values = provider.GetCalculatedValues(evalTime, evalPrice);
                if (values == null || values.Count == 0) continue;

                string displayName = DrawingObjectDisplayNameHelper.GetDisplayName(obj);
                foreach (var val in values)
                {
                    drawingItems.Add(new DrawingInformationItem(displayName, val.Label, val.FormattedText, val.Color));
                }
            }
        }

        return new ChartInformationSnapshot(
            Timestamp: candle?.Timestamp ?? targetTime,
            Candle: candleInfo,
            Indicators: indicatorItems,
            Drawings: drawingItems
        );
    }

    /// <summary>
    /// Extracts a ChartInformationSnapshot from a ChartViewModel instance.
    /// </summary>
    public static ChartInformationSnapshot Extract(ChartViewModel viewModel, DateTime targetTime, decimal? targetPrice = null)
    {
        if (viewModel == null) throw new ArgumentNullException(nameof(viewModel));

        return Extract(
            candles: viewModel.Candles,
            indicators: viewModel.Indicators,
            indicatorResults: viewModel.IndicatorResults,
            objectManager: viewModel.ObjectManager,
            targetTime: targetTime,
            targetPrice: targetPrice,
            theme: viewModel.ThemeManager?.CurrentTheme
        );
    }
}
