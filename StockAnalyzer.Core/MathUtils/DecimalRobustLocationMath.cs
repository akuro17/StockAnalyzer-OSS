using System;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Trimmed and winsorized means of a window of <see cref="decimal"/> values in pure decimal arithmetic (price-native statistics lose no precision; owner decision DX-2 = A of the
/// DSL indicator and drawing tool expansion). The definitions are those of <see cref="DslRobustStatisticsMath.TrimmedMean"/> and <see cref="DslRobustStatisticsMath.WinsorMean"/> (the DSL
/// kernels, which stay double-only and unchanged): <c>trim = floor(alpha x n)</c> values are cut (or replaced) at each end of the sorted window and <c>n - 2 trim &gt;= 1</c> must remain.
/// <list type="bullet">
/// <item>Trimmed mean = the mean of the sorted values <c>[trim, n - trim)</c>.</item>
/// <item>Winsorized mean = <c>(trim x (lo + hi) + sum of the sorted values [trim, n - trim)) / n</c> with <c>lo = sorted[trim]</c> and <c>hi = sorted[n - 1 - trim]</c>.</item>
/// </list>
/// One deliberate difference: <c>trim</c> is evaluated exactly in decimal, while the DSL kernel evaluates <c>floor(alpha x n)</c> in double, where for example <c>0.29 x 100 = 28.999999999999996</c> cuts 28
/// values instead of 29. The decimal result is the mathematically exact one; the two agree wherever <c>alpha x n</c> is not within double rounding of an integer.
/// </summary>
public static class DecimalRobustLocationMath
{
    /// <summary>The largest <c>alpha</c> accepted: the one of the DSL kernels, so the two accept the same settings.</summary>
    public const decimal MaxAlpha = (decimal)DslRobustStatisticsMath.MaxTrimAlpha;

    /// <summary>The number of values cut at each end: <c>floor(alpha x n)</c>, exact in decimal.</summary>
    public static int TrimCount(decimal alpha, int n) => (int)decimal.Floor(alpha * n);

    /// <summary>Trimmed mean of <paramref name="window"/>; <paramref name="scratch"/> (length at least the window length) is overwritten with the sorted values.</summary>
    public static MathResult<decimal> TrimmedMean(ReadOnlySpan<decimal> window, decimal alpha, Span<decimal> scratch)
        => Compute(window, alpha, scratch, winsorize: false);

    /// <summary>Winsorized mean of <paramref name="window"/>; <paramref name="scratch"/> (length at least the window length) is overwritten with the sorted values.</summary>
    public static MathResult<decimal> WinsorMean(ReadOnlySpan<decimal> window, decimal alpha, Span<decimal> scratch)
        => Compute(window, alpha, scratch, winsorize: true);

    private static MathResult<decimal> Compute(ReadOnlySpan<decimal> window, decimal alpha, Span<decimal> scratch, bool winsorize)
    {
        int n = window.Length;
        if (n < 1 || scratch.Length < n || alpha < 0m || alpha > MaxAlpha)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        int trim = TrimCount(alpha, n);
        int remaining = n - (2 * trim);
        if (remaining < 1)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DomainError);
        }

        Span<decimal> sorted = scratch.Slice(0, n);
        window.CopyTo(sorted);
        sorted.Sort();

        try
        {
            decimal sum = 0m;
            for (int i = trim; i < n - trim; i++)
            {
                sum += sorted[i];
            }

            if (!winsorize)
            {
                return MathResult<decimal>.Ok(sum / remaining);
            }

            sum += trim * (sorted[trim] + sorted[n - 1 - trim]);
            return MathResult<decimal>.Ok(sum / n);
        }
        catch (OverflowException)
        {
            return MathResult<decimal>.Fail(MathErrorCode.ArithmeticOverflow);
        }
    }
}
