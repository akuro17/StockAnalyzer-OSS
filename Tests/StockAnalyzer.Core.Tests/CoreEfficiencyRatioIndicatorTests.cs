using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class CoreEfficiencyRatioIndicatorTests
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
    public void Calculate_WithLinearTrend_ReturnsOne()
    {
        // 11 bars: 10, 11, 12, ..., 20 (strictly increasing linear trend)
        var candles = GenerateCandleData(11, 10m, i => 10m + i);
        int period = 10;
        var indicator = new CoreEfficiencyRatioIndicator { Period = period };
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(11, result.MainValues.Count);

        // Warmup: indices 0..9 must be null
        Assert.True(result.MainValues.Take(period).All(v => v == null));

        // At index 10: Change = |20 - 10| = 10, Volatility = 10 * 1 = 10 -> ER = 1.0m
        Assert.NotNull(result.MainValues[period]);
        Assert.Equal(1.0m, result.MainValues[period]!.Value);
    }

    [Fact]
    public void Calculate_WithConstantPrices_ReturnsZeroWithoutDivisionByZero()
    {
        var candles = GenerateCandleData(15, 100m, _ => 100m);
        int period = 10;
        var indicator = new CoreEfficiencyRatioIndicator { Period = period };
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(15, result.MainValues.Count);
        Assert.True(result.MainValues.Take(period).All(v => v == null));

        // When prices are constant, volatility = 0 -> ER must be 0.0m
        for (int i = period; i < 15; i++)
        {
            Assert.Equal(0.0m, result.MainValues[i]);
        }
    }

    [Fact]
    public void Calculate_WithCyclicalReversal_ReturnsZero()
    {
        // 11 bars where prices rise then return to original: 100, 101, 102, 103, 104, 105, 104, 103, 102, 101, 100
        var prices = new List<decimal?> { 100m, 101m, 102m, 103m, 104m, 105m, 104m, 103m, 102m, 101m, 100m };
        var result = IndicatorCalculationHelper.CalculateEfficiencyRatio(prices, period: 10);

        Assert.Equal(11, result.Count);
        Assert.True(result.Take(10).All(v => v == null));

        // At index 10: Change = |100 - 100| = 0, Volatility = 5 + 5 = 10 -> ER = 0.0m
        Assert.NotNull(result[10]);
        Assert.Equal(0.0m, result[10]!.Value);
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmptyList()
    {
        var candles = new List<CoreCandleData>();
        var indicator = new CoreEfficiencyRatioIndicator { Period = 10 };
        var result = indicator.Calculate(candles);
        if (result.IsSuccessful) Assert.Empty(result.MainValues);
    }

    [Fact]
    public void Calculate_WithNullData_ReturnsFailure()
    {
        var indicator = new CoreEfficiencyRatioIndicator { Period = 10 };
        var result = indicator.Calculate(null);
        Assert.False(result.IsSuccessful);
    }

    [Fact]
    public void Calculate_WithInsufficientData_ReturnsNullValues()
    {
        var candles = GenerateCandleData(9, 100, i => 100 + i);
        var indicator = new CoreEfficiencyRatioIndicator { Period = 10 };
        var result = indicator.Calculate(candles);

        Assert.Equal(9, result.MainValues.Count);
        Assert.True(result.MainValues.All(v => v == null));
    }

    [Fact]
    public void Calculate_WithIntermittentNulls_HandledDeterministically()
    {
        // 13 elements with nulls at index 1 and 8
        var prices = new List<decimal?> { 10m, null, 11m, 12m, 13m, 14m, 15m, 16m, null, 17m, 18m, 19m, 20m };
        var result = IndicatorCalculationHelper.CalculateEfficiencyRatio(prices, period: 10);

        Assert.Equal(prices.Count, result.Count);
        // Null inputs must produce null outputs
        Assert.Null(result[1]);
        Assert.Null(result[8]);

        // Valid values are at indices: 0(1), 2(2), 3(3), 4(4), 5(5), 6(6), 7(7), 9(8), 10(9), 11(10: warmup), 12(11: first ER)
        Assert.Null(result[11]);
        Assert.NotNull(result[12]);
        Assert.Equal(1.0m, result[12]!.Value); // strictly monotonic increasing
    }

    [Fact]
    public void Calculate_WithMonotonicDecreasingTrend_ReturnsOne()
    {
        // 11 bars strictly decreasing: 100, 99, 98, ..., 90
        var candles = GenerateCandleData(11, 100m, i => 100m - i);
        int period = 10;
        var indicator = new CoreEfficiencyRatioIndicator { Period = period };
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(11, result.MainValues.Count);
        Assert.True(result.MainValues.Take(period).All(v => v == null));

        // Downward monotonic trend must yield ER = 1.0m (directionless efficiency)
        Assert.NotNull(result.MainValues[period]);
        Assert.Equal(1.0m, result.MainValues[period]!.Value);
    }

    [Fact]
    public void Parameter_Validate_EnforcesLowerAndUpperBounds()
    {
        var validParam = new CoreEfficiencyRatioParameter { Period = 10 };
        validParam.Validate(); // Should not throw

        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreEfficiencyRatioParameter { Period = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreEfficiencyRatioParameter { Period = 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreEfficiencyRatioParameter { Period = 10001 }.Validate());
    }

    [Fact]
    public void CalculateEfficiencyRatio_Helper_ThrowsOnPeriodLessThanTwo()
    {
        var prices = new List<decimal?> { 10m, 11m, 12m };
        Assert.Throws<ArgumentOutOfRangeException>(() => IndicatorCalculationHelper.CalculateEfficiencyRatio(prices, period: 1));
    }

    [Fact]
    public void IndicatorFactory_DiscoversAndInstantiatesEfficiencyRatio()
    {
        var factory = IndicatorFactory.Default;
        Assert.True(factory.IsRegistered(IndicatorType.EfficiencyRatio));

        var indicator = factory.Create(IndicatorType.EfficiencyRatio);
        Assert.NotNull(indicator);
        var erIndicator = Assert.IsType<CoreEfficiencyRatioIndicator>(indicator);
        Assert.False(erIndicator.IsOverlay);
    }

    [Fact]
    public void CalculateEfficiencyRatio_Helper_MatchesKamaInternalEr()
    {
        // Verify mathematically that ER calculated by helper directly corresponds to KAMA's SC derivation
        var prices = new List<decimal?> { 10m, 12m, 11m, 13m, 15m, 14m, 16m, 18m, 17m, 19m, 21m };
        int period = 10;
        int fast = 2;
        int slow = 30;

        var erSeries = IndicatorCalculationHelper.CalculateEfficiencyRatio(prices, period);
        var kamaSeries = IndicatorCalculationHelper.CalculateKama(prices, period, fast, slow);

        Assert.Equal(prices.Count, erSeries.Count);
        Assert.Equal(prices.Count, kamaSeries.Count);

        decimal erAt10 = erSeries[10]!.Value;
        Assert.True(erAt10 >= 0.0m && erAt10 <= 1.0m);

        // In KAMA:
        // fastSC = 2 / (2 + 1) = 2/3
        // slowSC = 2 / (30 + 1) = 2/31
        // sc = er * (fastSC - slowSC) + slowSC
        // scSquared = sc * sc
        // initial SMA at index 9: average of index 0..9
        decimal fastSC = 2.0m / (fast + 1.0m);
        decimal slowSC = 2.0m / (slow + 1.0m);
        decimal sc = erAt10 * (fastSC - slowSC) + slowSC;
        decimal scSquared = sc * sc;

        decimal initialSma = prices.Take(period).Select(p => p!.Value).Average();
        decimal expectedKamaAt10 = initialSma + scSquared * (prices[10]!.Value - initialSma);

        Assert.Equal(Math.Round(expectedKamaAt10, 6), Math.Round(kamaSeries[10]!.Value, 6));
    }
}
