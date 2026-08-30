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
        Assert.Equal(11, options.Count);
        Assert.Equal(PriceType.Open, options[0]);
        Assert.Equal(PriceType.High, options[1]);
        Assert.Equal(PriceType.Low, options[2]);
        Assert.Equal(PriceType.Close, options[3]); // Close is directly below Low
        Assert.Equal(PriceType.Median, options[4]);
        Assert.Equal(PriceType.Midpoint, options[5]);
        Assert.Equal(PriceType.Typical, options[6]);
        Assert.Equal(PriceType.Weighted, options[7]);
        Assert.Equal(PriceType.Average, options[8]);
        Assert.Equal(PriceType.TrueHigh, options[9]);
        Assert.Equal(PriceType.TrueLow, options[10]);
    }
}
