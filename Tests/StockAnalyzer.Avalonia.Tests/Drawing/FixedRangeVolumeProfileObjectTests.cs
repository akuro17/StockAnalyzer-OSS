using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using Xunit;
using Point = global::Avalonia.Point;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public class FixedRangeVolumeProfileObjectTests
{
    private static LinearCoordinateTransform MakeTransform()
        => new LinearCoordinateTransform(
            new DateTime(2025, 1, 1), new DateTime(2025, 1, 10),
            0m, 100m, 800, 600);

    private static List<CoreCandleData> MakeSampleCandles()
    {
        var list = new List<CoreCandleData>();
        // Jan 2 (Fri), Jan 5 (Mon), Jan 6 (Tue), Jan 7 (Wed), Jan 8 (Thu)
        var dates = new[]
        {
            new DateTime(2025, 1, 2),
            new DateTime(2025, 1, 5),
            new DateTime(2025, 1, 6),
            new DateTime(2025, 1, 7),
            new DateTime(2025, 1, 8)
        };

        for (int i = 0; i < dates.Length; i++)
        {
            decimal price = 50m + i * 5m;
            list.Add(new CoreCandleData(dates[i], price, price + 5m, price - 5m, price + 2m, 1000));
        }
        return list;
    }

    [Fact]
    public void FixedRangeVolumeProfileObject_Defaults()
    {
        var p1 = new ChartPoint(new DateTime(2025, 1, 2), 50m);
        var p2 = new ChartPoint(new DateTime(2025, 1, 5), 60m);
        using var obj = new FixedRangeVolumeProfileObject(p1, p2);

        Assert.Equal(ChartObjectType.FixedRangeVolumeProfile, obj.Type);
        Assert.Equal(DrawingThemeContext.DefaultColor, obj.Color);
        Assert.Equal(Color.Parse("#FFEB3B"), obj.ValueAreaColor);
        Assert.Equal(Color.Parse("#90A4AE"), obj.ProfileColor);
        Assert.Equal(DrawingThemeContext.DefaultColor, obj.FillColor);
        Assert.Equal(10, obj.FillOpacity);
        Assert.False(obj.LockRange);
        Assert.Equal(0, obj.RangeBars);
        Assert.Equal(VolumeProfileRepeatMode.Default, obj.RepeatMode);
        Assert.Empty(obj.Segments);

        // SkiaFillColor should reflect 10% opacity
        var expectedAlpha = (byte)Math.Clamp((int)(255 * (10 / 100.0)), 0, 255);
        Assert.Equal(expectedAlpha, obj.SkiaFillColor.Alpha);
    }

    [Fact]
    public void AdjustRightPointToBars_ExpandsOrContractsRightPoint()
    {
        var candles = MakeSampleCandles();
        var p1 = new ChartPoint(candles[0].Timestamp, 50m);
        var p2 = new ChartPoint(candles[1].Timestamp, 60m);
        using var obj = new FixedRangeVolumeProfileObject(p1, p2);

        // Adjust to 4 bars
        obj.AdjustRightPointToBars(4, candles);
        Assert.Equal(candles[0].Timestamp, obj.Points[0].Time);
        Assert.Equal(candles[3].Timestamp, obj.Points[1].Time);

        // Adjust to 2 bars
        obj.AdjustRightPointToBars(2, candles);
        Assert.Equal(candles[0].Timestamp, obj.Points[0].Time);
        Assert.Equal(candles[1].Timestamp, obj.Points[1].Time);
    }

    [Fact]
    public void Setting_RangeBars_PendingAdjustment_AppliedOnRecalculate()
    {
        var candles = MakeSampleCandles();
        var p1 = new ChartPoint(candles[0].Timestamp, 50m);
        var p2 = new ChartPoint(candles[1].Timestamp, 60m);
        using var obj = new FixedRangeVolumeProfileObject(p1, p2);

        obj.Recalculate(candles);
        Assert.Equal(2, obj.RangeBars);

        // Mutate RangeBars as if committed from settings dialog
        obj.RangeBars = 4;
        Assert.Equal(4, obj.RangeBars);

        // Recalculate applies pending adjustment and moves right point
        obj.Recalculate(candles);
        Assert.Equal(4, obj.RangeBars);
        Assert.Equal(candles[0].Timestamp, obj.Points[0].Time);
        Assert.Equal(candles[3].Timestamp, obj.Points[1].Time);
    }

    [Fact]
    public void FillOpacity_VaryingValues_CalculatesAccurateAlpha()
    {
        var p1 = new ChartPoint(new DateTime(2025, 1, 2), 50m);
        var p2 = new ChartPoint(new DateTime(2025, 1, 5), 60m);
        using var obj = new FixedRangeVolumeProfileObject(p1, p2);

        obj.FillOpacity = 0;
        Assert.Equal(0, obj.SkiaFillColor.Alpha);

        obj.FillOpacity = 100;
        Assert.Equal(255, obj.SkiaFillColor.Alpha);

        obj.FillOpacity = 50;
        Assert.Equal(127, obj.SkiaFillColor.Alpha);
    }

    [Fact]
    public void Recalculate_CalculatesProfileData_And_UpdatesRangeBars()
    {
        var candles = MakeSampleCandles();
        var p1 = new ChartPoint(candles[0].Timestamp, 50m);
        var p2 = new ChartPoint(candles[2].Timestamp, 60m); // Index 0 to 2 = 3 bars
        using var obj = new FixedRangeVolumeProfileObject(p1, p2);

        obj.Recalculate(candles);

        Assert.Equal(3, obj.RangeBars);
        Assert.NotEmpty(obj.ProfileData);
        Assert.True(obj.VAH >= obj.VAL);
    }

    [Fact]
    public void HitTest_FullPanelHeight_ReturnsTrueWithinXRange_RegardlessOfPriceY()
    {
        var t = MakeTransform();
        var p1 = new ChartPoint(new DateTime(2025, 1, 2), 50m);
        var p2 = new ChartPoint(new DateTime(2025, 1, 6), 60m);
        using var obj = new FixedRangeVolumeProfileObject(p1, p2);

        var p1Screen = t.ChartToScreen(p1);
        var p2Screen = t.ChartToScreen(p2);
        double midX = (p1Screen.X + p2Screen.X) / 2.0;

        // Near top of screen
        var topPoint = new Point(midX, 10);
        Assert.True(obj.HitTest(topPoint, t));

        // Near bottom of screen
        var bottomPoint = new Point(midX, 590);
        Assert.True(obj.HitTest(bottomPoint, t));

        // Way outside X range
        var outsidePoint = new Point(p2Screen.X + 50, 300);
        Assert.False(obj.HitTest(outsidePoint, t));

        // When locked, HitTest returns false
        obj.IsLocked = true;
        Assert.False(obj.HitTest(topPoint, t));
    }

    [Fact]
    public void TranslateBars_PreservesBarCount_AcrossWeekends()
    {
        var candles = MakeSampleCandles();
        // candles[0] = Jan 2 (Fri), candles[1] = Jan 5 (Mon) -> 2 bars
        var p1 = new ChartPoint(candles[0].Timestamp, 50m);
        var p2 = new ChartPoint(candles[1].Timestamp, 60m);
        using var obj = new FixedRangeVolumeProfileObject(p1, p2);

        // Shift by 1 bar: Points[0] should move to candles[1] (Jan 5 Mon), Points[1] to candles[2] (Jan 6 Tue)
        obj.TranslateBars(1, candles);

        Assert.Equal(candles[1].Timestamp, obj.Points[0].Time);
        Assert.Equal(candles[2].Timestamp, obj.Points[1].Time);

        // Shift by -1 bar: Points should restore to Jan 2 and Jan 5
        obj.TranslateBars(-1, candles);

        Assert.Equal(candles[0].Timestamp, obj.Points[0].Time);
        Assert.Equal(candles[1].Timestamp, obj.Points[1].Time);
    }

    [Fact]
    public void TranslateBars_ClampsAtBoundaries()
    {
        var candles = MakeSampleCandles();
        var p1 = new ChartPoint(candles[0].Timestamp, 50m);
        var p2 = new ChartPoint(candles[1].Timestamp, 60m);
        using var obj = new FixedRangeVolumeProfileObject(p1, p2);

        // Try shifting left when already at index 0 -> clamped to 0
        obj.TranslateBars(-5, candles);
        Assert.Equal(candles[0].Timestamp, obj.Points[0].Time);
        Assert.Equal(candles[1].Timestamp, obj.Points[1].Time);

        // Shift to end (count = 5, maxIdx = 1, available = 5 - 1 - 1 = 3)
        obj.TranslateBars(100, candles);
        Assert.Equal(candles[3].Timestamp, obj.Points[0].Time);
        Assert.Equal(candles[4].Timestamp, obj.Points[1].Time);
    }

    [Fact]
    public void Dispose_CanBeCalledWithoutThrow()
    {
        var p1 = new ChartPoint(new DateTime(2025, 1, 2), 50m);
        var p2 = new ChartPoint(new DateTime(2025, 1, 6), 60m);
        var obj = new FixedRangeVolumeProfileObject(p1, p2);

        obj.Dispose();
        // Should not throw on second dispose
        obj.Dispose();
    }

    [Fact]
    public void RepeatMode_Property_CanBeMutated()
    {
        var p1 = new ChartPoint(new DateTime(2025, 1, 2), 50m);
        var p2 = new ChartPoint(new DateTime(2025, 1, 5), 60m);
        using var obj = new FixedRangeVolumeProfileObject(p1, p2);

        Assert.Equal(VolumeProfileRepeatMode.Default, obj.RepeatMode);
        obj.RepeatMode = VolumeProfileRepeatMode.RangeBar;
        Assert.Equal(VolumeProfileRepeatMode.RangeBar, obj.RepeatMode);
    }

    [Fact]
    public void Recalculate_DefaultMode_PopulatesSingleSegment()
    {
        var candles = MakeSampleCandles();
        var p1 = new ChartPoint(candles[0].Timestamp, 50m);
        var p2 = new ChartPoint(candles[2].Timestamp, 60m);
        using var obj = new FixedRangeVolumeProfileObject(p1, p2);

        obj.RepeatMode = VolumeProfileRepeatMode.Default;
        obj.Recalculate(candles);

        Assert.Single(obj.Segments);
        var seg = obj.Segments[0];
        Assert.Equal(3, seg.Count);
        Assert.Equal(candles[0].Timestamp, seg.StartTime);
        Assert.Equal(candles[2].Timestamp, seg.EndTime);
        Assert.False(seg.IsPartial);
        Assert.Equal(obj.VAH, seg.VAH);
        Assert.Equal(obj.VAL, seg.VAL);
        Assert.Equal(obj.ProfileData.Count, seg.Bins.Count);
    }

    [Fact]
    public void Recalculate_RangeBarMode_GeneratesMultipleSegments()
    {
        var candles = MakeSampleCandles(); // 5 candles: Jan 2, 5, 6, 7, 8
        var p1 = new ChartPoint(candles[0].Timestamp, 50m);
        var p2 = new ChartPoint(candles[1].Timestamp, 60m);
        using var obj = new FixedRangeVolumeProfileObject(p1, p2);

        // Set RangeBars = 2, RepeatMode = RangeBar
        obj.RangeBars = 2;
        obj.RepeatMode = VolumeProfileRepeatMode.RangeBar;
        obj.Recalculate(candles);

        // 5 candles with interval = 2:
        // Seg 0: idx 0..1 (count = 2, IsPartial = false)
        // Seg 1: idx 2..3 (count = 2, IsPartial = false)
        // Seg 2: idx 4..4 (count = 1, IsPartial = true)
        Assert.Equal(3, obj.Segments.Count);

        Assert.Equal(2, obj.Segments[0].Count);
        Assert.False(obj.Segments[0].IsPartial);
        Assert.Equal(candles[0].Timestamp, obj.Segments[0].StartTime);
        Assert.Equal(candles[1].Timestamp, obj.Segments[0].EndTime);
        Assert.NotNull(obj.Segments[0].POC);

        Assert.Equal(2, obj.Segments[1].Count);
        Assert.False(obj.Segments[1].IsPartial);
        Assert.Equal(candles[2].Timestamp, obj.Segments[1].StartTime);
        Assert.Equal(candles[3].Timestamp, obj.Segments[1].EndTime);
        Assert.NotNull(obj.Segments[1].POC);

        Assert.Equal(1, obj.Segments[2].Count);
        Assert.True(obj.Segments[2].IsPartial);
        Assert.Equal(candles[4].Timestamp, obj.Segments[2].StartTime);
        Assert.Equal(candles[4].Timestamp, obj.Segments[2].EndTime);
        Assert.NotNull(obj.Segments[2].POC);

        // Primary anchor segment values should match ProfileData and VAH/VAL
        Assert.Equal(obj.VAH, obj.Segments[0].VAH);
        Assert.Equal(obj.VAL, obj.Segments[0].VAL);
    }

    [Fact]
    public void HitTest_RangeBarMode_HitsRepeatedSegment()
    {
        var t = MakeTransform();
        var candles = MakeSampleCandles();
        var p1 = new ChartPoint(candles[0].Timestamp, 50m);
        var p2 = new ChartPoint(candles[1].Timestamp, 60m);
        using var obj = new FixedRangeVolumeProfileObject(p1, p2);

        obj.RangeBars = 2;
        obj.RepeatMode = VolumeProfileRepeatMode.RangeBar;
        obj.Recalculate(candles);

        // Segment 1 is at candles[2] and candles[3] (Jan 6 and Jan 7)
        var seg1Screen = t.ChartToScreen(new ChartPoint(candles[2].Timestamp, 55m));
        var testPoint = new Point(seg1Screen.X + 5, 200);

        Assert.True(obj.HitTest(testPoint, t));
    }

    [Fact]
    public void GetCalculatedValues_RangeBarMode_ReturnsMatchingSegmentValues()
    {
        var candles = MakeSampleCandles();
        var p1 = new ChartPoint(candles[0].Timestamp, 50m);
        var p2 = new ChartPoint(candles[1].Timestamp, 60m);
        using var obj = new FixedRangeVolumeProfileObject(p1, p2);

        obj.RangeBars = 2;
        obj.RepeatMode = VolumeProfileRepeatMode.RangeBar;
        obj.Recalculate(candles);

        // Timestamp in Segment 1 (Jan 6)
        var values = obj.GetCalculatedValues(candles[2].Timestamp);
        Assert.Equal(3, values.Count);
        Assert.Equal("POC", values[0].Key);
        Assert.Equal(obj.Segments[1].POC, values[0].NumericValue);
        Assert.Equal("VAH", values[1].Key);
        Assert.Equal(obj.Segments[1].VAH, values[1].NumericValue);
        Assert.Equal("VAL", values[2].Key);
        Assert.Equal(obj.Segments[1].VAL, values[2].NumericValue);
    }

    [Fact]
    public void Render_RangeBarMode_ExecutesCleanly()
    {
        var t = MakeTransform();
        var candles = MakeSampleCandles();
        var p1 = new ChartPoint(candles[0].Timestamp, 50m);
        var p2 = new ChartPoint(candles[1].Timestamp, 60m);
        using var obj = new FixedRangeVolumeProfileObject(p1, p2);

        obj.RangeBars = 2;
        obj.RepeatMode = VolumeProfileRepeatMode.RangeBar;
        obj.Recalculate(candles);

        using var bitmap = new SKBitmap(800, 600);
        using var canvas = new SKCanvas(bitmap);

        // Should render without exception
        obj.Render(canvas, t);
    }

    [Fact]
    public void Recalculate_WeeklyMode_GeneratesWeeklySegments()
    {
        var dates = new[]
        {
            new DateTime(2025, 1, 2),  // Thu, Week 1
            new DateTime(2025, 1, 3),  // Fri, Week 1
            new DateTime(2025, 1, 6),  // Mon, Week 2
            new DateTime(2025, 1, 7),  // Tue, Week 2
            new DateTime(2025, 1, 8),  // Wed, Week 2
            new DateTime(2025, 1, 13)  // Mon, Week 3
        };
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < dates.Length; i++)
        {
            decimal price = 100m + i * 5m;
            candles.Add(new CoreCandleData(dates[i], price, price + 5m, price - 5m, price + 2m, 1000));
        }

        var p1 = new ChartPoint(dates[0], 100m);
        var p2 = new ChartPoint(dates[1], 105m);
        using var obj = new FixedRangeVolumeProfileObject(p1, p2);

        obj.RepeatMode = VolumeProfileRepeatMode.Weekly;
        obj.Recalculate(candles);

        Assert.Equal(3, obj.Segments.Count);

        // Segment 0: Week 1 (Jan 2, Jan 3)
        Assert.Equal(2, obj.Segments[0].Count);
        Assert.Equal(dates[0], obj.Segments[0].StartTime);
        Assert.Equal(dates[1], obj.Segments[0].EndTime);
        Assert.True(obj.Segments[0].IsPartial); // Starts on Thursday
        Assert.NotNull(obj.Segments[0].POC);

        // Segment 1: Week 2 (Jan 6, Jan 7, Jan 8)
        Assert.Equal(3, obj.Segments[1].Count);
        Assert.Equal(dates[2], obj.Segments[1].StartTime);
        Assert.Equal(dates[4], obj.Segments[1].EndTime);
        Assert.False(obj.Segments[1].IsPartial);
        Assert.NotNull(obj.Segments[1].POC);

        // Segment 2: Week 3 (Jan 13)
        Assert.Equal(1, obj.Segments[2].Count);
        Assert.Equal(dates[5], obj.Segments[2].StartTime);
        Assert.Equal(dates[5], obj.Segments[2].EndTime);
        Assert.True(obj.Segments[2].IsPartial); // Ends at totalCandles
        Assert.NotNull(obj.Segments[2].POC);

        // Primary anchor segment values should match ProfileData and VAH/VAL
        Assert.Equal(obj.VAH, obj.Segments[0].VAH);
        Assert.Equal(obj.VAL, obj.Segments[0].VAL);
    }

    [Fact]
    public void Recalculate_MonthlyMode_GeneratesMonthlySegments()
    {
        var dates = new[]
        {
            new DateTime(2025, 1, 15), // Jan (starts on 15th)
            new DateTime(2025, 1, 31), // Jan
            new DateTime(2025, 2, 3),  // Feb
            new DateTime(2025, 2, 14), // Feb
            new DateTime(2025, 3, 3)   // Mar
        };
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < dates.Length; i++)
        {
            decimal price = 100m + i * 5m;
            candles.Add(new CoreCandleData(dates[i], price, price + 5m, price - 5m, price + 2m, 1000));
        }

        var p1 = new ChartPoint(dates[0], 100m);
        var p2 = new ChartPoint(dates[1], 105m);
        using var obj = new FixedRangeVolumeProfileObject(p1, p2);

        obj.RepeatMode = VolumeProfileRepeatMode.Monthly;
        obj.Recalculate(candles);

        Assert.Equal(3, obj.Segments.Count);

        // Segment 0: Jan (Jan 15, Jan 31)
        Assert.Equal(2, obj.Segments[0].Count);
        Assert.Equal(dates[0], obj.Segments[0].StartTime);
        Assert.Equal(dates[1], obj.Segments[0].EndTime);
        Assert.True(obj.Segments[0].IsPartial); // Starts on 15th
        Assert.NotNull(obj.Segments[0].POC);

        // Segment 1: Feb (Feb 3, Feb 14)
        Assert.Equal(2, obj.Segments[1].Count);
        Assert.Equal(dates[2], obj.Segments[1].StartTime);
        Assert.Equal(dates[3], obj.Segments[1].EndTime);
        Assert.False(obj.Segments[1].IsPartial);
        Assert.NotNull(obj.Segments[1].POC);

        // Segment 2: Mar (Mar 3)
        Assert.Equal(1, obj.Segments[2].Count);
        Assert.Equal(dates[4], obj.Segments[2].StartTime);
        Assert.Equal(dates[4], obj.Segments[2].EndTime);
        Assert.True(obj.Segments[2].IsPartial); // Ends at totalCandles
        Assert.NotNull(obj.Segments[2].POC);

        // Primary anchor segment values should match ProfileData and VAH/VAL
        Assert.Equal(obj.VAH, obj.Segments[0].VAH);
        Assert.Equal(obj.VAL, obj.Segments[0].VAL);
    }

    [Fact]
    public void HitTest_WeeklyAndMonthlyMode_HitsRepeatedSegment()
    {
        var t = new LinearCoordinateTransform(
            new DateTime(2025, 1, 1), new DateTime(2025, 3, 31),
            0m, 200m, 800, 600);

        var dates = new[]
        {
            new DateTime(2025, 1, 2),
            new DateTime(2025, 1, 3),
            new DateTime(2025, 1, 6),
            new DateTime(2025, 1, 7),
            new DateTime(2025, 1, 13)
        };
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < dates.Length; i++)
        {
            decimal price = 100m + i * 5m;
            candles.Add(new CoreCandleData(dates[i], price, price + 5m, price - 5m, price + 2m, 1000));
        }

        var p1 = new ChartPoint(dates[0], 100m);
        var p2 = new ChartPoint(dates[1], 105m);
        using var obj = new FixedRangeVolumeProfileObject(p1, p2);

        obj.RepeatMode = VolumeProfileRepeatMode.Weekly;
        obj.Recalculate(candles);

        // Segment 1 is at Jan 6..Jan 7
        var seg1Screen = t.ChartToScreen(new ChartPoint(dates[2], 100m));
        var testPoint = new Point(seg1Screen.X + 5, 300);
        Assert.True(obj.HitTest(testPoint, t));

        // Test Monthly mode
        obj.RepeatMode = VolumeProfileRepeatMode.Monthly;
        obj.Recalculate(candles);
        Assert.True(obj.HitTest(testPoint, t));
    }

    [Fact]
    public void GetCalculatedValues_WeeklyAndMonthlyMode_ReturnsMatchingSegmentValues()
    {
        var dates = new[]
        {
            new DateTime(2025, 1, 2),
            new DateTime(2025, 1, 3),
            new DateTime(2025, 1, 6),
            new DateTime(2025, 1, 7),
            new DateTime(2025, 1, 13)
        };
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < dates.Length; i++)
        {
            decimal price = 100m + i * 5m;
            candles.Add(new CoreCandleData(dates[i], price, price + 5m, price - 5m, price + 2m, 1000));
        }

        var p1 = new ChartPoint(dates[0], 100m);
        var p2 = new ChartPoint(dates[1], 105m);
        using var obj = new FixedRangeVolumeProfileObject(p1, p2);

        obj.RepeatMode = VolumeProfileRepeatMode.Weekly;
        obj.Recalculate(candles);

        // Timestamp in Segment 1 (Jan 6)
        var values = obj.GetCalculatedValues(dates[2]);
        Assert.Equal(3, values.Count);
        Assert.Equal("POC", values[0].Key);
        Assert.Equal(obj.Segments[1].POC, values[0].NumericValue);
        Assert.Equal("VAH", values[1].Key);
        Assert.Equal(obj.Segments[1].VAH, values[1].NumericValue);
        Assert.Equal("VAL", values[2].Key);
        Assert.Equal(obj.Segments[1].VAL, values[2].NumericValue);
    }

    [Fact]
    public void Render_WeeklyAndMonthlyMode_ExecutesCleanly()
    {
        var t = new LinearCoordinateTransform(
            new DateTime(2025, 1, 1), new DateTime(2025, 2, 1),
            0m, 200m, 800, 600);

        var dates = new[]
        {
            new DateTime(2025, 1, 2),
            new DateTime(2025, 1, 3),
            new DateTime(2025, 1, 6),
            new DateTime(2025, 1, 7),
            new DateTime(2025, 1, 13)
        };
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < dates.Length; i++)
        {
            decimal price = 100m + i * 5m;
            candles.Add(new CoreCandleData(dates[i], price, price + 5m, price - 5m, price + 2m, 1000));
        }

        var p1 = new ChartPoint(dates[0], 100m);
        var p2 = new ChartPoint(dates[1], 105m);
        using var obj = new FixedRangeVolumeProfileObject(p1, p2);

        using var bitmap = new SKBitmap(800, 600);
        using var canvas = new SKCanvas(bitmap);

        // Weekly
        obj.RepeatMode = VolumeProfileRepeatMode.Weekly;
        obj.Recalculate(candles);
        obj.Render(canvas, t);

        // Monthly
        obj.RepeatMode = VolumeProfileRepeatMode.Monthly;
        obj.Recalculate(candles);
        obj.Render(canvas, t);
    }

    [Fact]
    public void GetCalculatedValues_WeekendTimestamp_ResolvesPrecedingSegmentPOC()
    {
        // dates[0] = Friday Jan 3, dates[1] = Monday Jan 6
        var dates = new[]
        {
            new DateTime(2025, 1, 3, 15, 0, 0),
            new DateTime(2025, 1, 6, 9, 0, 0)
        };
        var candles = new List<CoreCandleData>
        {
            new CoreCandleData(dates[0], 100m, 105m, 95m, 102m, 1000),
            new CoreCandleData(dates[1], 110m, 115m, 105m, 112m, 2000)
        };

        var p1 = new ChartPoint(dates[0], 100m);
        var p2 = new ChartPoint(dates[1], 110m);
        using var obj = new FixedRangeVolumeProfileObject(p1, p2);

        obj.RepeatMode = VolumeProfileRepeatMode.Weekly;
        obj.Recalculate(candles);

        Assert.Equal(2, obj.Segments.Count);

        // Saturday Jan 4 falls on the weekend after Segment 0 (Jan 3) and before Segment 1 (Jan 6)
        var weekendTime = new DateTime(2025, 1, 4, 12, 0, 0);
        var values = obj.GetCalculatedValues(weekendTime);

        Assert.NotEmpty(values);
        Assert.Equal("POC", values[0].Key);
        Assert.Equal(obj.Segments[0].POC, values[0].NumericValue);
    }

    [Fact]
    public void HitTest_LastSegmentVisualBoundary_ReturnsTrue()
    {
        var t = new LinearCoordinateTransform(
            new DateTime(2025, 1, 1), new DateTime(2025, 1, 20),
            0m, 200m, 800, 600);

        var dates = new[]
        {
            new DateTime(2025, 1, 2),
            new DateTime(2025, 1, 3),
            new DateTime(2025, 1, 6),
            new DateTime(2025, 1, 7)
        };
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < dates.Length; i++)
        {
            candles.Add(new CoreCandleData(dates[i], 100m, 105m, 95m, 100m, 1000));
        }

        var p1 = new ChartPoint(dates[0], 100m);
        var p2 = new ChartPoint(dates[1], 105m);
        using var obj = new FixedRangeVolumeProfileObject(p1, p2);

        obj.RepeatMode = VolumeProfileRepeatMode.Weekly;
        obj.Recalculate(candles);

        // In Weekly mode, last segment is at Jan 6..Jan 7.
        // The visual right edge extends past Jan 7 EndTime.
        var endScreenX = t.ChartToScreen(new ChartPoint(dates[3], 100m)).X;
        var hitPointNearEnd = new Point(endScreenX + 3, 300);
        Assert.True(obj.HitTest(hitPointNearEnd, t));
    }
}
