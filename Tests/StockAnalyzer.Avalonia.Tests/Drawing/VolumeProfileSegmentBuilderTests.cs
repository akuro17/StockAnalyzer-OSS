using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// The repeat-mode segmentation that FixedRangeVolumeProfileObject and TimeAtPriceObject share (it was a verbatim copy in both), and the one paint rule of the histogram bins.
/// The two objects keep their own characterisation tests; these pin the shared parts directly.
/// </summary>
public class VolumeProfileSegmentBuilderTests
{
    private static List<VolumeBin> Bins(CoreCandleData[] slice) => VolumeAnalysis.CalculateProfile(slice, 10);

    private static (decimal VAH, decimal VAL) ValueArea(List<VolumeBin> bins) => VolumeAnalysis.CalculateValueArea(bins);

    private static List<CoreCandleData> Candles(params DateTime[] days)
    {
        var list = new List<CoreCandleData>();
        for (int i = 0; i < days.Length; i++)
        {
            decimal price = 100m + i * 3m;
            list.Add(new CoreCandleData(days[i], price, price + 5m, price - 5m, price + 1m, 1000 + i));
        }

        return list;
    }

    // 2025-01-02 Thu, 03 Fri, 06 Mon, 07 Tue, 08 Wed, 13 Mon
    private static List<CoreCandleData> TwoAndAHalfWeeks() => Candles(
        new DateTime(2025, 1, 2), new DateTime(2025, 1, 3), new DateTime(2025, 1, 6),
        new DateTime(2025, 1, 7), new DateTime(2025, 1, 8), new DateTime(2025, 1, 13));

    [Fact]
    public void Build_RangeBar_MakesConsecutiveGroups_AndOnlyTheLastOneCanBePartial()
    {
        var candles = TwoAndAHalfWeeks();
        var segments = new List<VolumeProfileSegment>();

        var primary = VolumeProfileSegmentBuilder.Build(candles, 0, 4, VolumeProfileRepeatMode.RangeBar, s => Bins(s.ToArray()), ValueArea, segments);

        Assert.Equal(2, segments.Count);
        Assert.Equal((0, 4, false), (segments[0].StartIndex, segments[0].Count, segments[0].IsPartial));
        Assert.Equal((4, 2, true), (segments[1].StartIndex, segments[1].Count, segments[1].IsPartial));
        Assert.Equal(candles[0].Timestamp, segments[0].StartTime);
        Assert.Equal(candles[3].Timestamp, segments[0].EndTime);
        Assert.Same(segments[0].Bins, primary.Bins);
        Assert.Equal(segments[0].VAH, primary.Vah);
        Assert.Equal(segments[0].VAL, primary.Val);
    }

    [Fact]
    public void Build_RangeBar_StartingMidSeries_BeginsAtTheAnchorBar()
    {
        var candles = TwoAndAHalfWeeks();
        var segments = new List<VolumeProfileSegment>();

        var primary = VolumeProfileSegmentBuilder.Build(candles, 2, 2, VolumeProfileRepeatMode.RangeBar, s => Bins(s.ToArray()), ValueArea, segments);

        Assert.Equal(new[] { 2, 4 }, segments.Select(s => s.StartIndex).ToArray());
        Assert.Equal(new[] { 2, 2 }, segments.Select(s => s.Count).ToArray());
        Assert.False(segments[1].IsPartial);
        Assert.Same(segments[0].Bins, primary.Bins);
    }

    [Fact]
    public void Build_Weekly_SplitsAtTheWeekKey_AndMarksTheFirstMidWeekAndTheLastSegmentPartial()
    {
        var candles = TwoAndAHalfWeeks();
        var segments = new List<VolumeProfileSegment>();

        VolumeProfileSegmentBuilder.Build(candles, 0, 99, VolumeProfileRepeatMode.Weekly, s => Bins(s.ToArray()), ValueArea, segments);

        Assert.Equal(new[] { 0, 2, 5 }, segments.Select(s => s.StartIndex).ToArray());
        Assert.Equal(new[] { 2, 3, 1 }, segments.Select(s => s.Count).ToArray());
        // Thursday start (partial), full Monday-Wednesday week in the middle (not partial), the last segment reaches the last bar (partial).
        Assert.Equal(new[] { true, false, true }, segments.Select(s => s.IsPartial).ToArray());
    }

    [Fact]
    public void Build_Monthly_SplitsAtTheCalendarMonth_AndMarksTheFirstMidMonthAndTheLastSegmentPartial()
    {
        var candles = Candles(
            new DateTime(2025, 1, 30), new DateTime(2025, 1, 31), new DateTime(2025, 2, 3),
            new DateTime(2025, 2, 4), new DateTime(2025, 3, 3));
        var segments = new List<VolumeProfileSegment>();

        VolumeProfileSegmentBuilder.Build(candles, 0, 99, VolumeProfileRepeatMode.Monthly, s => Bins(s.ToArray()), ValueArea, segments);

        Assert.Equal(new[] { 0, 2, 4 }, segments.Select(s => s.StartIndex).ToArray());
        Assert.Equal(new[] { 2, 2, 1 }, segments.Select(s => s.Count).ToArray());
        // Starts on the 30th (partial); February is complete (starts on day 3 only because the data does, but it is not the anchor segment, so it is not partial); March reaches the last bar.
        Assert.Equal(new[] { true, false, true }, segments.Select(s => s.IsPartial).ToArray());
    }

    [Fact]
    public void Build_EverySegment_CarriesItsOwnPocVahVal_FromTheInjectedCalculations()
    {
        var candles = TwoAndAHalfWeeks();
        var segments = new List<VolumeProfileSegment>();
        int binCalls = 0;
        int vaCalls = 0;

        VolumeProfileSegmentBuilder.Build(
            candles, 0, 2, VolumeProfileRepeatMode.RangeBar,
            s => { binCalls++; return Bins(s.ToArray()); },
            b => { vaCalls++; return (VAH: 777m, VAL: 111m); },
            segments);

        Assert.Equal(3, binCalls);
        Assert.Equal(3, vaCalls);
        Assert.All(segments, seg =>
        {
            Assert.Equal(777m, seg.VAH);
            Assert.Equal(111m, seg.VAL);
            Assert.Equal(VolumeAnalysis.FindPointOfControl(seg.Bins)?.Price, seg.POC);
        });
    }

    [Fact]
    public void Build_DefaultMode_IsRefused()
    {
        var segments = new List<VolumeProfileSegment>();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VolumeProfileSegmentBuilder.Build(TwoAndAHalfWeeks(), 0, 2, VolumeProfileRepeatMode.Default, s => Bins(s.ToArray()), ValueArea, segments));
        Assert.Empty(segments);
    }

    [Theory]
    [InlineData(VolumeProfileRepeatMode.RangeBar)]
    [InlineData(VolumeProfileRepeatMode.Weekly)]
    [InlineData(VolumeProfileRepeatMode.Monthly)]
    public void BothDrawingTools_ProduceTheSameSegmentGeometry_ForTheSameCandles(VolumeProfileRepeatMode mode)
    {
        var candles = TwoAndAHalfWeeks();
        var first = candles[0].Timestamp;
        var last = candles[1].Timestamp;

        using var frvp = new FixedRangeVolumeProfileObject(new ChartPoint(first, 100m), new ChartPoint(last, 110m)) { RangeBars = 2, RepeatMode = mode };
        using var tap = new TimeAtPriceObject(new ChartPoint(first, 100m), new ChartPoint(last, 110m)) { RangeBars = 2, RepeatMode = mode };
        frvp.Recalculate(candles);
        tap.Recalculate(candles);

        Assert.NotEmpty(frvp.Segments);
        Assert.Equal(frvp.Segments.Count, tap.Segments.Count);
        for (int i = 0; i < frvp.Segments.Count; i++)
        {
            Assert.Equal(
                (frvp.Segments[i].StartIndex, frvp.Segments[i].Count, frvp.Segments[i].IsPartial, frvp.Segments[i].StartTime, frvp.Segments[i].EndTime),
                (tap.Segments[i].StartIndex, tap.Segments[i].Count, tap.Segments[i].IsPartial, tap.Segments[i].StartTime, tap.Segments[i].EndTime));
            Assert.Equal(VolumeProfileSegmentBuilder.DrawingRowCount, frvp.Segments[i].Bins.Count);
            Assert.Equal(VolumeProfileSegmentBuilder.DrawingRowCount, tap.Segments[i].Bins.Count);
        }

        // The anchor segment is the primary profile of each object.
        Assert.Same(frvp.Segments[0].Bins, frvp.ProfileData);
        Assert.Same(tap.Segments[0].Bins, tap.ProfileData);
        Assert.Equal(frvp.Segments[0].VAH, frvp.VAH);
        Assert.Equal(tap.Segments[0].VAL, tap.VAL);
    }

    [Fact]
    public void DrawingRowCount_IsFifty_AsTheLiteralBeforeIt()
    {
        Assert.Equal(50, VolumeProfileSegmentBuilder.DrawingRowCount);
    }

    // ---- the paint rule ----

    [Fact]
    public void PaintSelector_PocBeatsValueArea_ValueAreaBeatsBase()
    {
        using var poc = new SKPaint();
        using var va = new SKPaint();
        using var baseP = new SKPaint();

        Assert.Same(poc, ProfileBinPaintSelector.Select(true, true, poc, va, baseP));
        Assert.Same(poc, ProfileBinPaintSelector.Select(true, false, poc, va, baseP));
        Assert.Same(va, ProfileBinPaintSelector.Select(false, true, poc, va, baseP));
        Assert.Same(baseP, ProfileBinPaintSelector.Select(false, false, poc, va, baseP));
    }
}
