using System;
using System.Collections.Concurrent;
using System.Reflection;
using StockAnalyzer.Avalonia.ViewModels.Watchlist;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services.Watchlist;

namespace StockAnalyzer.Avalonia.Services
{
    public class WatchlistFilterEngine
    {
        private const int MaxPropertyCacheSize = 100;
        private static readonly ConcurrentDictionary<string, PropertyInfo?> _propertyCache = new(StringComparer.OrdinalIgnoreCase);

        public static PropertyInfo? GetCachedPropertyInfo(string? propertyName)
        {
            if (string.IsNullOrEmpty(propertyName)) return null;

            if (_propertyCache.TryGetValue(propertyName, out var cachedProp))
            {
                return cachedProp;
            }

            var prop = typeof(WatchlistItemViewModel).GetProperty(propertyName);
            if (prop != null)
            {
                if (_propertyCache.Count >= MaxPropertyCacheSize)
                {
                    _propertyCache.Clear();
                }
                _propertyCache[propertyName] = prop;
            }

            return prop;
        }

        public static object? GetPropertyValue(IFilterableSymbol item, string? propertyName)
            => FilterRuleEvaluator.GetPropertyValue(item, propertyName);

        /// <summary>True when <paramref name="targetTag"/> is one of the comma- or semicolon-separated tokens of <paramref name="tags"/>; the single algorithm is <see cref="TagMatcher.Contains"/> (Core), shared with the Screener's text conditions.</summary>
        public static bool ContainsTag(string tags, string targetTag) => FilterRuleEvaluator.ContainsTag(tags, targetTag);

        public static bool TryGetDecimalValue(object value, out decimal result)
            => FilterRuleEvaluator.TryGetDecimalValue(value, out result);

        public bool EvaluateSettings(IFilterableSymbol item, FilterSettings settings)
            => FilterRuleEvaluator.Evaluate(item, settings);

        public bool EvaluateRule(IFilterableSymbol item, FilterRule rule)
            => FilterRuleEvaluator.EvaluateRule(item, rule);
    }
}
