using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Chart;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

public class CoreTimeAtPriceIndicatorTests
{
    private static List<CoreCandleData> CreateTestCandles(IEnumerable<decimal> closePrices)
    {
        var startDate = DateTime.Today;
        return closePrices.Select((price, i) => new CoreCandleData(
            startDate.AddDays(i), price, price + 5, price - 5, price, 1000
        )).ToList();
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmpty()
    {
        var indicator = new CoreTimeAtPriceIndicator();
        indicator.Calculate(new List<CoreCandleData>());
        Assert.Empty(indicator.Values);
    }

    [Fact]
    public void Calculate_WithSampleData_ReturnsValues()
    {
        var indicator = new CoreTimeAtPriceIndicator();
        var candles = CreateTestCandles(new decimal[] { 10, 11, 12, 13, 14, 15, 16 });
        indicator.Calculate(candles);
        Assert.Equal(candles.Count, indicator.Values.Count);
        Assert.NotEmpty(indicator.ProfileData);
    }

    [Fact]
    public void TimeAtPrice_IsOverlay_IsTrue_And_CategoryIsChart()
    {
        var indicator = new CoreTimeAtPriceIndicator();
        Assert.True(indicator.IsOverlay);

        var defaultSettings = indicator.GetDefaultSettings();
        Assert.True(defaultSettings.IsOverlay);
        Assert.Equal(CoreIndicatorCategory.Chart, defaultSettings.Category);
        Assert.Equal(IndicatorType.TimeAtPrice, defaultSettings.TypeEnum);
        Assert.IsType<CoreTimeAtPriceParameter>(defaultSettings.ParameterObject);
    }

    [Fact]
    public void CalculateSeries_WithValidSeries_CalculatesSuccessfully()
    {
        var indicator = new CoreTimeAtPriceIndicator { Period = 5, RowCount = 10 };
        var series = new decimal?[] { 100m, 105m, 110m, 115m, 120m, 110m, 110m };
        var result = indicator.CalculateSeries(series);

        Assert.True(result.IsSuccessful);
        Assert.Equal(series.Length, indicator.Values.Count);
        Assert.NotEmpty(indicator.ProfileData);
        Assert.NotNull(result.CustomData);
    }

    [Fact]
    public void Configure_SynchronizesPriceSource()
    {
        var indicator = new CoreTimeAtPriceIndicator();
        var param = new CoreTimeAtPriceParameter
        {
            Period = 50,
            RowCount = 20,
            PriceSource = PriceType.Median
        };

        indicator.Configure(param);

        Assert.Equal(50, indicator.Period);
        Assert.Equal(20, indicator.RowCount);
        Assert.Equal(PriceType.Median, indicator.PriceSource);
        Assert.Equal(PriceType.Median, indicator.PriceType);
    }

    [Fact]
    public void Calculate_WithDifferentPriceSources_ProducesExpectedProfiles()
    {
        var candles = new List<CoreCandleData>
        {
            new(DateTime.Today, 10m, 30m, 5m, 12m, 100),
            new(DateTime.Today.AddDays(1), 12m, 30m, 5m, 28m, 100)
        };

        var indicatorHigh = new CoreTimeAtPriceIndicator { RowCount = 2, PriceSource = PriceType.High };
        indicatorHigh.Calculate(candles);
        Assert.Equal(2, indicatorHigh.ProfileData.Count);
        // Both candles High=30, in top bin
        Assert.Equal(2, indicatorHigh.ProfileData[1].TotalVolume);

        var indicatorLow = new CoreTimeAtPriceIndicator { RowCount = 2, PriceSource = PriceType.Low };
        indicatorLow.Calculate(candles);
        Assert.Equal(2, indicatorLow.ProfileData.Count);
        // Both candles Low=5, in bottom bin
        Assert.Equal(2, indicatorLow.ProfileData[0].TotalVolume);
    }
}
