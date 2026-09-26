using System;
using System.Collections.Generic;
using System.Threading;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Analysis;

public static class TimeAtPriceViewportCalculator
{
    public static VolumeProfileViewportResult Calculate(
        string requestKey,
        IReadOnlyList<CoreCandleData> candles,
        int visibleStartIndex,
        int visibleCount,
        TimeframeType timeframe,
        CoreTimeAtPriceParameter parameter,
        CancellationToken cancellationToken = default)
    {
        return Calculate(
            requestKey,
            candles,
            visibleStartIndex,
            visibleCount,
            timeframe,
            parameter,
            null,
            parameter?.PriceSource ?? PriceType.Close,
            cancellationToken);
    }

    public static VolumeProfileViewportResult Calculate(
        string requestKey,
        IReadOnlyList<CoreCandleData> candles,
        int visibleStartIndex,
        int visibleCount,
        TimeframeType timeframe,
        CoreTimeAtPriceParameter parameter,
        IReadOnlyList<decimal?>? sourceSeries,
        PriceType priceSource = PriceType.Close,
        CancellationToken cancellationToken = default)
    {
        if (candles == null || candles.Count == 0 || visibleCount <= 0 || parameter == null)
        {
            return new VolumeProfileViewportResult
            {
                RequestKey = requestKey,
                Status = VolumeProfileResultStatus.Empty,
                Segments = Array.Empty<VolumeProfileSegment>()
            };
        }

        var ranges = new List<VolumeProfileRange>(1);
        VolumeProfileRangeBuilder.Build(
            candles,
            visibleStartIndex,
            visibleCount,
            parameter.Period,
            ranges);

        if (ranges.Count == 0)
        {
            return new VolumeProfileViewportResult
            {
                RequestKey = requestKey,
                Status = VolumeProfileResultStatus.Empty,
                Segments = Array.Empty<VolumeProfileSegment>()
            };
        }

        var segments = new List<VolumeProfileSegment>(ranges.Count);

        for (int r = 0; r < ranges.Count; r++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var range = ranges[r];
            if (range.Count <= 0 || range.StartIndex < 0 || range.StartIndex + range.Count > candles.Count)
            {
                continue;
            }

            var slice = new List<CoreCandleData>(range.Count);
            int endIndex = range.StartIndex + range.Count;
            for (int i = range.StartIndex; i < endIndex; i++)
            {
                slice.Add(candles[i]);
            }

            if (!IsSliceValid(slice))
            {
                throw new System.IO.InvalidDataException(
                    "TimeAtPrice candle slice failed validation (null element, negative volume, High<Low, or non-increasing timestamp).");
            }

            List<VolumeBin> bins;
            if (sourceSeries != null && sourceSeries.Count > 0)
            {
                var seriesSlice = new List<decimal?>(range.Count);
                for (int i = range.StartIndex; i < endIndex; i++)
                {
                    seriesSlice.Add(i < sourceSeries.Count ? sourceSeries[i] : null);
                }
                bins = TimeAtPriceAnalysis.CalculateProfile(seriesSlice, parameter.RowCount, slice);
            }
            else
            {
                var effectivePriceType = priceSource != PriceType.Close ? priceSource : parameter.PriceSource;
                bins = TimeAtPriceAnalysis.CalculateProfile(slice, parameter.RowCount, effectivePriceType);
            }
            decimal? poc = null;
            decimal? vah = null;
            decimal? val = null;

            if (bins.Count > 0)
            {
                VolumeBin? maxBin = null;
                long maxTime = -1;
                for (int b = 0; b < bins.Count; b++)
                {
                    if (bins[b].TotalVolume > maxTime)
                    {
                        maxTime = bins[b].TotalVolume;
                        maxBin = bins[b];
                    }
                }

                if (maxTime > 0)
                {
                    poc = maxBin?.Price;
                    var va = TimeAtPriceAnalysis.CalculateValueArea(bins);
                    vah = va.VAH;
                    val = va.VAL;
                }
            }

            segments.Add(new VolumeProfileSegment
            {
                StartIndex = range.StartIndex,
                Count = range.Count,
                StartTime = candles[range.StartIndex].Timestamp,
                EndTime = candles[endIndex - 1].Timestamp,
                IsPartial = range.IsPartial,
                Bins = bins,
                POC = poc,
                VAH = vah,
                VAL = val
            });
        }

        return new VolumeProfileViewportResult
        {
            RequestKey = requestKey,
            Status = segments.Count > 0 ? VolumeProfileResultStatus.Success : VolumeProfileResultStatus.Empty,
            Segments = segments
        };
    }

    private static bool IsSliceValid(List<CoreCandleData> slice)
    {
        for (int i = 0; i < slice.Count; i++)
        {
            var candle = slice[i];
            if (candle == null) return false;
            if (candle.Volume < 0) return false;
            if (candle.High < candle.Low) return false;
            if (i > 0 && candle.Timestamp <= slice[i - 1].Timestamp) return false;
        }
        return true;
    }
}
