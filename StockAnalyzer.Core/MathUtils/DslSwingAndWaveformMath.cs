using System;
using System.Buffers;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Pure zero-allocation kernels for DSL v9 causal swing detection (<c>pivot_high_causal</c>,
/// <c>pivot_low_causal</c>, <c>zigzag_causal</c>), geometric waveform simplification
/// (<c>rdp_simplify</c>, <c>pip_extract</c>), DTW bridge, and CUSUM change-point filtering.
/// </summary>
public static partial class DslSwingAndWaveformMath
{
    private const decimal ExactCollinearityMaxMagnitude = 1e24m;
    private const decimal ZigzagCrossMultiplyLimit = 1e14m;
    private const decimal ExactCrossOverflowBound = 1e27m;

    /// <summary>Maximum causal pivot half-window derived from <see cref="DslArrayArena.MaxArrayLength"/> (<c>(Max - 1) / 2</c>).</summary>
    internal const int MaxCausalPivotHalfWindow = (DslArrayArena.MaxArrayLength - 1) / 2;

    /// <summary>
    /// Detects strictly causal pivot highs (<paramref name="isHigh"/> = true) or pivot lows
    /// with left non-strict (&gt;= / &lt;=) and right strict (&gt; / &lt;) comparison (D-04=B),
    /// confirming the rightmost bar of a flat plateau once at <c>t = p + right</c> in O(N).
    /// </summary>
    public static void PivotCausal(
        scoped ReadOnlySpan<decimal> series,
        int left,
        int right,
        decimal fill,
        bool isHigh,
        scoped Span<decimal> output)
    {
        int n = series.Length;
        decimal normalizedFill = DslElementaryMath.NormalizeZero(fill);
        output.Fill(normalizedFill);

        int winSize = left + right + 1;
        if (n < winSize)
        {
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
            int warmupEnd = winSize - 1;

            for (int t = 0; t < n; t++)
            {
                if (head < tail && deque[head] <= t - winSize)
                {
                    head++;
                }

                decimal current = series[t];
                while (head < tail)
                {
                    decimal backVal = series[deque[tail - 1]];
                    // Popping equal values keeps only the rightmost plateau index at the deque front,
                    // guaranteeing left >=/<= (non-strict) and right >/< (strict) in a single O(N) pass.
                    bool popBack = isHigh ? backVal <= current : backVal >= current;
                    if (!popBack)
                    {
                        break;
                    }

                    tail--;
                }

                deque[tail++] = t;

                if (t >= warmupEnd)
                {
                    int candidateIdx = t - right;
                    if (deque[head] == candidateIdx)
                    {
                        output[t] = DslElementaryMath.NormalizeZero(series[candidateIdx]);
                    }
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
    /// Validates ZigZag input invariants upfront: <c>high[t] &gt;= low[t]</c>, positive lows in percent mode (<c>mode == 0</c>),
    /// and non-negative ATR values in ATR mode (<c>mode == 1</c>).
    /// </summary>
    public static bool ValidateZigzagInputs(
        scoped ReadOnlySpan<decimal> high,
        scoped ReadOnlySpan<decimal> low,
        int mode,
        scoped ReadOnlySpan<decimal> atrSeries)
    {
        for (int t = 0; t < high.Length; t++)
        {
            if (high[t] < low[t])
            {
                return false;
            }

            if (mode == 0 && low[t] <= 0m)
            {
                return false;
            }

            if (mode == 1 && atrSeries[t] < 0m)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Executes strictly causal ZigZag pivot detection over pure <see cref="decimal"/> OHLC arrays (D-08:
    /// reversals require a strictly later bar <c>extIdx &lt; t</c>, including during <c>dir == 0</c> seeding).
    /// </summary>
    public static MathErrorCode ZigzagCausal(
        scoped ReadOnlySpan<decimal> high,
        scoped ReadOnlySpan<decimal> low,
        decimal threshold,
        int mode,
        int outputKind,
        scoped ReadOnlySpan<decimal> atrSeries,
        decimal fill,
        scoped Span<decimal> output)
    {
        int n = high.Length;
        output.Fill(DslElementaryMath.NormalizeZero(fill));
        if (n <= 1)
        {
            return MathErrorCode.None;
        }

        int dir = 0;
        decimal initHigh = high[0];
        decimal initLow = low[0];
        int initHighIdx = 0;
        int initLowIdx = 0;
        decimal extPrice = 0m;
        int extIdx = 0;

        try
        {
            for (int t = 1; t < n; t++)
            {
                decimal h = high[t];
                decimal l = low[t];

                if (dir == 0)
                {
                    if (h > initHigh)
                    {
                        initHigh = h;
                        initHighIdx = t;
                    }

                    if (l < initLow)
                    {
                        initLow = l;
                        initLowIdx = t;
                    }

                    decimal reqUp = 0m;
                    decimal moveUp = 0m;
                    bool canUp = initLowIdx < t &&
                                 TryGetReversalDistance(initLow, threshold, mode, atrSeries, t, out reqUp) &&
                                 reqUp > 0m &&
                                 (moveUp = checked(h - initLow)) >= reqUp;

                    decimal reqDown = 0m;
                    decimal moveDown = 0m;
                    bool canDown = initHighIdx < t &&
                                   TryGetReversalDistance(initHigh, threshold, mode, atrSeries, t, out reqDown) &&
                                   reqDown > 0m &&
                                   (moveDown = checked(initHigh - l)) >= reqDown;

                    if (!canUp && !canDown)
                    {
                        continue;
                    }

                    bool chooseUp = canUp && (!canDown || ShouldChooseUpOnSimultaneousReversal(mode, moveUp, reqUp, initLow, moveDown, reqDown, initHigh));
                    if (chooseUp)
                    {
                        EmitZigzag(output, t, initLow, -1m, initLowIdx, outputKind);
                        dir = 1;
                        extPrice = h;
                        extIdx = t;
                    }
                    else
                    {
                        EmitZigzag(output, t, initHigh, 1m, initHighIdx, outputKind);
                        dir = -1;
                        extPrice = l;
                        extIdx = t;
                    }
                }
                else if (dir == 1)
                {
                    if (h > extPrice)
                    {
                        extPrice = h;
                        extIdx = t;
                    }

                    if (extIdx < t &&
                        TryGetReversalDistance(extPrice, threshold, mode, atrSeries, t, out decimal reqDown) &&
                        reqDown > 0m &&
                        checked(extPrice - l) >= reqDown)
                    {
                        EmitZigzag(output, t, extPrice, 1m, extIdx, outputKind);
                        dir = -1;
                        extPrice = l;
                        extIdx = t;
                    }
                }
                else
                {
                    if (l < extPrice)
                    {
                        extPrice = l;
                        extIdx = t;
                    }

                    if (extIdx < t &&
                        TryGetReversalDistance(extPrice, threshold, mode, atrSeries, t, out decimal reqUp) &&
                        reqUp > 0m &&
                        checked(h - extPrice) >= reqUp)
                    {
                        EmitZigzag(output, t, extPrice, -1m, extIdx, outputKind);
                        dir = 1;
                        extPrice = h;
                        extIdx = t;
                    }
                }
            }

            return MathErrorCode.None;
        }
        catch (OverflowException)
        {
            return MathErrorCode.ArithmeticOverflow;
        }
    }

    /// <summary>
    /// Non-recursive Ramer-Douglas-Peucker simplification returning sorted 0-based kept vertex indices
    /// in <paramref name="indicesOut"/> and the count in <paramref name="keptCount"/>.
    /// Uses a zero-exception exact <see cref="decimal"/> cross-product check when <c>max|series| &lt;= 1e24</c> (D-10).
    /// </summary>
    public static MathErrorCode RdpSimplify(
        scoped ReadOnlySpan<decimal> series,
        decimal epsilon,
        scoped Span<int> indicesOut,
        out int keptCount,
        ref DslExecutionBudget budget)
        => RdpSimplify(series, epsilon, metric: 0, indicesOut, out keptCount, ref budget);

    /// <summary>
    /// Non-recursive Ramer-Douglas-Peucker simplification supporting 2D Euclidean perpendicular distance (<paramref name="metric"/> = 0)
    /// and pure <see cref="decimal"/> (D1) price-unit Vertical Distance (<paramref name="metric"/> = 1).
    /// </summary>
    public static MathErrorCode RdpSimplify(
        scoped ReadOnlySpan<decimal> series,
        decimal epsilon,
        int metric,
        scoped Span<int> indicesOut,
        out int keptCount,
        ref DslExecutionBudget budget)
    {
        int n = series.Length;
        if (n == 0)
        {
            keptCount = 0;
            return MathErrorCode.None;
        }

        if (n == 1)
        {
            indicesOut[0] = 0;
            keptCount = 1;
            return MathErrorCode.None;
        }

        if (n == 2)
        {
            indicesOut[0] = 0;
            indicesOut[1] = 1;
            keptCount = 2;
            return MathErrorCode.None;
        }

        decimal maxAbs = 0m;
        for (int i = 0; i < n; i++)
        {
            decimal absVal = Math.Abs(series[i]);
            if (absVal > maxAbs)
            {
                maxAbs = absVal;
            }
        }

        bool verticalDistance = metric == 1;
        decimal exactCrossLimit = verticalDistance ? (ExactCrossOverflowBound / (2m * n)) : ExactCollinearityMaxMagnitude;
        bool useExactDecimal = maxAbs <= exactCrossLimit;
        double epsDouble = (double)epsilon;

        double[]? rentedDSeries = null;
        byte[]? rentedKeep = null;
        int[]? rentedStack = null;
        int stackCap = n * 2;

        Span<double> dSeries = useExactDecimal
            ? Span<double>.Empty
            : (n <= MathBufferLimits.StackAllocThreshold ? stackalloc double[n] : default);
        Span<byte> keep = n <= MathBufferLimits.StackAllocThreshold ? stackalloc byte[n] : default;
        Span<int> stack = stackCap <= MathBufferLimits.StackAllocThreshold ? stackalloc int[stackCap] : default;

        try
        {
            if (!useExactDecimal)
            {
                if (n > MathBufferLimits.StackAllocThreshold)
                {
                    rentedDSeries = ArrayPool<double>.Shared.Rent(n);
                    dSeries = rentedDSeries.AsSpan(0, n);
                }

                for (int i = 0; i < n; i++)
                {
                    dSeries[i] = (double)series[i];
                }
            }

            if (n > MathBufferLimits.StackAllocThreshold)
            {
                rentedKeep = ArrayPool<byte>.Shared.Rent(n);
                keep = rentedKeep.AsSpan(0, n);
            }

            if (stackCap > MathBufferLimits.StackAllocThreshold)
            {
                rentedStack = ArrayPool<int>.Shared.Rent(stackCap);
                stack = rentedStack.AsSpan(0, stackCap);
            }

            keep.Clear();
            keep[0] = 1;
            keep[n - 1] = 1;

            int sp = 0;
            stack[sp++] = 0;
            stack[sp++] = n - 1;

            while (sp > 0)
            {
                int b = stack[--sp];
                int a = stack[--sp];
                int innerCount = b - a - 1;
                if (innerCount <= 0)
                {
                    continue;
                }

                int segCost = Math.Max(1, innerCount / DslExecutionBudget.CheapOperationsPerUnit);
                if (!budget.TryConsume(segCost))
                {
                    keptCount = 0;
                    return MathErrorCode.BudgetExceeded;
                }

                int bestIdx = -1;
                bool exceedsThreshold = false;

                if (verticalDistance && useExactDecimal)
                {
                    int span = b - a;
                    decimal yA = series[a];
                    decimal segY = series[b] - yA;
                    decimal maxCross = -1m;
                    for (int i = a + 1; i < b; i++)
                    {
                        decimal cross = Math.Abs(((decimal)span * (series[i] - yA)) - ((decimal)(i - a) * segY));
                        if (cross > maxCross)
                        {
                            maxCross = cross;
                            bestIdx = i;
                        }
                    }

                    if (bestIdx >= 0)
                    {
                        exceedsThreshold = epsilon <= (ExactCrossOverflowBound / span)
                            ? maxCross > (epsilon * span)
                            : (maxCross / span) > epsilon;
                    }
                }
                else if (verticalDistance)
                {
                    int span = b - a;
                    double baseY = dSeries[a];
                    double slope = (dSeries[b] - baseY) / span;
                    double maxDist = -1.0;
                    for (int i = a + 1; i < b; i++)
                    {
                        double dist = Math.Abs(dSeries[i] - (baseY + ((i - a) * slope)));
                        if (!double.IsFinite(dist))
                        {
                            keptCount = 0;
                            return MathErrorCode.NonFiniteResult;
                        }

                        if (dist > maxDist)
                        {
                            maxDist = dist;
                            bestIdx = i;
                        }
                    }

                    exceedsThreshold = bestIdx >= 0 && maxDist > epsDouble;
                }
                else
                {
                    double dx = b - a;
                    decimal segY = useExactDecimal ? series[b] - series[a] : 0m;
                    double dy = useExactDecimal ? (double)segY : dSeries[b] - dSeries[a];
                    double segLen = Math.Sqrt((dx * dx) + (dy * dy));
                    if (!double.IsFinite(segLen) || segLen <= 0.0)
                    {
                        keptCount = 0;
                        return MathErrorCode.NonFiniteResult;
                    }

                    double maxDist = -1.0;
                    for (int i = a + 1; i < b; i++)
                    {
                        double dist;
                        if (useExactDecimal)
                        {
                            decimal pointY = series[i] - series[a];
                            decimal crossDec = ((decimal)(b - a) * pointY) - ((decimal)(i - a) * segY);
                            if (crossDec == 0m)
                            {
                                dist = 0.0;
                            }
                            else
                            {
                                double cross = (dx * (double)pointY) - ((i - a) * dy);
                                dist = Math.Abs(cross) / segLen;
                            }
                        }
                        else
                        {
                            double cross = (dx * (dSeries[i] - dSeries[a])) - ((i - a) * dy);
                            dist = Math.Abs(cross) / segLen;
                        }

                        if (!double.IsFinite(dist))
                        {
                            keptCount = 0;
                            return MathErrorCode.NonFiniteResult;
                        }

                        if (dist > maxDist)
                        {
                            maxDist = dist;
                            bestIdx = i;
                        }
                    }

                    exceedsThreshold = bestIdx >= 0 && maxDist > epsDouble;
                }

                if (exceedsThreshold)
                {
                    keep[bestIdx] = 1;
                    stack[sp++] = bestIdx;
                    stack[sp++] = b;
                    stack[sp++] = a;
                    stack[sp++] = bestIdx;
                }
            }

            int writeIdx = 0;
            for (int i = 0; i < n; i++)
            {
                if (keep[i] != 0)
                {
                    indicesOut[writeIdx++] = i;
                }
            }

            keptCount = writeIdx;
            return MathErrorCode.None;
        }
        finally
        {
            if (rentedDSeries is not null)
            {
                ArrayPool<double>.Shared.Return(rentedDSeries, clearArray: false);
            }

            if (rentedKeep is not null)
            {
                ArrayPool<byte>.Shared.Return(rentedKeep, clearArray: false);
            }

            if (rentedStack is not null)
            {
                ArrayPool<int>.Shared.Return(rentedStack, clearArray: false);
            }
        }
    }

    /// <summary>
    /// Reconstructs a length-<c>src.Length</c> piecewise-linear array in pure checked <see cref="decimal"/> (D1)
    /// from strictly increasing 0-based vertex <paramref name="indices"/>, filling exterior positions with <paramref name="fill"/>.
    /// </summary>
    public static MathErrorCode InterpAtIndices(
        scoped ReadOnlySpan<int> indices,
        scoped ReadOnlySpan<decimal> src,
        decimal fill,
        scoped Span<decimal> output)
    {
        int n = src.Length;
        if (n == 0)
        {
            return MathErrorCode.None;
        }

        decimal normalizedFill = DslElementaryMath.NormalizeZero(fill);
        int k = indices.Length;
        if (k == 0)
        {
            output.Fill(normalizedFill);
            return MathErrorCode.None;
        }

        int firstIdx = indices[0];
        int lastIdx = indices[k - 1];
        if (firstIdx > 0)
        {
            output.Slice(0, firstIdx).Fill(normalizedFill);
        }

        if (lastIdx + 1 < n)
        {
            output.Slice(lastIdx + 1).Fill(normalizedFill);
        }

        try
        {
            for (int m = 0; m < k - 1; m++)
            {
                int a = indices[m];
                int b = indices[m + 1];
                decimal yA = src[a];
                decimal yB = src[b];
                output[a] = DslElementaryMath.NormalizeZero(yA);

                int span = b - a;
                if (span <= 1)
                {
                    continue;
                }

                if (TrySubtractDecimal(yB, yA, out decimal dy))
                {
                    decimal maxMulDy = decimal.Truncate(decimal.MaxValue / span) - 1m;
                    if (Math.Abs(dy) <= maxMulDy)
                    {
                        for (int offset = 1; offset < span; offset++)
                        {
                            output[a + offset] = DslElementaryMath.NormalizeZero(checked(yA + (checked(offset * dy) / span)));
                        }
                    }
                    else
                    {
                        decimal slope = dy / span;
                        for (int offset = 1; offset < span; offset++)
                        {
                            output[a + offset] = DslElementaryMath.NormalizeZero(checked(yA + checked(offset * slope)));
                        }
                    }
                }
                else
                {
                    decimal unitA = yA / span;
                    decimal unitB = yB / span;
                    for (int offset = 1; offset < span; offset++)
                    {
                        output[a + offset] = DslElementaryMath.NormalizeZero(checked(((span - offset) * unitA) + (offset * unitB)));
                    }
                }
            }

            output[lastIdx] = DslElementaryMath.NormalizeZero(src[lastIdx]);
            return MathErrorCode.None;
        }
        catch (OverflowException)
        {
            return MathErrorCode.ArithmeticOverflow;
        }
    }

    private static bool TrySubtractDecimal(decimal yB, decimal yA, out decimal diff)
    {
        if (yB >= 0m && yA < 0m && yB > decimal.MaxValue + yA)
        {
            diff = 0m;
            return false;
        }

        if (yB < 0m && yA >= 0m && yB < -decimal.MaxValue + yA)
        {
            diff = 0m;
            return false;
        }

        diff = yB - yA;
        return true;
    }

    private enum PipScoreMode : byte
    {
        ExactCrossMul = 0,
        ExactDecimalDiv = 1,
        DoubleFallback = 2,
    }

    /// <summary>
    /// Extracts <paramref name="k"/> Perceptually Important Points (PIP) using Vertical Distance (D-05, <c>metric == 0</c>)
    /// and a binary max-heap with lowest-index tie-breaking, returning sorted 0-based indices.
    /// </summary>
    public static MathErrorCode PipExtract(
        scoped ReadOnlySpan<decimal> series,
        int k,
        scoped Span<decimal> output)
    {
        var fallbackBudget = DslExecutionBudget.Create(Math.Max(1, series.Length * Math.Max(1, k)));
        return PipExtract(series, k, output, ref fallbackBudget);
    }

    /// <summary>
    /// Extracts <paramref name="k"/> Perceptually Important Points (PIP) using exact <see cref="decimal"/> Vertical Distance (D1)
    /// when within overflow bounds (falling back to <see cref="double"/> only above exact cross bounds) and consumes
    /// <paramref name="budget"/> proportionally to scanned elements.
    /// </summary>
    public static MathErrorCode PipExtract(
        scoped ReadOnlySpan<decimal> series,
        int k,
        scoped Span<decimal> output,
        ref DslExecutionBudget budget)
    {
        int n = series.Length;
        if (k == n)
        {
            int fullCost = Math.Max(1, n / DslExecutionBudget.CheapOperationsPerUnit);
            if (!budget.TryConsume(fullCost))
            {
                return MathErrorCode.BudgetExceeded;
            }

            for (int i = 0; i < n; i++)
            {
                output[i] = i;
            }

            return MathErrorCode.None;
        }

        if (k == 2)
        {
            if (!budget.TryConsume(1))
            {
                return MathErrorCode.BudgetExceeded;
            }

            output[0] = 0m;
            output[1] = n - 1;
            return MathErrorCode.None;
        }

        decimal maxAbs = 0m;
        for (int i = 0; i < n; i++)
        {
            decimal absVal = Math.Abs(series[i]);
            if (absVal > maxAbs)
            {
                maxAbs = absVal;
            }
        }

        decimal exactCrossLimit = ExactCrossOverflowBound / (2m * n);
        decimal crossMulLimit = exactCrossLimit / n;
        PipScoreMode scoreMode = maxAbs <= crossMulLimit
            ? PipScoreMode.ExactCrossMul
            : (maxAbs <= exactCrossLimit ? PipScoreMode.ExactDecimalDiv : PipScoreMode.DoubleFallback);

        byte[]? rentedBytes = null;
        int[]? rentedInts = null;
        decimal[]? rentedDecimals = null;
        double[]? rentedDoubles = null;

        int intTotal = k * 4;
        Span<byte> selected = n <= MathBufferLimits.StackAllocThreshold ? stackalloc byte[n] : default;
        Span<int> heapInts = intTotal <= MathBufferLimits.StackAllocThreshold ? stackalloc int[intTotal] : default;
        Span<decimal> heapDecimals = scoreMode != PipScoreMode.DoubleFallback && k <= MathBufferLimits.DecimalStackAllocThreshold
            ? stackalloc decimal[k]
            : Span<decimal>.Empty;
        Span<double> workDoubles = scoreMode == PipScoreMode.DoubleFallback && (n + k) <= MathBufferLimits.StackAllocThreshold
            ? stackalloc double[n + k]
            : Span<double>.Empty;

        try
        {
            if (n > MathBufferLimits.StackAllocThreshold)
            {
                rentedBytes = ArrayPool<byte>.Shared.Rent(n);
                selected = rentedBytes.AsSpan(0, n);
            }

            if (intTotal > MathBufferLimits.StackAllocThreshold)
            {
                rentedInts = ArrayPool<int>.Shared.Rent(intTotal);
                heapInts = rentedInts.AsSpan(0, intTotal);
            }

            if (scoreMode != PipScoreMode.DoubleFallback && k > MathBufferLimits.DecimalStackAllocThreshold)
            {
                rentedDecimals = ArrayPool<decimal>.Shared.Rent(k);
                heapDecimals = rentedDecimals.AsSpan(0, k);
            }

            if (scoreMode == PipScoreMode.DoubleFallback && (n + k) > MathBufferLimits.StackAllocThreshold)
            {
                rentedDoubles = ArrayPool<double>.Shared.Rent(n + k);
                workDoubles = rentedDoubles.AsSpan(0, n + k);
            }

            Span<double> dSeries = scoreMode == PipScoreMode.DoubleFallback ? workDoubles.Slice(0, n) : Span<double>.Empty;
            Span<double> heapBestDouble = scoreMode == PipScoreMode.DoubleFallback ? workDoubles.Slice(n, k) : Span<double>.Empty;
            if (scoreMode == PipScoreMode.DoubleFallback)
            {
                for (int i = 0; i < n; i++)
                {
                    dSeries[i] = (double)series[i];
                }
            }

            selected.Clear();
            selected[0] = 1;
            selected[n - 1] = 1;

            Span<int> heapLeft = heapInts.Slice(0, k);
            Span<int> heapRight = heapInts.Slice(k, k);
            Span<int> heapBestIdx = heapInts.Slice(k * 2, k);
            Span<int> heapSpan = heapInts.Slice(k * 3, k);

            int heapCount = 0;
            MathErrorCode err = TryPushPipInterval(
                series,
                dSeries,
                scoreMode,
                0,
                n - 1,
                heapLeft,
                heapRight,
                heapBestIdx,
                heapSpan,
                heapDecimals,
                heapBestDouble,
                ref heapCount,
                ref budget);
            if (err != MathErrorCode.None)
            {
                return err;
            }

            int selectedCount = 2;
            while (selectedCount < k && heapCount > 0)
            {
                int a = heapLeft[0];
                int b = heapRight[0];
                int bestIdx = heapBestIdx[0];

                heapCount--;
                if (heapCount > 0)
                {
                    heapLeft[0] = heapLeft[heapCount];
                    heapRight[0] = heapRight[heapCount];
                    heapBestIdx[0] = heapBestIdx[heapCount];
                    heapSpan[0] = heapSpan[heapCount];
                    if (scoreMode != PipScoreMode.DoubleFallback)
                    {
                        heapDecimals[0] = heapDecimals[heapCount];
                    }
                    else
                    {
                        heapBestDouble[0] = heapBestDouble[heapCount];
                    }

                    SiftDownPip(scoreMode, heapLeft, heapRight, heapBestIdx, heapSpan, heapDecimals, heapBestDouble, heapCount, 0);
                }

                selected[bestIdx] = 1;
                selectedCount++;

                if (bestIdx - a >= 2)
                {
                    err = TryPushPipInterval(
                        series,
                        dSeries,
                        scoreMode,
                        a,
                        bestIdx,
                        heapLeft,
                        heapRight,
                        heapBestIdx,
                        heapSpan,
                        heapDecimals,
                        heapBestDouble,
                        ref heapCount,
                        ref budget);
                    if (err != MathErrorCode.None)
                    {
                        return err;
                    }
                }

                if (b - bestIdx >= 2)
                {
                    err = TryPushPipInterval(
                        series,
                        dSeries,
                        scoreMode,
                        bestIdx,
                        b,
                        heapLeft,
                        heapRight,
                        heapBestIdx,
                        heapSpan,
                        heapDecimals,
                        heapBestDouble,
                        ref heapCount,
                        ref budget);
                    if (err != MathErrorCode.None)
                    {
                        return err;
                    }
                }
            }

            int writeIdx = 0;
            for (int i = 0; i < n; i++)
            {
                if (selected[i] != 0)
                {
                    output[writeIdx++] = i;
                }
            }

            return MathErrorCode.None;
        }
        finally
        {
            if (rentedBytes is not null)
            {
                ArrayPool<byte>.Shared.Return(rentedBytes, clearArray: false);
            }

            if (rentedInts is not null)
            {
                ArrayPool<int>.Shared.Return(rentedInts, clearArray: false);
            }

            if (rentedDecimals is not null)
            {
                ArrayPool<decimal>.Shared.Return(rentedDecimals, clearArray: false);
            }

            if (rentedDoubles is not null)
            {
                ArrayPool<double>.Shared.Return(rentedDoubles, clearArray: false);
            }
        }
    }

    private static bool ShouldChooseUpOnSimultaneousReversal(
        int mode,
        decimal moveUp,
        decimal reqUp,
        decimal initLow,
        decimal moveDown,
        decimal reqDown,
        decimal initHigh)
    {
        if (mode == 1)
        {
            return moveUp >= moveDown;
        }

        if (moveUp <= ZigzagCrossMultiplyLimit &&
            initHigh <= ZigzagCrossMultiplyLimit &&
            moveDown <= ZigzagCrossMultiplyLimit &&
            initLow <= ZigzagCrossMultiplyLimit)
        {
            return (moveUp * initHigh) >= (moveDown * initLow);
        }

        return checked(moveUp / reqUp) >= checked(moveDown / reqDown);
    }

    private static bool TryGetReversalDistance(
        decimal refPrice,
        decimal threshold,
        int mode,
        scoped ReadOnlySpan<decimal> atrSeries,
        int t,
        out decimal distance)
    {
        if (mode == 0)
        {
            distance = checked(refPrice * (threshold / 100m));
            return true;
        }

        decimal atr = atrSeries[t];
        if (atr <= 0m)
        {
            distance = 0m;
            return false;
        }

        distance = checked(atr * threshold);
        return true;
    }

    private static void EmitZigzag(
        scoped Span<decimal> output,
        int confirmBar,
        decimal price,
        decimal typeSign,
        int pivotIndex,
        int outputKind)
    {
        output[confirmBar] = outputKind switch
        {
            0 => DslElementaryMath.NormalizeZero(price),
            1 => typeSign,
            _ => pivotIndex,
        };
    }

    private static MathErrorCode TryPushPipInterval(
        scoped ReadOnlySpan<decimal> series,
        scoped ReadOnlySpan<double> dSeries,
        PipScoreMode scoreMode,
        int a,
        int b,
        scoped Span<int> heapLeft,
        scoped Span<int> heapRight,
        scoped Span<int> heapBestIdx,
        scoped Span<int> heapSpan,
        scoped Span<decimal> heapDecimals,
        scoped Span<double> heapBestDouble,
        ref int heapCount,
        ref DslExecutionBudget budget)
    {
        int innerCount = b - a - 1;
        int segCost = Math.Max(1, innerCount / DslExecutionBudget.CheapOperationsPerUnit);
        if (!budget.TryConsume(segCost))
        {
            return MathErrorCode.BudgetExceeded;
        }

        int span = b - a;
        int bestIdx = -1;

        if (scoreMode != PipScoreMode.DoubleFallback)
        {
            decimal yA = series[a];
            decimal dy = series[b] - yA;
            decimal bestCross = -1m;

            for (int i = a + 1; i < b; i++)
            {
                decimal cross = Math.Abs(((decimal)span * (series[i] - yA)) - ((decimal)(i - a) * dy));
                if (cross > bestCross)
                {
                    bestCross = cross;
                    bestIdx = i;
                }
            }

            int pos = heapCount++;
            heapLeft[pos] = a;
            heapRight[pos] = b;
            heapBestIdx[pos] = bestIdx;
            heapSpan[pos] = span;
            heapDecimals[pos] = scoreMode == PipScoreMode.ExactCrossMul ? bestCross : bestCross / span;

            while (pos > 0)
            {
                int parent = (pos - 1) >> 1;
                if (!IsHigherPipPriority(scoreMode, heapDecimals, heapBestDouble, heapSpan, heapBestIdx, pos, parent))
                {
                    break;
                }

                SwapPipHeap(scoreMode, heapLeft, heapRight, heapBestIdx, heapSpan, heapDecimals, heapBestDouble, pos, parent);
                pos = parent;
            }

            return MathErrorCode.None;
        }
        else
        {
            double slope = (dSeries[b] - dSeries[a]) / span;
            double baseY = dSeries[a];
            double bestDist = -1.0;

            for (int i = a + 1; i < b; i++)
            {
                double vd = Math.Abs(dSeries[i] - (baseY + ((i - a) * slope)));
                if (!double.IsFinite(vd))
                {
                    return MathErrorCode.NonFiniteResult;
                }

                if (vd > bestDist)
                {
                    bestDist = vd;
                    bestIdx = i;
                }
            }

            int pos = heapCount++;
            heapLeft[pos] = a;
            heapRight[pos] = b;
            heapBestIdx[pos] = bestIdx;
            heapSpan[pos] = span;
            heapBestDouble[pos] = bestDist;

            while (pos > 0)
            {
                int parent = (pos - 1) >> 1;
                if (!IsHigherPipPriority(scoreMode, heapDecimals, heapBestDouble, heapSpan, heapBestIdx, pos, parent))
                {
                    break;
                }

                SwapPipHeap(scoreMode, heapLeft, heapRight, heapBestIdx, heapSpan, heapDecimals, heapBestDouble, pos, parent);
                pos = parent;
            }

            return MathErrorCode.None;
        }
    }

    private static void SiftDownPip(
        PipScoreMode scoreMode,
        scoped Span<int> heapLeft,
        scoped Span<int> heapRight,
        scoped Span<int> heapBestIdx,
        scoped Span<int> heapSpan,
        scoped Span<decimal> heapDecimals,
        scoped Span<double> heapBestDouble,
        int count,
        int pos)
    {
        while (true)
        {
            int left = (pos << 1) + 1;
            if (left >= count)
            {
                break;
            }

            int bestChild = left;
            int right = left + 1;
            if (right < count &&
                IsHigherPipPriority(scoreMode, heapDecimals, heapBestDouble, heapSpan, heapBestIdx, right, left))
            {
                bestChild = right;
            }

            if (!IsHigherPipPriority(scoreMode, heapDecimals, heapBestDouble, heapSpan, heapBestIdx, bestChild, pos))
            {
                break;
            }

            SwapPipHeap(scoreMode, heapLeft, heapRight, heapBestIdx, heapSpan, heapDecimals, heapBestDouble, pos, bestChild);
            pos = bestChild;
        }
    }

    private static bool IsHigherPipPriority(
        PipScoreMode scoreMode,
        scoped ReadOnlySpan<decimal> heapDecimals,
        scoped ReadOnlySpan<double> heapBestDouble,
        scoped ReadOnlySpan<int> heapSpan,
        scoped ReadOnlySpan<int> heapBestIdx,
        int a,
        int b)
    {
        if (scoreMode == PipScoreMode.ExactCrossMul)
        {
            decimal lhs = heapDecimals[a] * heapSpan[b];
            decimal rhs = heapDecimals[b] * heapSpan[a];
            return lhs > rhs || (lhs == rhs && heapBestIdx[a] < heapBestIdx[b]);
        }

        if (scoreMode == PipScoreMode.ExactDecimalDiv)
        {
            decimal decA = heapDecimals[a];
            decimal decB = heapDecimals[b];
            return decA > decB || (decA == decB && heapBestIdx[a] < heapBestIdx[b]);
        }

        double dblA = heapBestDouble[a];
        double dblB = heapBestDouble[b];
        return dblA > dblB || (dblA == dblB && heapBestIdx[a] < heapBestIdx[b]);
    }

    private static void SwapPipHeap(
        PipScoreMode scoreMode,
        scoped Span<int> heapLeft,
        scoped Span<int> heapRight,
        scoped Span<int> heapBestIdx,
        scoped Span<int> heapSpan,
        scoped Span<decimal> heapDecimals,
        scoped Span<double> heapBestDouble,
        int i,
        int j)
    {
        (heapLeft[i], heapLeft[j]) = (heapLeft[j], heapLeft[i]);
        (heapRight[i], heapRight[j]) = (heapRight[j], heapRight[i]);
        (heapBestIdx[i], heapBestIdx[j]) = (heapBestIdx[j], heapBestIdx[i]);
        (heapSpan[i], heapSpan[j]) = (heapSpan[j], heapSpan[i]);
        if (scoreMode != PipScoreMode.DoubleFallback)
        {
            (heapDecimals[i], heapDecimals[j]) = (heapDecimals[j], heapDecimals[i]);
        }
        else
        {
            (heapBestDouble[i], heapBestDouble[j]) = (heapBestDouble[j], heapBestDouble[i]);
        }
    }

    /// <summary>
    /// Computes symmetric CUSUM change-point triggers (<c>+1</c> / <c>-1</c> / <c>0</c>) in exact checked <see cref="decimal"/>
    /// with symmetric dual reset of both <c>S+</c> and <c>S-</c> upon either threshold trigger (D-06=B).
    /// </summary>
    public static MathErrorCode CusumFilter(
        scoped ReadOnlySpan<decimal> series,
        decimal threshold,
        scoped Span<decimal> output)
    {
        int n = series.Length;
        if (n == 0)
        {
            return MathErrorCode.None;
        }

        output[0] = 0m;
        if (n == 1)
        {
            return MathErrorCode.None;
        }

        decimal negThreshold = -threshold;
        decimal sPos = 0m;
        decimal sNeg = 0m;

        try
        {
            for (int t = 1; t < n; t++)
            {
                decimal delta = checked(series[t] - series[t - 1]);
                decimal nextPos = checked(sPos + delta);
                decimal nextNeg = checked(sNeg + delta);
                sPos = nextPos > 0m ? nextPos : 0m;
                sNeg = nextNeg < 0m ? nextNeg : 0m;

                if (sPos >= threshold)
                {
                    output[t] = 1m;
                    sPos = 0m;
                    sNeg = 0m;
                }
                else if (sNeg <= negThreshold)
                {
                    output[t] = -1m;
                    sPos = 0m;
                    sNeg = 0m;
                }
                else
                {
                    output[t] = 0m;
                }
            }

            return MathErrorCode.None;
        }
        catch (OverflowException)
        {
            return MathErrorCode.ArithmeticOverflow;
        }
    }
}
