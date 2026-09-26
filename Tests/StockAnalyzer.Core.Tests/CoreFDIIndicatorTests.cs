using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class CoreFDIIndicatorTests
{
    private static List<CoreCandleData> GenerateCandleData(int count, decimal startPrice, Func<int, (decimal High, decimal Low, decimal Close)> priceGenerator)
    {
        var candles = new List<CoreCandleData>(count);
        var date = DateTime.Today;
        for (int i = 0; i < count; i++)
        {
            var (high, low, close) = priceGenerator(i);
            candles.Add(new CoreCandleData(date.AddDays(i), startPrice, high, low, close, 1000));
        }
        return candles;
    }

    [Fact]
    public void Calculate_WithLinearTrend_ReturnsExpectedTrendDimension()
    {
        // 35 bars of strictly monotonic linear trend: price increases by 1 each day
        int period = 30;
        var candles = GenerateCandleData(35, 10m, i => (10m + i, 10m + i, 10m + i));
        var indicator = new CoreFDIIndicator { Period = period, SmoothingPeriod = 0 };
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(35, result.MainValues.Count);

        // Warmup: indices 0..28 must be null
        Assert.True(result.MainValues.Take(period - 1).All(v => v == null));

        // At index 29 (30th bar):
        // In normalized unit square [0, 1] x [0, 1], diagonal straight line has length L = sqrt(1^2 + 1^2) = sqrt(2)
        // D = 1.0 + (ln(sqrt(2)) + ln(2)) / ln(2 * (period - 1)) = 1.0 + 1.5 * ln(2) / ln(58) approx 1.25606
        double expected = 1.0 + (Math.Log(Math.Sqrt(2.0)) + Math.Log(2.0)) / Math.Log(2.0 * (period - 1));
        Assert.NotNull(result.MainValues[period - 1]);
        Assert.Equal(Math.Round((decimal)expected, 4), Math.Round(result.MainValues[period - 1]!.Value, 4));
        Assert.True(result.MainValues[period - 1]!.Value < 1.30m);
        Assert.True(result.MainValues[period - 1]!.Value > 1.20m);
    }

    [Fact]
    public void Calculate_WithConstantPrices_ReturnsTheoreticalMinimumDimension()
    {
        // Flatline prices where High == Low == Close
        int period = 30;
        var candles = GenerateCandleData(35, 100m, _ => (100m, 100m, 100m));
        var indicator = new CoreFDIIndicator { Period = period, SmoothingPeriod = 0 };
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(35, result.MainValues.Count);
        Assert.True(result.MainValues.Take(period - 1).All(v => v == null));

        // When priceRange == 0, horizontal line in unit square has L = 1.0
        // ln(1.0) = 0 -> D = 1.0 + ln(2) / ln(2 * (period - 1)) approx 1.170707
        double expected = 1.0 + Math.Log(2.0) / Math.Log(2.0 * (period - 1));
        for (int i = period - 1; i < 35; i++)
        {
            Assert.Equal(Math.Round((decimal)expected, 4), Math.Round(result.MainValues[i]!.Value, 4));
        }
    }

    [Fact]
    public void Calculate_WithExtremeOscillation_ReturnsTwo()
    {
        // Alternating zigzag: 10, 20, 10, 20...
        int period = 30;
        var candles = GenerateCandleData(35, 10m, i =>
        {
            decimal p = (i % 2 == 0) ? 10m : 20m;
            return (20m, 10m, p);
        });

        var indicator = new CoreFDIIndicator { Period = period, SmoothingPeriod = 0 };
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(35, result.MainValues.Count);
        Assert.True(result.MainValues.Take(period - 1).All(v => v == null));

        // Alternating series fills 2D box -> D is clamped at 2.0
        Assert.NotNull(result.MainValues[period - 1]);
        Assert.Equal(2.0m, result.MainValues[period - 1]!.Value);
    }

    [Fact]
    public void Calculate_ScaleInvariance_ProducesIdenticalResults()
    {
        int period = 30;
        var baseCandles = GenerateCandleData(40, 50m, i => (50m + i * 2m, 45m + i * 2m, 48m + i * 2m + (i % 3)));
        var scaledCandles = GenerateCandleData(40, 500m, i => (500m + i * 20m, 450m + i * 20m, 480m + i * 20m + (i % 3) * 10m));

        var ind1 = new CoreFDIIndicator { Period = period, SmoothingPeriod = 0 };
        var ind2 = new CoreFDIIndicator { Period = period, SmoothingPeriod = 0 };

        var res1 = ind1.Calculate(baseCandles);
        var res2 = ind2.Calculate(scaledCandles);

        for (int i = period - 1; i < 40; i++)
        {
            Assert.NotNull(res1.MainValues[i]);
            Assert.NotNull(res2.MainValues[i]);
            Assert.Equal(Math.Round(res1.MainValues[i]!.Value, 4), Math.Round(res2.MainValues[i]!.Value, 4));
        }
    }

    [Fact]
    public void Calculate_TranslationInvariance_ProducesIdenticalResults()
    {
        int period = 30;
        var baseCandles = GenerateCandleData(40, 50m, i => (50m + i * 2m, 45m + i * 2m, 48m + i * 2m));
        var translatedCandles = GenerateCandleData(40, 1050m, i => (1050m + i * 2m, 1045m + i * 2m, 1048m + i * 2m));

        var ind1 = new CoreFDIIndicator { Period = period, SmoothingPeriod = 0 };
        var ind2 = new CoreFDIIndicator { Period = period, SmoothingPeriod = 0 };

        var res1 = ind1.Calculate(baseCandles);
        var res2 = ind2.Calculate(translatedCandles);

        for (int i = period - 1; i < 40; i++)
        {
            Assert.NotNull(res1.MainValues[i]);
            Assert.NotNull(res2.MainValues[i]);
            Assert.Equal(Math.Round(res1.MainValues[i]!.Value, 6), Math.Round(res2.MainValues[i]!.Value, 6));
        }
    }

    [Fact]
    public void Calculate_DirectionSymmetry_ProducesIdenticalResults()
    {
        int period = 30;
        var upCandles = GenerateCandleData(35, 10m, i => (10m + i, 10m + i, 10m + i));
        var downCandles = GenerateCandleData(35, 100m, i => (100m - i, 100m - i, 100m - i));

        var indUp = new CoreFDIIndicator { Period = period, SmoothingPeriod = 0 };
        var indDown = new CoreFDIIndicator { Period = period, SmoothingPeriod = 0 };

        var resUp = indUp.Calculate(upCandles);
        var resDown = indDown.Calculate(downCandles);

        for (int i = period - 1; i < 35; i++)
        {
            Assert.NotNull(resUp.MainValues[i]);
            Assert.NotNull(resDown.MainValues[i]);
            Assert.Equal(Math.Round(resUp.MainValues[i]!.Value, 6), Math.Round(resDown.MainValues[i]!.Value, 6));
        }
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmptyList()
    {
        var candles = new List<CoreCandleData>();
        var indicator = new CoreFDIIndicator { Period = 30 };
        var result = indicator.Calculate(candles);
        Assert.True(result.IsSuccessful);
        Assert.Empty(result.MainValues);
    }

    [Fact]
    public void Calculate_WithNullData_ReturnsFailure()
    {
        var indicator = new CoreFDIIndicator { Period = 30 };
        var result = indicator.Calculate(null);
        Assert.False(result.IsSuccessful);
    }

    [Fact]
    public void Calculate_WithInsufficientData_ReturnsNullWarmup()
    {
        // 20 bars with Period = 30 -> all values must be null
        var candles = GenerateCandleData(20, 100m, i => (100m + i, 100m - i, 100m));
        var indicator = new CoreFDIIndicator { Period = 30 };
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(20, result.MainValues.Count);
        Assert.True(result.MainValues.All(v => v == null));
    }

    [Fact]
    public void Calculate_WithSmoothing_AppliesEma()
    {
        // 45 bars of linear trend
        int period = 30;
        int smoothing = 5;
        var candles = GenerateCandleData(45, 10m, i => (10m + i, 10m + i, 10m + i));

        var rawIndicator = new CoreFDIIndicator { Period = period, SmoothingPeriod = 0 };
        var rawResult = rawIndicator.Calculate(candles);

        var smoothedIndicator = new CoreFDIIndicator { Period = period, SmoothingPeriod = smoothing };
        var smoothedResult = smoothedIndicator.Calculate(candles);

        Assert.True(rawResult.IsSuccessful);
        Assert.True(smoothedResult.IsSuccessful);
        Assert.Equal(45, smoothedResult.MainValues.Count);

        // Warmup of smoothed series must have nulls for period - 1 + smoothing - 1 = 33 bars
        // First valid value of raw is at index 29. First smoothed value from CalculateEmaWithNulls
        // takes smoothing (5) valid values, so index 29, 30, 31, 32 are warmup nulls in EMA, index 33 is first EMA
        Assert.Null(smoothedResult.MainValues[32]);
        Assert.NotNull(smoothedResult.MainValues[33]);
    }

    [Fact]
    public void Parameter_Validate_EnforcesLowerAndUpperBounds()
    {
        var valid = new CoreFDIParameter { Period = 30, SmoothingPeriod = 0 };
        valid.Validate(); // 0 is valid and means no smoothing

        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreFDIParameter { Period = 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreFDIParameter { Period = 10001 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreFDIParameter { SmoothingPeriod = -1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreFDIParameter { SmoothingPeriod = 1001 }.Validate());
    }

    [Fact]
    public void IndicatorFactory_DiscoversAndInstantiatesFDI()
    {
        var factory = IndicatorFactory.Default;
        Assert.True(factory.IsRegistered(IndicatorType.FDI));

        var indicator = factory.Create(IndicatorType.FDI);
        Assert.NotNull(indicator);
        var fdi = Assert.IsType<CoreFDIIndicator>(indicator);
        Assert.False(fdi.IsOverlay);
        Assert.Equal(30, fdi.Period);
        Assert.Equal(0, fdi.SmoothingPeriod);
    }

    [Fact]
    public void CalculateFractalDimensionIndex_ThrowsOnInvalidArguments()
    {
        Assert.Throws<ArgumentNullException>(() => IndicatorCalculationHelper.CalculateFractalDimensionIndex(null!));
        var dummyCandles = new List<CoreCandleData>();
        Assert.Throws<ArgumentOutOfRangeException>(() => IndicatorCalculationHelper.CalculateFractalDimensionIndex(dummyCandles, period: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => IndicatorCalculationHelper.CalculateFractalDimensionIndex(dummyCandles, smoothingPeriod: -1));

        // smoothingPeriod = 0 must succeed without throwing
        var zeroSmoothResult = IndicatorCalculationHelper.CalculateFractalDimensionIndex(dummyCandles, period: 30, smoothingPeriod: 0);
        Assert.Empty(zeroSmoothResult);
    }
}
