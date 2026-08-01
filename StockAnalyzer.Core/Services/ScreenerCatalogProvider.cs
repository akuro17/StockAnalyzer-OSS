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

public static class ScreenerGroupNames
{
    public const string Basic = "Basic";
    public const string PriceVolume = "Price/Volume";
    public const string Valuation = "Valuation";
    public const string Profitability = "Profitability";
    public const string FinancialHealth = "Financial Health";
    public const string Performance = "Performance";
    public const string Solvency = "Solvency";
    public const string ManagementGrowth = "Management/Growth";
    public const string ShortOwnership = "Short/Ownership";

    public static int GetGroupSortOrder(string? groupName) => groupName switch
    {
        Basic => 1,
        PriceVolume => 2,
        Valuation => 3,
        Profitability => 4,
        FinancialHealth => 5,
        ManagementGrowth => 6,
        ShortOwnership => 7,
        _ => 8
    };
}

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
                "Basic" => "Basic",
                "PriceVolume" => "Price/Volume",
                "Ratio" => "Profitability",
                "Financial" => "Financial Health",
                "Valuation" => "Valuation",
                _ => "Basic"
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
            ("Head and Shoulders", "Pattern Recognition", "Classical Head & Shoulders Top/Bottom Pattern"),
            ("Double Top / Bottom", "Pattern Recognition", "Double Top or Double Bottom Reversal Pattern"),
            ("Bullish / Bearish Flag", "Pattern Recognition", "Trend Continuation Flag Formation"),

            // Market Structure
            ("Break of Structure (BOS)", "Market Structure", "Bullish or Bearish Market Structure Break"),
            ("Change of Character (CHoCH)", "Market Structure", "Early Reversal Trend Character Shift"),
            ("Higher High / Higher Low", "Market Structure", "Uptrend Market Structure Sequence"),

            // Harmonic
            ("Gartley Pattern", "Harmonic", "Fibonacci Harmonic Gartley 222 Pattern"),
            ("Butterfly Pattern", "Harmonic", "Fibonacci Harmonic Butterfly Extension Pattern"),
            ("Bat Pattern", "Harmonic", "Fibonacci Harmonic Bat Pattern"),

            // Trading Rules
            ("Granville's Law Buy Signal", "Trading Rules", "Granville's Moving Average Buy Signal 1-4"),
            ("Granville's Law Sell Signal", "Trading Rules", "Granville's Moving Average Sell Signal 1-4"),

            // Wave Theory
            ("Elliott Wave Impulse 1-5", "Wave Theory", "5-Wave Bullish/Bearish Impulse Wave"),
            ("Elliott Wave Corrective ABC", "Wave Theory", "3-Wave Corrective Wave Formation"),

            // Candlestick Patterns
            ("Bullish Marubozu", "Candlestick Patterns", "Large Bullish Body with No Shadows"),
            ("Bearish Marubozu", "Candlestick Patterns", "Large Bearish Body with No Shadows"),
            ("Bullish Engulfing", "Candlestick Patterns", "Bullish Candle Fully Engulfing Previous Bearish Body"),
            ("Bearish Engulfing", "Candlestick Patterns", "Bearish Candle Fully Engulfing Previous Bullish Body"),
            ("Bullish Harami", "Candlestick Patterns", "Small Bullish Body Inside Previous Bearish Body"),
            ("Bearish Harami", "Candlestick Patterns", "Small Bearish Body Inside Previous Bullish Body"),
            ("Morning Star", "Candlestick Patterns", "3-Candle Bullish Reversal Pattern"),
            ("Evening Star", "Candlestick Patterns", "3-Candle Bearish Reversal Pattern"),
            ("Hammer / Bullish Umbrella", "Candlestick Patterns", "Small Body at Top with Long Lower Shadow"),
            ("Inverted Hammer", "Candlestick Patterns", "Small Body at Bottom with Long Upper Shadow"),
            ("Hanging Man / Bearish Umbrella", "Candlestick Patterns", "Small Body at Top with Long Lower Shadow in Uptrend"),
            ("Shooting Star", "Candlestick Patterns", "Small Body at Bottom with Long Upper Shadow in Uptrend"),
            ("Doji / Cross Doji", "Candlestick Patterns", "Open and Close Equal with Both Shadows"),
            ("Dragonfly Doji", "Candlestick Patterns", "Open and Close at High with Long Lower Shadow"),
            ("Gravestone Doji", "Candlestick Patterns", "Open and Close at Low with Long Upper Shadow"),
            ("Piercing Line", "Candlestick Patterns", "Bullish Candle Closing Above Midpoint of Previous Bearish Candle"),
            ("Dark Cloud Cover", "Candlestick Patterns", "Bearish Candle Closing Below Midpoint of Previous Bullish Candle"),
            ("Three White Soldiers", "Candlestick Patterns", "Three Consecutive Strong Bullish Candles"),
            ("Three Black Crows", "Candlestick Patterns", "Three Consecutive Strong Bearish Candles")
        };

        foreach (var crit in criteriaItems)
        {
            items.Add(new ScreenerCatalogItem
            {
                CategoryType = ScreenerItemCategoryType.Criteria,
                GroupName = crit.Group,
                ShortName = crit.Name,
                DisplayName = crit.Desc
            });
        }
    }
}
