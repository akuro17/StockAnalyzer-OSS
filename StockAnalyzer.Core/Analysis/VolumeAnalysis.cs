using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;

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
                
                foreach (var bin in touchedBins)
                {
                    bin.TotalVolume += volToDistribute;
                    if (isBullish) bin.BuyVolume += volToDistribute;
                    else bin.SellVolume += volToDistribute;
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
    /// Calculates Value Area (typically 70% of volume).
    /// Returns the indices of the bins that comprise the Value Area.
    /// Logic: Start from POC (Point of Control - Max Volume) and expand.
    /// </summary>
    public static (decimal VAH, decimal VAL) CalculateValueArea(List<VolumeBin> bins, double valueAreaPercent = ChartConstants.DefaultValueAreaPercent)
    {
        if (bins == null || !bins.Any()) return (0, 0);

        long totalProfileVolume = bins.Sum(b => b.TotalVolume);
        long targetVolume = (long)(totalProfileVolume * valueAreaPercent);

        // Find POC index
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

    /// <summary>
    /// Calculates Anchored VWAP.
    /// </summary>
    public static List<(DateTime Time, decimal Vwap)> CalculateAnchoredVwap(IEnumerable<CoreCandleData> candles)
    {
        var result = new List<(DateTime, decimal)>();
        decimal cumulativePV = 0;
        long cumulativeVol = 0;

        foreach (var candle in candles)
        {
            decimal typicalPrice = (candle.High + candle.Low + candle.Close) / 3;
            cumulativePV += typicalPrice * candle.Volume;
            cumulativeVol += candle.Volume;

            if (cumulativeVol > 0)
            {
                result.Add((candle.Timestamp, cumulativePV / cumulativeVol));
            }
            else
            {
                result.Add((candle.Timestamp, typicalPrice));
            }
        }
        return result;
    }
}
