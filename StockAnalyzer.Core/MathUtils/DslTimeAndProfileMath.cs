using System;
using System.Buffers;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Pure zero-allocation <see cref="decimal"/> kernels for DSL v9 time-axis alignment,
/// intraday session range tracking, and volume profile binning.
/// </summary>
public static partial class DslTimeAndProfileMath
{
    /// <summary>Returns <c>true</c> iff <paramref name="values"/> is monotonically non-decreasing.</summary>
    public static bool IsMonotonicNonDecreasing(scoped ReadOnlySpan<decimal> values)
    {
        for (int i = 1; i < values.Length; i++)
        {
            if (values[i] < values[i - 1])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Validates that <c>high[t] &gt;= low[t]</c> for every bar <c>t</c>.
    /// </summary>
    public static bool AreHighLowConsistent(scoped ReadOnlySpan<decimal> high, scoped ReadOnlySpan<decimal> low)
    {
        for (int t = 0; t < high.Length; t++)
        {
            if (high[t] < low[t])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Strictly causal O(N + M) two-pointer as-of join: for each <c>targetTimes[i]</c>,
    /// selects <c>srcVals[j*]</c> where <c>j*</c> is the largest index with <c>srcTimes[j*] &lt;= targetTimes[i]</c>,
    /// or <paramref name="fill"/> when no prior source timestamp exists.
    /// </summary>
    public static void AlignAsof(
        scoped ReadOnlySpan<decimal> targetTimes,
        scoped ReadOnlySpan<decimal> srcTimes,
        scoped ReadOnlySpan<decimal> srcVals,
        decimal fill,
        scoped Span<decimal> output)
        => AlignAsof(targetTimes, srcTimes, srcVals, fill, allowExactMatch: true, maxTolerance: -1m, output);

    /// <summary>
    /// Strictly causal O(N + M) two-pointer as-of join supporting strict past match (<paramref name="allowExactMatch"/> = false)
    /// and optional staleness guard (<paramref name="maxTolerance"/> &gt;= 0).
    /// </summary>
    public static void AlignAsof(
        scoped ReadOnlySpan<decimal> targetTimes,
        scoped ReadOnlySpan<decimal> srcTimes,
        scoped ReadOnlySpan<decimal> srcVals,
        decimal fill,
        bool allowExactMatch,
        decimal maxTolerance,
        scoped Span<decimal> output)
    {
        int n = targetTimes.Length;
        if (n == 0)
        {
            return;
        }

        decimal normalizedFill = DslElementaryMath.NormalizeZero(fill);
        int m = srcTimes.Length;
        if (m == 0)
        {
            output.Fill(normalizedFill);
            return;
        }

        bool hasTolerance = maxTolerance >= 0m;
        int j = -1;
        for (int i = 0; i < n; i++)
        {
            decimal target = targetTimes[i];
            if (allowExactMatch)
            {
                while (j + 1 < m && srcTimes[j + 1] <= target)
                {
                    j++;
                }
            }
            else
            {
                while (j + 1 < m && srcTimes[j + 1] < target)
                {
                    j++;
                }
            }

            if (j < 0 || (hasTolerance && ExceedsTolerance(target, srcTimes[j], maxTolerance)))
            {
                output[i] = normalizedFill;
            }
            else
            {
                output[i] = DslElementaryMath.NormalizeZero(srcVals[j]);
            }
        }
    }

    private static bool ExceedsTolerance(decimal target, decimal srcTime, decimal maxTolerance)
    {
        if (target >= 0m && srcTime < 0m)
        {
            if (target > decimal.MaxValue + srcTime)
            {
                return true;
            }
        }

        return (target - srcTime) > maxTolerance;
    }

    /// <summary>
    /// Computes opening-range high (<paramref name="kind"/> = 0) or low (<paramref name="kind"/> = 1)
    /// over the first <paramref name="openingBars"/> bars of each session identified by <paramref name="sessionIds"/>.
    /// Emits <paramref name="fill"/> while a session has fewer than <paramref name="openingBars"/> bars.
    /// </summary>
    public static void SessionRange(
        scoped ReadOnlySpan<decimal> high,
        scoped ReadOnlySpan<decimal> low,
        scoped ReadOnlySpan<decimal> sessionIds,
        int openingBars,
        int kind,
        decimal fill,
        scoped Span<decimal> output)
        => SessionRange(high, low, sessionIds, openingBars, kind, fill, skipFirstSession: false, output);

    /// <summary>
    /// Computes opening-range high or low with optional suppression of the initial potentially truncated session
    /// (<paramref name="skipFirstSession"/> = true) until the first session ID boundary is observed.
    /// </summary>
    public static void SessionRange(
        scoped ReadOnlySpan<decimal> high,
        scoped ReadOnlySpan<decimal> low,
        scoped ReadOnlySpan<decimal> sessionIds,
        int openingBars,
        int kind,
        decimal fill,
        bool skipFirstSession,
        scoped Span<decimal> output)
    {
        int n = high.Length;
        if (n == 0)
        {
            return;
        }

        decimal normalizedFill = DslElementaryMath.NormalizeZero(fill);
        decimal openHigh = 0m;
        decimal openLow = 0m;
        int barInSession = 0;
        bool inFirstSession = true;

        for (int t = 0; t < n; t++)
        {
            if (t == 0)
            {
                barInSession = 1;
                openHigh = high[t];
                openLow = low[t];
            }
            else if (sessionIds[t] != sessionIds[t - 1])
            {
                inFirstSession = false;
                barInSession = 1;
                openHigh = high[t];
                openLow = low[t];
            }
            else
            {
                barInSession++;
                if (barInSession <= openingBars)
                {
                    if (high[t] > openHigh)
                    {
                        openHigh = high[t];
                    }

                    if (low[t] < openLow)
                    {
                        openLow = low[t];
                    }
                }
            }

            output[t] = (skipFirstSession && inFirstSession) || barInSession < openingBars
                ? normalizedFill
                : DslElementaryMath.NormalizeZero(kind == 0 ? openHigh : openLow);
        }
    }

    /// <summary>
    /// Validates that every volume entry is non-negative.
    /// </summary>
    public static bool AreVolumesNonNegative(scoped ReadOnlySpan<decimal> volumes)
    {
        for (int i = 0; i < volumes.Length; i++)
        {
            if (volumes[i] < 0m)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Computes equal-width Volume Profile cumulative volumes across <c>binsOut.Length</c> bins
    /// over <c>[min(prices), max(prices)]</c> in pure checked <see cref="decimal"/>.
    /// Outputs <paramref name="minPrice"/>, <paramref name="priceSpan"/>, and <paramref name="totalVolume"/> for POC calculation.
    /// </summary>
    public static MathErrorCode ComputeVolumeBins(
        scoped ReadOnlySpan<decimal> prices,
        scoped ReadOnlySpan<decimal> volumes,
        scoped Span<decimal> binsOut,
        out decimal minPrice,
        out decimal priceSpan,
        out decimal totalVolume)
    {
        minPrice = prices[0];
        decimal maxPrice = prices[0];
        for (int i = 1; i < prices.Length; i++)
        {
            decimal p = prices[i];
            if (p < minPrice)
            {
                minPrice = p;
            }

            if (p > maxPrice)
            {
                maxPrice = p;
            }
        }

        try
        {
            priceSpan = checked(maxPrice - minPrice);
            binsOut.Clear();

            int nBins = binsOut.Length;
            decimal sumVol = 0m;

            if (priceSpan == 0m)
            {
                for (int i = 0; i < volumes.Length; i++)
                {
                    sumVol = checked(sumVol + volumes[i]);
                }

                binsOut[0] = DslElementaryMath.NormalizeZero(sumVol);
                totalVolume = sumVol;
                return MathErrorCode.None;
            }

            decimal maxMultiplySpan = decimal.Truncate(decimal.MaxValue / nBins) - 1m;
            bool useMultiplyFirst = priceSpan <= maxMultiplySpan;
            decimal fallbackBinWidth = useMultiplyFirst ? 0m : priceSpan / nBins;
            if (!useMultiplyFirst && fallbackBinWidth == 0m)
            {
                for (int i = 0; i < volumes.Length; i++)
                {
                    sumVol = checked(sumVol + volumes[i]);
                }

                binsOut[0] = DslElementaryMath.NormalizeZero(sumVol);
                totalVolume = sumVol;
                return MathErrorCode.None;
            }

            for (int i = 0; i < prices.Length; i++)
            {
                decimal vol = volumes[i];
                sumVol = checked(sumVol + vol);

                decimal rawBin = useMultiplyFirst
                    ? decimal.Truncate(checked((prices[i] - minPrice) * nBins) / priceSpan)
                    : decimal.Truncate(checked((prices[i] - minPrice) / fallbackBinWidth));
                int binIdx = rawBin >= nBins ? nBins - 1 : (rawBin < 0m ? 0 : (int)rawBin);
                binsOut[binIdx] = checked(binsOut[binIdx] + vol);
            }

            for (int b = 0; b < nBins; b++)
            {
                binsOut[b] = DslElementaryMath.NormalizeZero(binsOut[b]);
            }

            totalVolume = sumVol;
            return MathErrorCode.None;
        }
        catch (OverflowException)
        {
            priceSpan = 0m;
            totalVolume = 0m;
            return MathErrorCode.ArithmeticOverflow;
        }
    }

    /// <summary>
    /// Computes the Point of Control (center price of the bin with maximum volume, lowest-index tie-break).
    /// Returns <see cref="MathErrorCode.DomainError"/> when total volume is zero.
    /// </summary>
    public static MathErrorCode VolumeProfilePoc(
        scoped ReadOnlySpan<decimal> prices,
        scoped ReadOnlySpan<decimal> volumes,
        int nBins,
        out decimal poc)
    {
        decimal[]? rented = nBins <= MathBufferLimits.DecimalStackAllocThreshold
            ? null
            : ArrayPool<decimal>.Shared.Rent(nBins);
        Span<decimal> bins = rented is null
            ? stackalloc decimal[nBins]
            : rented.AsSpan(0, nBins);

        try
        {
            MathErrorCode err = ComputeVolumeBins(prices, volumes, bins, out decimal minPrice, out decimal priceSpan, out decimal totalVolume);
            if (err != MathErrorCode.None)
            {
                poc = 0m;
                return err;
            }

            if (totalVolume == 0m)
            {
                poc = 0m;
                return MathErrorCode.DomainError;
            }

            if (priceSpan == 0m)
            {
                poc = DslElementaryMath.NormalizeZero(minPrice);
                return MathErrorCode.None;
            }

            int bestBin = 0;
            decimal maxVol = bins[0];
            for (int b = 1; b < nBins; b++)
            {
                if (bins[b] > maxVol)
                {
                    maxVol = bins[b];
                    bestBin = b;
                }
            }

            poc = ComputeBinCenterPrice(minPrice, priceSpan, nBins, bestBin);
            return MathErrorCode.None;
        }
        catch (OverflowException)
        {
            poc = 0m;
            return MathErrorCode.ArithmeticOverflow;
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
    /// Computes the Market Profile Value Area (<c>[VAL, POC, VAH]</c> written into <paramref name="output"/> of length 3)
    /// reusing <see cref="ComputeVolumeBins"/> in pure checked <see cref="decimal"/> (D1) with upper-neighbor preference on ties.
    /// Returns <see cref="MathErrorCode.DomainError"/> when total volume is zero.
    /// </summary>
    public static MathErrorCode VolumeProfileValueArea(
        scoped ReadOnlySpan<decimal> prices,
        scoped ReadOnlySpan<decimal> volumes,
        int nBins,
        decimal ratio,
        scoped Span<decimal> output)
    {
        decimal[]? rented = nBins <= MathBufferLimits.DecimalStackAllocThreshold
            ? null
            : ArrayPool<decimal>.Shared.Rent(nBins);
        Span<decimal> bins = rented is null
            ? stackalloc decimal[nBins]
            : rented.AsSpan(0, nBins);

        try
        {
            MathErrorCode err = ComputeVolumeBins(prices, volumes, bins, out decimal minPrice, out decimal priceSpan, out decimal totalVolume);
            if (err != MathErrorCode.None)
            {
                return err;
            }

            if (totalVolume == 0m)
            {
                return MathErrorCode.DomainError;
            }

            if (priceSpan == 0m)
            {
                decimal flat = DslElementaryMath.NormalizeZero(minPrice);
                output[0] = flat;
                output[1] = flat;
                output[2] = flat;
                return MathErrorCode.None;
            }

            int pocBin = 0;
            decimal maxVol = bins[0];
            for (int b = 1; b < nBins; b++)
            {
                if (bins[b] > maxVol)
                {
                    maxVol = bins[b];
                    pocBin = b;
                }
            }

            decimal targetVol = checked(totalVolume * ratio);
            decimal currentVol = bins[pocBin];
            int lowerBin = pocBin;
            int upperBin = pocBin;

            while (currentVol < targetVol)
            {
                bool hasUpper = upperBin + 1 < nBins;
                bool hasLower = lowerBin - 1 >= 0;
                if (!hasUpper && !hasLower)
                {
                    break;
                }

                decimal upperVol = hasUpper ? bins[upperBin + 1] : -1m;
                decimal lowerVol = hasLower ? bins[lowerBin - 1] : -1m;
                if (upperVol >= lowerVol)
                {
                    upperBin++;
                    currentVol = checked(currentVol + upperVol);
                }
                else
                {
                    lowerBin--;
                    currentVol = checked(currentVol + lowerVol);
                }
            }

            output[0] = ComputeBinBoundaryPrice(minPrice, priceSpan, nBins, lowerBin);
            output[1] = ComputeBinCenterPrice(minPrice, priceSpan, nBins, pocBin);
            output[2] = ComputeBinBoundaryPrice(minPrice, priceSpan, nBins, upperBin + 1);
            return MathErrorCode.None;
        }
        catch (OverflowException)
        {
            return MathErrorCode.ArithmeticOverflow;
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<decimal>.Shared.Return(rented, clearArray: false);
            }
        }
    }

    private static decimal ComputeBinCenterPrice(decimal minPrice, decimal priceSpan, int nBins, int binIndex)
    {
        decimal maxPocSpan = decimal.Truncate(decimal.MaxValue / (2m * nBins)) - 1m;
        decimal centerOffset = priceSpan <= maxPocSpan
            ? checked(((2m * binIndex) + 1m) * priceSpan) / (2m * nBins)
            : checked((binIndex + 0.5m) * (priceSpan / nBins));
        return DslElementaryMath.NormalizeZero(checked(minPrice + centerOffset));
    }

    private static decimal ComputeBinBoundaryPrice(decimal minPrice, decimal priceSpan, int nBins, int boundaryIndex)
    {
        if (boundaryIndex <= 0)
        {
            return DslElementaryMath.NormalizeZero(minPrice);
        }

        if (boundaryIndex >= nBins)
        {
            return DslElementaryMath.NormalizeZero(checked(minPrice + priceSpan));
        }

        decimal maxBoundarySpan = decimal.Truncate(decimal.MaxValue / nBins) - 1m;
        decimal offset = priceSpan <= maxBoundarySpan
            ? checked(boundaryIndex * priceSpan) / nBins
            : checked(boundaryIndex * (priceSpan / nBins));
        return DslElementaryMath.NormalizeZero(checked(minPrice + offset));
    }
}
