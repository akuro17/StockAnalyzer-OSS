using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>The binary operations of a derived series. The member names are the names a client writes (exact, case-sensitive).</summary>
public enum SeriesOperation
{
    Add,
    Subtract,
    Multiply,
    Divide,
    Max,
    Min,
}

/// <summary>A derived series: one value per bar (null = no value) and how many bars a division by zero left without a value.</summary>
/// <param name="Values">Oldest first, exactly the requested length.</param>
/// <param name="DivisionByZeroCount">Bars where both operands had a value and a <see cref="SeriesOperation.Divide"/> found a zero divisor.</param>
public sealed record DerivedSeries(IReadOnlyList<decimal?> Values, int DivisionByZeroCount);

/// <summary>
/// Bar-by-bar binary operations on two series that already share one time axis (index k is the same bar in both). Computed in <c>decimal</c>, so a value
/// keeps the unit and precision of its operands. A bar has no value (null) when either operand has none, when a division finds a zero divisor, or when the
/// result does not fit a <c>decimal</c>; an infinite or NaN value never appears.
/// </summary>
public static class SeriesArithmetic
{
    /// <summary>
    /// Applies <paramref name="operation"/> to the first <paramref name="length"/> bars of <paramref name="left"/> and <paramref name="right"/>.
    /// A series shorter than <paramref name="length"/> has no value at the missing bars; entries beyond it (for example a projection past the last bar) are ignored.
    /// </summary>
    public static DerivedSeries Apply(SeriesOperation operation, IReadOnlyList<decimal?> left, IReadOnlyList<decimal?> right, int length)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        var values = new decimal?[length];
        int divisionByZero = 0;
        for (int k = 0; k < length; k++)
        {
            decimal? l = k < left.Count ? left[k] : null;
            decimal? r = k < right.Count ? right[k] : null;
            values[k] = Apply(operation, l, r, out bool byZero);
            if (byZero)
            {
                divisionByZero++;
            }
        }

        return new DerivedSeries(values, divisionByZero);
    }

    /// <summary>One bar. <paramref name="divisionByZero"/> is true only for a division whose operands both had a value and whose divisor was zero.</summary>
    public static decimal? Apply(SeriesOperation operation, decimal? left, decimal? right, out bool divisionByZero)
    {
        divisionByZero = false;
        if (left is not { } l || right is not { } r)
        {
            return null;
        }

        try
        {
            switch (operation)
            {
                case SeriesOperation.Add:
                    return l + r;
                case SeriesOperation.Subtract:
                    return l - r;
                case SeriesOperation.Multiply:
                    return l * r;
                case SeriesOperation.Divide:
                    if (r == 0m)
                    {
                        divisionByZero = true;
                        return null;
                    }

                    return l / r;
                case SeriesOperation.Max:
                    return Math.Max(l, r);
                case SeriesOperation.Min:
                    return Math.Min(l, r);
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown series operation.");
            }
        }
        catch (OverflowException)
        {
            return null;
        }
    }
}
