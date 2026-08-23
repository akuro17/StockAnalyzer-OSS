using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Watchlist;

namespace StockAnalyzer.Core.Services;


/// <summary>
/// Domain service supplying metadata and catalog definitions for Technical Indicators, Custom Columns, and Screening Criteria.
/// </summary>
public class ScreenerCatalogProvider : IScreenerCatalogProvider
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> _propertyCache = new();
    private readonly Dictionary<IndicatorType, CoreIndicatorSettings> _defaultSettingsCache = new();

    public IReadOnlyList<ScreenerCatalogItem> GetCatalogItems(IIndicatorFactory? indicatorFactory = null)
    {
        var items = new List<ScreenerCatalogItem>();

        var defaultGroups = ScreenerIndicatorGroup.GetDefaultGroups();
        var indicatorGroupMap = new Dictionary<IndicatorType, string>();
        foreach (var group in defaultGroups.Where(g => g.CategoryType == "Indicator"))
        {
            foreach (var type in group.IndicatorTypes)
            {
                indicatorGroupMap[type] = group.Name;
            }
        }

        // 1. Technical Indicators catalog
        var staticDefaults = DefaultCoreIndicatorSettings.GetDefault();
        var knownTypes = indicatorFactory?.GetRegisteredTypes() ?? Enum.GetValues(typeof(IndicatorType)).Cast<IndicatorType>();

        var indicatorItems = new List<ScreenerCatalogItem>();
        foreach (var type in knownTypes)
        {
            var oldDefault = staticDefaults.FirstOrDefault(s => s.TypeEnum == type);
            if (oldDefault != null)
            {
                _defaultSettingsCache[type] = oldDefault;
            }
            else if (indicatorFactory != null)
            {
                var inst = indicatorFactory.Create(type);
                if (inst != null)
                {
                    var settings = inst.GetDefaultSettings();
                    settings.TypeEnum = type;
                    if (settings.ParameterObject == null)
                    {
                        settings.ParameterObject = new CoreSmaParameter { Period = 14 };
                    }
                    _defaultSettingsCache[type] = settings;
                }
            }

            string groupName = indicatorGroupMap.TryGetValue(type, out var gName) ? gName : "Trend";

            indicatorItems.Add(new ScreenerCatalogItem
            {
                CategoryType = ScreenerItemCategoryType.Indicator,
                GroupName = groupName,
                IndicatorType = type,
                ShortName = type.ToString(),
                DisplayName = type.GetDescription()
            });
        }

        var sortedIndicators = indicatorItems
            .OrderBy(i => i.GroupName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.DisplayName ?? i.ShortName, StringComparer.OrdinalIgnoreCase);

        items.AddRange(sortedIndicators);

        // 2. Custom Columns catalog
        AddColumnCatalogItems(items);

        // 3. Screening Criteria catalog
        AddCriteriaCatalogItems(items);

        return items;
    }

    public CoreIndicatorSettings? GetDefaultSettings(IndicatorType type, IIndicatorFactory? indicatorFactory = null)
    {
        if (_defaultSettingsCache.TryGetValue(type, out var cached))
        {
            return cached.Clone();
        }

        var staticDefaults = DefaultCoreIndicatorSettings.GetDefault();
        var oldDefault = staticDefaults.FirstOrDefault(s => s.TypeEnum == type);
        if (oldDefault != null)
        {
            _defaultSettingsCache[type] = oldDefault;
            return oldDefault.Clone();
        }

        if (indicatorFactory != null)
        {
            var inst = indicatorFactory.Create(type);
            if (inst != null)
            {
                var settings = inst.GetDefaultSettings();
                settings.TypeEnum = type;
                if (settings.ParameterObject == null)
                {
                    settings.ParameterObject = new CoreSmaParameter { Period = 14 };
                }
                _defaultSettingsCache[type] = settings;
                return settings.Clone();
            }
        }

        return null;
    }

    public IReadOnlyList<string> GetOutputSeriesNames(IndicatorType type, IIndicatorFactory? indicatorFactory = null)
    {
        var factory = indicatorFactory ?? IndicatorFactory.Default;
        var inst = factory.Create(type);
        if (inst == null)
        {
            return new[] { type.ToString() };
        }

        var list = new List<string>();
        var instType = inst.GetType();
        var props = _propertyCache.GetOrAdd(instType, t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance));

        PropertyInfo? mainMatchingProp = null;

        foreach (var prop in props)
        {
            if (Attribute.IsDefined(prop, typeof(StockAnalyzer.Core.Models.Attributes.IndicatorResultIgnoreAttribute)))
            {
                continue;
            }

            if (prop.Name == nameof(ICoreIndicator.Values))
            {
                continue;
            }

            if (prop.Name == "BullishSignals" || prop.Name == "BearishSignals" || prop.Name == "BuySignals" || prop.Name == "SellSignals" || prop.Name.EndsWith("Signals", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (typeof(IEnumerable<decimal?>).IsAssignableFrom(prop.PropertyType))
            {
                if (inst.Values != null && mainMatchingProp == null)
                {
                    var val = prop.GetValue(inst);
                    if (ReferenceEquals(val, inst.Values))
                    {
                        mainMatchingProp = prop;
                    }
                }

                if (!list.Contains(prop.Name))
                {
                    list.Add(prop.Name);
                }
            }
        }

        if (mainMatchingProp == null && list.Count == 0 && inst.Values != null)
        {
            string primaryName = type.ToString();
            if (!list.Contains(primaryName))
            {
                list.Insert(0, primaryName);
            }
        }

        if (list.Count == 0)
        {
            list.Add(type.ToString());
        }

        return list.Distinct().ToList();
    }

    private static void AddColumnCatalogItems(List<ScreenerCatalogItem> items)
    {
        var tempCols = new List<ScreenerCatalogItem>();

        foreach (var col in WatchlistColumnRegistry.AllColumns)
        {
            if (col.MemberName == "IsChecked" || col.HeaderKey == "Col_Select") continue;

            string shortName = col.DisplayName ?? col.MemberName;
            string group = col.Category switch
            {
                "Basic" => ScreenerGroupNames.Basic,
                "PriceVolume" => ScreenerGroupNames.PriceVolume,
                "Ratio" => ScreenerGroupNames.Profitability,
                "Financial" => ScreenerGroupNames.FinancialHealth,
                "Valuation" => ScreenerGroupNames.Valuation,
                _ => ScreenerGroupNames.Basic
            };
            string desc = col.Description ?? col.MemberName;

            tempCols.Add(new ScreenerCatalogItem
            {
                CategoryType = ScreenerItemCategoryType.Column,
                GroupName = group,
                ColumnMemberName = col.MemberName,
                ShortName = shortName,
                DisplayName = desc
            });
        }

        // Sort cleanly by GroupName order, then ShortName (bold UI label)
        var sorted = tempCols
            .OrderBy(c => ScreenerGroupNames.GetGroupSortOrder(c.GroupName))
            .ThenBy(c => c.ShortName, StringComparer.OrdinalIgnoreCase);

        items.AddRange(sorted);
    }

    private static void AddCriteriaCatalogItems(List<ScreenerCatalogItem> items)
    {
        var criteriaItems = new List<(string Name, string Group, string Desc)>
        {
            // Pattern Recognition
            ("Head and Shoulders", ScreenerGroupNames.PatternRecognition, "Classical Head & Shoulders Top/Bottom Pattern"),
            ("Double Top / Bottom", ScreenerGroupNames.PatternRecognition, "Double Top or Double Bottom Reversal Pattern"),
            ("Bullish / Bearish Flag", ScreenerGroupNames.PatternRecognition, "Trend Continuation Flag Formation"),

            // Market Structure
            ("Break of Structure (BOS)", ScreenerGroupNames.MarketStructure, "Bullish or Bearish Market Structure Break"),
            ("Change of Character (CHoCH)", ScreenerGroupNames.MarketStructure, "Early Reversal Trend Character Shift"),
            ("Higher High / Higher Low", ScreenerGroupNames.MarketStructure, "Uptrend Market Structure Sequence"),

            // Harmonic
            ("Gartley Pattern", ScreenerGroupNames.Harmonic, "Fibonacci Harmonic Gartley 222 Pattern"),
            ("Butterfly Pattern", ScreenerGroupNames.Harmonic, "Fibonacci Harmonic Butterfly Extension Pattern"),
            ("Bat Pattern", ScreenerGroupNames.Harmonic, "Fibonacci Harmonic Bat Pattern"),

            // Trading Rules
            ("Granville's Law Buy Signal", ScreenerGroupNames.TradingRules, "Granville's Moving Average Buy Signal 1-4"),
            ("Granville's Law Sell Signal", ScreenerGroupNames.TradingRules, "Granville's Moving Average Sell Signal 1-4"),

            // Wave Theory
            ("Elliott Wave Impulse 1-5", ScreenerGroupNames.WaveTheory, "5-Wave Bullish/Bearish Impulse Wave"),
            ("Elliott Wave Corrective ABC", ScreenerGroupNames.WaveTheory, "3-Wave Corrective Wave Formation"),

            // Candlestick Patterns
            ("Bullish Marubozu", ScreenerGroupNames.CandlestickPatterns, "Large Bullish Body with No Shadows"),
            ("Bearish Marubozu", ScreenerGroupNames.CandlestickPatterns, "Large Bearish Body with No Shadows"),
            ("Bullish Engulfing", ScreenerGroupNames.CandlestickPatterns, "Bullish Candle Fully Engulfing Previous Bearish Body"),
            ("Bearish Engulfing", ScreenerGroupNames.CandlestickPatterns, "Bearish Candle Fully Engulfing Previous Bullish Body"),
            ("Bullish Harami", ScreenerGroupNames.CandlestickPatterns, "Small Bullish Body Inside Previous Bearish Body"),
            ("Bearish Harami", ScreenerGroupNames.CandlestickPatterns, "Small Bearish Body Inside Previous Bullish Body"),
            ("Morning Star", ScreenerGroupNames.CandlestickPatterns, "3-Candle Bullish Reversal Pattern"),
            ("Evening Star", ScreenerGroupNames.CandlestickPatterns, "3-Candle Bearish Reversal Pattern"),
            ("Hammer / Bullish Umbrella", ScreenerGroupNames.CandlestickPatterns, "Small Body at Top with Long Lower Shadow"),
            ("Inverted Hammer", ScreenerGroupNames.CandlestickPatterns, "Small Body at Bottom with Long Upper Shadow"),
            ("Hanging Man / Bearish Umbrella", ScreenerGroupNames.CandlestickPatterns, "Small Body at Top with Long Lower Shadow in Uptrend"),
            ("Shooting Star", ScreenerGroupNames.CandlestickPatterns, "Small Body at Bottom with Long Upper Shadow in Uptrend"),
            ("Doji / Cross Doji", ScreenerGroupNames.CandlestickPatterns, "Open and Close Equal with Both Shadows"),
            ("Dragonfly Doji", ScreenerGroupNames.CandlestickPatterns, "Open and Close at High with Long Lower Shadow"),
            ("Gravestone Doji", ScreenerGroupNames.CandlestickPatterns, "Open and Close at Low with Long Upper Shadow"),
            ("Piercing Line", ScreenerGroupNames.CandlestickPatterns, "Bullish Candle Closing Above Midpoint of Previous Bearish Candle"),
            ("Dark Cloud Cover", ScreenerGroupNames.CandlestickPatterns, "Bearish Candle Closing Below Midpoint of Previous Bullish Candle"),
            ("Three White Soldiers", ScreenerGroupNames.CandlestickPatterns, "Three Consecutive Strong Bullish Candles"),
            ("Three Black Crows", ScreenerGroupNames.CandlestickPatterns, "Three Consecutive Strong Bearish Candles"),

            // Continuation Candlestick Patterns (Prompt 70-1)
            ("Rising Three Methods", ScreenerGroupNames.CandlestickPatterns, "5-Candle Bullish Continuation Pattern"),
            ("Falling Three Methods", ScreenerGroupNames.CandlestickPatterns, "5-Candle Bearish Continuation Pattern"),
            ("Mat Hold", ScreenerGroupNames.CandlestickPatterns, "5-Candle High-Momentum Bullish Continuation"),
            ("Bullish Side-by-Side White Lines", ScreenerGroupNames.CandlestickPatterns, "3-Candle Bullish Continuation with Gap"),
            ("Bearish Side-by-Side White Lines", ScreenerGroupNames.CandlestickPatterns, "3-Candle Bearish Continuation with Gap"),
            ("Bullish Three-Line Strike", ScreenerGroupNames.CandlestickPatterns, "4-Candle Bullish Continuation Pattern"),
            ("Bearish Three-Line Strike", ScreenerGroupNames.CandlestickPatterns, "4-Candle Bearish Continuation Pattern"),

            // Advanced Reversal Candlestick Patterns (Prompt 70-2)
            ("Bullish Abandoned Baby", ScreenerGroupNames.CandlestickPatterns, "3-Candle Extremely Powerful Bullish Reversal with Gap"),
            ("Bearish Abandoned Baby", ScreenerGroupNames.CandlestickPatterns, "3-Candle Extremely Powerful Bearish Reversal with Gap"),
            ("Advance Block", ScreenerGroupNames.CandlestickPatterns, "3-Candle Bearish Warning Pattern with Diminishing Bodies"),
            ("Deliberation", ScreenerGroupNames.CandlestickPatterns, "3-Candle Bearish Warning Pattern with Stalled Star Candle"),
            ("Stick Sandwich", ScreenerGroupNames.CandlestickPatterns, "3-Candle Bullish Reversal Pattern with Equal Close Support"),
            ("Ladder Bottom", ScreenerGroupNames.CandlestickPatterns, "5-Candle Major Bullish Bottom Reversal Pattern"),
            ("Homing Pigeon", ScreenerGroupNames.CandlestickPatterns, "2-Candle Bullish Reversal Pattern (Bearish Harami)"),

            // Gap Candlestick Patterns (Prompt 70-3)
            ("Bullish Tasuki Gap", ScreenerGroupNames.CandlestickPatterns, "3-Candle Bullish Continuation Pattern with Unfilled Gap"),
            ("Bearish Tasuki Gap", ScreenerGroupNames.CandlestickPatterns, "3-Candle Bearish Continuation Pattern with Unfilled Gap"),
            ("Bullish Gap Three Methods", ScreenerGroupNames.CandlestickPatterns, "3-Candle Bullish Continuation Pattern with Filled Gap"),
            ("Bearish Gap Three Methods", ScreenerGroupNames.CandlestickPatterns, "3-Candle Bearish Continuation Pattern with Filled Gap"),
            ("Bullish Breakaway", ScreenerGroupNames.CandlestickPatterns, "5-Candle Major Bullish Bottom Reversal Pattern with Gap"),
            ("Bearish Breakaway", ScreenerGroupNames.CandlestickPatterns, "5-Candle Major Bearish Top Reversal Pattern with Gap"),

            // Exotic & Minor Candlestick Patterns (Prompt 70-4)
            ("Bullish Kicking", ScreenerGroupNames.CandlestickPatterns, "2-Candle Strong Bullish Reversal with Gap Up Marubozu"),
            ("Bearish Kicking", ScreenerGroupNames.CandlestickPatterns, "2-Candle Strong Bearish Reversal with Gap Down Marubozu"),
            ("Concealing Baby Swallow", ScreenerGroupNames.CandlestickPatterns, "4-Candle Rare Bullish Bottom Reversal with Engulfing"),
            ("Identical Three Crows", ScreenerGroupNames.CandlestickPatterns, "3-Candle Relentless Bearish Panic Selling Reversal")
        };

        var sortedCriteria = criteriaItems
            .Select(crit => new ScreenerCatalogItem
            {
                CategoryType = ScreenerItemCategoryType.Criteria,
                GroupName = crit.Group,
                ShortName = crit.Name,
                DisplayName = crit.Desc
            })
            .OrderBy(c => ScreenerGroupNames.GetGroupSortOrder(c.GroupName))
            .ThenBy(c => c.ShortName, StringComparer.OrdinalIgnoreCase);

        items.AddRange(sortedCriteria);
    }
}
