using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class AdaptiveSmoothingHelperTests
{
    private readonly List<CoreCandleData> _sampleCandles;
    private readonly List<decimal?> _sampleSeries;

    public AdaptiveSmoothingHelperTests()
    {
        _sampleCandles = new List<CoreCandleData>();
        _sampleSeries = new List<decimal?>();
        var startDate = new DateTime(2023, 1, 1);
        decimal price = 100m;
        for (int i = 0; i < 50; i++)
        {
            decimal change = (i % 2 == 0) ? 2.5m : -1.5m;
            price += change;
            _sampleCandles.Add(new CoreCandleData(startDate.AddDays(i), price - 1, price + 2, price - 2, price, 1000));
            _sampleSeries.Add(price);
        }
    }

    [Fact]
    public void ClampPeriod_WithNullOrInvalidValues_ReturnsDefaultPeriod()
    {
        Assert.Equal(14m, AdaptiveSmoothingHelper.ClampPeriod(null, 14m, 5m, 50m));
        Assert.Equal(14m, AdaptiveSmoothingHelper.ClampPeriod(0m, 14m, 5m, 50m));
        Assert.Equal(14m, AdaptiveSmoothingHelper.ClampPeriod(-5m, 14m, 5m, 50m));
    }

    [Fact]
    public void ClampPeriod_WithOutOfRangeValues_ClampsCorrectly()
    {
        Assert.Equal(5m, AdaptiveSmoothingHelper.ClampPeriod(2m, 14m, 5m, 50m));
        Assert.Equal(50m, AdaptiveSmoothingHelper.ClampPeriod(100m, 14m, 5m, 50m));
        Assert.Equal(25m, AdaptiveSmoothingHelper.ClampPeriod(25m, 14m, 5m, 50m));
    }

    [Fact]
    public void CalculateAlphaForWilder_ReturnsExpectedAlpha()
    {
        Assert.Equal(1.0m / 10m, AdaptiveSmoothingHelper.CalculateAlphaForWilder(10m, 14m, 5m, 50m));
        Assert.Equal(1.0m / 14m, AdaptiveSmoothingHelper.CalculateAlphaForWilder(null, 14m, 5m, 50m));
    }

    [Fact]
    public void CalculateAlphaForEma_ReturnsExpectedAlpha()
    {
        Assert.Equal(2.0m / (9m + 1m), AdaptiveSmoothingHelper.CalculateAlphaForEma(9m, 20m, 5m, 100m));
        Assert.Equal(2.0m / (20m + 1m), AdaptiveSmoothingHelper.CalculateAlphaForEma(null, 20m, 5m, 100m));
    }

    [Fact]
    public void CalculateAdaptiveWilderRsi_WithEmptyCandles_ReturnsEmpty()
    {
        var result = AdaptiveSmoothingHelper.CalculateAdaptiveWilderRsi(new List<CoreCandleData>(), null);
        Assert.Empty(result);

        var seriesResult = AdaptiveSmoothingHelper.CalculateAdaptiveWilderRsi(new List<decimal?>(), null);
        Assert.Empty(seriesResult);
    }

    [Fact]
    public void CalculateAdaptiveWilderRsi_WithInsufficientCandles_ReturnsNulls()
    {
        var shortCandles = _sampleCandles.Take(10).ToList();
        var result = AdaptiveSmoothingHelper.CalculateAdaptiveWilderRsi(shortCandles, null, defaultPeriod: 14);

        Assert.Equal(10, result.Count);
        Assert.All(result, Assert.Null);
    }

    [Fact]
    public void CalculateAdaptiveWilderRsi_WithNullDynamicPeriods_ProducesValidRsiSeries()
    {
        var resultCandles = AdaptiveSmoothingHelper.CalculateAdaptiveWilderRsi(_sampleCandles, null, defaultPeriod: 14);
        var resultSeries = AdaptiveSmoothingHelper.CalculateAdaptiveWilderRsi(_sampleSeries, null, defaultPeriod: 14);

        Assert.Equal(_sampleCandles.Count, resultCandles.Count);
        Assert.Equal(resultCandles, resultSeries);

        for (int i = 0; i < 14; i++)
        {
            Assert.Null(resultCandles[i]);
        }
        for (int i = 14; i < resultCandles.Count; i++)
        {
            Assert.NotNull(resultCandles[i]);
            Assert.InRange(resultCandles[i]!.Value, 0m, 100m);
        }
    }

    [Fact]
    public void CalculateAdaptiveWilderRsi_WithDynamicPeriods_ProducesBoundedAdaptiveValues()
    {
        var dynamicPeriods = new List<decimal?>();
        for (int i = 0; i < _sampleCandles.Count; i++)
        {
            dynamicPeriods.Add(8m + (i % 22));
        }

        var result = AdaptiveSmoothingHelper.CalculateAdaptiveWilderRsi(_sampleCandles, dynamicPeriods, defaultPeriod: 14);

        Assert.Equal(_sampleCandles.Count, result.Count);
        for (int i = 14; i < result.Count; i++)
        {
            Assert.NotNull(result[i]);
            Assert.InRange(result[i]!.Value, 0m, 100m);
        }
    }

    [Fact]
    public void CalculateAdaptiveEma_WithNullDynamicPeriods_MatchesStandardEmaCalculation()
    {
        var resultCandles = AdaptiveSmoothingHelper.CalculateAdaptiveEma(_sampleCandles, null, defaultPeriod: 10);
        var resultSeries = AdaptiveSmoothingHelper.CalculateAdaptiveEma(_sampleSeries, null, defaultPeriod: 10);

        Assert.Equal(_sampleCandles.Count, resultCandles.Count);
        Assert.Equal(resultCandles, resultSeries);

        for (int i = 0; i < 9; i++)
        {
            Assert.Null(resultCandles[i]);
        }
        for (int i = 9; i < resultCandles.Count; i++)
        {
            Assert.NotNull(resultCandles[i]);
        }
    }

    [Fact]
    public void CalculateAdaptiveSma_WithDynamicPeriods_CalculatesAveragesCorrectly()
    {
        var dynamicPeriods = new List<decimal?>();
        for (int i = 0; i < _sampleCandles.Count; i++)
        {
            dynamicPeriods.Add(5m); // Constant 5-period SMA
        }

        var resultCandles = AdaptiveSmoothingHelper.CalculateAdaptiveSma(_sampleCandles, dynamicPeriods, defaultPeriod: 10);
        var resultSeries = AdaptiveSmoothingHelper.CalculateAdaptiveSma(_sampleSeries, dynamicPeriods, defaultPeriod: 10);

        Assert.Equal(_sampleCandles.Count, resultCandles.Count);
        Assert.Equal(resultCandles, resultSeries);

        for (int i = 0; i < 4; i++)
        {
            Assert.Null(resultCandles[i]);
        }
        for (int i = 4; i < resultCandles.Count; i++)
        {
            Assert.NotNull(resultCandles[i]);
            decimal expectedSma = _sampleCandles.Skip(i - 4).Take(5).Average(c => c.Close);
            Assert.Equal(expectedSma, resultCandles[i]!.Value);
        }
    }

    [Fact]
    public void MapPeriod_DirectAndInverseAndNormalized_CalculatesExpectedValues()
    {
        // Direct mapping
        Assert.Equal(20m, AdaptiveSmoothingHelper.MapPeriod(20m, 14m, 2m, 200m, DynamicPeriodMappingMode.Direct));

        // InverseRatio mapping (High volatility 50 vs reference 25 -> Base 14 * (25 / 50) = 7)
        decimal inverseMapped = AdaptiveSmoothingHelper.MapPeriod(50m, 14m, 2m, 200m, DynamicPeriodMappingMode.InverseRatio, referenceValue: 25m);
        Assert.Equal(7m, inverseMapped);

        // NormalizedRange mapping (ratio 0.0 -> max 50, ratio 1.0 -> min 5, ratio 0.5 -> 27.5)
        decimal normMapped = AdaptiveSmoothingHelper.MapPeriod(0.5m, 14m, 5m, 50m, DynamicPeriodMappingMode.NormalizedRange);
        Assert.Equal(27.5m, normMapped);
    }

    [Fact]
    public void SmoothDriverSeries_ReducesJitterAndPreservesNulls()
    {
        var rawDriver = new List<decimal?> { 10m, 20m, 30m, null, 20m };
        var smoothed = AdaptiveSmoothingHelper.SmoothDriverSeries(rawDriver, smoothingBeta: 0.5m);

        Assert.Equal(5, smoothed.Count);
        Assert.Equal(10m, smoothed[0]);
        Assert.Equal(15m, smoothed[1]); // 0.5*20 + 0.5*10 = 15
        Assert.Equal(22.5m, smoothed[2]); // 0.5*30 + 0.5*15 = 22.5
        Assert.Equal(22.5m, smoothed[3]); // holds previous
    }

    [Fact]
    public void CalculateAdaptiveSma_FractionalInterpolation_ProducesContinuousSmoothValues()
    {
        var series = new List<decimal?> { 10m, 20m, 30m, 40m, 50m };
        var periods = new List<decimal?> { 2.5m, 2.5m, 2.5m, 2.5m, 2.5m };

        // For index 2: values are 10, 20, 30
        // kFloor = 2 (sum = 20+30 = 50 / 2 = 25)
        // kCeil = 3 (sum = 10+20+30 = 60 / 3 = 20)
        // frac = 0.5 -> interpolated = 0.5*25 + 0.5*20 = 22.5
        var results = AdaptiveSmoothingHelper.CalculateAdaptiveSma(series, periods, defaultPeriod: 2);

        Assert.NotNull(results[2]);
        Assert.Equal(22.5m, results[2]!.Value);
    }
}
