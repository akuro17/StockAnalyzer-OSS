using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;
using System.Reflection;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Indicators.Chart;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.Core.Tests.Indicators;

public class NestedIndicatorWarmupTests
{
    private readonly ITestOutputHelper _output;

    public NestedIndicatorWarmupTests(ITestOutputHelper output)
    {
        _output = output;
    }
    private static List<CoreCandleData> CreateTestCandles(int count, decimal basePrice = 100m)
    {
        var startDate = new DateTime(2025, 1, 1);
        var list = new List<CoreCandleData>(count);
        for (int i = 0; i < count; i++)
        {
            decimal p = basePrice + (i * 0.5m);
            list.Add(new CoreCandleData(startDate.AddDays(i), p, p + 1m, p - 1m, p, 1000));
        }
        return list;
    }

    [Fact]
    public void CalculateSeries_SmaOverEma_OffsetsWarmupAndDoesNotSinkToZero()
    {
        // Arrange
        // Base indicator: EMA(5) -> first 4 values are null, 5th value is non-null (~102)
        var ema = new CoreEmaIndicator { Period = 5 };
        var candles = CreateTestCandles(20, basePrice: 100m);
        var emaResult = ema.Calculate(candles);

        Assert.Equal(20, emaResult.Count);
        for (int i = 0; i < 4; i++)
        {
            Assert.Null(emaResult[i]);
        }
        Assert.NotNull(emaResult[4]);
        Assert.InRange(emaResult[4]!.Value, 90m, 110m);

        // Act
        // Outer indicator: SMA(3) over EMA(5) series
        var sma = new CoreSmaIndicator { Period = 3 };
        var smaResult = sma.CalculateSeries(emaResult.MainValues);

        // Assert
        Assert.Equal(20, smaResult.Count);

        // Indices 0, 1, 2, 3 are null due to EMA warmup.
        // Indices 4, 5 are null due to SMA requiring 3 consecutive valid EMA values (indices 4, 5, 6).
        // First valid SMA index should be index 6.
        for (int i = 0; i < 6; i++)
        {
            Assert.Null(smaResult[i]);
        }

        // First non-null SMA value must be close to ~102, NEVER falling down towards 0!
        Assert.NotNull(smaResult[6]);
        Assert.InRange(smaResult[6]!.Value, 95m, 115m);
        Assert.True(smaResult[6]!.Value > 50m, "First nested SMA value must not sink to near-zero!");
    }

    [Fact]
    public void CalculateSeries_SmaOverRsi_OffsetsWarmupAndDoesNotSinkToZero()
    {
        // Arrange
        // Base indicator: RSI(5) -> warmup requires 5 periods
        var rsi = new CoreRsiIndicator { Period = 5 };
        var candles = CreateTestCandles(25, basePrice: 100m);
        var rsiResult = rsi.Calculate(candles);

        Assert.Equal(25, rsiResult.Count);
        for (int i = 0; i < 5; i++)
        {
            Assert.Null(rsiResult[i]);
        }
        Assert.NotNull(rsiResult[5]);
        Assert.InRange(rsiResult[5]!.Value, 0m, 100m);

        // Act
        // Outer indicator: SMA(3) over RSI(5) series
        var sma = new CoreSmaIndicator { Period = 3 };
        var smaResult = sma.CalculateSeries(rsiResult.MainValues);

        // Assert
        Assert.Equal(25, smaResult.Count);

        // Warmup indices must be null
        for (int i = 0; i < 7; i++)
        {
            Assert.Null(smaResult[i]);
        }

        // First valid SMA(RSI) value should reflect actual RSI average, not 0
        Assert.NotNull(smaResult[7]);
        Assert.InRange(smaResult[7]!.Value, 1m, 100m);
    }

    [Fact]
    public void Enumerate_Indicators_NotOverriding_CalculateSeriesCore()
    {
        var factory = IndicatorFactory.Default;
        var registeredTypes = factory.GetRegisteredTypes().OrderBy(t => t.ToString()).ToList();

        var notOverriding = new List<string>();
        var overriding = new List<string>();
        var returningCustomOrSignal = new List<string>();

        // Sample series with F=5 leading nulls and 20 valid values
        var sampleSeries = new List<decimal?>();
        for (int i = 0; i < 5; i++) sampleSeries.Add(null);
        for (int i = 0; i < 20; i++) sampleSeries.Add(100m + i);

        foreach (var type in registeredTypes)
        {
            var indicator = factory.Create(type);
            if (indicator == null) continue;

            var indType = indicator.GetType();
            var method = indType.GetMethod("CalculateSeriesCore", BindingFlags.Instance | BindingFlags.NonPublic);
            bool isOverridden = method != null && method.DeclaringType != typeof(CoreIndicatorBase);

            if (isOverridden)
            {
                overriding.Add(type.ToString());
            }
            else
            {
                notOverriding.Add(type.ToString());
                // Test if it returns CustomData or SignalProvider
                try
                {
                    var res = indicator.CalculateSeries(sampleSeries);
                    if (res != null && (res.CustomData != null || res.SignalProvider != null))
                    {
                        returningCustomOrSignal.Add(type.ToString());
                    }
                }
                catch
                {
                    // Some indicators may require specific parameters or candle fields
                }
            }
        }

        _output.WriteLine($"Total registered: {registeredTypes.Count}");
        _output.WriteLine($"Overriding CalculateSeriesCore ({overriding.Count}): {string.Join(", ", overriding)}");
        _output.WriteLine($"Not Overriding CalculateSeriesCore ({notOverriding.Count}): {string.Join(", ", notOverriding)}");
        _output.WriteLine($"Returning CustomData/SignalProvider ({returningCustomOrSignal.Count}): {string.Join(", ", returningCustomOrSignal)}");

        Assert.True(registeredTypes.Count > 0);
    }

    [Fact]
    public void CalculateSeries_ContaminatedWindow_Characterization_StdDev_BB_Keltner()
    {
        // F = 14 leading nulls + 40 valid values (total n = 54)
        int f = 14;
        var series = new List<decimal?>(54);
        for (int i = 0; i < f; i++) series.Add(null);
        for (int i = 0; i < 40; i++) series.Add(100m + i * 0.5m);

        // 1. StdDev(5): Warmup = 5 -> indices [14, 17] must be null; index 18 first non-null
        var stdDev = new CoreStandardDeviationIndicator();
        stdDev.Configure(new CoreStandardDeviationParameter { Period = 5 });
        var stdResult = stdDev.CalculateSeries(series);
        Assert.Equal(54, stdResult.Count);
        for (int i = 0; i < f + 5 - 1; i++)
        {
            Assert.Null(stdResult[i]);
        }
        Assert.NotNull(stdResult[f + 5 - 1]);

        // 2. BollingerBands(20): Warmup = 20 -> indices [14, 32] must be null; index 33 first non-null
        var bb = new CoreBollingerBandsIndicator();
        bb.Configure(new CoreBollingerBandsParameter { Period = 20 });
        var bbResult = bb.CalculateSeries(series);
        Assert.Equal(54, bbResult.Count);
        for (int i = 0; i < f + 20 - 1; i++)
        {
            Assert.Null(bbResult[i]);
            Assert.Null(bbResult.GetSeries("UpperBand")[i]);
            Assert.Null(bbResult.GetSeries("LowerBand")[i]);
        }
        Assert.NotNull(bbResult[f + 20 - 1]);

        // 3. KeltnerChannel(EmaPeriod=20, AtrPeriod=10): Warmup = 20 -> indices [14, 32] must be null
        var keltner = new CoreKeltnerChannelIndicator();
        keltner.Configure(new CoreKeltnerParameter { EmaPeriod = 20, AtrPeriod = 10 });
        var kcResult = keltner.CalculateSeries(series);
        Assert.Equal(54, kcResult.Count);
        for (int i = 0; i < f + 20 - 1; i++)
        {
            Assert.Null(kcResult[i]);
            Assert.Null(kcResult.GetSeries("UpperBand")[i]);
            Assert.Null(kcResult.GetSeries("LowerBand")[i]);
        }
        Assert.NotNull(kcResult[f + 20 - 1]);

        // Prove that valid-slice values from index 18 onward match un-padded calculation on the 40-value slice
        var slice40 = series.Skip(f).ToList();
        var sliceResult = stdDev.CalculateSeries(slice40);
        Assert.Equal(40, sliceResult.Count);
        for (int k = 0; k < 40; k++)
        {
            Assert.Equal(sliceResult[k], stdResult[f + k]);
        }
    }

    [Fact]
    public void CalculateSeries_FZero_EquivalenceWithLegacyPath_SampleTenIndicators()
    {
        // 50 valid prices (F = 0, no leading nulls)
        var fullSeries = new List<decimal?>(50);
        for (int i = 0; i < 50; i++)
        {
            fullSeries.Add(100m + i * 0.75m);
        }

        IndicatorType[] sampleTypes =
        [
            IndicatorType.StandardDeviation,
            IndicatorType.BB,
            IndicatorType.Keltner,
            IndicatorType.MACD,
            IndicatorType.ATR,
            IndicatorType.ADX,
            IndicatorType.CCI,
            IndicatorType.RVI,
            IndicatorType.ZScore,
            IndicatorType.BandWidth
        ];

        foreach (var type in sampleTypes)
        {
            var indicator = IndicatorFactory.Default.Create(type);
            Assert.NotNull(indicator);

            var result = indicator.CalculateSeries(fullSeries);
            Assert.True(result.IsSuccessful, $"{type} should calculate successfully");
            Assert.Equal(50, result.Count);

            // Verify that direct Calculate with synthetic candles produces exact match
            var syntheticCandles = new List<CoreCandleData>(50);
            var today = DateTime.Today;
            for (int i = 0; i < 50; i++)
            {
                decimal p = fullSeries[i]!.Value;
                syntheticCandles.Add(new CoreCandleData(today.AddDays(i), p, p, p, p, 0));
            }
            var directResult = indicator.Calculate(syntheticCandles);
            Assert.True(directResult.IsSuccessful);

            foreach (var sName in result.SeriesNamesList)
            {
                var seriesA = result.GetSeries(sName);
                var seriesB = directResult.GetSeries(sName);
                Assert.Equal(seriesB.Count, seriesA.Count);
                for (int i = 0; i < 50; i++)
                {
                    Assert.Equal(seriesB[i], seriesA[i]);
                }
            }
        }
    }

    [Fact]
    public void CalculateSeries_FGreaterThanZero_SynchronizesInstanceValuesAndBandProperties()
    {
        // 50 total bars with F = 10 leading nulls
        int total = 50;
        int f = 10;
        var series = new List<decimal?>(total);
        for (int i = 0; i < f; i++)
        {
            series.Add(null);
        }
        for (int i = f; i < total; i++)
        {
            series.Add(100m + (i - f) * 0.75m);
        }

        // Test Bollinger Bands
        var bb = new CoreBollingerBandsIndicator { Period = 14 };
        var bbResult = bb.CalculateSeries(series);
        Assert.True(bbResult.IsSuccessful);
        Assert.Equal(total, bbResult.Count);

        // Instance properties must be synchronized with paddedSeries (length == total, first F are null)
        Assert.Equal(total, bb.Values.Count);
        Assert.Equal(total, bb.UpperBand.Count);
        Assert.Equal(total, bb.LowerBand.Count);

        for (int i = 0; i < f; i++)
        {
            Assert.Null(bb.Values[i]);
            Assert.Null(bb.UpperBand[i]);
            Assert.Null(bb.LowerBand[i]);
        }
        for (int i = f; i < total; i++)
        {
            Assert.Equal(bbResult[i], bb.Values[i]);
            Assert.Equal(bbResult.GetSeries("UpperBand")[i], bb.UpperBand[i]);
            Assert.Equal(bbResult.GetSeries("LowerBand")[i], bb.LowerBand[i]);
        }

        // Test Keltner Channel
        var kc = new CoreKeltnerChannelIndicator();
        kc.Configure(new CoreKeltnerParameter { EmaPeriod = 10, AtrPeriod = 10 });
        var kcResult = kc.CalculateSeries(series);
        Assert.True(kcResult.IsSuccessful);
        Assert.Equal(total, kc.Values.Count);
        Assert.Equal(total, kc.UpperBand.Count);
        Assert.Equal(total, kc.LowerBand.Count);

        for (int i = 0; i < f; i++)
        {
            Assert.Null(kc.Values[i]);
            Assert.Null(kc.UpperBand[i]);
            Assert.Null(kc.LowerBand[i]);
        }
        for (int i = f; i < total; i++)
        {
            Assert.Equal(kcResult[i], kc.Values[i]);
            Assert.Equal(kcResult.GetSeries("UpperBand")[i], kc.UpperBand[i]);
            Assert.Equal(kcResult.GetSeries("LowerBand")[i], kc.LowerBand[i]);
        }
    }

    [Fact]
    public void CalculateSeries_CustomDataLegacyFallback_ResetsStateAndDoesNotDoubleCollections()
    {
        // 40 total bars with F = 5 leading nulls
        int total = 40;
        int f = 5;
        var series = new List<decimal?>(total);
        for (int i = 0; i < f; i++)
        {
            series.Add(null);
        }
        for (int i = f; i < total; i++)
        {
            series.Add(100m + (i - f) * 1.5m);
        }

        // CoreVolumeProfileIndicator returns CustomData (ProfileData), which forces ExecuteLegacyPath fallback
        var vp = new CoreVolumeProfileIndicator();
        var vpResult = vp.CalculateSeries(series);

        Assert.True(vpResult.IsSuccessful);
        Assert.NotNull(vpResult.CustomData);

        // Core defect check: collection must not contain (total - f) + total = 75 elements
        Assert.Equal(total, vpResult.Count);
        Assert.Equal(total, vpResult.GetSeries(IndicatorResult.MainSeriesName).Count);
        Assert.Equal(total, vp.Values.Count);

        // First F bars must be null on instance values as well as returned series
        for (int i = 0; i < f; i++)
        {
            Assert.Null(vpResult[i]);
            Assert.Null(vp.Values[i]);
        }

        // Post-warmup bars must be synchronized
        for (int i = f; i < total; i++)
        {
            Assert.Equal(vpResult[i], vp.Values[i]);
        }
    }
}
