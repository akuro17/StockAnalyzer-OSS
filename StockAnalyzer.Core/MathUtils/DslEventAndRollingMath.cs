using System;
using System.Buffers;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Pure zero-allocation <see cref="decimal"/> kernels for DSL v9 event/history tracking
/// and O(N) monotonic-deque rolling operations.
/// </summary>
public static partial class DslEventAndRollingMath
{
    /// <summary>Counts non-zero elements in <paramref name="cond"/>.</summary>
    public static int CountNonZero(scoped ReadOnlySpan<decimal> cond)
    {
        int count = 0;
        for (int i = 0; i < cond.Length; i++)
        {
            if (cond[i] != 0m)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Writes sorted 0-based indices where <paramref name="cond"/> is non-zero into <paramref name="output"/>.</summary>
    public static void ArgWhere(scoped ReadOnlySpan<decimal> cond, scoped Span<decimal> output)
    {
        int writeIdx = 0;
        for (int i = 0; i < cond.Length; i++)
        {
            if (cond[i] != 0m)
            {
                output[writeIdx++] = i;
            }
        }
    }

    /// <summary>
    /// Computes bars elapsed since the most recent non-zero <paramref name="cond"/> at or before each bar
    /// (<c>0</c> on trigger bar; normalized <paramref name="fill"/> prior to the first trigger).
    /// </summary>
    public static void BarsSince(scoped ReadOnlySpan<decimal> cond, decimal fill, scoped Span<decimal> output)
    {
        decimal normalizedFill = DslElementaryMath.NormalizeZero(fill);
        int lastTrue = -1;
        for (int t = 0; t < cond.Length; t++)
        {
            if (cond[t] != 0m)
            {
                lastTrue = t;
                output[t] = 0m;
            }
            else
            {
                output[t] = lastTrue >= 0 ? t - lastTrue : normalizedFill;
            }
        }
    }

    /// <summary>
    /// Computes the <paramref name="occurrence"/>-th most recent value of <paramref name="src"/> (0-based)
    /// where <paramref name="cond"/> was non-zero at or before bar <c>t</c>, or <paramref name="fill"/> when fewer matches exist.
    /// </summary>
    public static void ValueWhen(
        scoped ReadOnlySpan<decimal> cond,
        scoped ReadOnlySpan<decimal> src,
        int occurrence,
        decimal fill,
        scoped Span<decimal> output)
    {
        int n = cond.Length;
        if (n == 0)
        {
            return;
        }

        decimal normalizedFill = DslElementaryMath.NormalizeZero(fill);
        int ringSize = occurrence + 1;
        decimal[]? rented = ringSize <= MathBufferLimits.DecimalStackAllocThreshold
            ? null
            : ArrayPool<decimal>.Shared.Rent(ringSize);
        Span<decimal> ring = rented is null
            ? stackalloc decimal[ringSize]
            : rented.AsSpan(0, ringSize);

        try
        {
            int count = 0;
            int head = 0;
            for (int t = 0; t < n; t++)
            {
                if (cond[t] != 0m)
                {
                    ring[head] = DslElementaryMath.NormalizeZero(src[t]);
                    head++;
                    if (head == ringSize)
                    {
                        head = 0;
                    }

                    count++;
                }

                if (count > occurrence)
                {
                    int idx = head - 1 - occurrence;
                    if (idx < 0)
                    {
                        idx += ringSize;
                    }

                    output[t] = ring[idx];
                }
                else
                {
                    output[t] = normalizedFill;
                }
            }
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<decimal>.Shared.Return(rented, clearArray: false);
            }
        }
    }

    /// <summary>
    /// Forward-fills <paramref name="src"/> across bars where <paramref name="validMask"/> is zero,
    /// emitting <paramref name="fill"/> before the first valid bar.
    /// </summary>
    public static void ForwardFill(
        scoped ReadOnlySpan<decimal> src,
        scoped ReadOnlySpan<decimal> validMask,
        decimal fill,
        scoped Span<decimal> output)
    {
        decimal current = DslElementaryMath.NormalizeZero(fill);
        for (int t = 0; t < src.Length; t++)
        {
            if (validMask[t] != 0m)
            {
                current = DslElementaryMath.NormalizeZero(src[t]);
            }

            output[t] = current;
        }
    }

    /// <summary>
    /// Emits <c>1</c> at bar <c>t &gt;= 1</c> when <paramref name="a"/> crosses strictly above (<paramref name="isOver"/>)
    /// or strictly below (<c>!isOver</c>) <paramref name="b"/>, and <c>0</c> elsewhere.
    /// </summary>
    public static void CrossOverOrUnder(
        scoped ReadOnlySpan<decimal> a,
        scoped ReadOnlySpan<decimal> b,
        bool isOver,
        scoped Span<decimal> output)
    {
        int n = a.Length;
        if (n == 0)
        {
            return;
        }

        output[0] = 0m;
        for (int t = 1; t < n; t++)
        {
            bool crossed = isOver
                ? a[t - 1] <= b[t - 1] && a[t] > b[t]
                : a[t - 1] >= b[t - 1] && a[t] < b[t];
            output[t] = crossed ? 1m : 0m;
        }
    }

    /// <summary>
    /// Computes rolling maximum/minimum or bars-since-extremum (<c>0..window-1</c>, most-recent tie-break)
    /// in strict O(N) using a monotonic deque. Fills warmup bars (and full array when <c>n &lt; window</c>) with <paramref name="fill"/>.
    /// </summary>
    public static void RollingExtreme(
        scoped ReadOnlySpan<decimal> a,
        int window,
        decimal fill,
        bool isMax,
        bool returnBarsSince,
        scoped Span<decimal> output)
    {
        int n = a.Length;
        if (n == 0)
        {
            return;
        }

        decimal normalizedFill = DslElementaryMath.NormalizeZero(fill);
        if (n < window)
        {
            output.Fill(normalizedFill);
            return;
        }

        int[]? rentedDeque = n <= MathBufferLimits.StackAllocThreshold
            ? null
            : ArrayPool<int>.Shared.Rent(n);
        Span<int> deque = rentedDeque is null
            ? stackalloc int[n]
            : rentedDeque.AsSpan(0, n);

        try
        {
            int head = 0;
            int tail = 0;
            int warmupEnd = window - 1;
            for (int t = 0; t < n; t++)
            {
                if (head < tail && deque[head] <= t - window)
                {
                    head++;
                }

                decimal current = a[t];
                while (head < tail)
                {
                    decimal backVal = a[deque[tail - 1]];
                    // Equality pops the older index so the front always tracks the most-recent extremum on ties.
                    bool popBack = isMax ? backVal <= current : backVal >= current;
                    if (!popBack)
                    {
                        break;
                    }

                    tail--;
                }

                deque[tail++] = t;

                if (t < warmupEnd)
                {
                    output[t] = normalizedFill;
                }
                else
                {
                    int bestIdx = deque[head];
                    output[t] = returnBarsSince
                        ? t - bestIdx
                        : DslElementaryMath.NormalizeZero(a[bestIdx]);
                }
            }
        }
        finally
        {
            if (rentedDeque is not null)
            {
                ArrayPool<int>.Shared.Return(rentedDeque, clearArray: false);
            }
        }
    }

    /// <summary>
    /// Computes rolling Pearson correlation in O((N - window + 1) * window) over two equal-length <see cref="decimal"/> series
    /// by subtracting each window's leading element in <see cref="decimal"/> before narrowing to <see cref="double"/>,
    /// preserving translation invariance on high-offset low-volatility inputs while applying <see cref="FlatWindowRule.Series"/>.
    /// </summary>
    public static MathErrorCode RollingCorr(
        scoped ReadOnlySpan<decimal> x,
        scoped ReadOnlySpan<decimal> y,
        int window,
        decimal fill,
        scoped Span<decimal> output)
    {
        int n = x.Length;
        decimal normalizedFill = DslElementaryMath.NormalizeZero(fill);
        if (n < window)
        {
            output.Fill(normalizedFill);
            return MathErrorCode.None;
        }

        int warmupEnd = window - 1;
        for (int t = 0; t < warmupEnd; t++)
        {
            output[t] = normalizedFill;
        }

        int totalDoubles = window * 2;
        double[]? rented = totalDoubles <= MathBufferLimits.StackAllocThreshold
            ? null
            : ArrayPool<double>.Shared.Rent(totalDoubles);
        Span<double> work = rented is null
            ? stackalloc double[totalDoubles]
            : rented.AsSpan(0, totalDoubles);

        try
        {
            Span<double> wx = work.Slice(0, window);
            Span<double> wy = work.Slice(window, window);

            for (int t = warmupEnd; t < n; t++)
            {
                int start = t - window + 1;
                FillShiftedWindowToDouble(x.Slice(start, window), wx);
                FillShiftedWindowToDouble(y.Slice(start, window), wy);

                MathErrorCode err = ComputeWindowCorrelation(wx, wy, window, normalizedFill, out decimal corr);
                if (err != MathErrorCode.None)
                {
                    return err;
                }

                output[t] = corr;
            }

            return MathErrorCode.None;
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<double>.Shared.Return(rented, clearArray: false);
            }
        }
    }

    /// <summary>
    /// Computes rolling Pearson correlation in O((N - window + 1) * window) over two equal-length series
    /// using <see cref="WindowMoments.TryCompute"/> with <see cref="FlatWindowRule.Series"/>.
    /// Emits <paramref name="fill"/> during warmup (<c>t &lt; window - 1</c>) and on flat sub-windows.
    /// </summary>
    public static MathErrorCode RollingCorr(
        scoped ReadOnlySpan<double> x,
        scoped ReadOnlySpan<double> y,
        int window,
        decimal fill,
        scoped Span<decimal> output)
    {
        int n = x.Length;
        decimal normalizedFill = DslElementaryMath.NormalizeZero(fill);
        if (n < window)
        {
            output.Fill(normalizedFill);
            return MathErrorCode.None;
        }

        int warmupEnd = window - 1;
        for (int t = 0; t < warmupEnd; t++)
        {
            output[t] = normalizedFill;
        }

        for (int t = warmupEnd; t < n; t++)
        {
            int start = t - window + 1;
            MathErrorCode err = ComputeWindowCorrelation(
                x.Slice(start, window),
                y.Slice(start, window),
                window,
                normalizedFill,
                out decimal corr);
            if (err != MathErrorCode.None)
            {
                return err;
            }

            output[t] = corr;
        }

        return MathErrorCode.None;
    }

    private static void FillShiftedWindowToDouble(scoped ReadOnlySpan<decimal> src, scoped Span<double> dst)
    {
        decimal anchor = src[0];
        try
        {
            for (int i = 0; i < src.Length; i++)
            {
                dst[i] = (double)checked(src[i] - anchor);
            }
        }
        catch (OverflowException)
        {
            for (int i = 0; i < src.Length; i++)
            {
                dst[i] = (double)src[i];
            }
        }
    }

    private static MathErrorCode ComputeWindowCorrelation(
        scoped ReadOnlySpan<double> wx,
        scoped ReadOnlySpan<double> wy,
        int window,
        decimal normalizedFill,
        out decimal corr)
    {
        corr = normalizedFill;

        if (!WindowMoments.TryCompute(wx, FlatWindowRule.Series, out WindowMoments mx) ||
            !WindowMoments.TryCompute(wy, FlatWindowRule.Series, out WindowMoments my))
        {
            return MathErrorCode.NonFiniteResult;
        }

        if (mx.IsFlat || my.IsFlat)
        {
            return MathErrorCode.None;
        }

        double sxy = 0.0;
        double meanX = mx.Mean;
        double meanY = my.Mean;
        for (int i = 0; i < window; i++)
        {
            sxy += (wx[i] - meanX) * (wy[i] - meanY);
        }

        if (!double.IsFinite(sxy))
        {
            return MathErrorCode.NonFiniteResult;
        }

        double denom = Math.Sqrt(mx.M2 * my.M2);
        if (!(denom > 0.0) || !double.IsFinite(denom))
        {
            return MathErrorCode.None;
        }

        double r = Math.Clamp(sxy / denom, -1.0, 1.0);
        MathResult<decimal> narrowed = DslElementaryMath.NarrowDouble(r);
        if (!narrowed.IsOk)
        {
            return narrowed.Error;
        }

        corr = DslElementaryMath.NormalizeZero(narrowed.Value);
        return MathErrorCode.None;
    }
}
