using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.Common;

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

    private static List<CoreCandleData> WavyCandles(int count)
    {
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < count; i++)
        {
            decimal price = 100m + 10m * (decimal)Math.Sin(i * 0.3);
            candles.Add(new CoreCandleData(DateTime.Today.AddDays(i), price, price + 1m, price - 1m, price, 1000));
        }
        return candles;
    }

    // A condition evaluated on a thread must not inherit the settings of the previous condition for the same indicator type
    // (the extractor reuses one indicator instance per type and thread).
    [Fact]
    public void ExtractValue_EmptyParameters_DoesNotInheritPreviousIndicatorSettings()
    {
        var candles = WavyCandles(60);
        var custom = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.SMA, Parameters = new Dictionary<string, object> { { "Period", 50 } } };
        var defaults = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.SMA };

        decimal expectedDefault = new ScreenerValueExtractor().ExtractValue(defaults, candles);
        var extractor = new ScreenerValueExtractor();
        int threadId = Environment.CurrentManagedThreadId;
        decimal customValue = extractor.ExtractValue(custom, candles);
        decimal afterCustom = extractor.ExtractValue(defaults, candles);

        Assert.Equal(threadId, Environment.CurrentManagedThreadId);
        Assert.NotEqual(expectedDefault, customValue);
        Assert.Equal(expectedDefault, afterCustom);
    }

    [Fact]
    public void ExtractValue_PartialParameters_DoesNotInheritPreviousIndicatorSettings()
    {
        var candles = WavyCandles(60);
        var first = new ScreenerIndicatorSideConfig
        {
            IndicatorType = IndicatorType.MACD,
            Parameters = new Dictionary<string, object> { { "ShortPeriod", 5 }, { "LongPeriod", 10 }, { "SignalPeriod", 3 } }
        };
        var partial = new ScreenerIndicatorSideConfig
        {
            IndicatorType = IndicatorType.MACD,
            Parameters = new Dictionary<string, object> { { "ShortPeriod", 8 } }
        };

        decimal expected = new ScreenerValueExtractor().ExtractValue(partial, candles);
        var extractor = new ScreenerValueExtractor();
        decimal firstValue = extractor.ExtractValue(first, candles);
        decimal afterFirst = extractor.ExtractValue(partial, candles);

        Assert.NotEqual(expected, firstValue);
        Assert.Equal(expected, afterFirst);
    }

    // ---- Missing data is never a value (spec S1-S4): a missing bar, a warm-up value or an out-of-range / negative Offset is null, never 0m or the latest bar ----

    // Closes c_i = 100 + i, O = H = L = C, i = 0..count-1.
    private static List<CoreCandleData> RisingCandles(int count)
    {
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < count; i++)
        {
            decimal price = 100m + i;
            candles.Add(new CoreCandleData(new DateTime(2024, 1, 1).AddDays(i), price, price, price, price, 1000));
        }
        return candles;
    }

    private static ScreenerIndicatorSideConfig CloseSide(int offset) => new()
    {
        IndicatorType = IndicatorType.Price,
        OutputName = nameof(PriceType.Close),
        Offset = offset
    };

    // 100 bars: the last bar is index 99, so Offset o reads index 99 - o.
    [Theory]
    [InlineData(150)]
    [InlineData(100)]
    [InlineData(-1)]
    public void Extract_PriceOffsetOutsideData_IsMissing(int offset)
    {
        var extractor = new ScreenerValueExtractor();
        var candles = RisingCandles(100);

        Assert.Null(extractor.ExtractValueNullable(CloseSide(offset), candles, default(TickerMetadata)));
        Assert.Equal(0m, extractor.ExtractValue(CloseSide(offset), candles, default(TickerMetadata)));
    }

    [Theory]
    [InlineData(99, 100)]
    [InlineData(0, 199)]
    public void Extract_PriceOffsetInsideData_IsTheBarValue(int offset, int expectedClose)
    {
        var extractor = new ScreenerValueExtractor();
        var candles = RisingCandles(100);

        Assert.Equal((decimal)expectedClose, extractor.ExtractValueNullable(CloseSide(offset), candles, default(TickerMetadata)));
    }

    [Fact]
    public void Extract_CandleDataOverload_OffsetOutsideData_IsMissing()
    {
        var extractor = new ScreenerValueExtractor();
        var candles = new List<CandleData>();
        foreach (var c in RisingCandles(100))
        {
            candles.Add(new CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume));
        }
        var column = new ScreenerIndicatorSideConfig { CategoryType = ScreenerItemCategoryType.Column, OutputName = "Close", Offset = 150 };

        Assert.Null(extractor.ExtractValueNullable(CloseSide(150), candles));
        Assert.Null(extractor.ExtractValueNullable(column, candles));
        Assert.Equal(0m, extractor.ExtractValue(column, candles));
    }

    // SMA 5 over 30 bars: index 4 is the first bar with a value (102 = average of 100..104).
    [Fact]
    public void Extract_SmaWarmupBoundary_FirstValueAtIndexFour()
    {
        var extractor = new ScreenerValueExtractor();
        var candles = RisingCandles(30);
        var sma = new Dictionary<string, object> { { "Period", 5 } };

        Assert.Equal(102m, extractor.ExtractValueNullable(new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.SMA, Parameters = sma, Offset = 25 }, candles, default(TickerMetadata)));
        Assert.Null(extractor.ExtractValueNullable(new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.SMA, Parameters = sma, Offset = 26 }, candles, default(TickerMetadata)));
    }

    [Fact]
    public void Extract_TooFewBars_IsMissing()
    {
        var extractor = new ScreenerValueExtractor();
        var rsi = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.RSI, Parameters = new Dictionary<string, object> { { "Period", 14 } } };

        Assert.Null(extractor.ExtractValueNullable(rsi, RisingCandles(5), default(TickerMetadata)));
        Assert.Equal(0m, extractor.ExtractValue(rsi, RisingCandles(5)));
    }

    // Fundamentals are not read from a bar series: Offset does not apply to them (spec S2).
    [Fact]
    public void Extract_MetadataColumn_IgnoresOffset()
    {
        var extractor = new ScreenerValueExtractor();
        var meta = new TickerMetadata { TrailingPE = 25m };
        var column = new ScreenerIndicatorSideConfig { CategoryType = ScreenerItemCategoryType.Column, OutputName = "Trailing PE", Offset = 150 };

        Assert.Equal(25m, extractor.ExtractValueNullable(column, RisingCandles(100), meta));
    }

    // ---- Criteria: the pattern is evaluated on the bars up to index k = n - 1 - Offset, and Granville gets the window it needs (spec S1, S3) ----

    private static ScreenerIndicatorSideConfig CriteriaSide(string name, int offset = 0) => new()
    {
        CategoryType = ScreenerItemCategoryType.Criteria,
        CustomDisplayName = name,
        Offset = offset
    };

    private static List<CoreCandleData> FlatCandles(int count, decimal price)
    {
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < count; i++)
        {
            candles.Add(new CoreCandleData(new DateTime(2024, 1, 1).AddDays(i), price, price, price, price, 1000));
        }
        return candles;
    }

    // 499 flat bars at 100 followed by one bar closing at 110: the price crosses above a flat moving average on the last bar.
    private static List<CoreCandleData> FlatThenBreakout(int flatBars)
    {
        var candles = FlatCandles(flatBars, 100m);
        candles.Add(new CoreCandleData(new DateTime(2024, 1, 1).AddDays(flatBars), 110m, 110m, 110m, 110m, 1000));
        return candles;
    }

    [Fact]
    public void Granville_NewBuy_AtLastBar_IsOne()
    {
        var extractor = new ScreenerValueExtractor();

        Assert.Equal(1m, extractor.ExtractValue(CriteriaSide("Granville's Law Buy Signal"), FlatThenBreakout(499), default(TickerMetadata)));
    }

    // The window the extractor hands to Granville must give the same last-bar signal as the whole history before that bar.
    [Fact]
    public void Granville_WindowIndependence()
    {
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < 400; i++)
        {
            decimal price = Math.Round(100m + 15m * (decimal)Math.Sin(i / 7.0), 4);
            candles.Add(new CoreCandleData(new DateTime(2024, 1, 1).AddDays(i), price, price, price, price, 1000));
        }
        var all = ToCandleData(candles);
        var condition = new StockAnalyzer.Core.Models.ScreeningConditions.GranvilleLawCondition(StockAnalyzer.Core.Models.ScreeningConditions.GranvilleLawConditionType.AnyBuy);

        var expected = new Dictionary<int, bool>();
        for (int k = 250; k < 400; k++)
        {
            expected[k] = condition.IsMet(all.GetRange(0, k + 1));
        }
        Assert.True(expected.Values.Count(v => v) >= 3, "The series must contain at least 3 Granville buy bars in 250..399.");

        var extractor = new ScreenerValueExtractor();
        for (int k = 250; k < 400; k++)
        {
            decimal? value = extractor.ExtractValueNullable(CriteriaSide("Granville's Law Buy Signal", 399 - k), candles, default(TickerMetadata));
            Assert.Equal(expected[k] ? 1m : 0m, value);
        }
    }

    [Fact]
    public void Granville_Offset1_IsZero()
    {
        var extractor = new ScreenerValueExtractor();

        Assert.Equal(0m, extractor.ExtractValueNullable(CriteriaSide("Granville's Law Buy Signal", 1), FlatThenBreakout(499), default(TickerMetadata)));
    }

    [Fact]
    public void Granville_TooFewBars_IsZeroNotMissing()
    {
        var extractor = new ScreenerValueExtractor();

        Assert.Equal(0m, extractor.ExtractValueNullable(CriteriaSide("Granville's Law Buy Signal"), FlatThenBreakout(204), default(TickerMetadata)));
    }

    [Fact]
    public void Criteria_OffsetBeyondData_IsMissing()
    {
        var extractor = new ScreenerValueExtractor();
        var side = CriteriaSide("Bullish Engulfing", 60);

        Assert.Null(extractor.ExtractValueNullable(side, FlatCandles(60, 100m), default(TickerMetadata)));
        Assert.Equal(0m, extractor.ExtractValue(side, FlatCandles(60, 100m), default(TickerMetadata)));
    }

    // 60 bars: flat at 100 (0..39), a bearish bar (40), a bullish engulfing bar (41), then flat at 106 (42..59).
    private static List<CoreCandleData> EngulfingAtIndex41()
    {
        var candles = FlatCandles(40, 100m);
        candles.Add(new CoreCandleData(new DateTime(2024, 3, 1), 105m, 105m, 100m, 100m, 1000));
        candles.Add(new CoreCandleData(new DateTime(2024, 3, 2), 99m, 106m, 99m, 106m, 1000));
        candles.AddRange(FlatCandles(18, 106m));
        return candles;
    }

    private static List<CandleData> ToCandleData(List<CoreCandleData> core)
        => core.ConvertAll(c => new CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume));

    [Fact]
    public void Criteria_Offset_ShiftsEvaluationBar()
    {
        var core = EngulfingAtIndex41();
        var detector = new StockAnalyzer.Core.Models.ScreeningConditions.CandlePatternCondition(CandlePatternType.BullishEngulfing);
        // Detector precondition: the pattern is on bar 41 and not on the last bar.
        Assert.True(detector.IsMet(ToCandleData(core.GetRange(0, 42))));
        Assert.False(detector.IsMet(ToCandleData(core)));

        var extractor = new ScreenerValueExtractor();

        Assert.Equal(0m, extractor.ExtractValueNullable(CriteriaSide("Bullish Engulfing", 0), core, default(TickerMetadata)));
        Assert.Equal(1m, extractor.ExtractValueNullable(CriteriaSide("Bullish Engulfing", 18), core, default(TickerMetadata)));
    }

    [Fact]
    public void Criteria_DefaultWindow100_Unchanged()
    {
        var extractor = new ScreenerValueExtractor();
        var candles = FlatCandles(148, 100m);
        candles.Add(new CoreCandleData(new DateTime(2024, 9, 1), 105m, 105m, 100m, 100m, 1000));
        candles.Add(new CoreCandleData(new DateTime(2024, 9, 2), 99m, 106m, 99m, 106m, 1000));

        Assert.Equal(150, candles.Count);
        Assert.Equal(1m, extractor.ExtractValueNullable(CriteriaSide("Bullish Engulfing"), candles, default(TickerMetadata)));
    }

    // ---- One conversion per symbol: the CandleData list is converted once and reused by the next call on the thread ----

    private static List<CandleData> RisingCandleData(int count)
    {
        var candles = new List<CandleData>();
        for (int i = 0; i < count; i++)
        {
            decimal price = 100m + i;
            candles.Add(new CandleData(new DateTime(2024, 1, 1).AddDays(i), price, price + 1m, price - 1m, price, 1000));
        }
        return candles;
    }

    private static ScreenerIndicatorSideConfig Sma(int period) => new()
    {
        IndicatorType = IndicatorType.SMA,
        Parameters = new Dictionary<string, object> { { "Period", period } }
    };

    [Fact]
    public void ExtractValue_SameCandleList_SecondCallDoesNotConvertAgain()
    {
        var extractor = new ScreenerValueExtractor();
        extractor.ExtractValue(Sma(5), RisingCandleData(500)); // creates the indicator instance, so only the candle conversion differs below
        var list = RisingCandleData(500);

        // A garbage collection another test triggers meanwhile adds bytes to this thread's counter although it allocated nothing, so each side is
        // a sample in which no collection ran (AllocationProbe). A cold sample converts a fresh list each time; the warm samples reuse the last one.
        decimal cold = 0;
        decimal warm = 0;
        long coldBytes = AllocationProbe.InCollectionFreeWindow(() => cold = extractor.ExtractValue(Sma(5), list), prepare: () => list = RisingCandleData(500));
        long warmBytes = AllocationProbe.InCollectionFreeWindow(() => warm = extractor.ExtractValue(Sma(5), list));

        Assert.True(coldBytes >= 0 && warmBytes >= 0, "every sample overlapped a garbage collection");
        Assert.Equal(cold, warm);
        // The cold call converts the 500 bars, the warm call does not: it allocates clearly less (measured about one third).
        Assert.True(warmBytes < coldBytes * 0.75, $"warm {warmBytes} bytes, cold {coldBytes} bytes");
    }

    [Fact]
    public void ExtractValue_ListChangedAtTheLastBar_IsConvertedAgain()
    {
        var extractor = new ScreenerValueExtractor();
        var list = RisingCandleData(10);
        decimal before = extractor.ExtractValue(Sma(2), list); // mean of the last two closes: 108, 109

        list[^1] = new CandleData(list[^1].Timestamp, 200m, 201m, 199m, 200m, 1000);
        decimal afterReplace = extractor.ExtractValue(Sma(2), list);

        list.Add(new CandleData(new DateTime(2025, 1, 1), 300m, 301m, 299m, 300m, 1000));
        decimal afterAppend = extractor.ExtractValue(Sma(2), list);

        Assert.Equal(108.5m, before);
        Assert.Equal(154m, afterReplace);
        Assert.Equal(250m, afterAppend);
    }

    [Fact]
    public void ExtractValue_DifferentListOfTheSameShape_IsNotMistakenForTheCachedOne()
    {
        var extractor = new ScreenerValueExtractor();
        var first = RisingCandleData(10);
        var second = RisingCandleData(10);
        second[4] = new CandleData(second[4].Timestamp, 1m, 2m, 0m, 1m, 1000); // same count and first / last bar, but another list object

        extractor.ExtractValue(Sma(10), first);

        Assert.Equal(new ScreenerValueExtractor().ExtractValue(Sma(10), second), extractor.ExtractValue(Sma(10), second));
    }
}

