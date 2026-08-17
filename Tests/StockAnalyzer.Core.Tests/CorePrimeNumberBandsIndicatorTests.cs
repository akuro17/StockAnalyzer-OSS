using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Tests;

public class CorePrimeNumberBandsIndicatorTests
{
    [Fact]
    public void Calculate_NullOrEmptyCandles_ReturnsEmptyResult()
    {
        var indicator = new CorePrimeNumberBandsIndicator();
        
        // Null candles
        var nullResult = indicator.Calculate(null!);
        Assert.False(nullResult.IsSuccessful);

        // Empty candles
        var emptyResult = indicator.Calculate(new List<CoreCandleData>());
        Assert.True(emptyResult.IsSuccessful);
        Assert.Empty(indicator.MiddleBand);
        Assert.Empty(indicator.UpperBand);
        Assert.Empty(indicator.LowerBand);
    }

    [Fact]
    public void Calculate_CountLessThanPeriod_ReturnsAllNulls()
    {
        var indicator = new CorePrimeNumberBandsIndicator { Period = 5, ScaleMultiplier = 10.0m };
        var candles = new List<CoreCandleData>
        {
            new(DateTime.Today.AddDays(0), 10m, 12m, 8m, 10m, 1000),
            new(DateTime.Today.AddDays(1), 10m, 13m, 9m, 11m, 1000),
            new(DateTime.Today.AddDays(2), 11m, 14m, 10m, 12m, 1000),
            new(DateTime.Today.AddDays(3), 12m, 15m, 11m, 13m, 1000)
        };

        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(4, indicator.MiddleBand.Count);
        Assert.Equal(4, indicator.UpperBand.Count);
        Assert.Equal(4, indicator.LowerBand.Count);
        Assert.All(indicator.MiddleBand, v => Assert.Null(v));
        Assert.All(indicator.UpperBand, v => Assert.Null(v));
        Assert.All(indicator.LowerBand, v => Assert.Null(v));
    }

    [Fact]
    public void Calculate_KnownExtrema_ReturnsAccurateBands()
    {
        // Period = 3, ScaleMultiplier = 10.0
        // Window 0: i=0 (null)
        // Window 1: i=1 (null)
        // Window 2: i=2 -> [0..2]: Highs = [1.0, 1.2, 1.4] -> HH = 1.4 -> rawHigh = 14 -> upper prime = 17 -> UpperBand = 1.7
        //                         Lows  = [0.5, 0.6, 0.8] -> LL = 0.5 -> rawLow = 5  -> lower prime = 5  -> LowerBand = 0.5
        //                         MiddleBand = (1.7 + 0.5) / 2 = 1.1
        // Window 3: i=3 -> [1..3]: Highs = [1.2, 1.4, 1.1] -> HH = 1.4 -> UpperBand = 1.7
        //                         Lows  = [0.6, 0.8, 0.9] -> LL = 0.6 -> rawLow = 6 -> lower prime = 5 -> LowerBand = 0.5
        //                         MiddleBand = 1.1
        var indicator = new CorePrimeNumberBandsIndicator { Period = 3, ScaleMultiplier = 10.0m };
        var candles = new List<CoreCandleData>
        {
            new(DateTime.Today.AddDays(0), 0.8m, 1.0m, 0.5m, 0.9m, 1000),
            new(DateTime.Today.AddDays(1), 0.9m, 1.2m, 0.6m, 1.0m, 1000),
            new(DateTime.Today.AddDays(2), 1.0m, 1.4m, 0.8m, 1.3m, 1000),
            new(DateTime.Today.AddDays(3), 1.2m, 1.1m, 0.9m, 1.0m, 1000)
        };

        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Null(indicator.UpperBand[0]);
        Assert.Null(indicator.UpperBand[1]);
        Assert.Equal(1.7m, indicator.UpperBand[2]);
        Assert.Equal(0.5m, indicator.LowerBand[2]);
        Assert.Equal(1.1m, indicator.MiddleBand[2]);

        Assert.Equal(1.7m, indicator.UpperBand[3]);
        Assert.Equal(0.5m, indicator.LowerBand[3]);
        Assert.Equal(1.1m, indicator.MiddleBand[3]);

        // Verify dictionary series outputs
        var upperSeries = result.GetSeries(CorePrimeNumberBandsIndicator.UpperSeriesName);
        var lowerSeries = result.GetSeries(CorePrimeNumberBandsIndicator.LowerSeriesName);
        var middleSeries = result.GetSeries(CorePrimeNumberBandsIndicator.MiddleSeriesName);

        Assert.NotNull(upperSeries);
        Assert.NotNull(lowerSeries);
        Assert.NotNull(middleSeries);
        Assert.Equal(indicator.UpperBand, upperSeries);
        Assert.Equal(indicator.LowerBand, lowerSeries);
        Assert.Equal(indicator.MiddleBand, middleSeries);
    }

    [Fact]
    public void Calculate_ExtremeLargeValues_ClampsSafelyWithoutOverflow()
    {
        var indicator = new CorePrimeNumberBandsIndicator { Period = 1, ScaleMultiplier = 10.0m };
        var candles = new List<CoreCandleData>
        {
            new(DateTime.Today, 300_000m, 300_000m, 300_000m, 300_000m, 1000)
        };

        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        decimal expectedMaxPrime = (decimal)PrimeNumberHelper.Primes[^1] / 10.0m;
        Assert.Equal(expectedMaxPrime, indicator.UpperBand[0]);
        Assert.Equal(expectedMaxPrime, indicator.LowerBand[0]);
        Assert.Equal(expectedMaxPrime, indicator.MiddleBand[0]);
    }

    [Fact]
    public void Calculate_ZeroOrNegativePrices_ClampsToSmallestPrime()
    {
        var indicator = new CorePrimeNumberBandsIndicator { Period = 1, ScaleMultiplier = 10.0m };
        var candles = new List<CoreCandleData>
        {
            new(DateTime.Today, 0.0m, 0.0m, -5.0m, 0.0m, 1000)
        };

        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        // Clamped to 2 -> 2 / 10 = 0.2
        Assert.Equal(0.2m, indicator.UpperBand[0]);
        Assert.Equal(0.2m, indicator.LowerBand[0]);
        Assert.Equal(0.2m, indicator.MiddleBand[0]);
    }

    [Fact]
    public void Calculate_ExactPrimePrice_UpperAndLowerEqual()
    {
        // 0.7 * 10 = 7 (Prime)
        var indicator = new CorePrimeNumberBandsIndicator { Period = 1, ScaleMultiplier = 10.0m };
        var candles = new List<CoreCandleData>
        {
            new(DateTime.Today, 0.7m, 0.7m, 0.7m, 0.7m, 1000)
        };

        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(0.7m, indicator.UpperBand[0]);
        Assert.Equal(0.7m, indicator.LowerBand[0]);
        Assert.Equal(0.7m, indicator.MiddleBand[0]);
    }

    [Fact]
    public void Parameter_Validation_ThrowsArgumentOutOfRangeException()
    {
        var param = new CorePrimeNumberBandsParameter();

        // Valid default
        param.Validate();

        // Invalid Period low
        param.Period = 0;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());

        // Invalid Period high
        param.Period = 1001;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());

        // Reset Period
        param.Period = 8;

        // Invalid ScaleMultiplier low
        param.ScaleMultiplier = 0.5m;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());

        // Invalid ScaleMultiplier high
        param.ScaleMultiplier = 1000.1m;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
    }

    [Fact]
    public void Factory_CanCreateAndConfigurePrimeNumberBands()
    {
        var param = new CorePrimeNumberBandsParameter
        {
            Period = 14,
            ScaleMultiplier = 20.0m
        };

        var indicator = IndicatorFactory.Default.Create(IndicatorType.PrimeNumberBands, param) as CorePrimeNumberBandsIndicator;

        Assert.NotNull(indicator);
        Assert.Equal(14, indicator.Period);
        Assert.Equal(20.0m, indicator.ScaleMultiplier);
        Assert.Equal("PNB (14, 20.0)", indicator.Name);
        Assert.True(indicator.IsOverlay);
    }
}
