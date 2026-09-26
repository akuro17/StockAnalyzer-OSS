using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

public class TimeAtPriceAnalysisTests
{
    private static CoreCandleData CreateCandle(
        decimal open,
        decimal high,
        decimal low,
        decimal close,
        long volume = 1000,
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

    [Fact]
    public void CalculateProfile_EmptyCandles_ReturnsEmptyList()
    {
        var result = TimeAtPriceAnalysis.CalculateProfile(Array.Empty<CoreCandleData>());
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void CalculateProfile_NullCandles_ReturnsEmptyList()
    {
        var result = TimeAtPriceAnalysis.CalculateProfile(null!);
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void CalculateProfile_HighEqualsLow_ReturnsSingleBin()
    {
        var candle = CreateCandle(10m, 10m, 10m, 10m);
        var result = TimeAtPriceAnalysis.CalculateProfile(new[] { candle });
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(10m, result[0].Price);
        Assert.Equal(1, result[0].TotalVolume);
        Assert.Equal(1.0, result[0].WidthPercent);
    }

    [Fact]
    public void CalculateProfile_TotalTimeEqualsCandleCount()
    {
        var candles = new[]
        {
            CreateCandle(10m, 15m, 10m, 12m, timestamp: new DateTime(2026, 1, 1)),
            CreateCandle(12m, 18m, 11m, 16m, timestamp: new DateTime(2026, 1, 2)),
            CreateCandle(16m, 20m, 14m, 15m, timestamp: new DateTime(2026, 1, 3)),
            CreateCandle(15m, 17m, 12m, 13m, timestamp: new DateTime(2026, 1, 4)),
            CreateCandle(13m, 16m, 10m, 11m, timestamp: new DateTime(2026, 1, 5)),
        };

        var bins = TimeAtPriceAnalysis.CalculateProfile(candles, rowSize: 10, priceType: PriceType.Close);
        Assert.Equal(10, bins.Count);

        long totalResidenceTime = bins.Sum(b => b.TotalVolume);
        Assert.Equal(candles.Length, totalResidenceTime);
    }

    [Fact]
    public void CalculateProfile_PriceTypeRespectsSourceField()
    {
        // One candle where Open is 10, High is 20, Low is 10, Close is 20
        // Second candle where Open is 20, High is 20, Low is 10, Close is 10
        var candles = new[]
        {
            CreateCandle(10m, 20m, 10m, 20m, timestamp: new DateTime(2026, 1, 1)),
            CreateCandle(20m, 20m, 10m, 10m, timestamp: new DateTime(2026, 1, 2))
        };

        // PriceType.Open: candle 1 is 10 (bottom bin), candle 2 is 20 (top bin)
        var binsOpen = TimeAtPriceAnalysis.CalculateProfile(candles, rowSize: 2, priceType: PriceType.Open);
        Assert.Equal(2, binsOpen.Count);
        Assert.Equal(1, binsOpen[0].TotalVolume); // Price 10 in bottom bin
        Assert.Equal(1, binsOpen[1].TotalVolume); // Price 20 in top bin

        // PriceType.Low: both candles have Low=10, so both should fall into bottom bin
        var binsLow = TimeAtPriceAnalysis.CalculateProfile(candles, rowSize: 2, priceType: PriceType.Low);
        Assert.Equal(2, binsLow.Count);
        Assert.Equal(2, binsLow[0].TotalVolume); // Both in bottom bin
        Assert.Equal(0, binsLow[1].TotalVolume);
    }

    [Fact]
    public void CalculateProfile_BullishAndBearishClassification()
    {
        var candles = new[]
        {
            CreateCandle(10m, 15m, 10m, 14m, timestamp: new DateTime(2026, 1, 1)), // Bullish
            CreateCandle(14m, 15m, 10m, 11m, timestamp: new DateTime(2026, 1, 2)), // Bearish
        };

        var bins = TimeAtPriceAnalysis.CalculateProfile(candles, rowSize: 5, priceType: PriceType.Close);
        long totalBuy = bins.Sum(b => b.BuyVolume);
        long totalSell = bins.Sum(b => b.SellVolume);

        Assert.Equal(1, totalBuy);
        Assert.Equal(1, totalSell);
    }

    [Fact]
    public void CalculateValueArea_ReturnsValidVAHandVAL()
    {
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < 20; i++)
        {
            // Most prices clustered around 50
            decimal price = 45m + (i % 10);
            candles.Add(CreateCandle(price, price + 5, price - 5, price, timestamp: new DateTime(2026, 1, 1).AddDays(i)));
        }

        var bins = TimeAtPriceAnalysis.CalculateProfile(candles, rowSize: 10, priceType: PriceType.Close);
        var va = TimeAtPriceAnalysis.CalculateValueArea(bins, 0.70);

        Assert.True(va.VAH >= va.VAL);
        Assert.True(va.VAH <= bins.Max(b => b.UpperBound));
        Assert.True(va.VAL >= bins.Min(b => b.LowerBound));
    }

    [Fact]
    public void CalculateProfile_FromSeries_DistributesValuesAccurately()
    {
        // Series values: 10, 20, 30, 40, 50
        var series = new decimal?[] { 10m, 20m, 30m, 40m, 50m };
        var bins = TimeAtPriceAnalysis.CalculateSeriesProfile(series, rowSize: 4);

        Assert.Equal(4, bins.Count);
        long totalCount = bins.Sum(b => b.TotalVolume);
        Assert.Equal(5, totalCount);
    }

    [Fact]
    public void CalculateProfile_FromSeries_NullValues_AreSkipped()
    {
        var series = new decimal?[] { 10m, null, 20m, null, 30m };
        var bins = TimeAtPriceAnalysis.CalculateProfile(series, rowSize: 2);

        Assert.Equal(2, bins.Count);
        long totalCount = bins.Sum(b => b.TotalVolume);
        Assert.Equal(3, totalCount);
    }

    [Fact]
    public void CalculateProfile_FromSeries_WithCandles_UsesCandleColorForBullishBearish()
    {
        var series = new decimal?[] { 10m, 20m };
        var candles = new[]
        {
            CreateCandle(10m, 15m, 10m, 14m, timestamp: new DateTime(2026, 1, 1)), // Bullish (Close 14 >= Open 10)
            CreateCandle(20m, 25m, 15m, 16m, timestamp: new DateTime(2026, 1, 2)), // Bearish (Close 16 < Open 20)
        };

        var bins = TimeAtPriceAnalysis.CalculateProfile(series, rowSize: 2, candles);
        Assert.Equal(2, bins.Count);
        Assert.Equal(1, bins[0].BuyVolume);
        Assert.Equal(0, bins[0].SellVolume);
        Assert.Equal(0, bins[1].BuyVolume);
        Assert.Equal(1, bins[1].SellVolume);
    }

    [Fact]
    public void CalculateProfile_MaxEqualsMin_Candles_ReturnsSingleFullWidthBin()
    {
        var candles = new[]
        {
            CreateCandle(100m, 100m, 100m, 100m, timestamp: new DateTime(2026, 1, 1)),
            CreateCandle(100m, 100m, 100m, 100m, timestamp: new DateTime(2026, 1, 2)),
        };

        var bins = TimeAtPriceAnalysis.CalculateProfile(candles, rowSize: 10);
        Assert.Single(bins);
        Assert.Equal(100m, bins[0].Price);
        Assert.Equal(100m, bins[0].LowerBound);
        Assert.Equal(100m, bins[0].UpperBound);
        Assert.Equal(2, bins[0].TotalVolume);
        Assert.Equal(1.0, bins[0].WidthPercent);

        var va = TimeAtPriceAnalysis.CalculateValueArea(bins);
        Assert.Equal(100m, va.VAH);
        Assert.Equal(100m, va.VAL);
    }

    [Fact]
    public void CalculateProfile_MaxEqualsMin_Series_ReturnsSingleFullWidthBin()
    {
        var series = new decimal?[] { 50m, 50m, 50m, null, 50m };
        var bins = TimeAtPriceAnalysis.CalculateSeriesProfile(series, rowSize: 5);

        Assert.Single(bins);
        Assert.Equal(50m, bins[0].Price);
        Assert.Equal(4, bins[0].TotalVolume);
        Assert.Equal(1.0, bins[0].WidthPercent);

        var va = TimeAtPriceAnalysis.CalculateValueArea(bins);
        Assert.Equal(50m, va.VAH);
        Assert.Equal(50m, va.VAL);
    }

    [Fact]
    public void CalculateProfile_FromSeries_FlatSeries_TreatsAsNeutral()
    {
        // Consecutive identical values without candles: all bars should be neutral (neither Buy nor Sell)
        var series = new decimal?[] { 30m, 30m, 30m, 30m };
        var bins = TimeAtPriceAnalysis.CalculateSeriesProfile(series, rowSize: 1);

        Assert.Single(bins);
        Assert.Equal(4, bins[0].TotalVolume);
        Assert.Equal(0, bins[0].BuyVolume);
        Assert.Equal(0, bins[0].SellVolume);
    }

    [Fact]
    public void CalculateProfile_FromSeries_DirectionChanges_ProperlyAssignsBuySellNeutral()
    {
        // Series: 10 (i=0: neutral), 20 (up: buy), 20 (flat: neutral), 15 (down: sell)
        var series = new decimal?[] { 10m, 20m, 20m, 15m };
        var bins = TimeAtPriceAnalysis.CalculateSeriesProfile(series, rowSize: 2);

        long totalBuy = bins.Sum(b => b.BuyVolume);
        long totalSell = bins.Sum(b => b.SellVolume);
        long totalVol = bins.Sum(b => b.TotalVolume);

        Assert.Equal(4, totalVol);
        Assert.Equal(1, totalBuy);
        Assert.Equal(1, totalSell);
        // Neutral count = 4 - (1 + 1) = 2 (i=0 and i=2)
    }

    [Fact]
    public void CalculateValueArea_PocTie_SelectsLowerBin()
    {
        // Two bins with identical maximum volume: bin 0 has 10, bin 1 has 10
        var bins = new List<VolumeBin>
        {
            new VolumeBin { Price = 100m, LowerBound = 90m, UpperBound = 110m, TotalVolume = 10 },
            new VolumeBin { Price = 120m, LowerBound = 110m, UpperBound = 130m, TotalVolume = 10 }
        };

        var va = TimeAtPriceAnalysis.CalculateValueArea(bins, 0.50);
        // POC should be bin 0 (lower index / lower price: 100m)
        // Since target volume = 20 * 0.5 = 10, bin 0 alone satisfies 70%/50%
        Assert.Equal(110m, va.VAH);
        Assert.Equal(90m, va.VAL);
    }

    [Fact]
    public void CalculateProfile_ConservationInvariant_TotalVolumeEqualsValidCount()
    {
        var series = new decimal?[] { 10m, null, 25m, 15m, 30m, null, 20m, 20m, 12m, 28m };
        int validCount = series.Count(s => s.HasValue);

        var bins = TimeAtPriceAnalysis.CalculateSeriesProfile(series, rowSize: 5);
        long totalVolume = bins.Sum(b => b.TotalVolume);

        Assert.Equal(validCount, totalVolume);
    }
}
