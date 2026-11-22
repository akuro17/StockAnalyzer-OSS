using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// The repeat-mode segmentation of the Fixed Range Volume Profile and Time at Price drawing tools (one profile per bar group, week or month, starting at the
/// anchor bar). It was a verbatim copy in both objects; the two things that differ (how a slice becomes bins, and how bins become a Value Area) are passed in.
/// </summary>
internal static class VolumeProfileSegmentBuilder
{
    /// <summary>Price rows of the profiles the drawing tools calculate (a literal 50 in both tools before this single definition).</summary>
    public const int DrawingRowCount = 50;

    /// <summary>The profile of the segment that starts at the anchor bar; the object shows it as its primary profile (ProfileData, VAH, VAL).</summary>
    public readonly record struct PrimaryProfile(List<VolumeBin> Bins, decimal Vah, decimal Val);

    /// <summary>
    /// Appends the segments of <paramref name="mode"/> to <paramref name="segments"/> and returns the profile of the first one (the segment at <paramref name="startBarIdx"/>).
    /// <list type="bullet">
    /// <item><description><see cref="VolumeProfileRepeatMode.RangeBar"/>: consecutive groups of <paramref name="baseBars"/> bars; the last group may be shorter (partial).</description></item>
    /// <item><description><see cref="VolumeProfileRepeatMode.Weekly"/>: one segment per W1 period key of <see cref="TimeFrameAggregator"/>; partial when the first one does not start on a Monday or the segment reaches the last bar.</description></item>
    /// <item><description><see cref="VolumeProfileRepeatMode.Monthly"/>: one segment per calendar month; partial when the first one does not start on day 1 or the segment reaches the last bar.</description></item>
    /// </list>
    /// </summary>
    /// <param name="candles">All candles in chronological order.</param>
    /// <param name="startBarIdx">Index of the anchor bar, 0 or more and below the candle count (the caller checks).</param>
    /// <param name="baseBars">Bars per group in RangeBar mode, 1 or more.</param>
    /// <param name="mode">A repeat mode other than <see cref="VolumeProfileRepeatMode.Default"/>.</param>
    /// <param name="computeBins">Bins of one slice (the profile calculation of the tool).</param>
    /// <param name="computeValueArea">Value Area of those bins (the Value Area calculation of the tool).</param>
    /// <param name="segments">Receives the segments; the caller clears it first.</param>
    public static PrimaryProfile Build(
        IReadOnlyList<CoreCandleData> candles,
        int startBarIdx,
        int baseBars,
        VolumeProfileRepeatMode mode,
        Func<List<CoreCandleData>, List<VolumeBin>> computeBins,
        Func<List<VolumeBin>, (decimal VAH, decimal VAL)> computeValueArea,
        List<VolumeProfileSegment> segments)
    {
        if (mode != VolumeProfileRepeatMode.RangeBar && mode != VolumeProfileRepeatMode.Weekly && mode != VolumeProfileRepeatMode.Monthly)
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Only a repeat mode builds segments.");
        }

        int totalCandles = candles.Count;
        int currentIdx = startBarIdx;
        PrimaryProfile primary = default;

        while (currentIdx < totalCandles)
        {
            int count;
            int nextIdx;
            bool isPartial;

            if (mode == VolumeProfileRepeatMode.RangeBar)
            {
                count = Math.Min(baseBars, totalCandles - currentIdx);
                if (count <= 0) break;
                isPartial = count < baseBars;
                nextIdx = currentIdx + baseBars;
            }
            else
            {
                CoreCandleData firstCandle = candles[currentIdx];
                int periodKey = PeriodKey(firstCandle, mode);

                int endIdx = currentIdx;
                while (endIdx < totalCandles && PeriodKey(candles[endIdx], mode) == periodKey)
                {
                    endIdx++;
                }

                count = endIdx - currentIdx;
                bool startsMidPeriod = mode == VolumeProfileRepeatMode.Weekly
                    ? firstCandle.Timestamp.DayOfWeek != DayOfWeek.Monday
                    : firstCandle.Timestamp.Day != 1;
                isPartial = (currentIdx == startBarIdx && startsMidPeriod) || endIdx == totalCandles;
                nextIdx = endIdx;
            }

            var slice = new List<CoreCandleData>(count);
            for (int i = 0; i < count; i++)
            {
                slice.Add(candles[currentIdx + i]);
            }

            var profile = computeBins(slice);
            var segVa = computeValueArea(profile);
            var pocBin = VolumeAnalysis.FindPointOfControl(profile);

            segments.Add(new VolumeProfileSegment
            {
                StartIndex = currentIdx,
                Count = count,
                StartTime = slice[0].Timestamp,
                EndTime = slice[^1].Timestamp,
                IsPartial = isPartial,
                Bins = profile,
                POC = pocBin?.Price,
                VAH = segVa.VAH,
                VAL = segVa.VAL
            });

            if (currentIdx == startBarIdx)
            {
                primary = new PrimaryProfile(profile, segVa.VAH, segVa.VAL);
            }

            currentIdx = nextIdx;
        }

        return primary;
    }

    private static int PeriodKey(CoreCandleData candle, VolumeProfileRepeatMode mode)
        => mode == VolumeProfileRepeatMode.Weekly
            ? TimeFrameAggregator.GetPeriodKey(candle.Timestamp, TimeFrame.W1)
            : candle.Timestamp.Year * 100 + candle.Timestamp.Month;
}
