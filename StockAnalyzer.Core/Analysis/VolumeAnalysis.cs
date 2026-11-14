using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Volume;

namespace StockAnalyzer.Core.Analysis;

public class VolumeBin
{
    public decimal Price { get; set; }
    public decimal LowerBound { get; set; }
    public decimal UpperBound { get; set; }
    public long TotalVolume { get; set; }
    public long BuyVolume { get; set; }
    public long SellVolume { get; set; }
    public double WidthPercent { get; set; } // For display relative to max volume in profile
}

public static class VolumeAnalysis
{
    /// <summary>
    /// Calculates the Volume Profile for a given range of candles.
    /// Uses Row Size approximation (High-Low range distribution).
    /// </summary>
    public static List<VolumeBin> CalculateProfile(IEnumerable<CoreCandleData> candles, int rowSize = ChartConstants.DefaultVolumeProfileRowSize, VolumeDistributionMode mode = VolumeDistributionMode.Proportional)
    {
        var candleList = candles.ToList();
        if (!candleList.Any()) return new List<VolumeBin>();

        decimal minPrice = candleList.Min(c => c.Low);
        decimal maxPrice = candleList.Max(c => c.High);
        
        if (minPrice == maxPrice) return new List<VolumeBin>();

        decimal priceRange = maxPrice - minPrice;
        decimal binSize = priceRange / rowSize;
        if (binSize == 0) binSize = ChartConstants.MinBinSize; // Safety

        var bins = new List<VolumeBin>();
        for (int i = 0; i < rowSize; i++)
        {
            var lower = minPrice + (i * binSize);
            var upper = minPrice + ((i + 1) * binSize);
            bins.Add(new VolumeBin 
            { 
                LowerBound = lower, 
                UpperBound = upper, 
                Price = (lower + upper) / 2 
            });
        }

        foreach (var candle in candleList)
        {
            long volume = candle.Volume;
            bool isBullish = candle.Close >= candle.Open;

            // Distribute volume across bins that the candle touches
            var touchedBins = bins.Where(b => b.UpperBound >= candle.Low && b.LowerBound <= candle.High).ToList();
            if (touchedBins.Any())
            {
                long volToDistribute = volume;
                
                if (mode == VolumeDistributionMode.Proportional)
                {
                    // Even split (naive proportional)
                    volToDistribute = volume / touchedBins.Count;
                }
                // If Mode is Full, we add FULL volume to EACH touched bin (cumulative overlap)
                // Or maybe the user meant "distribute only to bins covering body"? 
                // Creating "Full" mode as "Duplicate Volume" is one interpretation but inflates total.
                // Creating "Full" mode as "Distribute to single bin (POC of candle)?"
                // Given the user's focus on "Close", maybe they want "Close Price Bin gets all volume"?
                // BUT "Full" usually implies "Whole volume at price".
                // Let's implement documented behavior:
                // Proportional: Split volume.
                // Full: Add full volume to each touched bin (heatmap style).
                
                // F12 fix: `long` accumulation is unchecked by default in this project, so a
                // pathological but not-otherwise-invalid input (e.g. many candles with volume near
                // long.MaxValue, amplified by Full mode's per-bin duplication) could silently wrap
                // around to a negative TotalVolume instead of failing -- presenting corrupted data as
                // a normal "Success" result. `checked` does not alter the result for any input that
                // does not overflow; it only turns the overflow case into an explicit, already-handled
                // failure (the coordinator's existing catch-all around this call already maps any
                // exception here to a Failed state) instead of silent data corruption. See
                // sa_analysis_report_VolumeProfile_PostImplementation_StrictReview_20260914.md F12:
                // Explicitly handle long addition overflow caused by Full accumulation.
                checked
                {
                    foreach (var bin in touchedBins)
                    {
                        bin.TotalVolume += volToDistribute;
                        if (isBullish) bin.BuyVolume += volToDistribute;
                        else bin.SellVolume += volToDistribute;
                    }
                }
            }
        }

        // Normalize Width for rendering
        long maxVol = bins.Any() ? bins.Max(b => b.TotalVolume) : 1;
        if (maxVol == 0) maxVol = 1;

        foreach (var bin in bins)
        {
            bin.WidthPercent = (double)bin.TotalVolume / maxVol;
        }

        return bins;
    }

    /// <summary>
    /// Index of the Point of Control: the bin with the greatest <see cref="VolumeBin.TotalVolume"/>.
    /// Tie-break rule: if several bins share the maximum volume, the first one (lowest index, i.e. the lowest price) is returned.
    /// Returns -1 when <paramref name="bins"/> is null or empty. One pass, no allocation.
    /// </summary>
    public static int FindPointOfControlIndex(IReadOnlyList<VolumeBin>? bins)
    {
        if (bins == null || bins.Count == 0) return -1;

        int pocIndex = 0;
        long maxVol = -1;
        for (int i = 0; i < bins.Count; i++)
        {
            if (bins[i].TotalVolume > maxVol)
            {
                maxVol = bins[i].TotalVolume;
                pocIndex = i;
            }
        }

        return pocIndex;
    }

    /// <summary>The Point of Control bin (see <see cref="FindPointOfControlIndex"/>), or null when there are no bins.</summary>
    public static VolumeBin? FindPointOfControl(IReadOnlyList<VolumeBin>? bins)
    {
        int index = FindPointOfControlIndex(bins);
        return index >= 0 ? bins![index] : null;
    }

    /// <summary>
    /// Calculates Value Area (typically 70% of volume).
    /// Returns the indices of the bins that comprise the Value Area.
    /// Logic: Start from POC (Point of Control - Max Volume) and expand.
    /// Tie-break rule: If multiple bins share the maximum volume, the lower-priced bin (lowest index) is selected as POC.
    /// </summary>
    public static (decimal VAH, decimal VAL) CalculateValueArea(List<VolumeBin> bins, double valueAreaPercent = ChartConstants.DefaultValueAreaPercent)
    {
        if (bins == null || !bins.Any()) return (0, 0);
        if (bins.Count == 1) return (bins[0].UpperBound, bins[0].LowerBound);

        long totalProfileVolume = bins.Sum(b => b.TotalVolume);
        long targetVolume = (long)(totalProfileVolume * valueAreaPercent);

        // Find POC index (bins is non-empty here, so the index is >= 0)
        int pocIndex = FindPointOfControlIndex(bins);

        long currentVolume = bins[pocIndex].TotalVolume;
        int upperIndex = pocIndex;
        int lowerIndex = pocIndex;

        // Expand
        while (currentVolume < targetVolume)
        {
            long upperVol = (upperIndex + 1 < bins.Count) ? bins[upperIndex + 1].TotalVolume : 0;
            long lowerVol = (lowerIndex - 1 >= 0) ? bins[lowerIndex - 1].TotalVolume : 0;

            if (upperVol == 0 && lowerVol == 0) break; // Finished

            if (upperVol >= lowerVol)
            {
                if (upperIndex + 1 < bins.Count)
                {
                    upperIndex++;
                    currentVolume += bins[upperIndex].TotalVolume;
                }
            }
            else
            {
                if (lowerIndex - 1 >= 0)
                {
                    lowerIndex--;
                    currentVolume += bins[lowerIndex].TotalVolume;
                }
            }
        }

        return (bins[upperIndex].UpperBound, bins[lowerIndex].LowerBound);
    }

    // ===== Allocation-free counterparts of CalculateProfile / FindPointOfControlIndex / CalculateValueArea (WebAiExpansion plan 03 T7) =====
    // They apply the SAME rules as the three functions above (price range, bin width, bin bounds, touched-bin test, volume split, tie-break, value-area expansion)
    // and write into caller-owned storage, so a time series of profile levels needs no per-bar allocation. Equality with the list-based functions is pinned by
    // VolumeAnalysisSpanTests (every bin volume, POC price, VAH and VAL). The list-based functions are unchanged.

    /// <summary>Lower bound of bin <paramref name="index"/>; the same expression as <see cref="CalculateProfile"/>.</summary>
    public static decimal BinLowerBound(decimal minPrice, decimal binSize, int index) => minPrice + (index * binSize);

    /// <summary>Upper bound of bin <paramref name="index"/>; the same expression as <see cref="CalculateProfile"/>.</summary>
    public static decimal BinUpperBound(decimal minPrice, decimal binSize, int index) => minPrice + ((index + 1) * binSize);

    /// <summary>Price (centre) of bin <paramref name="index"/>; the same expression as <see cref="CalculateProfile"/>.</summary>
    public static decimal BinPrice(decimal minPrice, decimal binSize, int index) =>
        (BinLowerBound(minPrice, binSize, index) + BinUpperBound(minPrice, binSize, index)) / 2;

    /// <summary>
    /// Fills <paramref name="binVolumes"/> (its length is the row count) with the volume of each bin of the profile of <paramref name="window"/>, by the rules of
    /// <see cref="CalculateProfile"/>: price range = lowest Low to highest High, bin width = range / row count (<see cref="ChartConstants.MinBinSize"/> when that is 0),
    /// a candle touches every bin with UpperBound &gt;= Low and LowerBound &lt;= High, <see cref="VolumeDistributionMode.Proportional"/> gives each touched bin
    /// volume / touched-bin count (integer division) and <see cref="VolumeDistributionMode.Full"/> the whole volume. The array is cleared first.
    /// Returns false (and leaves the volumes cleared) when the window is empty or all prices are equal, the cases where <see cref="CalculateProfile"/> returns no bins.
    /// </summary>
    public static bool FillProfile(
        ReadOnlySpan<CoreCandleData> window,
        Span<decimal> binVolumes,
        VolumeDistributionMode mode,
        out decimal minPrice,
        out decimal binSize)
    {
        binVolumes.Clear();
        minPrice = 0m;
        binSize = 0m;
        int rowCount = binVolumes.Length;
        if (window.Length == 0 || rowCount == 0) return false;

        decimal low = window[0].Low;
        decimal high = window[0].High;
        for (int i = 1; i < window.Length; i++)
        {
            if (window[i].Low < low) low = window[i].Low;
            if (window[i].High > high) high = window[i].High;
        }

        if (low == high) return false;

        minPrice = low;
        binSize = (high - low) / rowCount;
        if (binSize == 0) binSize = ChartConstants.MinBinSize;

        for (int c = 0; c < window.Length; c++)
        {
            CoreCandleData candle = window[c];

            // The touched bins are contiguous: UpperBound and LowerBound never decrease with the index.
            int first = IndexEstimate(candle.Low - minPrice, binSize, rowCount) - 1;
            if (first < 0) first = 0;
            while (first > 0 && BinUpperBound(minPrice, binSize, first - 1) >= candle.Low) first--;
            while (first < rowCount - 1 && BinUpperBound(minPrice, binSize, first) < candle.Low) first++;
            if (BinUpperBound(minPrice, binSize, first) < candle.Low) continue;

            int last = IndexEstimate(candle.High - minPrice, binSize, rowCount) + 1;
            if (last > rowCount - 1) last = rowCount - 1;
            while (last < rowCount - 1 && BinLowerBound(minPrice, binSize, last + 1) <= candle.High) last++;
            while (last >= 0 && BinLowerBound(minPrice, binSize, last) > candle.High) last--;
            if (last < first) continue;

            long share = mode == VolumeDistributionMode.Proportional ? candle.Volume / (last - first + 1) : candle.Volume;
            for (int b = first; b <= last; b++) binVolumes[b] += share;
        }

        return true;
    }

    /// <summary>The distance in bins, as an int clamped to [0, rowCount] (a plain cast could overflow for a tiny bin width).</summary>
    private static int IndexEstimate(decimal distance, decimal binSize, int rowCount)
    {
        if (distance <= 0m) return 0;
        decimal bins = distance / binSize;
        return bins >= rowCount ? rowCount : (int)bins;
    }

    /// <summary>
    /// Index of the Point of Control in <paramref name="binVolumes"/>: the bin with the greatest volume; on a tie the first one (lowest index, lowest price), as
    /// <see cref="FindPointOfControlIndex(IReadOnlyList{VolumeBin}?)"/>. Returns -1 when the span is empty.
    /// </summary>
    public static int FindPointOfControlIndexFromVolumes(ReadOnlySpan<decimal> binVolumes)
    {
        if (binVolumes.Length == 0) return -1;

        int pocIndex = 0;
        decimal maxVol = -1m;
        for (int i = 0; i < binVolumes.Length; i++)
        {
            if (binVolumes[i] > maxVol)
            {
                maxVol = binVolumes[i];
                pocIndex = i;
            }
        }

        return pocIndex;
    }

    /// <summary>
    /// Value Area of a filled profile by the rules of <see cref="CalculateValueArea"/>: start at the Point of Control and extend towards the neighbour with the larger
    /// volume (upper on a tie) until the covered volume reaches (long)(total x <paramref name="valueAreaPercent"/>) or both neighbours are empty.
    /// <paramref name="vah"/> is the upper bound of the highest bin included and <paramref name="val"/> the lower bound of the lowest one; (0, 0) for an empty span.
    /// </summary>
    public static void ComputeValueArea(
        ReadOnlySpan<decimal> binVolumes,
        decimal minPrice,
        decimal binSize,
        double valueAreaPercent,
        out decimal vah,
        out decimal val)
    {
        vah = 0m;
        val = 0m;
        int count = binVolumes.Length;
        if (count == 0) return;
        if (count == 1)
        {
            vah = BinUpperBound(minPrice, binSize, 0);
            val = BinLowerBound(minPrice, binSize, 0);
            return;
        }

        decimal total = 0m;
        for (int i = 0; i < count; i++) total += binVolumes[i];
        long targetVolume = (long)((long)total * valueAreaPercent);

        int pocIndex = FindPointOfControlIndexFromVolumes(binVolumes);
        decimal currentVolume = binVolumes[pocIndex];
        int upperIndex = pocIndex;
        int lowerIndex = pocIndex;

        while (currentVolume < targetVolume)
        {
            decimal upperVol = (upperIndex + 1 < count) ? binVolumes[upperIndex + 1] : 0m;
            decimal lowerVol = (lowerIndex - 1 >= 0) ? binVolumes[lowerIndex - 1] : 0m;

            if (upperVol == 0 && lowerVol == 0) break;

            if (upperVol >= lowerVol)
            {
                if (upperIndex + 1 < count)
                {
                    upperIndex++;
                    currentVolume += binVolumes[upperIndex];
                }
            }
            else
            {
                if (lowerIndex - 1 >= 0)
                {
                    lowerIndex--;
                    currentVolume += binVolumes[lowerIndex];
                }
            }
        }

        vah = BinUpperBound(minPrice, binSize, upperIndex);
        val = BinLowerBound(minPrice, binSize, lowerIndex);
    }

    /// <summary>
    /// Calculates Anchored VWAP.
    /// </summary>
    public static List<(DateTime Time, decimal Vwap)> CalculateAnchoredVwap(
        IEnumerable<CoreCandleData> candles,
        PriceType priceType = PriceType.Typical)
    {
        var result = new List<(DateTime, decimal)>();
        decimal cumulativePV = 0;
        long cumulativeVol = 0;

        // Heikin-Ashi sources are recursive over the supplied sequence (it starts at the first supplied candle);
        // True High / True Low use the previous bar's close.
        var candleList = candles as IReadOnlyList<CoreCandleData> ?? candles.ToList();
        decimal[] prices = ArrayPool<decimal>.Shared.Rent(candleList.Count);
        try
        {
            PriceDataHelper.FillPrices(candleList, 0, prices.AsSpan(0, candleList.Count), priceType);

            for (int i = 0; i < candleList.Count; i++)
            {
                var candle = candleList[i];
                decimal price = prices[i];
                cumulativePV += price * candle.Volume;
                cumulativeVol += candle.Volume;

                if (cumulativeVol > 0)
                {
                    result.Add((candle.Timestamp, cumulativePV / cumulativeVol));
                }
                else
                {
                    result.Add((candle.Timestamp, price));
                }
            }
        }
        finally
        {
            ArrayPool<decimal>.Shared.Return(prices);
        }

        return result;
    }

    /// <summary>
    /// Calculates Anchored VWAP alongside the volume-weighted standard deviation at each bar
    /// via <see cref="VwapBandMath"/> (null while cumulative volume is 0 or on overflow).
    /// </summary>
    public static List<(DateTime Time, decimal Vwap, decimal? StdDev)> CalculateAnchoredVwapWithDeviation(
        IEnumerable<CoreCandleData> candles,
        PriceType priceType = PriceType.Typical)
    {
        var candleList = candles as IReadOnlyList<CoreCandleData> ?? candles.ToList();
        var result = new List<(DateTime Time, decimal Vwap, decimal? StdDev)>(candleList.Count);
        decimal cumulativePV = 0;
        long cumulativeVol = 0;
        decimal cumulativePSV = 0;
        bool squaredSumOverflowed = false;

        decimal[] prices = ArrayPool<decimal>.Shared.Rent(candleList.Count);
        try
        {
            PriceDataHelper.FillPrices(candleList, 0, prices.AsSpan(0, candleList.Count), priceType);

            for (int i = 0; i < candleList.Count; i++)
            {
                var candle = candleList[i];
                decimal price = prices[i];
                cumulativePV += price * candle.Volume;
                cumulativeVol += candle.Volume;

                if (!squaredSumOverflowed)
                {
                    try
                    {
                        cumulativePSV += price * price * candle.Volume;
                    }
                    catch (OverflowException)
                    {
                        squaredSumOverflowed = true;
                    }
                }

                decimal vwap = cumulativeVol > 0 ? cumulativePV / cumulativeVol : price;
                decimal? stdDev = cumulativeVol > 0
                    ? VwapBandMath.ComputeSigma(vwap, cumulativePSV, cumulativeVol, squaredSumOverflowed)
                    : null;

                result.Add((candle.Timestamp, vwap, stdDev));
            }
        }
        finally
        {
            ArrayPool<decimal>.Shared.Return(prices);
        }

        return result;
    }
}
