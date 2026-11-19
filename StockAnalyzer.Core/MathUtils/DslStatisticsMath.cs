using System;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Zero-allocation variadic descriptive statistics operating on <see cref="ReadOnlySpan{T}"/>.
/// Reuses <see cref="OrderStatisticsMath"/> and <see cref="WindowMoments"/> as single sources of truth.
/// </summary>
public static class DslStatisticsMath
{
    private const int StackBufferLimit = 256;

    public static MathResult<decimal> GeometricMean(ReadOnlySpan<decimal> values)
    {
        if (values.Length < 2)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double logSum = 0.0;
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] <= 0m)
            {
                return MathResult<decimal>.Fail(MathErrorCode.DomainError);
            }

            logSum += Math.Log((double)values[i]);
        }

        return DslElementaryMath.NarrowDouble(Math.Exp(logSum / values.Length));
    }

    public static MathResult<decimal> HarmonicMean(ReadOnlySpan<decimal> values)
    {
        if (values.Length < 2)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        double recipSum = 0.0;
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] <= 0m)
            {
                return MathResult<decimal>.Fail(MathErrorCode.DomainError);
            }

            recipSum += 1.0 / (double)values[i];
        }

        if (!(recipSum > 0.0) || !double.IsFinite(recipSum))
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }

        return DslElementaryMath.NarrowDouble(values.Length / recipSum);
    }

    public static MathResult<decimal> SampleSkewness(ReadOnlySpan<decimal> values)
    {
        if (values.Length < 3)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (!TryComputeMoments(values, out WindowMoments moments))
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }

        if (moments.IsFlat)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DivisionByZero);
        }

        return DslElementaryMath.NarrowDouble(moments.SampleSkewness);
    }

    public static MathResult<decimal> SampleExcessKurtosis(ReadOnlySpan<decimal> values)
    {
        if (values.Length < 4)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        if (!TryComputeMoments(values, out WindowMoments moments))
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }

        if (moments.IsFlat)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DivisionByZero);
        }

        return DslElementaryMath.NarrowDouble(moments.SampleExcessKurtosis);
    }

    public static MathResult<decimal> Quantile(decimal p, ReadOnlySpan<decimal> values)
    {
        if (values.Length < 2 || p < 0m || p > 1m)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        Span<decimal> buffer = values.Length <= StackBufferLimit
            ? stackalloc decimal[values.Length]
            : new decimal[values.Length];
        values.CopyTo(buffer);

        decimal? q = OrderStatisticsMath.QuantileType7(buffer, p);
        return q is { } result
            ? MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(result))
            : MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
    }

    public static MathResult<decimal> InterquartileRange(ReadOnlySpan<decimal> values)
    {
        if (values.Length < 2)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        Span<decimal> buffer = values.Length <= StackBufferLimit
            ? stackalloc decimal[values.Length]
            : new decimal[values.Length];
        values.CopyTo(buffer);

        decimal? iqr = OrderStatisticsMath.InterquartileRange(buffer);
        return iqr is { } result
            ? MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(result))
            : MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
    }

    public static MathResult<decimal> MedianAbsoluteDeviation(ReadOnlySpan<decimal> values)
    {
        if (values.Length < 2)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        Span<decimal> buffer = values.Length <= StackBufferLimit
            ? stackalloc decimal[values.Length]
            : new decimal[values.Length];
        Span<decimal> scratch = values.Length <= StackBufferLimit
            ? stackalloc decimal[values.Length]
            : new decimal[values.Length];
        values.CopyTo(buffer);

        decimal? mad = OrderStatisticsMath.MedianAbsoluteDeviation(buffer, scratch);
        return mad is { } result
            ? MathResult<decimal>.Ok(DslElementaryMath.NormalizeZero(result))
            : MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
    }

    /// <summary>
    /// Standardized score <c>(x - mean) / stdev_p(sample)</c> using the <see cref="WindowMoments"/> flat-window guard.
    /// </summary>
    public static MathResult<decimal> ZScore(decimal x, ReadOnlySpan<decimal> sample)
    {
        if (sample.Length < 2)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        try
        {
            decimal sum = 0m;
            for (int i = 0; i < sample.Length; i++)
            {
                sum = checked(sum + sample[i]);
            }

            decimal mean = sum / sample.Length;
            decimal m2 = 0m;
            for (int i = 0; i < sample.Length; i++)
            {
                decimal dev = checked(sample[i] - mean);
                m2 = checked(m2 + checked(dev * dev));
            }

            double popDev = Math.Sqrt((double)m2 / sample.Length);
            double flatThreshold = Math.Max(
                ComparisonSeriesDefaults.FlatWindowDeviationEpsilon,
                WindowMoments.SeriesRelativeFlatEpsilon * Math.Abs((double)mean));
            if (!(popDev >= flatThreshold))
            {
                return MathResult<decimal>.Fail(MathErrorCode.DivisionByZero);
            }

            double z = ((double)x - (double)mean) / popDev;
            return DslElementaryMath.NarrowDouble(z);
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }

    /// <summary>
    /// Overflow-safe Log-Sum-Exp <c>ln(sum(exp(x_i))) = m + ln(sum(exp(x_i - m)))</c>.
    /// </summary>
    public static MathResult<decimal> LogSumExp(ReadOnlySpan<decimal> values)
    {
        if (values.Length < 2)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        decimal maxVal = values[0];
        for (int i = 1; i < values.Length; i++)
        {
            if (values[i] > maxVal)
            {
                maxVal = values[i];
            }
        }

        double dMax = (double)maxVal;
        double sumExp = 0.0;
        for (int i = 0; i < values.Length; i++)
        {
            sumExp += Math.Exp((double)values[i] - dMax);
        }

        return DslElementaryMath.NarrowDouble(dMax + Math.Log(sumExp));
    }

    private static bool TryComputeMoments(ReadOnlySpan<decimal> values, out WindowMoments moments)
    {
        Span<double> buf = values.Length <= StackBufferLimit
            ? stackalloc double[values.Length]
            : new double[values.Length];

        for (int i = 0; i < values.Length; i++)
        {
            buf[i] = (double)values[i];
        }

        return WindowMoments.TryCompute(buf, FlatWindowRule.Series, out moments);
    }
}
