using System;
using System.Collections.Concurrent;
using System.Reflection;
using StockAnalyzer.Avalonia.ViewModels.Watchlist;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Settings;

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
        {
            if (item == null || string.IsNullOrEmpty(propertyName)) return null;
            var val = item.GetPropertyValue(propertyName);
            if (val != null) return val;

            var prop = item.GetType().GetProperty(propertyName);
            return prop?.GetValue(item);
        }

        public static bool ContainsTag(string tags, string targetTag)
        {
            if (string.IsNullOrEmpty(tags) || string.IsNullOrEmpty(targetTag)) return false;

            var targetSpan = targetTag.AsSpan().Trim();
            if (targetSpan.IsEmpty) return false;

            var span = tags.AsSpan();
            while (!span.IsEmpty)
            {
                // Find the nearest separator (comma or semicolon)
                int commaIndex = span.IndexOf(',');
                int semicolonIndex = span.IndexOf(';');
                int separatorIndex;
                if (commaIndex >= 0 && semicolonIndex >= 0)
                    separatorIndex = Math.Min(commaIndex, semicolonIndex);
                else if (commaIndex >= 0)
                    separatorIndex = commaIndex;
                else
                    separatorIndex = semicolonIndex;

                ReadOnlySpan<char> tagSpan;
                if (separatorIndex >= 0)
                {
                    tagSpan = span.Slice(0, separatorIndex).Trim();
                    span = span.Slice(separatorIndex + 1);
                }
                else
                {
                    tagSpan = span.Trim();
                    span = ReadOnlySpan<char>.Empty;
                }

                if (tagSpan.Equals(targetSpan, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        public static bool TryGetDecimalValue(object value, out decimal result)
        {
            if (value is decimal d) { result = d; return true; }
            if (value is double dbl) { result = (decimal)dbl; return true; }
            if (value is float f) { result = (decimal)f; return true; }
            if (value is int i) { result = i; return true; }
            if (value is long l) { result = l; return true; }
            if (value is string s && decimal.TryParse(s, out var p)) { result = p; return true; }
            result = 0;
            return false;
        }

        public bool EvaluateSettings(IFilterableSymbol item, FilterSettings settings)
        {
            if (item == null || settings?.Rules == null || settings.Rules.Count == 0)
            {
                return true;
            }

            foreach (var rule in settings.Rules)
            {
                if (!EvaluateRule(item, rule))
                {
                    return false;
                }
            }
            return true;
        }

        public bool EvaluateRule(IFilterableSymbol item, FilterRule rule)
        {
            if (item == null || rule == null) return false;

            if (string.Equals(rule.Field, "Tag", StringComparison.OrdinalIgnoreCase))
            {
                var value = item.Tag ?? string.Empty;
                var filterVal = rule.Value ?? string.Empty;
                if (rule.IsCompareToField)
                {
                    filterVal = GetPropertyValue(item, rule.Value)?.ToString() ?? string.Empty;
                }

                var op = rule.Operator?.Trim();
                if (string.IsNullOrEmpty(op) || string.Equals(op, "*=", StringComparison.OrdinalIgnoreCase) || string.Equals(op, "Contains", StringComparison.OrdinalIgnoreCase) || string.Equals(op, "ContainsTag", StringComparison.OrdinalIgnoreCase))
                {
                    return ContainsTag(value, filterVal);
                }
                if (string.Equals(op, "!*=", StringComparison.OrdinalIgnoreCase) || string.Equals(op, "NotContains", StringComparison.OrdinalIgnoreCase) || string.Equals(op, "!Contains", StringComparison.OrdinalIgnoreCase))
                {
                    return !ContainsTag(value, filterVal);
                }
                if (string.Equals(op, "==", StringComparison.OrdinalIgnoreCase) || string.Equals(op, "=", StringComparison.OrdinalIgnoreCase) || string.Equals(op, "Equals", StringComparison.OrdinalIgnoreCase))
                {
                    return value.AsSpan().Trim().Equals(filterVal.AsSpan().Trim(), StringComparison.OrdinalIgnoreCase);
                }
                if (string.Equals(op, "!=", StringComparison.OrdinalIgnoreCase) || string.Equals(op, "NotEquals", StringComparison.OrdinalIgnoreCase))
                {
                    return !value.AsSpan().Trim().Equals(filterVal.AsSpan().Trim(), StringComparison.OrdinalIgnoreCase);
                }
                return ContainsTag(value, filterVal);
            }
            else
            {
                var val = GetPropertyValue(item, rule.Field);
                if (val != null)
                {
                    if (rule.IsCompareToField)
                    {
                        var targetVal = GetPropertyValue(item, rule.Value);
                        if (targetVal == null) return false;

                        if (TryGetDecimalValue(val, out var valDec) && TryGetDecimalValue(targetVal, out var targetDec))
                        {
                            return EvaluateNumericOp(valDec, targetDec, rule.Operator);
                        }
                        else
                        {
                            var strVal = val.ToString() ?? string.Empty;
                            var targetStr = targetVal.ToString() ?? string.Empty;
                            return EvaluateTextOp(strVal, targetStr, rule.Operator);
                        }
                    }
                    else
                    {
                        if (TryGetDecimalValue(val, out var decVal))
                        {
                            if (decimal.TryParse(rule.Value, out var filterDec))
                            {
                                return EvaluateNumericOp(decVal, filterDec, rule.Operator);
                            }
                        }
                        else
                        {
                            var strVal = val.ToString() ?? string.Empty;
                            var filterStr = rule.Value ?? string.Empty;
                            return EvaluateTextOp(strVal, filterStr, rule.Operator);
                        }
                    }
                }
            }
            return false;
        }

        private static bool EvaluateNumericOp(decimal a, decimal b, string? op)
        {
            var cleanOp = op?.Trim();
            if (string.Equals(cleanOp, "==", StringComparison.OrdinalIgnoreCase) || string.Equals(cleanOp, "=", StringComparison.OrdinalIgnoreCase) || string.Equals(cleanOp, "Equals", StringComparison.OrdinalIgnoreCase))
                return a == b;
            if (string.Equals(cleanOp, "!=", StringComparison.OrdinalIgnoreCase) || string.Equals(cleanOp, "NotEquals", StringComparison.OrdinalIgnoreCase))
                return a != b;
            if (string.Equals(cleanOp, ">", StringComparison.OrdinalIgnoreCase) || string.Equals(cleanOp, "GreaterThan", StringComparison.OrdinalIgnoreCase))
                return a > b;
            if (string.Equals(cleanOp, ">=", StringComparison.OrdinalIgnoreCase) || string.Equals(cleanOp, "GreaterThanOrEqual", StringComparison.OrdinalIgnoreCase))
                return a >= b;
            if (string.Equals(cleanOp, "<", StringComparison.OrdinalIgnoreCase) || string.Equals(cleanOp, "LessThan", StringComparison.OrdinalIgnoreCase))
                return a < b;
            if (string.Equals(cleanOp, "<=", StringComparison.OrdinalIgnoreCase) || string.Equals(cleanOp, "LessThanOrEqual", StringComparison.OrdinalIgnoreCase))
                return a <= b;
            return false;
        }

        private static bool EvaluateTextOp(string strVal, string filterStr, string? op)
        {
            var cleanOp = op?.Trim();
            if (string.IsNullOrEmpty(cleanOp) || string.Equals(cleanOp, "*=", StringComparison.OrdinalIgnoreCase) || string.Equals(cleanOp, "Contains", StringComparison.OrdinalIgnoreCase))
                return strVal.Contains(filterStr, StringComparison.OrdinalIgnoreCase);
            if (string.Equals(cleanOp, "!*=", StringComparison.OrdinalIgnoreCase) || string.Equals(cleanOp, "NotContains", StringComparison.OrdinalIgnoreCase))
                return !strVal.Contains(filterStr, StringComparison.OrdinalIgnoreCase);
            if (string.Equals(cleanOp, "==", StringComparison.OrdinalIgnoreCase) || string.Equals(cleanOp, "=", StringComparison.OrdinalIgnoreCase) || string.Equals(cleanOp, "Equals", StringComparison.OrdinalIgnoreCase))
                return string.Equals(strVal, filterStr, StringComparison.OrdinalIgnoreCase);
            if (string.Equals(cleanOp, "!=", StringComparison.OrdinalIgnoreCase) || string.Equals(cleanOp, "NotEquals", StringComparison.OrdinalIgnoreCase))
                return !string.Equals(strVal, filterStr, StringComparison.OrdinalIgnoreCase);
            return strVal.Contains(filterStr, StringComparison.OrdinalIgnoreCase);
        }
    }
}
