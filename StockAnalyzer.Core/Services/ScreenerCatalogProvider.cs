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

            items.Add(new ScreenerCatalogItem
            {
                CategoryType = ScreenerItemCategoryType.Indicator,
                GroupName = groupName,
                IndicatorType = type,
                ShortName = type.ToString(),
                DisplayName = type.GetDescription()
            });
        }

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
        var columnItems = new List<(string Name, string Group, string Desc)>
        {
            // Basic
            ("Symbol", "Basic", "Ticker Symbol"),
            ("Name", "Basic", "Company Full Name"),
            ("Sector", "Basic", "GICS Sector Classification"),
            ("Industry", "Basic", "GICS Industry Classification"),
            ("Region", "Basic", "Geographical Issuer Region"),

            // Price/Volume
            ("Open", "Price/Volume", "Opening Price"),
            ("High", "Price/Volume", "Highest Price"),
            ("Low", "Price/Volume", "Lowest Price"),
            ("Close", "Price/Volume", "Closing / Market Price"),
            ("Volume", "Price/Volume", "Total Daily Volume"),
            ("Change", "Price/Volume", "Daily Price Change"),
            ("Change %", "Price/Volume", "Daily Percentage Change"),

            // Valuation
            ("Trailing P/E", "Valuation", "Price-to-Earnings Ratio (TTM)"),
            ("Forward P/E", "Valuation", "Forward Price-to-Earnings Ratio"),
            ("P/B Ratio", "Valuation", "Price-to-Book Ratio"),
            ("EV", "Valuation", "Enterprise Value"),
            ("EV/EBITDA", "Valuation", "Enterprise Value to EBITDA"),
            ("P/S (TTM)", "Valuation", "Price-to-Sales Ratio (TTM)"),
            ("PEG Ratio", "Valuation", "Price/Earnings-to-Growth Ratio"),

            // Profitability
            ("ROE", "Profitability", "Return on Equity"),
            ("ROA", "Profitability", "Return on Assets"),
            ("Gross Margin", "Profitability", "Gross Profit Margin %"),
            ("Operating Margin", "Profitability", "Operating Income Margin %"),
            ("Profit Margin", "Profitability", "Net Profit Margin %"),
            ("Current Ratio", "Profitability", "Short-term Liquidity Ratio"),
            ("D/E Ratio", "Profitability", "Debt-to-Equity Ratio"),
            ("Dividend Yield", "Profitability", "Annualized Dividend Yield %"),

            // Financial Health
            ("EBITDA", "Financial Health", "Earnings Before Interest, Taxes, Depreciation"),
            ("Free Cash Flow", "Financial Health", "Operating Cash Flow minus CapEx"),
            ("Operating Cash Flow", "Financial Health", "Cash from Business Operations"),
            ("Market Cap", "Financial Health", "Total Equity Market Capitalization"),
            ("Shares Outstanding", "Financial Health", "Total Issued Shares"),
            ("Float Shares", "Financial Health", "Public Trading Float Shares"),
            ("Total Debt", "Financial Health", "Total Outstanding Liabilities"),
            ("Total Revenue", "Financial Health", "Total Sales Revenue (TTM)")
        };

        foreach (var col in columnItems)
        {
            items.Add(new ScreenerCatalogItem
            {
                CategoryType = ScreenerItemCategoryType.Column,
                GroupName = col.Group,
                ColumnMemberName = col.Name,
                ShortName = col.Name,
                DisplayName = col.Desc
            });
        }
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
            ("Elliott Wave Corrective ABC", "Wave Theory", "3-Wave Corrective Wave Formation")
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
