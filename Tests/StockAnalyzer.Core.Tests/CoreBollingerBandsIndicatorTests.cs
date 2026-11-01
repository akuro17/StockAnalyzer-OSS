using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using StockAnalyzer.Core.Optimization;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests;

public class CoreBollingerBandsIndicatorTests
{
    private static List<CoreCandleData> CreateTestCandles(IEnumerable<decimal> closePrices)
    {
        var startDate = DateTime.Today;
        return closePrices.Select((price, i) => new CoreCandleData(
            startDate.AddDays(i), price, price, price, price, 1000
        )).ToList();
    }

    private static (List<decimal?> Middle, List<decimal?> Upper, List<decimal?> Lower) CalculateOldBollinger(
        IReadOnlyList<CoreCandleData> candles, int period, decimal multiplier)
    {
        var middle = new List<decimal?>();
        var upper = new List<decimal?>();
        var lower = new List<decimal?>();

        for (int i = 0; i < candles.Count; i++)
        {
            if (i < period - 1)
            {
                middle.Add(null);
                upper.Add(null);
                lower.Add(null);
                continue;
            }

            decimal sum = 0;
            for (int j = 0; j < period; j++)
            {
                sum += candles[i - j].Close;
            }
            decimal sma = sum / period;

            decimal variance = 0;
            for (int j = 0; j < period; j++)
            {
                decimal diff = candles[i - j].Close - sma;
                variance += diff * diff;
            }

            decimal stdDev = (decimal)Math.Sqrt((double)(variance / period));
            middle.Add(sma);
            upper.Add(sma + multiplier * stdDev);
            lower.Add(sma - multiplier * stdDev);
        }

        return (middle, upper, lower);
    }

    [Fact]
    public void Calculate_WithValidData_ReturnsCorrectBands_ViaResult()
    {
        var indicator = new CoreBollingerBandsIndicator { Period = 3, StdDevMultiplier = 2 };
        var candles = CreateTestCandles(new decimal[] { 10, 12, 14, 16, 18 });

        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.True(result.HasSeries(CoreBollingerBandsIndicator.UpperSeriesName));
        Assert.True(result.HasSeries(CoreBollingerBandsIndicator.LowerSeriesName));
        Assert.True(result.HasSeries(CoreBollingerBandsIndicator.MiddleSeriesName));

        var upper = result.GetSeries(CoreBollingerBandsIndicator.UpperSeriesName);
        var lower = result.GetSeries(CoreBollingerBandsIndicator.LowerSeriesName);
        var middle = result.GetSeries(CoreBollingerBandsIndicator.MiddleSeriesName);

        Assert.Equal(5, middle.Count);
        Assert.Equal(5, upper.Count);
        Assert.Equal(5, lower.Count);

        // Check values for index 2 (10, 12, 14 -> SMA 12)
        // StdDev: Sqrt(((10-12)^2 + (12-12)^2 + (14-12)^2) / 3) = Sqrt((4 + 0 + 4) / 3) = Sqrt(8/3) = Sqrt(2.666) ~= 1.633
        // Upper: 12 + 2 * 1.633 = 15.266
        // Lower: 12 - 2 * 1.633 = 8.734
        
        Assert.Equal(12m, middle[2]);
        Assert.NotNull(upper[2]);
        Assert.NotNull(lower[2]);
        
        // Use precision for float comparison
        Assert.Equal(15.266m, upper[2].Value, 3);
        Assert.Equal(8.734m, lower[2].Value, 3);
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmptyResult()
    {
        var indicator = new CoreBollingerBandsIndicator { Period = 5 };
        var result = indicator.Calculate(new List<CoreCandleData>());
        
        Assert.True(result.IsSuccessful);
        Assert.Empty(result.MainValues);
    }

    [Fact]
    public void Calculate_PriceSourceClose_MatchesPreChangeAlgorithm_AcrossParallelThreshold()
    {
        var optContext = new OptimizationContext(ExecutionMode.Balanced);
        int count = optContext.ParallelizationThreshold + 150;

        var startDate = DateTime.Today;
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < count; i++)
        {
            decimal close = 100m + (i % 50) + (i * 0.1m);
            candles.Add(new CoreCandleData(startDate.AddDays(i), close - 5, close + 10, close - 10, close, 1000));
        }

        var indicator = new CoreBollingerBandsIndicator
        {
            Period = 20,
            StdDevMultiplier = 2.0m,
            PriceSource = PriceType.Close
        };

        var result = indicator.Calculate(candles, optContext);
        Assert.True(result.IsSuccessful);

        var (oldMiddle, oldUpper, oldLower) = CalculateOldBollinger(candles, 20, 2.0m);

        Assert.Equal(count, indicator.Values.Count);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(oldMiddle[i], indicator.Values[i]);
            Assert.Equal(oldUpper[i], indicator.UpperBand[i]);
            Assert.Equal(oldLower[i], indicator.LowerBand[i]);
        }
    }

    [Fact]
    public void Calculate_PriceSourceOpen_MatchesHandComputed3Bars()
    {
        var startDate = DateTime.Today;
        var candles = new List<CoreCandleData>
        {
            new CoreCandleData(startDate, 10m, 15m, 5m, 12m, 1000),
            new CoreCandleData(startDate.AddDays(1), 20m, 25m, 15m, 22m, 1000),
            new CoreCandleData(startDate.AddDays(2), 30m, 35m, 25m, 32m, 1000),
        };

        var indicator = new CoreBollingerBandsIndicator
        {
            Period = 3,
            StdDevMultiplier = 2.0m,
            PriceSource = PriceType.Open
        };

        var result = indicator.Calculate(candles);
        Assert.True(result.IsSuccessful);

        Assert.Equal(3, indicator.Values.Count);
        Assert.Null(indicator.Values[0]);
        Assert.Null(indicator.UpperBand[0]);
        Assert.Null(indicator.LowerBand[0]);
        Assert.Null(indicator.Values[1]);
        Assert.Null(indicator.UpperBand[1]);
        Assert.Null(indicator.LowerBand[1]);

        // Hand calculation for index 2 (Open = 10, 20, 30):
        // SMA = (10 + 20 + 30) / 3 = 20
        // Variance = ((10-20)^2 + (20-20)^2 + (30-20)^2) / 3 = 200 / 3
        // StdDev = sqrt(200/3) ~= 8.1649658
        // Upper = 20 + 2 * 8.1649658 ~= 36.3299
        // Lower = 20 - 2 * 8.1649658 ~= 3.6701
        Assert.NotNull(indicator.Values[2]);
        Assert.NotNull(indicator.UpperBand[2]);
        Assert.NotNull(indicator.LowerBand[2]);

        Assert.Equal(20m, indicator.Values[2]!.Value);
        Assert.Equal(36.330m, indicator.UpperBand[2]!.Value, 3);
        Assert.Equal(3.670m, indicator.LowerBand[2]!.Value, 3);
    }

    [Fact]
    public void Calculate_HeikinAshi_ParallelMatchesSequential()
    {
        var optContext = new OptimizationContext(ExecutionMode.Balanced);
        int count = optContext.ParallelizationThreshold + 150;

        var startDate = DateTime.Today;
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < count; i++)
        {
            decimal basePrice = 100m + (i % 30);
            candles.Add(new CoreCandleData(
                startDate.AddDays(i),
                basePrice - 2,
                basePrice + 5,
                basePrice - 4,
                basePrice + 1,
                1000 + i));
        }

        var indicator = new CoreBollingerBandsIndicator
        {
            Period = 14,
            StdDevMultiplier = 2.0m,
            PriceSource = PriceType.HeikinAshiClose
        };

        // Parallel execution
        var parallelResult = indicator.Calculate(candles, optContext);
        Assert.True(parallelResult.IsSuccessful);

        // Sequential ground truth: extract price series directly and calculate sequentially
        var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceType.HeikinAshiClose);
        var expectedMiddle = new List<decimal?>();
        var expectedUpper = new List<decimal?>();
        var expectedLower = new List<decimal?>();

        for (int i = 0; i < count; i++)
        {
            if (i < 13)
            {
                expectedMiddle.Add(null);
                expectedUpper.Add(null);
                expectedLower.Add(null);
                continue;
            }

            decimal sum = 0;
            for (int j = 0; j < 14; j++) sum += priceSeries[i - j]!.Value;
            decimal sma = sum / 14;

            decimal variance = 0;
            for (int j = 0; j < 14; j++)
            {
                decimal diff = priceSeries[i - j]!.Value - sma;
                variance += diff * diff;
            }
            decimal stdDev = (decimal)Math.Sqrt((double)(variance / 14));

            expectedMiddle.Add(sma);
            expectedUpper.Add(sma + 2.0m * stdDev);
            expectedLower.Add(sma - 2.0m * stdDev);
        }

        Assert.Equal(count, indicator.Values.Count);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(expectedMiddle[i], indicator.Values[i]);
            Assert.Equal(expectedUpper[i], indicator.UpperBand[i]);
            Assert.Equal(expectedLower[i], indicator.LowerBand[i]);
        }
    }
}
