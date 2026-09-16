using System;
using System.Collections.Generic;
using System.Threading;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

public class VolumeProfileViewportTests
{
    private static CoreCandleData CreateCandle(
        DateTime timestamp,
        decimal open,
        decimal high,
        decimal low,
        decimal close,
        long volume)
    {
        return new CoreCandleData(timestamp, open, high, low, close, volume);
    }

    #region VolumeProfileRangeBuilder Tests

    [Fact]
    public void BuildRanges_NullDestination_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            VolumeProfileRangeBuilder.Build(
                Array.Empty<CoreCandleData>(),
                0,
                10,
                50,
                null!));
    }

    [Fact]
    public void BuildRanges_EmptyOrZeroVisible_ReturnsEmpty()
    {
        var dest = new List<VolumeProfileRange>();
        VolumeProfileRangeBuilder.Build(
            Array.Empty<CoreCandleData>(),
            0,
            0,
            50,
            dest);

        Assert.Empty(dest);
    }

    [Fact]
    public void BuildRanges_TakesSpecifiedPeriod_FromVisibleEnd()
    {
        // Anchor reversal (2026-09-16, user-approved): Period now counts backward from the end of the
        // visible range. N=100, visible[20,60), Period=7 => Single segment [53,60), count 7.
        var candles = new List<CoreCandleData>();
        var baseDate = new DateTime(2026, 1, 1);
        for (int i = 0; i < 100; i++)
        {
            candles.Add(CreateCandle(baseDate.AddDays(i), 10m, 15m, 8m, 12m, 1000));
        }

        var dest = new List<VolumeProfileRange>();
        VolumeProfileRangeBuilder.Build(
            candles,
            20,
            40,
            7,
            dest);

        Assert.Single(dest);
        Assert.Equal(53, dest[0].StartIndex);
        Assert.Equal(7, dest[0].Count);
        Assert.False(dest[0].IsPartial);
    }

    [Fact]
    public void BuildRanges_PeriodZero_TakesEntireVisibleRange()
    {
        // N=100, visibleStart=20, visibleCount=40, Period=0 => Single segment of count 40
        var candles = new List<CoreCandleData>();
        var baseDate = new DateTime(2026, 1, 1);
        for (int i = 0; i < 100; i++)
        {
            candles.Add(CreateCandle(baseDate.AddDays(i), 10m, 15m, 8m, 12m, 1000));
        }

        var dest = new List<VolumeProfileRange>();
        VolumeProfileRangeBuilder.Build(
            candles,
            20,
            40,
            0,
            dest);

        Assert.Single(dest);
        Assert.Equal(20, dest[0].StartIndex);
        Assert.Equal(40, dest[0].Count);
        Assert.False(dest[0].IsPartial);
    }

    [Fact]
    public void BuildRanges_PeriodExceedingVisibleRange_ExtendsBeforeVisibleStart()
    {
        // Anchor reversal (2026-09-16): Period may now extend BEFORE visibleStart (into off-screen
        // older history) when it exceeds the visible window, mirroring the old design's symmetric
        // allowance to extend past the visible end. visibleStart=80,visibleCount=30,Period=50,N=100:
        // visibleEnd=min(100,110)=100, count=min(50,100)=50, startIndex=100-50=50.
        var candles = new List<CoreCandleData>();
        var baseDate = new DateTime(2026, 1, 1);
        for (int i = 0; i < 100; i++)
        {
            candles.Add(CreateCandle(baseDate.AddDays(i), 10m, 15m, 8m, 12m, 1000));
        }

        var dest = new List<VolumeProfileRange>();
        VolumeProfileRangeBuilder.Build(
            candles,
            80,
            30,
            50,
            dest);

        Assert.Single(dest);
        Assert.Equal(50, dest[0].StartIndex);
        Assert.Equal(50, dest[0].Count);
    }

    [Fact]
    public void BuildRanges_VisibleStartPastEnd_ReturnsEmpty()
    {
        var candles = new List<CoreCandleData>();
        var baseDate = new DateTime(2026, 1, 1);
        for (int i = 0; i < 50; i++)
        {
            candles.Add(CreateCandle(baseDate.AddDays(i), 10m, 15m, 8m, 12m, 1000));
        }

        var dest = new List<VolumeProfileRange>();
        VolumeProfileRangeBuilder.Build(
            candles,
            60,
            20,
            10,
            dest);

        Assert.Empty(dest);
    }

    [Fact]
    public void BuildRanges_NegativeVisibleStart_ClampsToZero()
    {
        // Anchor reversal (2026-09-16): visibleStart clamps to 0 as before, but Period now counts
        // backward from visibleEnd. visibleStart=-10=>0,visibleCount=20,Period=15,N=50:
        // visibleEnd=min(50,20)=20, count=min(15,20)=15, startIndex=20-15=5.
        var candles = new List<CoreCandleData>();
        var baseDate = new DateTime(2026, 1, 1);
        for (int i = 0; i < 50; i++)
        {
            candles.Add(CreateCandle(baseDate.AddDays(i), 10m, 15m, 8m, 12m, 1000));
        }

        var dest = new List<VolumeProfileRange>();
        VolumeProfileRangeBuilder.Build(
            candles,
            -10,
            20,
            15,
            dest);

        Assert.Single(dest);
        Assert.Equal(5, dest[0].StartIndex);
        Assert.Equal(15, dest[0].Count);
    }

    #endregion

    #region VolumeProfileViewportCalculator Tests

    [Fact]
    public void Calculate_EmptyCandles_ReturnsEmptyResult()
    {
        // V01
        var param = new CoreVolumeProfileParameter();
        var result = VolumeProfileViewportCalculator.Calculate(
            "key1",
            Array.Empty<CoreCandleData>(),
            0,
            0,
            TimeframeType.Daily,
            param);

        Assert.Equal(VolumeProfileResultStatus.Empty, result.Status);
        Assert.Empty(result.Segments);
    }

    [Fact]
    public void Calculate_SingleSegment_CalculatesBinsAndValueArea()
    {
        var candles = new List<CoreCandleData>();
        var cur = new DateTime(2026, 1, 5);
        for (int i = 0; i < 20; i++)
        {
            // Range 10 to 30
            candles.Add(CreateCandle(cur.AddDays(i), 10m, 30m, 10m, 20m, 1000));
        }

        var param = new CoreVolumeProfileParameter
        {
            Period = 15,
            RowCount = 10
        };

        var result = VolumeProfileViewportCalculator.Calculate(
            "test_req",
            candles,
            5,
            15,
            TimeframeType.Daily,
            param);

        Assert.Equal(VolumeProfileResultStatus.Success, result.Status);
        Assert.Single(result.Segments);

        var seg = result.Segments[0];
        Assert.Equal(5, seg.StartIndex);
        Assert.Equal(15, seg.Count);
        Assert.NotEmpty(seg.Bins);
        Assert.True(seg.POC.HasValue);
        Assert.True(seg.VAH.HasValue);
        Assert.True(seg.VAL.HasValue);
        Assert.True(seg.VAH >= seg.VAL);
    }

    [Fact]
    public void Calculate_Cancellation_ThrowsOperationCanceledException()
    {
        // V19: Cancellation terminates calculation
        var candles = new List<CoreCandleData>();
        var cur = new DateTime(2026, 1, 1);
        for (int i = 0; i < 50; i++)
        {
            candles.Add(CreateCandle(cur.AddDays(i), 10m, 20m, 5m, 15m, 1000));
        }

        var param = new CoreVolumeProfileParameter();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            VolumeProfileViewportCalculator.Calculate(
                "cancelled_req",
                candles,
                0,
                candles.Count,
                TimeframeType.Daily,
                param,
                cts.Token));
    }

    [Fact]
    public void Calculate_ExtremeValues_DoesNotOverflow()
    {
        // V27: Extreme decimal & large long volume
        var candles = new List<CoreCandleData>
        {
            CreateCandle(new DateTime(2026, 1, 1), 999990m, 1000000m, 999980m, 999995m, long.MaxValue / 4),
            CreateCandle(new DateTime(2026, 1, 2), 999995m, 1000010m, 999990m, 1000000m, long.MaxValue / 4)
        };

        var param = new CoreVolumeProfileParameter
        {
            RowCount = 50
        };

        var result = VolumeProfileViewportCalculator.Calculate(
            "extreme_req",
            candles,
            0,
            candles.Count,
            TimeframeType.Daily,
            param);

        Assert.Equal(VolumeProfileResultStatus.Success, result.Status);
        Assert.Single(result.Segments);
        Assert.NotEmpty(result.Segments[0].Bins);
    }

    #endregion

    #region F12: Input Validation Tests (sa_analysis_report_VolumeProfile_PostImplementation_StrictReview_20260914.md F12/A17)

    [Fact]
    public void Calculate_NegativeVolumeCandle_ThrowsInvalidDataException()
    {
        // V02 fix (sa_analysis_VolumeProfile_V02_V09-V12_Resolution_20260916.md): unified with the
        // OverflowException pathway -- invalid input data is now signaled via exception, not a
        // Failed-status return value, matching the original plan's InvalidDataException contract.
        var candles = new List<CoreCandleData>
        {
            CreateCandle(new DateTime(2026, 1, 1), 10m, 15m, 8m, 12m, 1000),
            CreateCandle(new DateTime(2026, 1, 2), 10m, 15m, 8m, 12m, -500), // negative volume
            CreateCandle(new DateTime(2026, 1, 3), 10m, 15m, 8m, 12m, 1000)
        };
        var param = new CoreVolumeProfileParameter { RowCount = 10 };

        Assert.Throws<System.IO.InvalidDataException>(() => VolumeProfileViewportCalculator.Calculate(
            "req", candles, 0, candles.Count, TimeframeType.Daily, param));
    }

    [Fact]
    public void Calculate_HighLessThanLowCandle_ThrowsInvalidDataException()
    {
        var candles = new List<CoreCandleData>
        {
            CreateCandle(new DateTime(2026, 1, 1), 10m, 15m, 8m, 12m, 1000),
            CreateCandle(new DateTime(2026, 1, 2), 10m, 5m, 9m, 12m, 1000), // High(5) < Low(9)
            CreateCandle(new DateTime(2026, 1, 3), 10m, 15m, 8m, 12m, 1000)
        };
        var param = new CoreVolumeProfileParameter { RowCount = 10 };

        Assert.Throws<System.IO.InvalidDataException>(() => VolumeProfileViewportCalculator.Calculate(
            "req", candles, 0, candles.Count, TimeframeType.Daily, param));
    }

    [Fact]
    public void Calculate_OutOfOrderTimestamps_ThrowsInvalidDataException()
    {
        var candles = new List<CoreCandleData>
        {
            CreateCandle(new DateTime(2026, 1, 3), 10m, 15m, 8m, 12m, 1000),
            CreateCandle(new DateTime(2026, 1, 1), 10m, 15m, 8m, 12m, 1000), // reversed order
            CreateCandle(new DateTime(2026, 1, 2), 10m, 15m, 8m, 12m, 1000)
        };
        var param = new CoreVolumeProfileParameter { RowCount = 10 };

        Assert.Throws<System.IO.InvalidDataException>(() => VolumeProfileViewportCalculator.Calculate(
            "req", candles, 0, candles.Count, TimeframeType.Daily, param));
    }

    [Fact]
    public void Calculate_DuplicateTimestamps_ThrowsInvalidDataException()
    {
        var duplicated = new DateTime(2026, 1, 2);
        var candles = new List<CoreCandleData>
        {
            CreateCandle(new DateTime(2026, 1, 1), 10m, 15m, 8m, 12m, 1000),
            CreateCandle(duplicated, 10m, 15m, 8m, 12m, 1000),
            CreateCandle(duplicated, 10m, 15m, 8m, 12m, 1000) // duplicate timestamp
        };
        var param = new CoreVolumeProfileParameter { RowCount = 10 };

        Assert.Throws<System.IO.InvalidDataException>(() => VolumeProfileViewportCalculator.Calculate(
            "req", candles, 0, candles.Count, TimeframeType.Daily, param));
    }

    [Fact]
    public void Calculate_NullCandleElement_ThrowsInvalidDataException()
    {
        var candles = new List<CoreCandleData>
        {
            CreateCandle(new DateTime(2026, 1, 1), 10m, 15m, 8m, 12m, 1000),
            null!,
            CreateCandle(new DateTime(2026, 1, 3), 10m, 15m, 8m, 12m, 1000)
        };
        var param = new CoreVolumeProfileParameter { RowCount = 10 };

        Assert.Throws<System.IO.InvalidDataException>(() => VolumeProfileViewportCalculator.Calculate(
            "req", candles, 0, candles.Count, TimeframeType.Daily, param));
    }

    [Fact]
    public void Calculate_AllZeroVolumeCandles_YieldsBinsWithNullPocAndValueArea()
    {
        // F12/A17: total volume 0 across the whole window must not present an arbitrary bin as a real
        // POC/VAH/VAL statistic.
        var candles = new List<CoreCandleData>();
        var cur = new DateTime(2026, 1, 1);
        for (int i = 0; i < 10; i++)
        {
            candles.Add(CreateCandle(cur.AddDays(i), 10m, 15m, 8m, 12m, 0));
        }
        var param = new CoreVolumeProfileParameter { RowCount = 10 };

        var result = VolumeProfileViewportCalculator.Calculate(
            "req", candles, 0, candles.Count, TimeframeType.Daily, param);

        Assert.Equal(VolumeProfileResultStatus.Success, result.Status);
        Assert.Single(result.Segments);
        var seg = result.Segments[0];
        Assert.NotEmpty(seg.Bins);
        Assert.All(seg.Bins, b => Assert.Equal(0, b.TotalVolume));
        Assert.False(seg.POC.HasValue);
        Assert.False(seg.VAH.HasValue);
        Assert.False(seg.VAL.HasValue);
    }

    [Fact]
    public void Calculate_FullModeVolumeAccumulationOverflows_ThrowsOverflowException()
    {
        // F12: `checked` around VolumeAnalysis.CalculateProfile's Full-mode bin accumulation must turn
        // a long overflow into an explicit exception (which the coordinator's own catch-all already
        // maps to a Failed state) instead of silently wrapping to a corrupted negative TotalVolume.
        var candles = new List<CoreCandleData>();
        var cur = new DateTime(2026, 1, 1);
        // Same tight price range on every candle so all of them land in the single bin created by a
        // RowCount of 1 -- Full mode adds the FULL volume to every touched bin per candle, so three
        // candles at ~long.MaxValue/2 each overflow that one bin's TotalVolume on the third addition.
        for (int i = 0; i < 3; i++)
        {
            candles.Add(CreateCandle(cur.AddDays(i), 10m, 11m, 10m, 10.5m, long.MaxValue / 2));
        }
        var param = new CoreVolumeProfileParameter { RowCount = 1, Mode = VolumeDistributionMode.Full };

        Assert.Throws<OverflowException>(() =>
            VolumeProfileViewportCalculator.Calculate(
                "req", candles, 0, candles.Count, TimeframeType.Daily, param));
    }

    #endregion
}
