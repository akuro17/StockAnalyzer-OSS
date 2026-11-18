using System;
using System.Numerics;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Order statistics of a window: selection of the k-th smallest value, the Hyndman-Fan type 7 quantile, median, interquartile range and median absolute deviation.
/// One generic implementation serves <see cref="decimal"/> (price-native windows: no precision is lost, owner decision U-2 = A) and <see cref="double"/> (dimensionless windows).
/// <para>
/// The functions work in place on a caller-owned buffer (the buffer is permuted, its multiset is unchanged) and allocate nothing.
/// Undefined results are <c>null</c> (empty window, a NaN element, a probability outside [0, 1], a decimal overflow); there is no exception.
/// </para>
/// </summary>
public static class OrderStatisticsMath
{
    /// <summary>
    /// Factor that turns the median absolute deviation of a normal sample into its standard deviation, <c>1 / Phi^-1(0.75)</c>. The same 16-digit value serves the
    /// <see cref="double"/> and <see cref="decimal"/> paths: <see cref="MadToSigmaDecimal"/> is the same literal (a generic <c>decimal.CreateChecked</c> of the double would round it to 15 digits).
    /// </summary>
    public const double MadToSigma = 1.482602218505602;

    /// <summary><see cref="MadToSigma"/> for <see cref="decimal"/> windows (price-native robust statistics); the same 16-digit literal.</summary>
    public const decimal MadToSigmaDecimal = 1.482602218505602m;

    private const double MedianProbability = 0.5;
    private const double LowerQuartileProbability = 0.25;
    private const double UpperQuartileProbability = 0.75;

    /// <summary>
    /// Returns the value that stands at index <paramref name="k"/> (0-based) when <paramref name="data"/> is sorted ascending; the buffer is rearranged so that
    /// every element before <paramref name="k"/> is not greater and every element after it is not smaller. Hoare partition with a median-of-three pivot
    /// (average O(n), worst case O(n^2)). The caller guarantees that no element is NaN and that <c>0 &lt;= k &lt; data.Length</c>.
    /// </summary>
    public static T Select<T>(Span<T> data, int k)
        where T : struct, INumber<T>
    {
        if ((uint)k >= (uint)data.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(k), "k must index an element of the buffer.");
        }

        int lo = 0;
        int hi = data.Length - 1;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) / 2);
            if (data[mid] < data[lo])
            {
                (data[mid], data[lo]) = (data[lo], data[mid]);
            }

            if (data[hi] < data[lo])
            {
                (data[hi], data[lo]) = (data[lo], data[hi]);
            }

            if (data[hi] < data[mid])
            {
                (data[hi], data[mid]) = (data[mid], data[hi]);
            }

            T pivot = data[mid];
            int i = lo;
            int j = hi;
            while (i <= j)
            {
                while (data[i] < pivot)
                {
                    i++;
                }

                while (pivot < data[j])
                {
                    j--;
                }

                if (i <= j)
                {
                    (data[i], data[j]) = (data[j], data[i]);
                    i++;
                    j--;
                }
            }

            if (k <= j)
            {
                hi = j;
            }
            else if (k >= i)
            {
                lo = i;
            }
            else
            {
                return data[k];
            }
        }

        return data[k];
    }

    /// <summary>
    /// Hyndman-Fan type 7 quantile: <c>h = p (n - 1)</c>, <c>k = floor(h)</c>, <c>gamma = h - k</c>, <c>Q = (1 - gamma) x(k) + gamma x(min(k + 1, n - 1))</c> with x(.) the ascending order statistics.
    /// Null for an empty window, a NaN element, or <paramref name="p"/> outside [0, 1]. The buffer is rearranged.
    /// </summary>
    public static T? QuantileType7<T>(Span<T> data, T p)
        where T : struct, INumber<T>, IFloatingPoint<T>
    {
        int n = data.Length;
        if (n == 0 || T.IsNaN(p) || p < T.Zero || p > T.One)
        {
            return null;
        }

        for (int i = 0; i < n; i++)
        {
            if (T.IsNaN(data[i]))
            {
                return null;
            }
        }

        try
        {
            T h = p * T.CreateChecked(n - 1);
            T floor = T.Floor(h);
            int k = int.CreateChecked(floor);
            T gamma = h - floor;

            T lower = Select(data, k);
            if (gamma == T.Zero || k + 1 >= n)
            {
                return lower;
            }

            // After the selection every element right of k is not smaller than x(k): the next order statistic is the minimum of that part.
            T upper = data[k + 1];
            for (int i = k + 2; i < n; i++)
            {
                if (data[i] < upper)
                {
                    upper = data[i];
                }
            }

            return ((T.One - gamma) * lower) + (gamma * upper);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    /// <summary>Median, the type 7 quantile at p = 0.5 (the mean of the two middle values for an even count). Null as for <see cref="QuantileType7{T}"/>.</summary>
    public static T? Median<T>(Span<T> data)
        where T : struct, INumber<T>, IFloatingPoint<T>
        => QuantileType7(data, T.CreateChecked(MedianProbability));

    /// <summary>Interquartile range <c>Q(0.75) - Q(0.25)</c> (type 7). Null as for <see cref="QuantileType7{T}"/>.</summary>
    public static T? InterquartileRange<T>(Span<T> data)
        where T : struct, INumber<T>, IFloatingPoint<T>
    {
        T? upper = QuantileType7(data, T.CreateChecked(UpperQuartileProbability));
        T? lower = QuantileType7(data, T.CreateChecked(LowerQuartileProbability));
        if (upper is not { } u || lower is not { } l)
        {
            return null;
        }

        try
        {
            return u - l;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// Median absolute deviation <c>median(|x_i - median(x)|)</c> (raw, not scaled by <see cref="MadToSigma"/>). <paramref name="scratch"/> must be at least as long as
    /// <paramref name="data"/>; its first <c>data.Length</c> elements are overwritten. Null as for <see cref="QuantileType7{T}"/>.
    /// </summary>
    public static T? MedianAbsoluteDeviation<T>(Span<T> data, Span<T> scratch)
        where T : struct, INumber<T>, IFloatingPoint<T>
    {
        int n = data.Length;
        if (scratch.Length < n)
        {
            throw new ArgumentException("scratch must be at least as long as data.", nameof(scratch));
        }

        if (Median(data) is not { } median)
        {
            return null;
        }

        try
        {
            Span<T> deviations = scratch.Slice(0, n);
            for (int i = 0; i < n; i++)
            {
                deviations[i] = T.Abs(data[i] - median);
            }

            return Median(deviations);
        }
        catch (OverflowException)
        {
            return null;
        }
    }
}
