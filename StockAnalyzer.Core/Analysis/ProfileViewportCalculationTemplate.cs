using System;
using System.Collections.Generic;
using System.Threading;

namespace StockAnalyzer.Core.Analysis;

/// <summary>
/// The calculation skeleton shared by <see cref="VolumeProfileViewportCalculator"/> and <see cref="TimeAtPriceViewportCalculator"/>:
/// split the visible window into ranges, validate every slice, build the bins, locate the Point of Control and the Value Area.
/// The two profiles differ only in how the bins and the Value Area are computed, which the callers pass in.
/// </summary>
internal static class ProfileViewportCalculationTemplate
{
    /// <summary>The result of a request that has nothing to draw (no parameter, no candles, nothing visible, or no range).</summary>
    internal static VolumeProfileViewportResult Empty(string requestKey)
    {
        return new VolumeProfileViewportResult
        {
            RequestKey = requestKey,
            Status = VolumeProfileResultStatus.Empty,
            Segments = Array.Empty<VolumeProfileSegment>()
        };
    }

    /// <param name="period">Bars per segment (0 = all visible bars), as interpreted by <see cref="VolumeProfileRangeBuilder"/>.</param>
    /// <param name="domainName">Prefix of the <see cref="System.IO.InvalidDataException"/> message ("VolumeProfile" / "TimeAtPrice").</param>
    /// <param name="computeBins">Bins of one validated slice of one range.</param>
    /// <param name="computeValueArea">Value Area of non-empty bins whose largest bin has a positive weight.</param>
    internal static VolumeProfileViewportResult Execute(
        string requestKey,
        IReadOnlyList<CoreCandleData> candles,
        int visibleStartIndex,
        int visibleCount,
        int period,
        string domainName,
        Func<List<CoreCandleData>, VolumeProfileRange, List<VolumeBin>> computeBins,
        Func<List<VolumeBin>, (decimal VAH, decimal VAL)> computeValueArea,
        CancellationToken cancellationToken)
    {
        if (candles == null || candles.Count == 0 || visibleCount <= 0)
        {
            return Empty(requestKey);
        }

        var ranges = new List<VolumeProfileRange>(1);
        VolumeProfileRangeBuilder.Build(
            candles,
            visibleStartIndex,
            visibleCount,
            period,
            ranges);

        if (ranges.Count == 0)
        {
            return Empty(requestKey);
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

            // F12 fix: a null element, a negative Volume, a High < Low candle, or an out-of-order/duplicate Timestamp inside the
            // computed window would either crash with an unrelated exception deep inside the analysis (a null element) or silently
            // produce a plausible-looking but meaningless "Success" result. Rejecting the whole request explicitly, rather than
            // silently re-sorting or dropping the offending candle, matches the domain invariant that chronological order must be
            // preserved and never silently repaired.
            // V02 fix: the contract for this rejection is an InvalidDataException, not a Failed-status return value.
            if (!IsSliceValid(slice))
            {
                throw new System.IO.InvalidDataException(
                    $"{domainName} candle slice failed validation (null element, negative volume, High<Low, or non-increasing timestamp).");
            }

            var bins = computeBins(slice, range);
            decimal? poc = null;
            decimal? vah = null;
            decimal? val = null;

            if (bins.Count > 0)
            {
                VolumeBin? maxBin = null;
                long maxWeight = -1;
                for (int b = 0; b < bins.Count; b++)
                {
                    if (bins[b].TotalVolume > maxWeight)
                    {
                        maxWeight = bins[b].TotalVolume;
                        maxBin = bins[b];
                    }
                }

                // F12 fix: when every bin weighs 0 (e.g. every candle in the window traded zero volume), `maxBin` still resolves to
                // bins[0] and the Value Area still returns a bin-index-derived range -- both would present an arbitrary bin as a real
                // Point of Control / Value Area statistic. With no weight distribution, POC/VAH/VAL stay null.
                if (maxWeight > 0)
                {
                    poc = maxBin?.Price;

                    var va = computeValueArea(bins);
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

    /// <summary>
    /// Rejects a candle slice that would otherwise reach the analysis with no explicit contract: a null element, negative Volume,
    /// High &lt; Low, or a non-strictly-increasing Timestamp (covers out-of-order and duplicate bars). The data is never repaired
    /// (no sorting, no clamping); the whole request fails instead.
    /// </summary>
    internal static bool IsSliceValid(List<CoreCandleData> slice)
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
