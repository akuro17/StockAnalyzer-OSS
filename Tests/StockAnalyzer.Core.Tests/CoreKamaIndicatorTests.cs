using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class CoreKamaIndicatorTests
{
    private static List<CoreCandleData> GenerateCandleData(int count, decimal startPrice, Func<int, decimal> priceProgression)
    {
        var candles = new List<CoreCandleData>();
        var date = DateTime.Today;
        for (int i = 0; i < count; i++)
        {
            var price = priceProgression(i);
            candles.Add(new CoreCandleData(date.AddDays(i), price, price, price, price, 100));
        }
        return candles;
    }

    [Fact]
    public void Calculate_WithValidData_ReturnsExpectedValues()
    {
        var candles = GenerateCandleData(30, 100, i => 100 + (decimal)Math.Sin(i / 5.0) * 5); 
        int period = 10;
        var indicator = new CoreKamaIndicator { Period = period };
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(30, result.MainValues.Count);
        Assert.True(result.MainValues.Take(period).All(v => v == null));

        Assert.NotNull(result.MainValues[period]);
        Assert.True(result.MainValues[period] > 0);

        var priceRange = candles.Skip(period).Max(c => c.Close) - candles.Skip(period).Min(c => c.Close);
        var kamaRange = result.MainValues.Skip(period).Max(v => v.Value) - result.MainValues.Skip(period).Min(v => v.Value);
        Assert.True(kamaRange < priceRange);
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmptyList()
    {
        var candles = new List<CoreCandleData>();
        var indicator = new CoreKamaIndicator { Period = 10 };
        var result = indicator.Calculate(candles);
        if (result.IsSuccessful) Assert.Empty(result.MainValues);
    }

    [Fact]
    public void Calculate_WithNullData_ReturnsFailure()
    {
        var indicator = new CoreKamaIndicator { Period = 10 };
        var result = indicator.Calculate(null);
        Assert.False(result.IsSuccessful);
    }

    [Fact]
    public void Calculate_WithInsufficientData_ReturnsNullValues()
    {
        var candles = GenerateCandleData(9, 100, i => 100 + i);
        var indicator = new CoreKamaIndicator { Period = 10 };
        var result = indicator.Calculate(candles);

        Assert.Equal(9, result.MainValues.Count);
        Assert.True(result.MainValues.All(v => v == null));
    }

    [Fact]
    public void Calculate_WithConstantPrices_ZeroVolatilityHandledGracefully()
    {
        var candles = GenerateCandleData(15, 100m, _ => 100m);
        var indicator = new CoreKamaIndicator { Period = 10 };
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(15, result.MainValues.Count);
        Assert.True(result.MainValues.Take(10).All(v => v == null));
        for (int i = 10; i < 15; i++)
        {
            Assert.Equal(100m, result.MainValues[i]);
        }
    }

    [Fact]
    public void CalculateKama_Helper_LinearTrend_ERMatchesOne()
    {
        // 11 bars: 10, 11, 12, ..., 20 (strictly increasing linear trend)
        var prices = Enumerable.Range(10, 11).Select(x => (decimal?)x).ToList();
        var result = IndicatorCalculationHelper.CalculateKama(prices, period: 10, fastPeriod: 2, slowPeriod: 30);

        Assert.Equal(11, result.Count);
        Assert.True(result.Take(10).All(v => v == null));

        // At index 10: Change = |20 - 10| = 10, Volatility = 10 * 1 = 10 -> ER = 1.0
        // fastSC = 2/3, slowSC = 2/31, SC = fastSC^2 = (2/3)^2 = 4/9
        // Initial SMA at index 9: average of 10..19 = 14.5
        // KAMA at index 10 = 14.5 + (4/9) * (20 - 14.5) = 14.5 + 4/9 * 5.5 = 14.5 + 22/9 = 152.5/9 ≈ 16.9444m
        Assert.NotNull(result[10]);
        decimal expected = 14.5m + (4.0m / 9.0m) * (20.0m - 14.5m);
        Assert.Equal(Math.Round(expected, 4), Math.Round(result[10]!.Value, 4));
    }

    [Fact]
    public void CalculateKama_Helper_IntermittentNulls_HandledDeterministically()
    {
        // 13 elements with nulls at index 1 and 8
        var prices = new List<decimal?> { 10m, null, 11m, 12m, 13m, 14m, 15m, 16m, null, 17m, 18m, 19m, 20m };
        var result = IndicatorCalculationHelper.CalculateKama(prices, period: 10, fastPeriod: 2, slowPeriod: 30);

        Assert.Equal(prices.Count, result.Count);
        // Null inputs must produce null outputs
        Assert.Null(result[1]);
        Assert.Null(result[8]);

        // Valid values are at indices: 0(1), 2(2), 3(3), 4(4), 5(5), 6(6), 7(7), 9(8), 10(9), 11(10: warmup done), 12(11: first KAMA)
        // Warmup completes at index 11 (validCount == 10), so result[11] is null
        Assert.Null(result[11]);
        // First calculated KAMA is at index 12 (validCount == 11)
        Assert.NotNull(result[12]);
        Assert.True(result[12] > 0m);
    }

    [Fact]
    public void Parameter_Validate_EnforcesLowerAndUpperBounds()
    {
        var validParam = new CoreKamaParameter { Period = 10, Fast = 2, Slow = 30 };
        validParam.Validate(); // Should not throw

        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreKamaParameter { Period = 0, Fast = 2, Slow = 30 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreKamaParameter { Period = 10001, Fast = 2, Slow = 30 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreKamaParameter { Period = 10, Fast = 0, Slow = 30 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreKamaParameter { Period = 10, Fast = 1001, Slow = 30 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreKamaParameter { Period = 10, Fast = 2, Slow = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreKamaParameter { Period = 10, Fast = 2, Slow = 10001 }.Validate());
    }

    [Fact]
    public void CalculateSeries_DirectExecution_Succeeds()
    {
        var indicator = new CoreKamaIndicator { Period = 5, Fast = 2, Slow = 30 };
        var prices = new List<decimal?> { 10m, 11m, 12m, 13m, 14m, 15m, 16m };

        var result = indicator.CalculateSeries(prices);

        Assert.True(result.IsSuccessful);
        Assert.Equal(prices.Count, result.MainValues.Count);
        Assert.Equal(prices.Count, indicator.Values.Count);
        // First 5 elements must be null (indices 0..4)
        for (int i = 0; i < 5; i++)
        {
            Assert.Null(result.MainValues[i]);
        }
        // Recurrence starts at index 5
        Assert.NotNull(result.MainValues[5]);
        Assert.NotNull(result.MainValues[6]);
    }
}

