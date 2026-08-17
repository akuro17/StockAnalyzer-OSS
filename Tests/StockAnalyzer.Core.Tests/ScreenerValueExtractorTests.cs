using System;
using System.Collections.Generic;
using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Tests;

public class ScreenerValueExtractorTests
{
    [Fact]
    public void ExtractValue_WithValidCandles_CalculatesValueAndCachesIndicator()
    {
        var extractor = ScreenerValueExtractor.Default;
        var config = new ScreenerIndicatorSideConfig
        {
            IndicatorType = IndicatorType.SMA,
            Parameters = new Dictionary<string, object> { { "Period", 5 } }
        };

        var candles = new List<CoreCandleData>();
        var now = DateTime.UtcNow;
        for (int i = 1; i <= 10; i++)
        {
            candles.Add(new CoreCandleData(now.AddDays(i), i * 10m, i * 10m + 5m, i * 10m - 5m, i * 10m, 1000));
        }

        // Act
        decimal val1 = extractor.ExtractValue(config, candles);
        decimal val2 = extractor.ExtractValue(config, candles);

        // Assert: Average of last 5 closes (60, 70, 80, 90, 100) = 80
        Assert.Equal(80m, val1);
        Assert.Equal(80m, val2);
    }

    [Fact]
    public void ExtractValue_WithEmptyOrNullCandles_ReturnsZero()
    {
        var extractor = ScreenerValueExtractor.Default;
        var config = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.SMA };

        Assert.Equal(0m, extractor.ExtractValue(config, null!));
        Assert.Equal(0m, extractor.ExtractValue(config, new List<CoreCandleData>()));
    }
}
