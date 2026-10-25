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
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Utilities;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Extracts indicator, price, column and criteria values for screening conditions.
/// Reuses one indicator instance per type and thread and the candle conversion of the previous call on the thread, to limit the allocation of a scan;
/// the calculation itself still allocates (it is not allocation-free).
/// </summary>
public class ScreenerValueExtractor : IScreenerValueExtractor
{
    private static readonly Lazy<ScreenerValueExtractor> _instance = new(() => new ScreenerValueExtractor());
    public static ScreenerValueExtractor Default => _instance.Value;

    public ScreenerFaultLog Faults { get; } = new();

    /// <summary>
    /// Maximum bars since confirmation for pattern detection in screening (D5 = B'', N = 5).
    /// Default is 5.
    /// </summary>
    public int PatternMaxBarsSinceConfirmation { get; set; } = ChartConstants.DefaultPatternMaxBarsSinceConfirmation;

    private static readonly ConcurrentDictionary<string, PropertyInfo?> _metadataPropertyCache = new(StringComparer.OrdinalIgnoreCase);
    // One indicator instance per (type, managed thread id), kept as long as this extractor lives (the Default singleton: the process). The size is bounded
    // by the distinct thread ids ever seen x the types used; the runtime recycles ids, which keeps the count small (observed, not guaranteed: 150 short-lived
    // threads used 13 ids). Measured 2026-10-16 with 500 bars: about 20 KB per entry; all 167 registered types on 13 ids held about 44 MB. Estimated from
    // that figure (not measured): a scan of 5 indicator types on 8 workers holds about 0.8 MB. A bounded cache, not a leak, so its lifecycle was left
    // as it is (backlog B2).
    private readonly ConcurrentDictionary<(IndicatorType type, int threadId), ICoreIndicator?> _threadLocalIndicators = new();

    public decimal ExtractValue(ScreenerIndicatorSideConfig? config, IReadOnlyList<CoreCandleData> candles)
    {
        return ExtractValue(config, candles, default);
    }

    public decimal ExtractValue(ScreenerIndicatorSideConfig? config, IReadOnlyList<CandleData> candles, TickerMetadata metadata = default)
    {
        return ExtractValueNullable(config, candles, metadata) ?? 0m;
    }

    public decimal ExtractValue(ScreenerIndicatorSideConfig? config, IReadOnlyList<CoreCandleData> candles, TickerMetadata metadata)
    {
        return ExtractValueNullable(config, candles, metadata) ?? 0m;
    }

    public string? ExtractStringValue(ScreenerIndicatorSideConfig? config, TickerMetadata metadata)
        => ScreenerTextFields.GetValue(metadata, ScreenerTextFields.ResolveFieldName(config));

    /// <summary>
    /// Index of the bar a side reads: <c>count - 1 - Offset</c>. False (the value is missing) when the Offset is negative (it would read a future bar)
    /// or reaches before the first bar; the latest bar is never substituted.
    /// </summary>
    private static bool TryResolveBarIndex(int offset, int count, out int barIndex)
    {
        barIndex = count - 1 - offset;
        return count >= 1 && BacktestConditionOffsetRule.IsValid(offset) && barIndex >= 0;
    }

    // The name that selects the metric is the one definition ScreenerIndicatorSideConfig.ResolveLookupName; a side that names nothing reads its DisplayName.
    // A name that is one of the registry's names for a column (member name, short name or description) is resolved to the column's member name, so the
    // metric a Column condition reads does not depend on which of its names the condition stored: the keyword matching of GetMetadataValue read no value
    // for 39 and another metric for 26 of the 79 numeric columns when the description was the stored name.
    private static string ResolveColumnName(ScreenerIndicatorSideConfig config)
        => RegistryMember(config.CustomDisplayName) ?? RegistryMember(config.OutputName) ?? RegistryMember(config.DisplayName)
           ?? config.ResolveLookupName() ?? config.DisplayName ?? "";

    private static readonly Lazy<Dictionary<string, string>> _columnMembersByName = new(BuildColumnMembersByName);

    private static Dictionary<string, string> BuildColumnMembersByName()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in Models.Watchlist.WatchlistColumnRegistry.AllColumns)
        {
            map.TryAdd(column.MemberName, column.MemberName);
            if (!string.IsNullOrWhiteSpace(column.DisplayName)) map.TryAdd(column.DisplayName, column.MemberName);
            if (!string.IsNullOrWhiteSpace(column.Description)) map.TryAdd(column.Description, column.MemberName);
        }

        return map;
    }

    private static string? RegistryMember(string? name)
        => !string.IsNullOrWhiteSpace(name) && _columnMembersByName.Value.TryGetValue(name, out string? member) ? member : null;

    private static readonly CoreCandleData[] ProbeBar = { new(DateTime.MinValue, 0m, 0m, 0m, 0m, 0L) };

    /// <summary>True when the Column names a value read from the bar series (Open, Close, Change, ...) rather than from the fundamentals.</summary>
    private static bool NamesCandleValue(ScreenerIndicatorSideConfig config)
        => TryGetColumnOhlcValue(config, ProbeBar[0], 0, ProbeBar, out _);

    /// <summary>
    /// True when the Column <paramref name="config"/> names a value read from the bar series of its own time frame (Open, High, Low, Close, Volume,
    /// Change, Change %); a fundamental (market cap, ...) is a property of the ticker, not of a bar, so it has no time frame.
    /// </summary>
    internal static bool IsCandleValueColumn(ScreenerIndicatorSideConfig config) => NamesCandleValue(config);

    public decimal? ExtractValueNullable(ScreenerIndicatorSideConfig? config, IReadOnlyList<CandleData> candles, TickerMetadata metadata = default)
        => ExtractValueNullable(config, candles, metadata, (IScreenerSecondaryCandles?)null);

    /// <summary>
    /// <see cref="ExtractValueNullable(ScreenerIndicatorSideConfig?, IReadOnlyList{CandleData}, TickerMetadata)"/> with the bars of the comparison symbols:
    /// an indicator that compares two tickers reads its secondary bars from <paramref name="secondary"/>. Null keeps the original rule (no secondary data,
    /// no value), which is what every caller that does not pass it gets.
    /// </summary>
    public decimal? ExtractValueNullable(ScreenerIndicatorSideConfig? config, IReadOnlyList<CandleData> candles, TickerMetadata metadata, IScreenerSecondaryCandles? secondary)
    {
        if (config == null || candles == null || candles.Count == 0) return null;

        bool hasBar = TryResolveBarIndex(config.Offset, candles.Count, out int targetIdx);

        // Delegate Column fundamental & OHLC metric extraction directly without heavy allocations
        if (config.CategoryType == ScreenerItemCategoryType.Column)
        {
            // A value read from the bar series is missing when its bar does not exist; fundamentals ignore the Offset.
            if (hasBar && TryGetColumnOhlcValue(config, candles[targetIdx], targetIdx, candles, out decimal ohlcVal))
                return ohlcVal;
            if (!hasBar && NamesCandleValue(config))
                return null;

            return GetMetadataValue(metadata, ResolveColumnName(config));
        }

        if (!hasBar) return null;

        // For Indicator/Criteria evaluation, convert to CoreCandleData list
        return ExtractValueNullable(config, ToCoreCandles(candles), metadata, secondary);
    }

    // The conversion of the candle list the previous call on this thread converted. A symbol's conditions are evaluated one after another on one thread
    // (the extraction path has no await), each side asking for the same list, so converting it once per symbol instead of once per side removes most
    // of the allocation of a scan (measured: about 30 % of the bytes of 2,000 symbols x 5 conditions).
    [ThreadStatic] private static IReadOnlyList<CandleData>? _convertedSource;
    [ThreadStatic] private static List<CoreCandleData>? _convertedCandles;

    /// <summary>
    /// The <see cref="CoreCandleData"/> form of <paramref name="candles"/>. The previous conversion on this thread is reused when it is for the very same list object
    /// with the same count and the same first and last bar; anything else is converted again. Limitation: a list changed in place in a MIDDLE bar, keeping its
    /// count and its first and last bar, is not noticed (the candle lists of the Screener are loaded per scan and not edited afterwards). The returned list is
    /// shared: callers must not modify it.
    /// </summary>
    private static List<CoreCandleData> ToCoreCandles(IReadOnlyList<CandleData> candles)
    {
        var cached = _convertedCandles;
        if (cached != null && ReferenceEquals(_convertedSource, candles) && cached.Count == candles.Count
            && IsSameBar(candles[0], cached[0]) && IsSameBar(candles[^1], cached[^1]))
        {
            return cached;
        }

        List<CoreCandleData> coreCandles = new(candles.Count);
        for (int i = 0; i < candles.Count; i++)
        {
            var c = candles[i];
            coreCandles.Add(new CoreCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume));
        }

        _convertedSource = candles;
        _convertedCandles = coreCandles;
        return coreCandles;
    }

    private static bool IsSameBar(CandleData source, CoreCandleData converted)
        => source.Timestamp == converted.Timestamp && source.Open == converted.Open && source.High == converted.High
           && source.Low == converted.Low && source.Close == converted.Close && source.Volume == converted.Volume;

    /// <summary>
    /// The one extraction core. Returns null when the value is missing (no bar at the Offset, an indicator that has no value on that bar, or an error),
    /// so a comparison can treat "no value" as "not met" instead of as 0.
    /// </summary>
    public decimal? ExtractValueNullable(ScreenerIndicatorSideConfig? config, IReadOnlyList<CoreCandleData> candles, TickerMetadata metadata = default)
        => ExtractValueNullable(config, candles, metadata, (IScreenerSecondaryCandles?)null);

    /// <inheritdoc cref="ExtractValueNullable(ScreenerIndicatorSideConfig?, IReadOnlyList{CandleData}, TickerMetadata, IScreenerSecondaryCandles?)"/>
    public decimal? ExtractValueNullable(ScreenerIndicatorSideConfig? config, IReadOnlyList<CoreCandleData> candles, TickerMetadata metadata, IScreenerSecondaryCandles? secondary)
    {
        if (config == null || candles == null || candles.Count == 0) return null;

        bool hasBar = TryResolveBarIndex(config.Offset, candles.Count, out int targetIdx);

        if (config.CategoryType == ScreenerItemCategoryType.Column)
        {
            if (hasBar && TryGetColumnOhlcValue(config, candles[targetIdx], targetIdx, candles, out decimal ohlcVal))
                return ohlcVal;
            if (!hasBar && NamesCandleValue(config))
                return null;

            // Extract fundamental metric from TickerMetadata (the Offset does not apply to it)
            return GetMetadataValue(metadata, ResolveColumnName(config));
        }

        if (!hasBar) return null;

        var candle = candles[targetIdx];

        if (config.IndicatorType == IndicatorType.Price)
        {
            PriceType pt = ResolvePriceType(config);
            decimal? prevClose = targetIdx > 0 ? candles[targetIdx - 1].Close : null;
            decimal? prevHaOpen = null;
            decimal? prevHaClose = null;
            if (targetIdx > 0 && PriceDataHelper.IsHeikinAshi(pt))
            {
                (prevHaOpen, prevHaClose) = HeikinAshiConverter.GetStateAt(candles, targetIdx - 1);
            }

            return PriceDataHelper.ExtractPrice(candle, pt, prevClose, prevHaOpen, prevHaClose);
        }

        if (config.CategoryType == ScreenerItemCategoryType.Criteria)
        {
            string name = config.ResolveCriteriaName() ?? "";

            // The pattern is evaluated on the bars up to the target bar, on the window its detector needs (the history sizing uses the same definition).
            int maxWindow = ScreenerHistoryRequirements.GetCriteriaWindowBars(name);
            int takeCount = Math.Min(targetIdx + 1, maxWindow);
            int startIdx = targetIdx + 1 - takeCount;

            List<CandleData> portfolioCandles = new(takeCount);
            for (int i = startIdx; i <= targetIdx; i++)
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
                    var condition = new HarmonicPatternCondition(
                        HarmonicPatternType.Gartley,
                        maxBarsSinceConfirmation: PatternMaxBarsSinceConfirmation);
                    return condition.IsMet(portfolioCandles) ? 1m : 0m;
                }
                if (name.Contains("Butterfly", StringComparison.OrdinalIgnoreCase))
                {
                    var condition = new HarmonicPatternCondition(
                        HarmonicPatternType.Butterfly,
                        maxBarsSinceConfirmation: PatternMaxBarsSinceConfirmation);
                    return condition.IsMet(portfolioCandles) ? 1m : 0m;
                }
                if (name.Contains("Bat", StringComparison.OrdinalIgnoreCase))
                {
                    var condition = new HarmonicPatternCondition(
                        HarmonicPatternType.Bat,
                        maxBarsSinceConfirmation: PatternMaxBarsSinceConfirmation);
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
                    bool hasTop = name.Contains("Top", StringComparison.OrdinalIgnoreCase);
                    bool hasBottom = name.Contains("Bottom", StringComparison.OrdinalIgnoreCase) || name.Contains("Inverse", StringComparison.OrdinalIgnoreCase);

                    if (hasTop && !hasBottom)
                        return MarketStructureDetector.IsHeadAndShouldersTopConfirmed(portfolioCandles) ? 1m : 0m;
                    if (hasBottom && !hasTop)
                        return MarketStructureDetector.IsHeadAndShouldersBottomConfirmed(portfolioCandles) ? 1m : 0m;

                    // When both or neither side is specified (catalog item "Head and Shoulders" / "Top/Bottom"), evaluate Top || Bottom per D3=A
                    return (MarketStructureDetector.IsHeadAndShouldersTopConfirmed(portfolioCandles) ||
                            MarketStructureDetector.IsHeadAndShouldersBottomConfirmed(portfolioCandles)) ? 1m : 0m;
                }
                if (name.Contains("Double", StringComparison.OrdinalIgnoreCase))
                {
                    bool hasTop = name.Contains("Top", StringComparison.OrdinalIgnoreCase);
                    bool hasBottom = name.Contains("Bottom", StringComparison.OrdinalIgnoreCase);

                    if (hasTop && !hasBottom)
                        return MarketStructureDetector.IsDoubleTopConfirmed(portfolioCandles) ? 1m : 0m;
                    if (hasBottom && !hasTop)
                        return MarketStructureDetector.IsDoubleBottomConfirmed(portfolioCandles) ? 1m : 0m;

                    // When both or neither side is specified (catalog item "Double Top / Bottom"), evaluate Top || Bottom per D3=A
                    return (MarketStructureDetector.IsDoubleTopConfirmed(portfolioCandles) ||
                            MarketStructureDetector.IsDoubleBottomConfirmed(portfolioCandles)) ? 1m : 0m;
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
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                Faults.Record(ScreenerFaultLog.SiteCriteria, ex);
                // A detector that throws gives no value for this bar.
                return null;
            }

            return 0m;
        }

        if (config.TimeFrame != TimeFrame.D1)
        {
            Faults.Warn(ScreenerFaultLog.SiteIndicatorCalculate, $"Unsupported non-D1 TimeFrame '{config.TimeFrame}' in daily screener for '{config.IndicatorType}'. Failing closed.");
            return null;
        }

        if (!BacktestIndicatorEligibility.IsAllowed(config.IndicatorType, config.OutputName))
        {
            Faults.Warn(ScreenerFaultLog.SiteIndicatorCalculate, $"Blocked non-causal indicator or series in screener: {config.IndicatorType} ({config.OutputName}).");
            return null;
        }

        try
        {
            int threadId = Environment.CurrentManagedThreadId;
            var cacheKey = (config.IndicatorType, threadId);
            ICoreIndicator? indicator;
            if (config.Parameters != null && config.Parameters.Count > 0)
            {
                indicator = _threadLocalIndicators.GetOrAdd(cacheKey, key => IndicatorFactory.CreateStatic(key.type));
            }
            else
            {
                // No parameters to apply: start from a fresh instance (class defaults) so the settings a previous condition
                // configured on the cached instance of this type are not inherited.
                indicator = IndicatorFactory.CreateStatic(config.IndicatorType);
                _threadLocalIndicators[cacheKey] = indicator;
            }

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
                    catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                    {
                        Faults.Record(ScreenerFaultLog.SiteIndicatorConfigure, ex);
                        // Best effort parameter configuration fallback
                    }
                }

                // An indicator that compares two tickers reads the comparison symbol's bars from the scan's shared set. Without a set (or a symbol) it is
                // given none: the instance is cached per thread, so a set an earlier scan gave it must not outlive that scan.
                if (indicator is ICrossTickerIndicator crossTicker)
                {
                    crossTicker.SetSecondaryCandles(!string.IsNullOrWhiteSpace(crossTicker.ComparisonSymbol) ? secondary?.Get(crossTicker.ComparisonSymbol) : null);
                }

                var result = indicator.Calculate(candles);
                if (result != null && result.IsSuccessful)
                {
                    if (!IndicatorOutputSeriesResolver.TryResolveSeries(result, config.IndicatorType, config.OutputName, (PriceType?)null, out var series))
                    {
                        Faults.Warn(ScreenerFaultLog.SiteIndicatorCalculate, $"Requested output series '{config.OutputName}' was not found for indicator '{config.IndicatorType}'.");
                        return null;
                    }

                    int latestBarIndex = Math.Min(candles.Count - 1, series.Count - 1);
                    int targetIndex = latestBarIndex - Math.Max(0, config.Offset);
                    if (targetIndex >= 0 && targetIndex < series.Count)
                    {
                        return series[targetIndex];
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            Faults.Record(ScreenerFaultLog.SiteIndicatorCalculate, ex);
            // An indicator that throws gives no value for this bar.
        }

        return null;
    }

    /// <summary>
    /// Maps a catalog display name to a <see cref="CandlePatternType"/> enum value.
    /// Returns null if the name does not correspond to a known candlestick pattern.
    /// </summary>
    internal static CandlePatternType? ResolveCandlePatternType(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        // Exact matches first (high priority catalog ShortNames)
        if (name.Contains("Rising Three Methods", StringComparison.OrdinalIgnoreCase) || name.Contains("RisingThreeMethods", StringComparison.OrdinalIgnoreCase)) return CandlePatternType.RisingThreeMethods;
        if (name.Contains("Falling Three Methods", StringComparison.OrdinalIgnoreCase) || name.Contains("FallingThreeMethods", StringComparison.OrdinalIgnoreCase)) return CandlePatternType.FallingThreeMethods;
        if (name.Contains("Mat Hold", StringComparison.OrdinalIgnoreCase) || name.Contains("MatHold", StringComparison.OrdinalIgnoreCase)) return CandlePatternType.MatHold;
        if (name.Contains("Bullish Side", StringComparison.OrdinalIgnoreCase) || name.Contains("BullishSideBySideWhiteLines", StringComparison.OrdinalIgnoreCase)) return CandlePatternType.BullishSideBySideWhiteLines;
        if (name.Contains("Bearish Side", StringComparison.OrdinalIgnoreCase) || name.Contains("BearishSideBySideWhiteLines", StringComparison.OrdinalIgnoreCase)) return CandlePatternType.BearishSideBySideWhiteLines;
        if (name.Contains("Bullish Three-Line Strike", StringComparison.OrdinalIgnoreCase) || name.Contains("BullishThreeLineStrike", StringComparison.OrdinalIgnoreCase)) return CandlePatternType.BullishThreeLineStrike;
        if (name.Contains("Bearish Three-Line Strike", StringComparison.OrdinalIgnoreCase) || name.Contains("BearishThreeLineStrike", StringComparison.OrdinalIgnoreCase)) return CandlePatternType.BearishThreeLineStrike;

        if (name.Contains("Bullish Abandoned Baby", StringComparison.OrdinalIgnoreCase) || name.Contains("BullishAbandonedBaby", StringComparison.OrdinalIgnoreCase)) return CandlePatternType.BullishAbandonedBaby;
        if (name.Contains("Bearish Abandoned Baby", StringComparison.OrdinalIgnoreCase) || name.Contains("BearishAbandonedBaby", StringComparison.OrdinalIgnoreCase)) return CandlePatternType.BearishAbandonedBaby;
        if (name.Contains("Advance Block", StringComparison.OrdinalIgnoreCase) || name.Contains("AdvanceBlock", StringComparison.OrdinalIgnoreCase)) return CandlePatternType.AdvanceBlock;
        if (name.Contains("Deliberation", StringComparison.OrdinalIgnoreCase)) return CandlePatternType.Deliberation;
        if (name.Contains("Stick Sandwich", StringComparison.OrdinalIgnoreCase) || name.Contains("StickSandwich", StringComparison.OrdinalIgnoreCase)) return CandlePatternType.StickSandwich;
        if (name.Contains("Ladder Bottom", StringComparison.OrdinalIgnoreCase) || name.Contains("LadderBottom", StringComparison.OrdinalIgnoreCase)) return CandlePatternType.LadderBottom;
        if (name.Contains("Homing Pigeon", StringComparison.OrdinalIgnoreCase) || name.Contains("HomingPigeon", StringComparison.OrdinalIgnoreCase)) return CandlePatternType.HomingPigeon;

        if (name.Contains("Bullish Tasuki Gap", StringComparison.OrdinalIgnoreCase) || name.Contains("BullishTasukiGap", StringComparison.OrdinalIgnoreCase)) return CandlePatternType.BullishTasukiGap;
        if (name.Contains("Bearish Tasuki Gap", StringComparison.OrdinalIgnoreCase) || name.Contains("BearishTasukiGap", StringComparison.OrdinalIgnoreCase)) return CandlePatternType.BearishTasukiGap;
        if (name.Contains("Bullish Gap Three Methods", StringComparison.OrdinalIgnoreCase) || name.Contains("BullishGapThreeMethods", StringComparison.OrdinalIgnoreCase)) return CandlePatternType.BullishGapThreeMethods;
        if (name.Contains("Bearish Gap Three Methods", StringComparison.OrdinalIgnoreCase) || name.Contains("BearishGapThreeMethods", StringComparison.OrdinalIgnoreCase)) return CandlePatternType.BearishGapThreeMethods;
        if (name.Contains("Bullish Breakaway", StringComparison.OrdinalIgnoreCase) || name.Contains("BullishBreakaway", StringComparison.OrdinalIgnoreCase)) return CandlePatternType.BullishBreakaway;
        if (name.Contains("Bearish Breakaway", StringComparison.OrdinalIgnoreCase) || name.Contains("BearishBreakaway", StringComparison.OrdinalIgnoreCase)) return CandlePatternType.BearishBreakaway;

        if (name.Contains("Bullish Kicking", StringComparison.OrdinalIgnoreCase) || name.Contains("BullishKicking", StringComparison.OrdinalIgnoreCase)) return CandlePatternType.BullishKicking;
        if (name.Contains("Bearish Kicking", StringComparison.OrdinalIgnoreCase) || name.Contains("BearishKicking", StringComparison.OrdinalIgnoreCase)) return CandlePatternType.BearishKicking;
        if (name.Contains("Concealing Baby Swallow", StringComparison.OrdinalIgnoreCase) || name.Contains("ConcealingBabySwallow", StringComparison.OrdinalIgnoreCase)) return CandlePatternType.ConcealingBabySwallow;
        if (name.Contains("Identical Three Crows", StringComparison.OrdinalIgnoreCase) || name.Contains("IdenticalThreeCrows", StringComparison.OrdinalIgnoreCase)) return CandlePatternType.IdenticalThreeCrows;

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
    internal static GeometricFormationType? ResolveGeometricFormationType(string name)
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

    /// <summary>1 for a raised signal flag, 0 for a flag known to be down, null (missing) for a flag whose state is not known.</summary>
    private static decimal? FlagValue(bool? flag) => flag.HasValue ? (flag.Value ? 1m : 0m) : null;

    /// <summary>
    /// Safely extracts a fundamental metric value from <see cref="TickerMetadata"/> by column name or property alias.
    /// Uses fuzzy string normalization combined with reflection fallback to support 100% of UI column metrics.
    /// </summary>
    private decimal? GetMetadataValue(TickerMetadata meta, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        // Clean name safely without removing functional words like 'Ratio' or 'Margin'
        string cleaned = new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        // Signal Flags (Boolean): an unknown flag (null) is a missing value like any other missing metric, not "no signal" (0).
        if (cleaned == "islong") return FlagValue(meta.IsLong);
        if (cleaned == "istplong") return FlagValue(meta.IsTPLong);
        if (cleaned == "issllong") return FlagValue(meta.IsSLLong);
        if (cleaned == "isshort") return FlagValue(meta.IsShort);
        if (cleaned == "istpshort") return FlagValue(meta.IsTPShort);
        if (cleaned == "isslshort") return FlagValue(meta.IsSLShort);

        // Strategy Price Targets
        if (cleaned.Contains("tplong") || cleaned.Contains("exitlong") || cleaned.Contains("takeprofitlong")) return meta.ExitLong;
        if (cleaned.Contains("sllong") || cleaned.Contains("stoplosslong")) return meta.StopLossLong;
        if (cleaned.Contains("tpshort") || cleaned.Contains("exitshort") || cleaned.Contains("takeprofitshort")) return meta.ExitShort;
        if (cleaned.Contains("slshort") || cleaned.Contains("stoplossshort")) return meta.StopLossShort;

        // 1. Specific & Derived Metrics
        if (cleaned.Contains("pbrcalculated") || cleaned.Contains("pblive") || cleaned == "pblive")
        {
            return meta.PbrCalculated ?? FundamentalsCalculator.ComputePbr(meta.CurrentPrice, meta.BookValue, meta.PriceToBook);
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
            return meta.NetDebtEquityRatio ?? FundamentalsCalculator.ComputeNetDebtEquityRatio(meta.TotalDebt, meta.TotalCash, meta.BookValue, meta.SharesOutstanding);
        }

        if (cleaned.Contains("deratio") || cleaned.Contains("debtofequity") || cleaned.Contains("debttoequity") || cleaned == "de")
        {
            return meta.DebtToEquity ?? FundamentalsCalculator.ComputeDebtToEquityRatio(meta.TotalDebt, meta.BookValue, meta.SharesOutstanding);
        }

        if (cleaned.Contains("netdebttoebitda") || cleaned.Contains("netdebtebitda"))
        {
            return meta.NetDebtToEbitda ?? FundamentalsCalculator.ComputeNetDebtToEbitda(meta.NetDebt, meta.Ebitda, requirePositiveEbitda: false);
        }

        if (cleaned.Contains("evebitda") || cleaned.Contains("enterprisetoebitda")) return meta.EnterpriseToEbitda;
        if (cleaned.Contains("ebitda")) return meta.Ebitda;
        if (cleaned.Contains("freecash") || cleaned == "fcf") return meta.FreeCashflow;
        if ((cleaned.Contains("operatingcash") && !cleaned.Contains("yield")) || cleaned == "ocf") return meta.OperatingCashflow;
        if (cleaned.Contains("trailingpe") || cleaned == "pe" || cleaned == "per") return meta.TrailingPE;
        if (cleaned.Contains("forwardpe")) return meta.ForwardPE;
        if (cleaned.Contains("pbr") || cleaned.Contains("pricetobook") || cleaned.Contains("pbratio") || cleaned == "pb") return meta.PriceToBook;
        if (cleaned.Contains("psr") || cleaned.Contains("pricetosales") || cleaned.Contains("psttm") || cleaned == "ps") return meta.PriceToSalesTrailing12Months;
        if (cleaned.Contains("peg")) return meta.PegRatio;
        if (cleaned == "ev" || cleaned.Contains("enterprisevalue")) return meta.EnterpriseValue;
        if (cleaned.Contains("marketcap") && !cleaned.Contains("employee")) return meta.MarketCap;
        if (cleaned.Contains("sharesoutstanding")) return meta.SharesOutstanding;
        if (cleaned.Contains("floatshares")) return meta.FloatShares;
        if (cleaned.Contains("totaldebt")) return meta.TotalDebt;
        if (cleaned.Contains("totalcash")) return meta.TotalCash;
        if (cleaned.Contains("totalrevenue")) return meta.TotalRevenue;
        if (cleaned.Contains("beta")) return meta.Beta;
        if (cleaned.Contains("fiftytwoweekrangeposition") || cleaned.Contains("52weekrangepos") || cleaned.Contains("52wrangepos") || cleaned.Contains("rangeposition"))
        {
            return meta.FiftyTwoWeekRangePosition ?? FundamentalsCalculator.ComputeFiftyTwoWeekRangePosition(meta.CurrentPrice, meta.FiftyTwoWeekLow, meta.FiftyTwoWeekHigh);
        }

        if (cleaned.Contains("pctfromfiftytwoweekhigh") || cleaned.Contains("from52weekhigh") || cleaned.Contains("from52whigh"))
        {
            return meta.PctFromFiftyTwoWeekHigh ?? FundamentalsCalculator.ComputePctFromFiftyTwoWeekHigh(meta.CurrentPrice, meta.FiftyTwoWeekHigh);
        }

        if (cleaned.Contains("marketcapperemployee") || cleaned.Contains("marketcapemployee"))
        {
            return meta.MarketCapPerEmployee ?? FundamentalsCalculator.ComputeMarketCapPerEmployee(meta.MarketCap, meta.FullTimeEmployees);
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
        if (cleaned.Contains("averagefloatturnover") || cleaned.Contains("avgfloatturnover")) return meta.AverageFloatTurnover;
        if (cleaned.Contains("dailyfloatshareturnover") || cleaned.Contains("floatturnover")) return meta.DailyFloatShareTurnoverRatio;
        if (cleaned.Contains("averageturnoverrate") || cleaned.Contains("avgturnoverrate")) return meta.AverageTurnoverRate;
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
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            Faults.Record(ScreenerFaultLog.SiteMetadataProperty, ex);
        }

        return null;
    }

    /// <summary>
    /// The candle value (Open, High, Low, Close, Volume, Change, Change %) a Column condition names. The registration dialog stores the short name
    /// ("Change %") in <see cref="ScreenerIndicatorSideConfig.OutputName"/> and the catalog description in
    /// <see cref="ScreenerIndicatorSideConfig.CustomDisplayName"/>, while tests and saved templates may carry the short name in either, so every name is tried
    /// in the order CustomDisplayName, OutputName, DisplayName. A name that is not a candle value falls through to the metadata lookup.
    /// </summary>
    private static bool TryGetColumnOhlcValue(ScreenerIndicatorSideConfig config, CandleData candle, int targetIdx, IReadOnlyList<CandleData> candles, out decimal val)
        => TryGetOhlcValue(config.CustomDisplayName, candle, targetIdx, candles, out val)
           || TryGetOhlcValue(config.OutputName, candle, targetIdx, candles, out val)
           || TryGetOhlcValue(config.DisplayName, candle, targetIdx, candles, out val);

    /// <inheritdoc cref="TryGetColumnOhlcValue(ScreenerIndicatorSideConfig, CandleData, int, IReadOnlyList{CandleData}, out decimal)"/>
    private static bool TryGetColumnOhlcValue(ScreenerIndicatorSideConfig config, CoreCandleData candle, int targetIdx, IReadOnlyList<CoreCandleData> candles, out decimal val)
        => TryGetOhlcValue(config.CustomDisplayName, candle, targetIdx, candles, out val)
           || TryGetOhlcValue(config.OutputName, candle, targetIdx, candles, out val)
           || TryGetOhlcValue(config.DisplayName, candle, targetIdx, candles, out val);

    private static bool TryGetOhlcValue(string name, CandleData candle, int targetIdx, IReadOnlyList<CandleData> candles, out decimal val)
    {
        switch (name)
        {
            case "Open": val = candle.Open; return true;
            case "High": val = candle.High; return true;
            case "Low": val = candle.Low; return true;
            case "Close": val = candle.Close; return true;
            case "Volume": val = candle.Volume; return true;
            case "Change": val = PriceChange.FromPreviousClose(candles, targetIdx).Amount; return true;
            case "Change %":
            case "ChangePercent":
            case "Ratio":
                val = PriceChange.FromPreviousClose(candles, targetIdx).Percent;
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
            case "Change": val = PriceChange.FromPreviousClose(candles, targetIdx).Amount; return true;
            case "Change %":
            case "ChangePercent":
            case "Ratio":
                val = PriceChange.FromPreviousClose(candles, targetIdx).Percent;
                return true;
            default: val = 0m; return false;
        }
    }

    private static PriceType ResolvePriceType(ScreenerIndicatorSideConfig config)
    {
        if (Enum.TryParse<PriceType>(config.OutputName, true, out var p1)) return p1;
        if (Enum.TryParse<PriceType>(config.CustomDisplayName, true, out var p2)) return p2;

        foreach (var opt in PriceDataHelper.PriceTypeOptions)
        {
            if (string.Equals(PriceDataHelper.FormatPriceTypeLabel(opt), config.CustomDisplayName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(PriceDataHelper.FormatPriceTypeLabel(opt), config.OutputName, StringComparison.OrdinalIgnoreCase))
            {
                return opt;
            }
        }

        return PriceType.Close;
    }
}
