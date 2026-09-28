using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.Analysis;

public static class TimeAtPriceAnalysis
{
    /// <summary>
    /// Calculates the Time at Price (residence time profile) for a given range of candles.
    /// Distributes time duration (counted in bars) into price bins according to the selected PriceType.
    /// </summary>
    public static List<VolumeBin> CalculateProfile(
        IEnumerable<CoreCandleData> candles,
        int rowSize = ChartConstants.DefaultVolumeProfileRowSize,
        PriceType priceType = PriceType.Close)
    {
        if (candles == null) return new List<VolumeBin>();

        var candleList = candles as IReadOnlyList<CoreCandleData> ?? candles.ToList();
        if (candleList.Count == 0) return new List<VolumeBin>();

        decimal minPrice = candleList.Min(c => c.Low);
        decimal maxPrice = candleList.Max(c => c.High);

        if (minPrice > maxPrice) return new List<VolumeBin>();

        if (minPrice == maxPrice)
        {
            long buyCount = 0;
            long sellCount = 0;
            foreach (var c in candleList)
            {
                if (c.Close >= c.Open) buyCount++;
                else sellCount++;
            }

            return new List<VolumeBin>
            {
                new VolumeBin
                {
                    LowerBound = minPrice,
                    UpperBound = maxPrice,
                    Price = minPrice,
                    TotalVolume = candleList.Count,
                    BuyVolume = buyCount,
                    SellVolume = sellCount,
                    WidthPercent = 1.0
                }
            };
        }

        if (rowSize < 1) rowSize = 1;

        decimal priceRange = maxPrice - minPrice;
        decimal binSize = priceRange / rowSize;
        if (binSize == 0) binSize = ChartConstants.MinBinSize;

        var bins = new List<VolumeBin>(rowSize);
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

        checked
        {
            for (int i = 0; i < candleList.Count; i++)
            {
                var candle = candleList[i];
                decimal price = PriceDataHelper.ExtractPrice(candle, priceType);
                bool isBullish = candle.Close >= candle.Open;

                int idx = (int)((price - minPrice) / binSize);
                if (idx < 0) idx = 0;
                if (idx >= rowSize) idx = rowSize - 1;

                var targetBin = bins[idx];
                targetBin.TotalVolume += 1;
                if (isBullish) targetBin.BuyVolume += 1;
                else targetBin.SellVolume += 1;
            }
        }

        long maxTime = bins.Count > 0 ? bins.Max(b => b.TotalVolume) : 1;
        if (maxTime <= 0) maxTime = 1;

        for (int i = 0; i < bins.Count; i++)
        {
            bins[i].WidthPercent = (double)bins[i].TotalVolume / maxTime;
        }

        return bins;
    }

    /// <summary>
    /// Calculates the Time at Price (residence time profile) for an arbitrary series (e.g. from a Base Indicator).
    /// If candles are provided, candle color (Close >= Open) determines bullish/bearish classification.
    /// Otherwise, bar-to-bar price direction (series[i] >= series[i-1]) is used.
    /// </summary>
    public static List<VolumeBin> CalculateSeriesProfile(
        IReadOnlyList<decimal?> series,
        int rowSize = ChartConstants.DefaultVolumeProfileRowSize,
        IReadOnlyList<CoreCandleData>? candles = null)
    {
        return CalculateProfile(series, rowSize, candles);
    }

    /// <summary>
    /// Calculates the Time at Price (residence time profile) for an arbitrary series (e.g. from a Base Indicator).
    /// If candles are provided, candle color (Close >= Open) determines bullish/bearish classification.
    /// Otherwise, bar-to-bar price direction (series[i] >= series[i-1]) is used.
    /// </summary>
    public static List<VolumeBin> CalculateProfile(
        IReadOnlyList<decimal?> series,
        int rowSize,
        IReadOnlyList<CoreCandleData>? candles = null)
    {
        if (series == null || series.Count == 0) return new List<VolumeBin>();

        decimal minPrice = decimal.MaxValue;
        decimal maxPrice = decimal.MinValue;
        int validCount = 0;

        for (int i = 0; i < series.Count; i++)
        {
            if (series[i].HasValue)
            {
                decimal v = series[i]!.Value;
                if (v < minPrice) minPrice = v;
                if (v > maxPrice) maxPrice = v;
                validCount++;
            }
        }

        if (validCount == 0 || minPrice > maxPrice) return new List<VolumeBin>();

        if (minPrice == maxPrice)
        {
            long buyCount = 0;
            long sellCount = 0;
            if (candles != null)
            {
                for (int i = 0; i < series.Count; i++)
                {
                    if (!series[i].HasValue) continue;
                    if (i < candles.Count && candles[i] != null)
                    {
                        if (candles[i].Close >= candles[i].Open) buyCount++;
                        else sellCount++;
                    }
                }
            }

            return new List<VolumeBin>
            {
                new VolumeBin
                {
                    LowerBound = minPrice,
                    UpperBound = maxPrice,
                    Price = minPrice,
                    TotalVolume = validCount,
                    BuyVolume = buyCount,
                    SellVolume = sellCount,
                    WidthPercent = 1.0
                }
            };
        }

        if (rowSize < 1) rowSize = 1;

        decimal priceRange = maxPrice - minPrice;
        decimal binSize = priceRange / rowSize;
        if (binSize == 0) binSize = ChartConstants.MinBinSize;

        var bins = new List<VolumeBin>(rowSize);
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

        checked
        {
            for (int i = 0; i < series.Count; i++)
            {
                if (!series[i].HasValue) continue;
                decimal price = series[i]!.Value;

                bool? isBullish = null;
                if (candles != null && i < candles.Count && candles[i] != null)
                {
                    isBullish = candles[i].Close >= candles[i].Open;
                }
                else
                {
                    if (i > 0 && series[i - 1].HasValue)
                    {
                        decimal prev = series[i - 1]!.Value;
                        if (price > prev) isBullish = true;
                        else if (price < prev) isBullish = false;
                        // price == prev: neutral (remains null)
                    }
                    // i == 0 or prev is null: neutral (remains null)
                }

                int idx = (int)((price - minPrice) / binSize);
                if (idx < 0) idx = 0;
                if (idx >= rowSize) idx = rowSize - 1;

                var targetBin = bins[idx];
                targetBin.TotalVolume += 1;
                if (isBullish == true) targetBin.BuyVolume += 1;
                else if (isBullish == false) targetBin.SellVolume += 1;
            }
        }

        long maxTime = bins.Count > 0 ? bins.Max(b => b.TotalVolume) : 1;
        if (maxTime <= 0) maxTime = 1;

        for (int i = 0; i < bins.Count; i++)
        {
            bins[i].WidthPercent = (double)bins[i].TotalVolume / maxTime;
        }

        return bins;
    }

    /// <summary>
    /// Calculates Value Area (typically 70% of residence time) using the shared ValueArea algorithm.
    /// </summary>
    public static (decimal VAH, decimal VAL) CalculateValueArea(
        List<VolumeBin> bins,
        double valueAreaPercent = ChartConstants.DefaultValueAreaPercent)
    {
        return VolumeAnalysis.CalculateValueArea(bins, valueAreaPercent);
    }
}
