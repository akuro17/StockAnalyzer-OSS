using System;
using System.Collections.Generic;
using System.Threading;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Analysis;

public static class VolumeProfileViewportCalculator
{
    public static VolumeProfileViewportResult Calculate(
        string requestKey,
        IReadOnlyList<CoreCandleData> candles,
        int visibleStartIndex,
        int visibleCount,
        TimeframeType timeframe,
        CoreVolumeProfileParameter parameter,
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

            // F12 fix: previously the only validation before calling into VolumeAnalysis was the
            // top-level null/empty/visibleCount check above -- a null element, a negative Volume, a
            // High < Low candle, or an out-of-order/duplicate Timestamp inside the actual computed
            // window would either crash with an unrelated exception deep inside LINQ (a null element)
            // or, worse, silently produce a plausible-looking but meaningless "Success" result (a
            // negative volume just adds like any other value; a reversed High/Low candle simply drops
            // out of every bin's touch test with no signal that anything was wrong). Rejecting the
            // whole request explicitly here, rather than silently re-sorting or dropping the offending
            // candle, matches the domain invariant that chronological order must be preserved and
            // never silently repaired. See
            // sa_analysis_report_VolumeProfile_PostImplementation_StrictReview_20260914.md F12/A17.
            //
            // V02 fix (sa_analysis_VolumeProfile_V02_V09-V12_Resolution_20260916.md): the original plan's
            // contract for this exact rejection is an InvalidDataException, not a Failed-status return
            // value -- unify with the OverflowException path below (both represent "the input itself is
            // invalid") using the existing BCL type instead of a Failed result object.
            if (!IsSliceValid(slice))
            {
                throw new System.IO.InvalidDataException(
                    "VolumeProfile candle slice failed validation (null element, negative volume, High<Low, or non-increasing timestamp).");
            }

            var bins = VolumeAnalysis.CalculateProfile(slice, parameter.RowCount, parameter.Mode);
            decimal? poc = null;
            decimal? vah = null;
            decimal? val = null;

            if (bins.Count > 0)
            {
                VolumeBin? maxBin = null;
                long maxVol = -1;
                for (int b = 0; b < bins.Count; b++)
                {
                    if (bins[b].TotalVolume > maxVol)
                    {
                        maxVol = bins[b].TotalVolume;
                        maxBin = bins[b];
                    }
                }

                // F12 fix: when every bin's TotalVolume is 0 (e.g. every candle in the window traded
                // zero volume), `maxBin` still resolves to bins[0] (any TotalVolume of 0 clears the
                // initial maxVol=-1 sentinel) and CalculateValueArea still returns a bin-index-derived
                // range -- both present an arbitrary bin as if it were a real Point of Control / Value
                // Area statistic, when in fact there is no volume distribution to derive one from. See
                // sa_analysis_report_..._20260914.md F12: "total volume0→POC/VAH/VAL=null".
                if (maxVol > 0)
                {
                    poc = maxBin?.Price;

                    var va = VolumeAnalysis.CalculateValueArea(bins);
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
    /// F12 fix: rejects a candle slice that would otherwise reach <see cref="VolumeAnalysis"/> with no
    /// explicit contract -- a null element, negative Volume, High &lt; Low, or a non-strictly-increasing
    /// Timestamp (covers both out-of-order and duplicate bars). Does not attempt to repair the data
    /// (e.g. by sorting or clamping); the whole request explicitly fails instead
    /// (sa_analysis_report_VolumeProfile_PostImplementation_StrictReview_20260914.md F12/A17: "誤順序を
    /// sortで隠蔽しない").
    /// </summary>
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
