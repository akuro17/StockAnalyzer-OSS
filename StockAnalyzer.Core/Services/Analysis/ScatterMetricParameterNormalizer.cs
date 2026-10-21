using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;

namespace StockAnalyzer.Core.Services.Analysis;

/// <summary>
/// Gives the parameters of a metric that was read from JSON the CLR types they had when the registration screen made them.
/// <c>System.Text.Json</c> reads every <c>Dictionary&lt;string, object&gt;</c> value as a <see cref="JsonElement"/>; left like that, a reloaded
/// metric would show another <see cref="ScatterMetricDefinition.Label"/> (an enum as its number, a bool no longer hidden) and the Screener value
/// extraction could not apply an enum-typed parameter at all (it converts with <c>Convert.ChangeType</c>). The type of each value is taken from
/// the property of the same name on the indicator's default parameter object; a value without such a property is typed by its JSON kind.
/// </summary>
public static class ScatterMetricParameterNormalizer
{
    /// <summary>Replaces every <see cref="JsonElement"/> parameter of <paramref name="definition"/> in place. Other values are left as they are.</summary>
    public static void Normalize(ScatterMetricDefinition definition, IIndicatorFactory indicatorFactory)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(indicatorFactory);

        Dictionary<string, object>? parameters = definition.Source?.Parameters;
        if (parameters is null || parameters.Count == 0 || !parameters.Values.Any(value => value is JsonElement))
        {
            return;
        }

        Type? parameterType = definition.Source!.CategoryType == ScreenerItemCategoryType.Indicator
            ? indicatorFactory.Create(definition.Source.IndicatorType)?.GetDefaultSettings()?.ParameterObject?.GetType()
            : null;

        foreach (string key in parameters.Keys.ToArray())
        {
            if (parameters[key] is JsonElement element)
            {
                Type? propertyType = parameterType?.GetProperty(key, BindingFlags.Public | BindingFlags.Instance)?.PropertyType;
                parameters[key] = ConvertElement(element, propertyType);
            }
        }
    }

    private static object ConvertElement(JsonElement element, Type? target)
    {
        Type? type = target is null ? null : Nullable.GetUnderlyingType(target) ?? target;

        switch (element.ValueKind)
        {
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.String:
                {
                    string text = element.GetString() ?? string.Empty;
                    return type is { IsEnum: true } && Enum.TryParse(type, text, ignoreCase: true, out object? parsed) ? parsed! : text;
                }
            case JsonValueKind.Number:
                return ConvertNumber(element, type);
            default:
                return element;
        }
    }

    private static object ConvertNumber(JsonElement element, Type? type)
    {
        if (type is { IsEnum: true } && element.TryGetInt64(out long enumNumber))
        {
            return Enum.ToObject(type, enumNumber);
        }

        if (type == typeof(decimal) && element.TryGetDecimal(out decimal decimalValue)) return decimalValue;
        if (type == typeof(double) && element.TryGetDouble(out double doubleValue)) return doubleValue;
        if (type == typeof(float) && element.TryGetSingle(out float singleValue)) return singleValue;
        if (type == typeof(int) && element.TryGetInt32(out int intValue)) return intValue;
        if (type == typeof(long) && element.TryGetInt64(out long longValue)) return longValue;
        if (type == typeof(short) && element.TryGetInt16(out short shortValue)) return shortValue;
        if (type == typeof(byte) && element.TryGetByte(out byte byteValue)) return byteValue;
        if (type == typeof(uint) && element.TryGetUInt32(out uint uintValue)) return uintValue;
        if (type == typeof(ulong) && element.TryGetUInt64(out ulong ulongValue)) return ulongValue;

        // No known property type: the narrowest CLR type that holds the JSON number.
        if (element.TryGetInt32(out int anyInt)) return anyInt;
        if (element.TryGetInt64(out long anyLong)) return anyLong;
        return element.TryGetDouble(out double anyDouble) ? anyDouble : element;
    }
}
