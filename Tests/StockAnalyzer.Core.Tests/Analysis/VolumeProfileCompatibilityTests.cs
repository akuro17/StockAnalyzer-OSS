using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

/// <summary>
/// Characterization and compatibility tests for <see cref="VolumeAnalysis"/>.
/// Locks in existing behavior, mathematical edge cases, and boundary conditions
/// to prevent unintended regressions during feature implementation.
/// </summary>
public class VolumeProfileCompatibilityTests
{
    private static CoreCandleData CreateCandle(
        decimal open,
        decimal high,
        decimal low,
        decimal close,
        long volume,
        DateTime? timestamp = null)
    {
        return new CoreCandleData(
            timestamp ?? new DateTime(2026, 1, 1),
            open,
            high,
            low,
            close,
            volume
        );
    }

    #region CalculateProfile Tests

    [Fact]
    public void CalculateProfile_EmptyCandles_ReturnsEmptyList()
    {
        var result = VolumeAnalysis.CalculateProfile(Array.Empty<CoreCandleData>());
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void CalculateProfile_NullCandles_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => VolumeAnalysis.CalculateProfile(null!));
    }

    [Fact]
    public void CalculateProfile_SingleCandle_HighEqualsLow_ReturnsEmptyList()
    {
        var candle = CreateCandle(10m, 10m, 10m, 10m, 1000);
        var result = VolumeAnalysis.CalculateProfile(new[] { candle });
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void CalculateProfile_SingleCandle_Proportional_EvenDistributionAcrossBins()
    {
        // Low = 0, High = 2, Open = 1, Close = 1.5 (Bullish), Volume = 5, rowSize = 2
        var candle = CreateCandle(1m, 2m, 0m, 1.5m, 5);
        var result = VolumeAnalysis.CalculateProfile(new[] { candle }, rowSize: 2, mode: VolumeDistributionMode.Proportional);

        Assert.Equal(2, result.Count);

        // Bin 0: [0, 1], Price = 0.5
        Assert.Equal(0m, result[0].LowerBound);
        Assert.Equal(1m, result[0].UpperBound);
        Assert.Equal(0.5m, result[0].Price);
        Assert.Equal(2, result[0].TotalVolume); // 5 / 2 = 2
        Assert.Equal(2, result[0].BuyVolume);
        Assert.Equal(0, result[0].SellVolume);
        Assert.Equal(1.0, result[0].WidthPercent);

        // Bin 1: [1, 2], Price = 1.5
        Assert.Equal(1m, result[1].LowerBound);
        Assert.Equal(2m, result[1].UpperBound);
        Assert.Equal(1.5m, result[1].Price);
        Assert.Equal(2, result[1].TotalVolume); // 5 / 2 = 2
        Assert.Equal(2, result[1].BuyVolume);
        Assert.Equal(0, result[1].SellVolume);
        Assert.Equal(1.0, result[1].WidthPercent);
    }

    [Fact]
    public void CalculateProfile_SingleCandle_FullMode_DistributesFullVolumeToEachTouchedBin()
    {
        // Low = 0, High = 2, Volume = 5, rowSize = 2, Full Mode
        var candle = CreateCandle(1m, 2m, 0m, 1.5m, 5);
        var result = VolumeAnalysis.CalculateProfile(new[] { candle }, rowSize: 2, mode: VolumeDistributionMode.Full);

        Assert.Equal(2, result.Count);
        Assert.Equal(5, result[0].TotalVolume);
        Assert.Equal(5, result[0].BuyVolume);
        Assert.Equal(5, result[1].TotalVolume);
        Assert.Equal(5, result[1].BuyVolume);
    }

    [Fact]
    public void CalculateProfile_BearishCandle_AssignsSellVolume()
    {
        // Close < Open -> Bearish
        var candle = CreateCandle(1.8m, 2m, 0m, 0.5m, 6);
        var result = VolumeAnalysis.CalculateProfile(new[] { candle }, rowSize: 2, mode: VolumeDistributionMode.Proportional);

        Assert.Equal(2, result.Count);
        Assert.Equal(3, result[0].TotalVolume);
        Assert.Equal(0, result[0].BuyVolume);
        Assert.Equal(3, result[0].SellVolume);

        Assert.Equal(3, result[1].TotalVolume);
        Assert.Equal(0, result[1].BuyVolume);
        Assert.Equal(3, result[1].SellVolume);
    }

    [Fact]
    public void CalculateProfile_MultipleCandles_BoundaryContactTouchesBothBins()
    {
        // RowSize = 2: Bin 0 is [0, 2], Bin 1 is [2, 4]
        // Candle 1 spans [0, 4] -> touches both bins (vol 10 / 2 = 5 each)
        // Candle 2 spans [2, 4] -> Low=2 matches Bin 0 UpperBound (2 >= 2 && 0 <= 4)
        // Hence Candle 2 touches BOTH Bin 0 and Bin 1 per existing Touch condition:
        // b.UpperBound >= candle.Low && b.LowerBound <= candle.High
        var candle1 = CreateCandle(1m, 4m, 0m, 3m, 10);
        var candle2 = CreateCandle(2m, 4m, 2m, 3m, 20);

        var result = VolumeAnalysis.CalculateProfile(new[] { candle1, candle2 }, rowSize: 2, mode: VolumeDistributionMode.Proportional);

        Assert.Equal(2, result.Count);
        // Candle 1 adds 5 to Bin 0, 5 to Bin 1
        // Candle 2 touches Bin 0 (UpperBound 2 >= Low 2 && LowerBound 0 <= High 4) and Bin 1 (UpperBound 4 >= Low 2 && LowerBound 2 <= High 4)
        // Candle 2 adds 20 / 2 = 10 to Bin 0 and 10 to Bin 1
        Assert.Equal(15, result[0].TotalVolume);
        Assert.Equal(15, result[1].TotalVolume);
    }

    [Fact]
    public void CalculateProfile_WidthPercent_ScalesRelativeToMaxVolume()
    {
        // Bin 0 has vol 10, Bin 1 has vol 20
        // Max volume is 20 -> Bin 0 has WidthPercent = 0.5, Bin 1 has WidthPercent = 1.0
        var candle1 = CreateCandle(0m, 1m, 0m, 1m, 10); // Spans [0, 1]
        var candle2 = CreateCandle(1m, 2m, 1m, 2m, 20); // Spans [1, 2]

        var result = VolumeAnalysis.CalculateProfile(new[] { candle1, candle2 }, rowSize: 2, mode: VolumeDistributionMode.Proportional);

        Assert.Equal(2, result.Count);
        var maxVol = result.Max(b => b.TotalVolume);
        Assert.True(maxVol > 0);

        var bin0 = result[0];
        var bin1 = result[1];
        Assert.Equal((double)bin0.TotalVolume / maxVol, bin0.WidthPercent, 4);
        Assert.Equal((double)bin1.TotalVolume / maxVol, bin1.WidthPercent, 4);
    }

    #endregion

    #region CalculateValueArea Tests

    [Fact]
    public void CalculateValueArea_EmptyOrNullBins_ReturnsZeroZero()
    {
        var emptyResult = VolumeAnalysis.CalculateValueArea(new List<VolumeBin>());
        Assert.Equal((0m, 0m), emptyResult);

        var nullResult = VolumeAnalysis.CalculateValueArea(null!);
        Assert.Equal((0m, 0m), nullResult);
    }

    [Fact]
    public void CalculateValueArea_SinglePocWithAllVolume_ReturnsPocBounds()
    {
        var bins = new List<VolumeBin>
        {
            new() { LowerBound = 10m, UpperBound = 11m, Price = 10.5m, TotalVolume = 0 },
            new() { LowerBound = 11m, UpperBound = 12m, Price = 11.5m, TotalVolume = 100 }, // POC
            new() { LowerBound = 12m, UpperBound = 13m, Price = 12.5m, TotalVolume = 0 }
        };

        var (vah, val) = VolumeAnalysis.CalculateValueArea(bins, 0.70);

        Assert.Equal(12m, vah);
        Assert.Equal(11m, val);
    }

    [Fact]
    public void CalculateValueArea_StandardDistribution_ExpandsTowardHigherVolumeNeighbor()
    {
        // Bins:
        // Index 0: vol 10, [10, 11]
        // Index 1: vol 30, [11, 12]
        // Index 2: vol 100, [12, 13] (POC)
        // Index 3: vol 50, [13, 14]
        // Index 4: vol 20, [14, 15]
        // Total volume = 210. Target at 70% = 147.
        // Step 1: POC (index 2) volume = 100 < 147.
        // Compare upper (index 3, vol 50) vs lower (index 1, vol 30).
        // 50 >= 30 -> expand upper to index 3.
        // Current volume = 100 + 50 = 150 >= 147. Terminates!
        // VAH = bins[3].UpperBound (14m), VAL = bins[2].LowerBound (12m).
        var bins = new List<VolumeBin>
        {
            new() { LowerBound = 10m, UpperBound = 11m, Price = 10.5m, TotalVolume = 10 },
            new() { LowerBound = 11m, UpperBound = 12m, Price = 11.5m, TotalVolume = 30 },
            new() { LowerBound = 12m, UpperBound = 13m, Price = 12.5m, TotalVolume = 100 },
            new() { LowerBound = 13m, UpperBound = 14m, Price = 13.5m, TotalVolume = 50 },
            new() { LowerBound = 14m, UpperBound = 15m, Price = 14.5m, TotalVolume = 20 }
        };

        var (vah, val) = VolumeAnalysis.CalculateValueArea(bins, 0.70);

        Assert.Equal(14m, vah);
        Assert.Equal(12m, val);
    }

    [Fact]
    public void CalculateValueArea_TieBreaking_PrefersUpperExpansion()
    {
        // Bins:
        // Index 0: vol 20, [0, 1]
        // Index 1: vol 100, [1, 2] (POC)
        // Index 2: vol 20, [2, 3]
        // Total volume = 140. Target at 80% = 112.
        // POC vol = 100 < 112.
        // Upper vol = 20, Lower vol = 20.
        // Tie-breaker (upperVol >= lowerVol) expands UPPER first to index 2.
        // Current vol = 120 >= 112. Terminates!
        // VAH = bins[2].UpperBound (3m), VAL = bins[1].LowerBound (1m).
        var bins = new List<VolumeBin>
        {
            new() { LowerBound = 0m, UpperBound = 1m, Price = 0.5m, TotalVolume = 20 },
            new() { LowerBound = 1m, UpperBound = 2m, Price = 1.5m, TotalVolume = 100 },
            new() { LowerBound = 2m, UpperBound = 3m, Price = 2.5m, TotalVolume = 20 }
        };

        var (vah, val) = VolumeAnalysis.CalculateValueArea(bins, 0.80);

        Assert.Equal(3m, vah);
        Assert.Equal(1m, val);
    }

    [Fact]
    public void CalculateValueArea_ZeroVolumeNeighbors_TerminatesEarlyWithoutInfiniteLoop()
    {
        // Bins:
        // Index 0: vol 10, [0, 1]
        // Index 1: vol 0,  [1, 2]
        // Index 2: vol 100, [2, 3] (POC)
        // Index 3: vol 0,  [3, 4]
        // Index 4: vol 10, [4, 5]
        // Total volume = 120. Target at 95% = 114.
        // POC vol = 100 < 114.
        // Upper (index 3) vol = 0, Lower (index 1) vol = 0.
        // Early break condition (upperVol == 0 && lowerVol == 0) prevents infinite loop.
        var bins = new List<VolumeBin>
        {
            new() { LowerBound = 0m, UpperBound = 1m, Price = 0.5m, TotalVolume = 10 },
            new() { LowerBound = 1m, UpperBound = 2m, Price = 1.5m, TotalVolume = 0 },
            new() { LowerBound = 2m, UpperBound = 3m, Price = 2.5m, TotalVolume = 100 },
            new() { LowerBound = 3m, UpperBound = 4m, Price = 3.5m, TotalVolume = 0 },
            new() { LowerBound = 4m, UpperBound = 5m, Price = 4.5m, TotalVolume = 10 }
        };

        var (vah, val) = VolumeAnalysis.CalculateValueArea(bins, 0.95);

        Assert.Equal(3m, vah);
        Assert.Equal(2m, val);
    }

    #endregion
}
