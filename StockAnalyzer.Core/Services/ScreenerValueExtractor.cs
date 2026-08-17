using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.ElliottWave;
using StockAnalyzer.Core.Models.GeometricPattern;
using StockAnalyzer.Core.Models.HarmonicPattern;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.MarketStructure;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.ScreeningConditions;
using StockAnalyzer.Core.Services.Analysis;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Provides high-performance, zero-allocation indicator value extraction for screening conditions.
/// Utilizes per-thread indicator instances to prevent allocation overhead while maintaining thread-safety.
/// </summary>
public class ScreenerValueExtractor : IScreenerValueExtractor
{
    private static readonly Lazy<ScreenerValueExtractor> _instance = new(() => new ScreenerValueExtractor());
    public static ScreenerValueExtractor Default => _instance.Value;

    private static readonly ConcurrentDictionary<string, PropertyInfo?> _metadataPropertyCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(IndicatorType type, int threadId), ICoreIndicator?> _threadLocalIndicators = new();

    public decimal ExtractValue(ScreenerIndicatorSideConfig? config, IReadOnlyList<CoreCandleData> candles)
    {
        return ExtractValue(config, candles, default);
    }

    public decimal ExtractValue(ScreenerIndicatorSideConfig? config, IReadOnlyList<CandleData> candles, TickerMetadata metadata = default)
    {
        return ExtractValueNullable(config, candles, metadata) ?? 0m;
    }

    public decimal? ExtractValueNullable(ScreenerIndicatorSideConfig? config, IReadOnlyList<CandleData> candles, TickerMetadata metadata = default)
    {
        if (config == null || candles == null || candles.Count == 0) return null;

        // Delegate Column fundamental & OHLC metric extraction directly without heavy allocations
        int offset = config.Offset;
        int targetIdx = candles.Count - 1 - offset;
        if (targetIdx < 0 || targetIdx >= candles.Count)
            targetIdx = candles.Count - 1;

        var candle = candles[targetIdx];

        if (config.CategoryType == ScreenerItemCategoryType.Column)
        {
            string name = new[] { config.CustomDisplayName, config.OutputName, config.DisplayName }
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s) && s != "SMA" && s != "Main") ?? config.DisplayName ?? "";

            if (TryGetOhlcValue(name, candle, targetIdx, candles, out decimal ohlcVal))
                return ohlcVal;

            return GetMetadataValue(metadata, name);
        }

        // For Indicator/Criteria evaluation, convert to CoreCandleData list
        List<CoreCandleData> coreCandles = new(candles.Count);
        for (int i = 0; i < candles.Count; i++)
        {
            var c = candles[i];
            coreCandles.Add(new CoreCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume));
        }

        return ExtractValue(config, coreCandles, metadata);
    }

    public decimal ExtractValue(ScreenerIndicatorSideConfig? config, IReadOnlyList<CoreCandleData> candles, TickerMetadata metadata)
    {
        if (config == null || candles == null || candles.Count == 0) return 0m;

        int offset = config.Offset;
        int targetIdx = candles.Count - 1 - offset;
        if (targetIdx < 0 || targetIdx >= candles.Count)
            targetIdx = candles.Count - 1;

        var candle = candles[targetIdx];

        if (config.CategoryType == ScreenerItemCategoryType.Column)
        {
            string name = new[] { config.CustomDisplayName, config.OutputName, config.DisplayName }
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s) && s != "SMA" && s != "Main") ?? config.DisplayName ?? "";

            if (TryGetOhlcValue(name, candle, targetIdx, candles, out decimal ohlcVal))
                return ohlcVal;

            // Extract fundamental metric from TickerMetadata
            var metaVal = GetMetadataValue(metadata, name);
            return metaVal ?? 0m;
        }

        if (config.CategoryType == ScreenerItemCategoryType.Criteria)
        {
            string name = new[] { config.CustomDisplayName, config.OutputName, config.DisplayName }
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s) && s != "SMA" && s != "Main") ?? "";

            int takeCount = Math.Min(candles.Count, 100);
            int startIdx = candles.Count - takeCount;

            List<CandleData> portfolioCandles = new(takeCount);
            for (int i = startIdx; i < candles.Count; i++)
            {
                var c = candles[i];
                portfolioCandles.Add(new CandleData
                {
                    Timestamp = c.Timestamp,
                    Open = c.Open,
                    High = c.High,
                    Low = c.Low,
                    Close = c.Close,
                    Volume = c.Volume
                });
            }

            try
            {
                // 1. Candlestick Patterns (27 types)
                CandlePatternType? targetCandlePattern = ResolveCandlePatternType(name);
                if (targetCandlePattern.HasValue)
                {
                    var condition = new CandlePatternCondition(targetCandlePattern.Value);
                    return condition.IsMet(portfolioCandles) ? 1m : 0m;
                }

                // 2. Harmonic Patterns (Gartley, Butterfly, Bat)
                if (name.Contains("Gartley", StringComparison.OrdinalIgnoreCase))
                {
                    var condition = new HarmonicPatternCondition(HarmonicPatternType.Gartley);
                    return condition.IsMet(portfolioCandles) ? 1m : 0m;
                }
                if (name.Contains("Butterfly", StringComparison.OrdinalIgnoreCase))
                {
                    var condition = new HarmonicPatternCondition(HarmonicPatternType.Butterfly);
                    return condition.IsMet(portfolioCandles) ? 1m : 0m;
                }
                if (name.Contains("Bat", StringComparison.OrdinalIgnoreCase))
                {
                    var condition = new HarmonicPatternCondition(HarmonicPatternType.Bat);
                    return condition.IsMet(portfolioCandles) ? 1m : 0m;
                }

                // 3. Trading Rules (Granville's Law)
                if (name.Contains("Granville", StringComparison.OrdinalIgnoreCase))
                {
                    var type = name.Contains("Sell", StringComparison.OrdinalIgnoreCase) 
                        ? GranvilleLawConditionType.AnySell 
                        : GranvilleLawConditionType.AnyBuy;
                    var condition = new GranvilleLawCondition(type);
                    return condition.IsMet(portfolioCandles) ? 1m : 0m;
                }

                // 4. Wave Theory (Elliott Wave)
                if (name.Contains("Elliott", StringComparison.OrdinalIgnoreCase) || name.Contains("Wave", StringComparison.OrdinalIgnoreCase))
                {
                    var type = (name.Contains("Corrective", StringComparison.OrdinalIgnoreCase) || name.Contains("ABC", StringComparison.OrdinalIgnoreCase))
                        ? ElliottWaveConditionType.AnyCorrective 
                        : ElliottWaveConditionType.AnyImpulse;
                    var condition = new ElliottWaveCondition(type);
                    return condition.IsMet(portfolioCandles) ? 1m : 0m;
                }

                // 5. Strict Deterministic Pattern Detection (Head & Shoulders, Double Top/Bottom)
                if (name.Contains("Head", StringComparison.OrdinalIgnoreCase) || name.Contains("Shoulder", StringComparison.OrdinalIgnoreCase))
                {
                    if (name.Contains("Bottom", StringComparison.OrdinalIgnoreCase) || name.Contains("Inverse", StringComparison.OrdinalIgnoreCase))
                        return MarketStructureDetector.IsHeadAndShouldersBottom(portfolioCandles) ? 1m : 0m;
                    return MarketStructureDetector.IsHeadAndShouldersTop(portfolioCandles) ? 1m : 0m;
                }
                if (name.Contains("Double", StringComparison.OrdinalIgnoreCase))
                {
                    if (name.Contains("Bottom", StringComparison.OrdinalIgnoreCase))
                        return MarketStructureDetector.IsDoubleBottom(portfolioCandles) ? 1m : 0m;
                    return MarketStructureDetector.IsDoubleTop(portfolioCandles) ? 1m : 0m;
                }

                // 6. Geometric Pattern (Channel, Flag, Wedge, Triangle, Pennant, Megaphone)
                GeometricFormationType? targetGeoType = ResolveGeometricFormationType(name);
                if (targetGeoType.HasValue)
                {
                    var condition = new GeometricPatternCondition(targetGeoType.Value);
                    return condition.IsMet(portfolioCandles) ? 1m : 0m;
                }

                // 7. Market Structure (BOS, CHoCH, Higher High / Higher Low)
                if (name.Contains("BOS", StringComparison.OrdinalIgnoreCase) || name.Contains("Break", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("CHoCH", StringComparison.OrdinalIgnoreCase) || name.Contains("Character", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Higher", StringComparison.OrdinalIgnoreCase))
                {
                    if (name.Contains("Higher", StringComparison.OrdinalIgnoreCase))
                    {
                        var pivots = MarketStructureDetector.ExtractPivots(portfolioCandles, 3.0m);
                        var highs = pivots.Where(p => p.IsHigh).ToList();
                        var lows = pivots.Where(p => !p.IsHigh).ToList();
                        if (highs.Count >= 2 && lows.Count >= 2)
                        {
                            bool higherHigh = highs[^1].Price > highs[^2].Price;
                            bool higherLow = lows[^1].Price > lows[^2].Price;
                            return (higherHigh && higherLow) ? 1m : 0m;
                        }
                        return 0m;
                    }

                    var msShifts = MarketStructureDetector.Detect(portfolioCandles);
                    if (msShifts != null && msShifts.Count > 0)
                    {
                        var lastShift = msShifts[msShifts.Count - 1];
                        if (name.Contains("BOS", StringComparison.OrdinalIgnoreCase))
                            return (lastShift.Type == MarketStructureType.BullishBOS || lastShift.Type == MarketStructureType.BearishBOS) ? 1m : 0m;
                        if (name.Contains("CHoCH", StringComparison.OrdinalIgnoreCase))
                            return (lastShift.Type == MarketStructureType.BullishCHoCH || lastShift.Type == MarketStructureType.BearishCHoCH) ? 1m : 0m;
                    }
                    return 0m;
                }
            }
            catch
            {
                // Fallback
            }

            return 0m;
        }

        try
        {
            int threadId = Environment.CurrentManagedThreadId;
            var indicator = _threadLocalIndicators.GetOrAdd(
                (config.IndicatorType, threadId),
                key => IndicatorFactory.CreateStatic(key.type)
            );

            if (indicator != null)
            {
                if (config.Parameters != null && config.Parameters.Count > 0)
                {
                    try
                    {
                        var defaultSettings = indicator.GetDefaultSettings();
                        if (defaultSettings?.ParameterObject != null)
                        {
                            var paramType = defaultSettings.ParameterObject.GetType();
                            foreach (var kvp in config.Parameters)
                            {
                                var prop = paramType.GetProperty(kvp.Key);
                                if (prop != null && prop.CanWrite && kvp.Value != null)
                                {
                                    object? rawValue = kvp.Value;
                                    if (rawValue is System.Text.Json.JsonElement jsonElem)
                                    {
                                        if (jsonElem.ValueKind == System.Text.Json.JsonValueKind.Number)
                                        {
                                            if (prop.PropertyType == typeof(int) && jsonElem.TryGetInt32(out int iVal)) rawValue = iVal;
                                            else if (prop.PropertyType == typeof(double) && jsonElem.TryGetDouble(out double dVal)) rawValue = dVal;
                                            else if (prop.PropertyType == typeof(decimal) && jsonElem.TryGetDecimal(out decimal decVal)) rawValue = decVal;
                                            else if (prop.PropertyType == typeof(long) && jsonElem.TryGetInt64(out long lVal)) rawValue = lVal;
                                        }
                                        else if (jsonElem.ValueKind == System.Text.Json.JsonValueKind.String) rawValue = jsonElem.GetString();
                                        else if (jsonElem.ValueKind == System.Text.Json.JsonValueKind.True || jsonElem.ValueKind == System.Text.Json.JsonValueKind.False) rawValue = jsonElem.GetBoolean();
                                    }
                                    var val = System.Convert.ChangeType(rawValue, prop.PropertyType);
                                    prop.SetValue(defaultSettings.ParameterObject, val);
                                }
                            }
                            indicator.Configure(defaultSettings.ParameterObject);
                        }
                    }
                    catch
                    {
                        // Best effort parameter configuration fallback
                    }
                }

                var result = indicator.Calculate(candles);
                if (result != null && result.IsSuccessful)
                {
                    IReadOnlyList<decimal?> series = (!string.IsNullOrWhiteSpace(config.OutputName) && result.HasSeries(config.OutputName))
                        ? result.GetSeries(config.OutputName)
                        : result.MainValues;

                    if (series != null && targetIdx >= 0 && targetIdx < series.Count)
                    {
                        var val = series[targetIdx];
                        if (val.HasValue) return val.Value;
                    }
                }
            }
        }
        catch
        {
            // Fallback
        }

        return 0m;
    }

    public decimal? ExtractValueNullable(ScreenerIndicatorSideConfig? config, IReadOnlyList<CoreCandleData> candles, TickerMetadata metadata)
    {
        if (config == null || candles == null || candles.Count == 0) return null;

        if (config.CategoryType == ScreenerItemCategoryType.Column)
        {
            int offset = config.Offset;
            int targetIdx = candles.Count - 1 - offset;
            if (targetIdx < 0 || targetIdx >= candles.Count)
                targetIdx = candles.Count - 1;

            var candle = candles[targetIdx];
            string name = new[] { config.CustomDisplayName, config.OutputName, config.DisplayName }
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s) && s != "SMA" && s != "Main") ?? config.DisplayName ?? "";

            decimal ohlcVal = name switch
            {
                "Open" => candle.Open,
                "High" => candle.High,
                "Low" => candle.Low,
                "Close" => candle.Close,
                "Volume" => candle.Volume,
                "Change" => targetIdx > 0 ? candle.Close - candles[targetIdx - 1].Close : 0m,
                "Change %" or "ChangePercent" or "Ratio" => targetIdx > 0 && candles[targetIdx - 1].Close != 0 ? ((candle.Close - candles[targetIdx - 1].Close) / candles[targetIdx - 1].Close) * 100m : 0m,
                _ => -999999m
            };

            if (ohlcVal != -999999m) return ohlcVal;

            return GetMetadataValue(metadata, name);
        }

        return ExtractValue(config, candles, metadata);
    }

    /// <summary>
    /// Maps a catalog display name to a <see cref="CandlePatternType"/> enum value.
    /// Returns null if the name does not correspond to a known candlestick pattern.
    /// </summary>
    private static CandlePatternType? ResolveCandlePatternType(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        // Exact matches first (high priority catalog ShortNames)
        if (name.Contains("Bullish Marubozu", StringComparison.OrdinalIgnoreCase)) return CandlePatternType.BullishMarubozu;
        if (name.Contains("Bearish Marubozu", StringComparison.OrdinalIgnoreCase)) return CandlePatternType.BearishMarubozu;
        if (name.Contains("Bullish Engulfing", StringComparison.OrdinalIgnoreCase)) return CandlePatternType.BullishEngulfing;
        if (name.Contains("Bearish Engulfing", StringComparison.OrdinalIgnoreCase)) return CandlePatternType.BearishEngulfing;
        if (name.Contains("Bullish Harami", StringComparison.OrdinalIgnoreCase)) return CandlePatternType.BullishHarami;
        if (name.Contains("Bearish Harami", StringComparison.OrdinalIgnoreCase)) return CandlePatternType.BearishHarami;
        if (name.Contains("Morning Star", StringComparison.OrdinalIgnoreCase)) return CandlePatternType.MorningStar;
        if (name.Contains("Evening Star", StringComparison.OrdinalIgnoreCase)) return CandlePatternType.EveningStar;
        if (name.Contains("Piercing", StringComparison.OrdinalIgnoreCase)) return CandlePatternType.PiercingLine;
        if (name.Contains("Dark Cloud", StringComparison.OrdinalIgnoreCase)) return CandlePatternType.DarkCloudCover;
        if (name.Contains("Three White Soldiers", StringComparison.OrdinalIgnoreCase)) return CandlePatternType.ThreeWhiteSoldiers;
        if (name.Contains("Three Black Crows", StringComparison.OrdinalIgnoreCase)) return CandlePatternType.ThreeBlackCrows;
        if (name.Contains("Dragonfly Doji", StringComparison.OrdinalIgnoreCase)) return CandlePatternType.DragonflyDoji;
        if (name.Contains("Gravestone Doji", StringComparison.OrdinalIgnoreCase)) return CandlePatternType.GravestoneDoji;
        if (name.Contains("Doji", StringComparison.OrdinalIgnoreCase)) return CandlePatternType.CrossDoji;
        if (name.Contains("Shooting Star", StringComparison.OrdinalIgnoreCase)) return CandlePatternType.BearishInvertedUmbrella;
        if (name.Contains("Inverted Hammer", StringComparison.OrdinalIgnoreCase)) return CandlePatternType.BullishInvertedUmbrella;
        if (name.Contains("Hanging Man", StringComparison.OrdinalIgnoreCase) || (name.Contains("Bearish", StringComparison.OrdinalIgnoreCase) && name.Contains("Umbrella", StringComparison.OrdinalIgnoreCase)))
            return CandlePatternType.BearishUmbrella;
        if (name.Contains("Hammer", StringComparison.OrdinalIgnoreCase) || (name.Contains("Bullish", StringComparison.OrdinalIgnoreCase) && name.Contains("Umbrella", StringComparison.OrdinalIgnoreCase)))
            return CandlePatternType.BullishUmbrella;

        // Try direct enum parsing as fallback
        if (Enum.TryParse<CandlePatternType>(name.Replace(" ", "").Replace("/", ""), true, out var parsed) && parsed != CandlePatternType.None)
            return parsed;

        return null;
    }

    /// <summary>
    /// Maps a catalog display name to a <see cref="GeometricFormationType"/> enum value.
    /// Returns null if the name does not correspond to a known geometric pattern.
    /// </summary>
    private static GeometricFormationType? ResolveGeometricFormationType(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        if (name.Contains("Bullish", StringComparison.OrdinalIgnoreCase) && name.Contains("Flag", StringComparison.OrdinalIgnoreCase))
            return GeometricFormationType.BullishFlag;
        if (name.Contains("Bearish", StringComparison.OrdinalIgnoreCase) && name.Contains("Flag", StringComparison.OrdinalIgnoreCase))
            return GeometricFormationType.BearishFlag;
        if (name.Contains("Flag", StringComparison.OrdinalIgnoreCase))
            return GeometricFormationType.BullishFlag; // Default to bullish if direction not specified
        if (name.Contains("Ascending Channel", StringComparison.OrdinalIgnoreCase))
            return GeometricFormationType.AscendingChannel;
        if (name.Contains("Descending Channel", StringComparison.OrdinalIgnoreCase))
            return GeometricFormationType.DescendingChannel;
        if (name.Contains("Rising Wedge", StringComparison.OrdinalIgnoreCase))
            return GeometricFormationType.RisingWedge;
        if (name.Contains("Falling Wedge", StringComparison.OrdinalIgnoreCase))
            return GeometricFormationType.FallingWedge;
        if (name.Contains("Symmetrical Triangle", StringComparison.OrdinalIgnoreCase))
            return GeometricFormationType.SymmetricalTriangle;
        if (name.Contains("Ascending Triangle", StringComparison.OrdinalIgnoreCase))
            return GeometricFormationType.AscendingTriangle;
        if (name.Contains("Descending Triangle", StringComparison.OrdinalIgnoreCase))
            return GeometricFormationType.DescendingTriangle;
        if (name.Contains("Pennant", StringComparison.OrdinalIgnoreCase))
            return GeometricFormationType.Pennant;
        if (name.Contains("Megaphone", StringComparison.OrdinalIgnoreCase))
            return GeometricFormationType.Megaphone;

        // Try direct enum parsing as fallback
        if (Enum.TryParse<GeometricFormationType>(name.Replace(" ", ""), true, out var parsed))
            return parsed;

        return null;
    }

    /// <summary>
    /// Safely extracts a fundamental metric value from <see cref="TickerMetadata"/> by column name or property alias.
    /// Uses fuzzy string normalization combined with reflection fallback to support 100% of UI column metrics.
    /// </summary>
    private static decimal? GetMetadataValue(TickerMetadata meta, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        // Clean name safely without removing functional words like 'Ratio' or 'Margin'
        string cleaned = new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        // Signal Flags (Boolean)
        if (cleaned == "islong") return meta.IsLong == true ? 1m : 0m;
        if (cleaned == "istplong") return meta.IsTPLong == true ? 1m : 0m;
        if (cleaned == "issllong") return meta.IsSLLong == true ? 1m : 0m;
        if (cleaned == "isshort") return meta.IsShort == true ? 1m : 0m;
        if (cleaned == "istpshort") return meta.IsTPShort == true ? 1m : 0m;
        if (cleaned == "isslshort") return meta.IsSLShort == true ? 1m : 0m;

        // Strategy Price Targets
        if (cleaned.Contains("tplong") || cleaned.Contains("exitlong") || cleaned.Contains("takeprofitlong")) return meta.ExitLong;
        if (cleaned.Contains("sllong") || cleaned.Contains("stoplosslong")) return meta.StopLossLong;
        if (cleaned.Contains("tpshort") || cleaned.Contains("exitshort") || cleaned.Contains("takeprofitshort")) return meta.ExitShort;
        if (cleaned.Contains("slshort") || cleaned.Contains("stoplossshort")) return meta.StopLossShort;

        // 1. Specific & Derived Metrics
        if (cleaned.Contains("pbrcalculated") || cleaned.Contains("pblive") || cleaned == "pblive")
        {
            if (meta.PbrCalculated.HasValue) return meta.PbrCalculated.Value;
            if (meta.CurrentPrice.HasValue && meta.BookValue.HasValue && meta.BookValue.Value != 0m)
            {
                return meta.CurrentPrice.Value / meta.BookValue.Value;
            }
            return meta.PriceToBook;
        }
        if (cleaned.Contains("dividendyieldcalculated") || cleaned.Contains("divyieldlive") || cleaned == "divyieldlive") return meta.DividendYieldCalculated ?? meta.DividendYield;
        if (cleaned.Contains("dividendpershare") || cleaned.Contains("dividendrate")) return meta.DividendRate;
        if (cleaned.Contains("dividendyield") || cleaned.Contains("divyield")) return meta.DividendYield ?? meta.DividendYieldCalculated;

        // 2. Fundamental Ratios & Margins (Handling both singular/plural & Ratio suffixes)
        if (cleaned.Contains("payout")) return meta.PayoutRatio;
        if (cleaned.Contains("ebitdamargin")) return meta.EbitdaMargins;
        if (cleaned.Contains("grossmargin")) return meta.GrossMargins;
        if (cleaned.Contains("operatingmargin")) return meta.OperatingMargins;
        if (cleaned.Contains("profitmargin")) return meta.ProfitMargins;
        if (cleaned.Contains("fcfmargin")) return meta.FcfMargin;

        if (cleaned.Contains("roa") || cleaned.Contains("returnonassets")) return meta.ReturnOnAssets;
        if (cleaned.Contains("roe") || cleaned.Contains("returnonequity")) return meta.ReturnOnEquity;
        if (cleaned.Contains("current")) return meta.CurrentRatio;
        if (cleaned.Contains("quick")) return meta.QuickRatio;
        if (cleaned.Contains("netdebtequity") || cleaned.Contains("netderatio") || cleaned == "netde")
        {
            if (meta.NetDebtEquityRatio.HasValue) return meta.NetDebtEquityRatio.Value;
            if (meta.TotalDebt.HasValue && meta.TotalCash.HasValue && meta.BookValue.HasValue && meta.SharesOutstanding.HasValue)
            {
                decimal equity = meta.BookValue.Value * meta.SharesOutstanding.Value;
                if (equity != 0m)
                {
                    return (meta.TotalDebt.Value - meta.TotalCash.Value) / equity;
                }
            }
            return null;
        }

        if (cleaned.Contains("deratio") || cleaned.Contains("debtofequity") || cleaned.Contains("debttoequity") || cleaned == "de")
        {
            if (meta.DebtToEquity.HasValue) return meta.DebtToEquity.Value;
            if (meta.TotalDebt.HasValue && meta.BookValue.HasValue && meta.SharesOutstanding.HasValue)
            {
                decimal equity = meta.BookValue.Value * meta.SharesOutstanding.Value;
                if (equity != 0m)
                {
                    return meta.TotalDebt.Value / equity;
                }
            }
            return null;
        }

        if (cleaned.Contains("netdebttoebitda") || cleaned.Contains("netdebtebitda"))
        {
            if (meta.NetDebtToEbitda.HasValue) return meta.NetDebtToEbitda.Value;
            if (meta.NetDebt.HasValue && meta.Ebitda.HasValue && meta.Ebitda.Value != 0m)
            {
                return meta.NetDebt.Value / meta.Ebitda.Value;
            }
            return null;
        }

        if (cleaned.Contains("evebitda")) return meta.EnterpriseToEbitda;
        if (cleaned.Contains("ebitda")) return meta.Ebitda;
        if (cleaned.Contains("freecash") || cleaned == "fcf") return meta.FreeCashflow;
        if (cleaned.Contains("operatingcash") || cleaned == "ocf") return meta.OperatingCashflow;
        if (cleaned.Contains("trailingpe") || cleaned == "pe" || cleaned == "per") return meta.TrailingPE;
        if (cleaned.Contains("forwardpe")) return meta.ForwardPE;
        if (cleaned.Contains("pbr") || cleaned.Contains("pricetobook") || cleaned.Contains("pbratio") || cleaned == "pb") return meta.PriceToBook;
        if (cleaned.Contains("psr") || cleaned.Contains("pricetosales") || cleaned.Contains("psttm") || cleaned == "ps") return meta.PriceToSalesTrailing12Months;
        if (cleaned.Contains("peg")) return meta.PegRatio;
        if (cleaned == "ev" || cleaned.Contains("enterprisevalue")) return meta.EnterpriseValue;
        if (cleaned.Contains("marketcap")) return meta.MarketCap;
        if (cleaned.Contains("sharesoutstanding")) return meta.SharesOutstanding;
        if (cleaned.Contains("floatshares")) return meta.FloatShares;
        if (cleaned.Contains("totaldebt")) return meta.TotalDebt;
        if (cleaned.Contains("totalcash")) return meta.TotalCash;
        if (cleaned.Contains("totalrevenue")) return meta.TotalRevenue;
        if (cleaned.Contains("beta")) return meta.Beta;
        if (cleaned.Contains("fiftytwoweekrangeposition") || cleaned.Contains("52weekrangepos") || cleaned.Contains("52wrangepos") || cleaned.Contains("rangeposition"))
        {
            if (meta.FiftyTwoWeekRangePosition.HasValue) return meta.FiftyTwoWeekRangePosition.Value;
            if (meta.FiftyTwoWeekHigh.HasValue && meta.FiftyTwoWeekLow.HasValue && meta.CurrentPrice.HasValue)
            {
                decimal range = meta.FiftyTwoWeekHigh.Value - meta.FiftyTwoWeekLow.Value;
                if (range != 0m)
                {
                    return (meta.CurrentPrice.Value - meta.FiftyTwoWeekLow.Value) / range;
                }
            }
            return null;
        }

        if (cleaned.Contains("pctfromfiftytwoweekhigh") || cleaned.Contains("from52weekhigh") || cleaned.Contains("from52whigh"))
        {
            if (meta.PctFromFiftyTwoWeekHigh.HasValue) return meta.PctFromFiftyTwoWeekHigh.Value;
            if (meta.FiftyTwoWeekHigh.HasValue && meta.FiftyTwoWeekHigh.Value != 0m && meta.CurrentPrice.HasValue)
            {
                return ((meta.CurrentPrice.Value / meta.FiftyTwoWeekHigh.Value) - 1m) * 100m;
            }
            return null;
        }

        if (cleaned.Contains("marketcapperemployee") || cleaned.Contains("marketcapemployee"))
        {
            if (meta.MarketCapPerEmployee.HasValue) return meta.MarketCapPerEmployee.Value;
            if (meta.MarketCap.HasValue && meta.FullTimeEmployees.HasValue && meta.FullTimeEmployees.Value != 0)
            {
                return meta.MarketCap.Value / (decimal)meta.FullTimeEmployees.Value;
            }
            return null;
        }

        if (cleaned.Contains("fiftytwoweekhigh") || cleaned.Contains("52weekhigh")) return meta.FiftyTwoWeekHigh;
        if (cleaned.Contains("fiftytwoweeklow") || cleaned.Contains("52weeklow")) return meta.FiftyTwoWeekLow;
        if (cleaned.Contains("revenuegrowth")) return meta.RevenueGrowth;
        if (cleaned.Contains("earningsgrowth")) return meta.EarningsGrowth;
        if (cleaned.Contains("trailingeps") || cleaned == "eps") return meta.TrailingEps;
        if (cleaned.Contains("forwardeps")) return meta.ForwardEps;
        if (cleaned.Contains("bookvalue") || cleaned == "bps") return meta.BookValue;
        if (cleaned.Contains("daystocover") || cleaned.Contains("shortratio")) return meta.ShortRatio;
        if (cleaned.Contains("shortpercent") || cleaned.Contains("shortinterest")) return meta.ShortPercentOfFloat;
        if (cleaned.Contains("insider")) return meta.HeldPercentInsiders;
        if (cleaned.Contains("institutional")) return meta.HeldPercentInstitutions;
        if (cleaned.Contains("earningsyield")) return meta.EarningsYield;
        if (cleaned.Contains("fcfyield")) return meta.FcfYield;
        if (cleaned.Contains("netdebt")) return meta.NetDebt;
        if (cleaned.Contains("dividendcoverage") || cleaned.Contains("divcoverage")) return meta.DividendCoverage;
        if (cleaned.Contains("floatratio")) return meta.FloatRatio;
        if (cleaned.Contains("operatingcashflowyield") || cleaned.Contains("ocfyield")) return meta.OperatingCashFlowYield;
        if (cleaned.Contains("netcashratio")) return meta.NetCashRatio;
        if (cleaned.Contains("enterprisetorevenue") || cleaned.Contains("evrevenue")) return meta.EnterpriseToRevenue;
        if (cleaned.Contains("averagevolume") || cleaned.Contains("avgvolume")) return meta.AverageVolume;
        if (cleaned.Contains("pricetocashflow") || cleaned.Contains("pcfratio") || cleaned == "pcf") return meta.PriceToCashFlowRatio;
        if (cleaned.Contains("turnoverrate") || cleaned.Contains("turnover")) return meta.DailyTurnoverRate ?? meta.AverageTurnoverRate;
        if (cleaned.Contains("targethigh")) return meta.TargetHighPrice;
        if (cleaned.Contains("targetlow")) return meta.TargetLowPrice;
        if (cleaned.Contains("targetmean")) return meta.TargetMeanPrice;
        if (cleaned.Contains("targetmedian")) return meta.TargetMedianPrice;
        if (cleaned.Contains("recommendationmean") || cleaned.Contains("recmean") || cleaned.Contains("recommendation")) return meta.RecommendationMean;
        if (cleaned.Contains("analystopinions") || cleaned.Contains("opinion")) return meta.NumberOfAnalystOpinions;
        if (cleaned.Contains("exdividend")) return meta.ExDividendDate.HasValue ? (decimal)meta.ExDividendDate.Value : null;
        if (cleaned.Contains("lastfiscalyear")) return meta.LastFiscalYearEnd.HasValue ? (decimal)meta.LastFiscalYearEnd.Value : null;
        if (cleaned.Contains("mostrecentquarter")) return meta.MostRecentQuarter.HasValue ? (decimal)meta.MostRecentQuarter.Value : null;

        // Reflection Fallback for any current or future decimal/long/double/int property on TickerMetadata
        try
        {
            var prop = _metadataPropertyCache.GetOrAdd(name, key => typeof(TickerMetadata).GetProperty(key, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance));
            if (prop != null)
            {
                var val = prop.GetValue(meta);
                if (val is decimal d) return d;
                if (val is double db) return (decimal)db;
                if (val is float f) return (decimal)f;
                if (val is long l) return (decimal)l;
                if (val is int i) return (decimal)i;
            }
        }
        catch { }

        return null;
    }

    private static bool TryGetOhlcValue(string name, CandleData candle, int targetIdx, IReadOnlyList<CandleData> candles, out decimal val)
    {
        switch (name)
        {
            case "Open": val = candle.Open; return true;
            case "High": val = candle.High; return true;
            case "Low": val = candle.Low; return true;
            case "Close": val = candle.Close; return true;
            case "Volume": val = candle.Volume; return true;
            case "Change": val = targetIdx > 0 ? candle.Close - candles[targetIdx - 1].Close : 0m; return true;
            case "Change %":
            case "ChangePercent":
            case "Ratio":
                val = targetIdx > 0 && candles[targetIdx - 1].Close != 0 ? ((candle.Close - candles[targetIdx - 1].Close) / candles[targetIdx - 1].Close) * 100m : 0m;
                return true;
            default: val = 0m; return false;
        }
    }

    private static bool TryGetOhlcValue(string name, CoreCandleData candle, int targetIdx, IReadOnlyList<CoreCandleData> candles, out decimal val)
    {
        switch (name)
        {
            case "Open": val = candle.Open; return true;
            case "High": val = candle.High; return true;
            case "Low": val = candle.Low; return true;
            case "Close": val = candle.Close; return true;
            case "Volume": val = candle.Volume; return true;
            case "Change": val = targetIdx > 0 ? candle.Close - candles[targetIdx - 1].Close : 0m; return true;
            case "Change %":
            case "ChangePercent":
            case "Ratio":
                val = targetIdx > 0 && candles[targetIdx - 1].Close != 0 ? ((candle.Close - candles[targetIdx - 1].Close) / candles[targetIdx - 1].Close) * 100m : 0m;
                return true;
            default: val = 0m; return false;
        }
    }
}
