using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Output boundary of the <c>double</c> math engines: maps a computed series to indicator values,
/// where NaN / ±Infinity ("no value at this bar") become <c>null</c> instead of throwing in the decimal cast.
/// </summary>
public static class NullableDecimalConversion
{
    /// <summary>Appends <c>null</c> for every non-finite element of <paramref name="values"/>, otherwise its <c>decimal</c> value.</summary>
    public static void AppendTo(ReadOnlySpan<double> values, List<decimal?> destination)
    {
        for (int i = 0; i < values.Length; i++)
        {
            double value = values[i];
            destination.Add(double.IsFinite(value) ? (decimal)value : null);
        }
    }
}
