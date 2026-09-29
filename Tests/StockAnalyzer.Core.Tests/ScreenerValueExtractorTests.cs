using System;
using System.Collections.Generic;
using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Advanced;
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
    public void ExtractValue_IfftInstantaneousPhase_ResolvesPhaseAngleOutputNameToMainSeries()
    {
        var extractor = ScreenerValueExtractor.Default;
        var config = new ScreenerIndicatorSideConfig
        {
            IndicatorType = IndicatorType.IFFTInstantaneousPhase,
            // "PhaseAngle" is the Screener's friendly display name for the base "Main" series; it
            // is not itself a key in the indicator's IIndicatorResult, so this exercises the
            // HasSeries fallback to result.MainValues in ScreenerValueExtractor.
            OutputName = CoreIfftInstantaneousPhaseIndicator.ScreenerPhaseAngleOutputName,
            Parameters = new Dictionary<string, object> { { "WindowSize", 32 } }
        };

        var candles = new List<CoreCandleData>();
        var date = DateTime.UtcNow.Date;
        for (int i = 0; i < 100; i++)
        {
            decimal price = (decimal)(100.0 + Math.Sin(i * 0.3) * 5.0);
            candles.Add(new CoreCandleData(date.AddDays(i), price, price + 1m, price - 1m, price, 1000));
        }

        decimal val = extractor.ExtractValue(config, candles);

        // A valid instantaneous phase is expressed in degrees, [0, 360).
        Assert.InRange(val, 0m, 360m);
    }

    [Fact]
    public void ExtractValue_WithContinuationCandlePattern_ExtractsCorrectValue()
    {
        var extractor = ScreenerValueExtractor.Default;
        var config = new ScreenerIndicatorSideConfig
        {
            CategoryType = ScreenerItemCategoryType.Criteria,
            CustomDisplayName = "Mat Hold"
        };

        // Context 14 bars + 5 MatHold bars
        var candles = new List<CandleData>();
        for (int i = 0; i < 14; i++)
            candles.Add(new CandleData(DateTime.Now.AddDays(-30 + i), 100m, 104m, 100m, 104m, 1000));

        candles.Add(new CandleData(DateTime.Now.AddDays(-4), 100, 111, 99, 110, 1000));
        candles.Add(new CandleData(DateTime.Now.AddDays(-3), 112, 113.5m, 111.5m, 113, 1000));
        candles.Add(new CandleData(DateTime.Now.AddDays(-2), 112.5m, 113.5m, 111.5m, 111.5m, 1000));
        candles.Add(new CandleData(DateTime.Now.AddDays(-1), 111.5m, 112.5m, 110.5m, 111, 1000));
        candles.Add(new CandleData(DateTime.Now, 111, 117, 110.5m, 116, 1000));

        var val = extractor.ExtractValue(config, candles);
        Assert.Equal(1m, val);
    }

    [Fact]
    public void ExtractValue_WithAdvancedReversalCandlePattern_ExtractsCorrectValue()
    {
        var extractor = ScreenerValueExtractor.Default;
        var config = new ScreenerIndicatorSideConfig
        {
            CategoryType = ScreenerItemCategoryType.Criteria,
            CustomDisplayName = "Bullish Abandoned Baby"
        };

        // Context 14 bars + 3 AbandonedBaby bars
        var candles = new List<CandleData>();
        for (int i = 0; i < 14; i++)
            candles.Add(new CandleData(DateTime.Now.AddDays(-30 + i), 100m, 104m, 100m, 104m, 1000));

        candles.Add(new CandleData(DateTime.Now.AddDays(-2), 110, 112, 98, 100, 1000));
        candles.Add(new CandleData(DateTime.Now.AddDays(-1), 95, 96, 94, 95.05m, 1000));
        candles.Add(new CandleData(DateTime.Now, 97, 109, 97, 108, 1000));

        var val = extractor.ExtractValue(config, candles);
        Assert.Equal(1m, val);
    }

    [Fact]
    public void ExtractValue_WithGapCandlePattern_ExtractsCorrectValue()
    {
        var extractor = ScreenerValueExtractor.Default;
        var config = new ScreenerIndicatorSideConfig
        {
            CategoryType = ScreenerItemCategoryType.Criteria,
            CustomDisplayName = "Bullish Tasuki Gap"
        };

        // Context 14 bars + 3 TasukiGap bars
        var candles = new List<CandleData>();
        for (int i = 0; i < 14; i++)
            candles.Add(new CandleData(DateTime.Now.AddDays(-30 + i), 100m, 104m, 100m, 104m, 1000));

        candles.Add(new CandleData(DateTime.Now.AddDays(-2), 100, 107, 99, 106, 1000));
        candles.Add(new CandleData(DateTime.Now.AddDays(-1), 107, 113, 106, 112, 1000));
        candles.Add(new CandleData(DateTime.Now, 111, 112, 106.2m, 106.5m, 1000));

        var val = extractor.ExtractValue(config, candles);
        Assert.Equal(1m, val);
    }

    [Fact]
    public void ExtractValue_WithExoticAndMinorCandlePatterns_ExtractsCorrectValue()
    {
        var extractor = ScreenerValueExtractor.Default;
        var config = new ScreenerIndicatorSideConfig
        {
            CategoryType = ScreenerItemCategoryType.Criteria,
            CustomDisplayName = "Bullish Kicking"
        };

        // Context 14 bars + 2 BullishKicking bars
        var candles = new List<CandleData>();
        for (int i = 0; i < 14; i++)
            candles.Add(new CandleData(DateTime.Now.AddDays(-30 + i), 100m, 104m, 100m, 104m, 1000));

        candles.Add(new CandleData(DateTime.Now.AddDays(-1), 104, 104, 100, 100, 1000));
        candles.Add(new CandleData(DateTime.Now, 107, 113, 107, 113, 1000));

        var val = extractor.ExtractValue(config, candles);
        Assert.Equal(1m, val);
    }

    [Fact]
    public void ExtractValue_WithPriceType_ExtractsAccurately()
    {
        var extractor = ScreenerValueExtractor.Default;
        var candles = new List<CoreCandleData>
        {
            new(DateTime.Today.AddDays(-1), 100m, 120m, 80m, 110m, 1000),
            new(DateTime.Today, 110m, 130m, 90m, 120m, 1000)
        };

        // Close
        var configClose = new ScreenerIndicatorSideConfig
        {
            IndicatorType = IndicatorType.Price,
            OutputName = "Close",
            CustomDisplayName = "Close"
        };
        Assert.Equal(120m, extractor.ExtractValue(configClose, candles));

        // Median (130 + 90) / 2 = 110
        var configMedian = new ScreenerIndicatorSideConfig
        {
            IndicatorType = IndicatorType.Price,
            OutputName = "Median",
            CustomDisplayName = "Median (H+L)/2"
        };
        Assert.Equal(110m, extractor.ExtractValue(configMedian, candles));

        // Heikin-Ashi Close: (110 + 130 + 90 + 120) / 4 = 112.5
        var configHaClose = new ScreenerIndicatorSideConfig
        {
            IndicatorType = IndicatorType.Price,
            OutputName = "HeikinAshiClose",
            CustomDisplayName = "Heikin-Ashi Close"
        };
        Assert.Equal(112.5m, extractor.ExtractValue(configHaClose, candles));

        // With Offset = 1 (previous day Close = 110)
        var configPrevClose = new ScreenerIndicatorSideConfig
        {
            IndicatorType = IndicatorType.Price,
            OutputName = "Close",
            CustomDisplayName = "Close",
            Offset = 1
        };
        Assert.Equal(110m, extractor.ExtractValue(configPrevClose, candles));
    }
}

