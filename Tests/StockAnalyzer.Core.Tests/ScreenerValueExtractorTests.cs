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

    private static List<CoreCandleData> HeikinAshiRecursionCandles() => new()
    {
        new(DateTime.Today.AddDays(0), 100m, 110m, 95m, 105m, 1000),
        new(DateTime.Today.AddDays(1), 106m, 115m, 102m, 112m, 1200),
        new(DateTime.Today.AddDays(2), 111m, 118m, 108m, 110m, 1100),
        new(DateTime.Today.AddDays(3), 109m, 114m, 100m, 102m, 1500),
        new(DateTime.Today.AddDays(4), 103m, 108m, 98m, 107m, 1300),
    };

    private static ScreenerIndicatorSideConfig HeikinAshiConfig(PriceType type, int offset) => new()
    {
        IndicatorType = IndicatorType.Price,
        OutputName = type.ToString(),
        CustomDisplayName = PriceDataHelper.FormatPriceTypeLabel(type),
        Offset = offset
    };

    // Spec indicators_formulas_0466: HA_Open_t = (HA_Open_{t-1} + HA_Close_{t-1}) / 2, seeded with (O_0 + C_0) / 2.
    // Rows: targetIdx, HA Open, HA High, HA Low, HA Close (hand computed, exact decimals).
    [Theory]
    [InlineData(0, 102.5, 110, 95, 102.5)]
    [InlineData(1, 102.5, 115, 102, 108.75)]
    [InlineData(2, 105.625, 118, 105.625, 111.75)]
    [InlineData(3, 108.6875, 114, 100, 106.25)]
    [InlineData(4, 107.46875, 108, 98, 104)]
    public void ExtractValue_HeikinAshi_UsesRecursiveState(int targetIdx, double open, double high, double low, double close)
    {
        var extractor = ScreenerValueExtractor.Default;
        var candles = HeikinAshiRecursionCandles();
        int offset = candles.Count - 1 - targetIdx;

        Assert.Equal((decimal)open, extractor.ExtractValue(HeikinAshiConfig(PriceType.HeikinAshiOpen, offset), candles));
        Assert.Equal((decimal)high, extractor.ExtractValue(HeikinAshiConfig(PriceType.HeikinAshiHigh, offset), candles));
        Assert.Equal((decimal)low, extractor.ExtractValue(HeikinAshiConfig(PriceType.HeikinAshiLow, offset), candles));
        Assert.Equal((decimal)close, extractor.ExtractValue(HeikinAshiConfig(PriceType.HeikinAshiClose, offset), candles));
    }

    [Fact]
    public void ExtractValue_HeikinAshi_SingleCandle_UsesSeed()
    {
        var extractor = ScreenerValueExtractor.Default;
        var candles = new List<CoreCandleData> { new(DateTime.Today, 100m, 120m, 80m, 110m, 1000) };

        Assert.Equal(105m, extractor.ExtractValue(HeikinAshiConfig(PriceType.HeikinAshiOpen, 0), candles));
        Assert.Equal(102.5m, extractor.ExtractValue(HeikinAshiConfig(PriceType.HeikinAshiClose, 0), candles));
    }

    [Theory]
    [InlineData(PriceType.HeikinAshiOpen)]
    [InlineData(PriceType.HeikinAshiHigh)]
    [InlineData(PriceType.HeikinAshiLow)]
    [InlineData(PriceType.HeikinAshiClose)]
    public void ExtractValue_HeikinAshi_MatchesRecursiveSeriesAtEveryIndex(PriceType type)
    {
        var extractor = ScreenerValueExtractor.Default;
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < 40; i++)
        {
            decimal open = 100m + (i % 7) * 1.5m - (i % 3) * 0.75m;
            decimal close = open + ((i % 5) - 2) * 1.25m;
            decimal high = Math.Max(open, close) + 1m + (i % 4) * 0.5m;
            decimal low = Math.Min(open, close) - 1m - (i % 3) * 0.25m;
            candles.Add(new CoreCandleData(DateTime.Today.AddDays(i), open, high, low, close, 1000 + i));
        }

        var expected = PriceDataHelper.ExtractPriceSeries(candles, type);

        for (int idx = 0; idx < candles.Count; idx++)
        {
            int offset = candles.Count - 1 - idx;
            Assert.Equal(expected[idx]!.Value, extractor.ExtractValue(HeikinAshiConfig(type, offset), candles));
        }
    }
}

