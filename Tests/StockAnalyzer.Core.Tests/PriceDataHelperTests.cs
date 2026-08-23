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
        // Open: 100, High: 120, Low: 80, Close: 110
        var candle = new CoreCandleData(DateTime.Today, 100m, 120m, 80m, 110m, 1000);

        Assert.Equal(110m, PriceDataHelper.ExtractPrice(candle, PriceType.Close));
        Assert.Equal(100m, PriceDataHelper.ExtractPrice(candle, PriceType.Open));
        Assert.Equal(120m, PriceDataHelper.ExtractPrice(candle, PriceType.High));
        Assert.Equal(80m, PriceDataHelper.ExtractPrice(candle, PriceType.Low));
        Assert.Equal(100m, PriceDataHelper.ExtractPrice(candle, PriceType.Median)); // (120 + 80) / 2 = 100
        Assert.Equal(310m / 3.0m, PriceDataHelper.ExtractPrice(candle, PriceType.Typical)); // (120 + 80 + 110) / 3 = 310 / 3
        Assert.Equal(420m / 4.0m, PriceDataHelper.ExtractPrice(candle, PriceType.Weighted)); // (120 + 80 + 2*110) / 4 = 105
        Assert.Equal(105m, PriceDataHelper.ExtractPrice(candle, PriceType.Weighted));
    }

    [Fact]
    public void ExtractPrice_CalculatesAllPriceTypesCorrectly_ForCandleData()
    {
        var candle = new CandleData(DateTime.Today, 100m, 120m, 80m, 110m, 1000);

        Assert.Equal(110m, PriceDataHelper.ExtractPrice(candle, PriceType.Close));
        Assert.Equal(100m, PriceDataHelper.ExtractPrice(candle, PriceType.Open));
        Assert.Equal(120m, PriceDataHelper.ExtractPrice(candle, PriceType.High));
        Assert.Equal(80m, PriceDataHelper.ExtractPrice(candle, PriceType.Low));
        Assert.Equal(100m, PriceDataHelper.ExtractPrice(candle, PriceType.Median));
        Assert.Equal(310m / 3.0m, PriceDataHelper.ExtractPrice(candle, PriceType.Typical));
        Assert.Equal(105m, PriceDataHelper.ExtractPrice(candle, PriceType.Weighted));
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

        var typicalSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceType.Typical);
        Assert.Equal(2, typicalSeries.Count);
        Assert.Equal(40m / 3.0m, typicalSeries[0]); // (20 + 5 + 15) / 3
        Assert.Equal(55m / 3.0m, typicalSeries[1]); // (25 + 10 + 20) / 3
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
        Assert.Equal(7, options.Count);
        Assert.Equal(PriceType.Open, options[0]);
        Assert.Equal(PriceType.High, options[1]);
        Assert.Equal(PriceType.Low, options[2]);
        Assert.Equal(PriceType.Close, options[3]); // Close is directly below Low
        Assert.Equal(PriceType.Median, options[4]);
        Assert.Equal(PriceType.Typical, options[5]);
        Assert.Equal(PriceType.Weighted, options[6]);
    }
}
