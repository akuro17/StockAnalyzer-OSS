using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class PriceDataHelperTests
{
    [Fact]
    public void ExtractPrice_CalculatesAllPriceTypesCorrectly_ForCoreCandleData()
    {
        // Open: 100, High: 120, Low: 80, Close: 110, prevClose: 105
        var candle = new CoreCandleData(DateTime.Today, 100m, 120m, 80m, 110m, 1000);
        decimal prevClose = 105m;

        Assert.Equal(110m, PriceDataHelper.ExtractPrice(candle, PriceType.Close));
        Assert.Equal(100m, PriceDataHelper.ExtractPrice(candle, PriceType.Open));
        Assert.Equal(120m, PriceDataHelper.ExtractPrice(candle, PriceType.High));
        Assert.Equal(80m, PriceDataHelper.ExtractPrice(candle, PriceType.Low));
        Assert.Equal(100m, PriceDataHelper.ExtractPrice(candle, PriceType.Median)); // (120 + 80) / 2 = 100
        Assert.Equal(105m, PriceDataHelper.ExtractPrice(candle, PriceType.Midpoint)); // (100 + 110) / 2 = 105
        Assert.Equal(310m / 3.0m, PriceDataHelper.ExtractPrice(candle, PriceType.Typical)); // (120 + 80 + 110) / 3 = 310 / 3
        Assert.Equal(420m / 4.0m, PriceDataHelper.ExtractPrice(candle, PriceType.Weighted)); // (120 + 80 + 2*110) / 4 = 105
        Assert.Equal(105m, PriceDataHelper.ExtractPrice(candle, PriceType.Weighted));
        Assert.Equal(410m / 4.0m, PriceDataHelper.ExtractPrice(candle, PriceType.Average)); // (100 + 120 + 80 + 110) / 4 = 102.5
        Assert.Equal(102.5m, PriceDataHelper.ExtractPrice(candle, PriceType.Average));

        // TrueHigh: max(High, prevClose)
        Assert.Equal(120m, PriceDataHelper.ExtractPrice(candle, PriceType.TrueHigh, prevClose)); // max(120, 105) = 120
        Assert.Equal(130m, PriceDataHelper.ExtractPrice(candle, PriceType.TrueHigh, 130m)); // max(120, 130) = 130
        Assert.Equal(120m, PriceDataHelper.ExtractPrice(candle, PriceType.TrueHigh, null)); // fallback High = 120

        // TrueLow: min(Low, prevClose)
        Assert.Equal(80m, PriceDataHelper.ExtractPrice(candle, PriceType.TrueLow, prevClose)); // min(80, 105) = 80
        Assert.Equal(70m, PriceDataHelper.ExtractPrice(candle, PriceType.TrueLow, 70m)); // min(80, 70) = 70
        Assert.Equal(80m, PriceDataHelper.ExtractPrice(candle, PriceType.TrueLow, null)); // fallback Low = 80
    }

    [Fact]
    public void ExtractPrice_CalculatesAllPriceTypesCorrectly_ForCandleData()
    {
        var candle = new CandleData(DateTime.Today, 100m, 120m, 80m, 110m, 1000);
        decimal prevClose = 105m;

        Assert.Equal(110m, PriceDataHelper.ExtractPrice(candle, PriceType.Close));
        Assert.Equal(100m, PriceDataHelper.ExtractPrice(candle, PriceType.Open));
        Assert.Equal(120m, PriceDataHelper.ExtractPrice(candle, PriceType.High));
        Assert.Equal(80m, PriceDataHelper.ExtractPrice(candle, PriceType.Low));
        Assert.Equal(100m, PriceDataHelper.ExtractPrice(candle, PriceType.Median));
        Assert.Equal(105m, PriceDataHelper.ExtractPrice(candle, PriceType.Midpoint));
        Assert.Equal(310m / 3.0m, PriceDataHelper.ExtractPrice(candle, PriceType.Typical));
        Assert.Equal(105m, PriceDataHelper.ExtractPrice(candle, PriceType.Weighted));
        Assert.Equal(102.5m, PriceDataHelper.ExtractPrice(candle, PriceType.Average));
        Assert.Equal(120m, PriceDataHelper.ExtractPrice(candle, PriceType.TrueHigh, prevClose));
        Assert.Equal(130m, PriceDataHelper.ExtractPrice(candle, PriceType.TrueHigh, 130m));
        Assert.Equal(80m, PriceDataHelper.ExtractPrice(candle, PriceType.TrueLow, prevClose));
        Assert.Equal(70m, PriceDataHelper.ExtractPrice(candle, PriceType.TrueLow, 70m));
    }

    [Fact]
    public void ExtractPriceSeries_ExtractsCorrectSeries()
    {
        var candles = new List<CoreCandleData>
        {
            new(DateTime.Today, 10m, 20m, 5m, 15m, 100),
            new(DateTime.Today.AddDays(1), 15m, 25m, 10m, 20m, 200)
        };

        var medianSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceType.Median);
        Assert.Equal(2, medianSeries.Count);
        Assert.Equal(12.5m, medianSeries[0]); // (20 + 5) / 2
        Assert.Equal(17.5m, medianSeries[1]); // (25 + 10) / 2

        var midpointSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceType.Midpoint);
        Assert.Equal(2, midpointSeries.Count);
        Assert.Equal(12.5m, midpointSeries[0]); // (10 + 15) / 2
        Assert.Equal(17.5m, midpointSeries[1]); // (15 + 20) / 2

        var averageSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceType.Average);
        Assert.Equal(2, averageSeries.Count);
        Assert.Equal(12.5m, averageSeries[0]); // (10 + 20 + 5 + 15) / 4
        Assert.Equal(17.5m, averageSeries[1]); // (15 + 25 + 10 + 20) / 4

        var trueHighSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceType.TrueHigh);
        Assert.Equal(2, trueHighSeries.Count);
        Assert.Equal(20m, trueHighSeries[0]); // bar 0: High = 20
        Assert.Equal(25m, trueHighSeries[1]); // bar 1: max(25, 15) = 25

        var trueLowSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceType.TrueLow);
        Assert.Equal(2, trueLowSeries.Count);
        Assert.Equal(5m, trueLowSeries[0]); // bar 0: Low = 5
        Assert.Equal(10m, trueLowSeries[1]); // bar 1: min(10, 15) = 10
    }

    [Fact]
    public void ExtractPriceSeries_HandlesEmptyAndNullGracefully()
    {
        var emptyResult = PriceDataHelper.ExtractPriceSeries((IReadOnlyList<CoreCandleData>?)null);
        Assert.NotNull(emptyResult);
        Assert.Empty(emptyResult);

        var emptyListResult = PriceDataHelper.ExtractPriceSeries(new List<CoreCandleData>());
        Assert.NotNull(emptyListResult);
        Assert.Empty(emptyListResult);

        var nonNullEmpty = PriceDataHelper.ExtractNonNullablePriceSeries((IReadOnlyList<CoreCandleData>?)null);
        Assert.NotNull(nonNullEmpty);
        Assert.Empty(nonNullEmpty);
    }

    [Fact]
    public void PriceTypeOptions_OrderingAndDefault_FollowsOHLCSpec()
    {
        Assert.Equal(PriceType.Close, PriceDataHelper.DefaultPriceType);
        Assert.Equal(PriceType.Close, PriceDataHelper.GetDefaultPriceType());

        var options = PriceDataHelper.PriceTypeOptions;
        Assert.Equal(15, options.Count);
        Assert.Equal(PriceType.Open, options[0]);
        Assert.Equal(PriceType.High, options[1]);
        Assert.Equal(PriceType.Low, options[2]);
        Assert.Equal(PriceType.Close, options[3]); // Close is directly below Low
        Assert.Equal(PriceType.Median, options[4]);
        Assert.Equal(PriceType.Midpoint, options[5]);
        Assert.Equal(PriceType.Typical, options[6]);
        Assert.Equal(PriceType.Weighted, options[7]);
        Assert.Equal(PriceType.Average, options[8]);
        Assert.Equal(PriceType.HeikinAshiOpen, options[9]);
        Assert.Equal(PriceType.HeikinAshiHigh, options[10]);
        Assert.Equal(PriceType.HeikinAshiLow, options[11]);
        Assert.Equal(PriceType.HeikinAshiClose, options[12]);
        Assert.Equal(PriceType.TrueHigh, options[13]);
        Assert.Equal(PriceType.TrueLow, options[14]);
    }

    [Fact]
    public void ExtractPriceSeries_HeikinAshi_MatchesHeikinAshiConverter()
    {
        var now = DateTime.Today;
        var candles = new List<CoreCandleData>
        {
            new CoreCandleData(now.AddDays(0), 100m, 110m, 95m, 105m, 1000),
            new CoreCandleData(now.AddDays(1), 106m, 115m, 102m, 112m, 1200),
            new CoreCandleData(now.AddDays(2), 111m, 118m, 108m, 110m, 1100),
            new CoreCandleData(now.AddDays(3), 109m, 114m, 100m, 102m, 1500),
            new CoreCandleData(now.AddDays(4), 103m, 108m, 98m, 107m, 1300),
        };

        var expectedHaCandles = StockAnalyzer.Core.Utilities.HeikinAshiConverter.Convert(candles);

        var haOpenSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceType.HeikinAshiOpen);
        var haHighSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceType.HeikinAshiHigh);
        var haLowSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceType.HeikinAshiLow);
        var haCloseSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceType.HeikinAshiClose);

        Assert.Equal(candles.Count, expectedHaCandles.Count);

        for (int i = 0; i < candles.Count; i++)
        {
            Assert.Equal(expectedHaCandles[i].Open, haOpenSeries[i]);
            Assert.Equal(expectedHaCandles[i].High, haHighSeries[i]);
            Assert.Equal(expectedHaCandles[i].Low, haLowSeries[i]);
            Assert.Equal(expectedHaCandles[i].Close, haCloseSeries[i]);
        }

        var nonNullCloseSeries = PriceDataHelper.ExtractNonNullablePriceSeries(candles, PriceType.HeikinAshiClose);
        for (int i = 0; i < candles.Count; i++)
        {
            Assert.Equal(expectedHaCandles[i].Close, nonNullCloseSeries[i]);
        }
    }

    [Fact]
    public void ExtractPrice_SingleBar_CalculatesInitialHeikinAshiFormula()
    {
        var candle = new CoreCandleData(DateTime.Today, 100m, 120m, 80m, 110m, 1000);

        // First bar: Open = (100+110)/2 = 105, Close = (100+120+80+110)/4 = 102.5
        // High = max(120, 105, 102.5) = 120, Low = min(80, 105, 102.5) = 80
        Assert.Equal(105m, PriceDataHelper.ExtractPrice(candle, PriceType.HeikinAshiOpen));
        Assert.Equal(102.5m, PriceDataHelper.ExtractPrice(candle, PriceType.HeikinAshiClose));
        Assert.Equal(120m, PriceDataHelper.ExtractPrice(candle, PriceType.HeikinAshiHigh));
        Assert.Equal(80m, PriceDataHelper.ExtractPrice(candle, PriceType.HeikinAshiLow));
    }

    [Fact]
    public void FormatPriceTypeLabel_ReturnsFormattedLabels_ForAll15Options()
    {
        Assert.Equal("Open", PriceDataHelper.FormatPriceTypeLabel(PriceType.Open));
        Assert.Equal("High", PriceDataHelper.FormatPriceTypeLabel(PriceType.High));
        Assert.Equal("Low", PriceDataHelper.FormatPriceTypeLabel(PriceType.Low));
        Assert.Equal("Close", PriceDataHelper.FormatPriceTypeLabel(PriceType.Close));
        Assert.Equal("Median (H+L)/2", PriceDataHelper.FormatPriceTypeLabel(PriceType.Median));
        Assert.Equal("Midpoint (O+C)/2", PriceDataHelper.FormatPriceTypeLabel(PriceType.Midpoint));
        Assert.Equal("Typical (H+L+C)/3", PriceDataHelper.FormatPriceTypeLabel(PriceType.Typical));
        Assert.Equal("Weighted (H+L+2C)/4", PriceDataHelper.FormatPriceTypeLabel(PriceType.Weighted));
        Assert.Equal("Average (O+H+L+C)/4", PriceDataHelper.FormatPriceTypeLabel(PriceType.Average));
        Assert.Equal("Heikin-Ashi Open", PriceDataHelper.FormatPriceTypeLabel(PriceType.HeikinAshiOpen));
        Assert.Equal("Heikin-Ashi High", PriceDataHelper.FormatPriceTypeLabel(PriceType.HeikinAshiHigh));
        Assert.Equal("Heikin-Ashi Low", PriceDataHelper.FormatPriceTypeLabel(PriceType.HeikinAshiLow));
        Assert.Equal("Heikin-Ashi Close", PriceDataHelper.FormatPriceTypeLabel(PriceType.HeikinAshiClose));
        Assert.Equal("True High", PriceDataHelper.FormatPriceTypeLabel(PriceType.TrueHigh));
        Assert.Equal("True Low", PriceDataHelper.FormatPriceTypeLabel(PriceType.TrueLow));

        foreach (var opt in PriceDataHelper.PriceTypeOptions)
        {
            var label = PriceDataHelper.FormatPriceTypeLabel(opt);
            Assert.False(string.IsNullOrWhiteSpace(label));
        }
    }

    [Fact]
    public void PriceType_IndexResolution_RoundTripAndBoundaryChecks()
    {
        for (int i = 0; i < PriceDataHelper.PriceTypeOptions.Count; i++)
        {
            var pt = PriceDataHelper.PriceTypeOptions[i];
            int index = PriceDataHelper.GetPriceTypeIndex(pt);
            Assert.Equal(i, index);

            var resolvedPt = PriceDataHelper.GetPriceTypeByIndex(i);
            Assert.Equal(pt, resolvedPt);
        }

        // Out-of-range boundary fallbacks
        Assert.Equal(PriceType.Median, PriceDataHelper.GetPriceTypeByIndex(-1));
        Assert.Equal(PriceType.Median, PriceDataHelper.GetPriceTypeByIndex(999));

        // Default mapping verification: Median is index 4
        Assert.Equal(4, PriceDataHelper.GetPriceTypeIndex(PriceType.Median));
    }
}
