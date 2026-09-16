using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Statistics;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class CoreCorrelationIndicatorTests
{
    private static List<CoreCandleData> CreateTestCandles(decimal[] closes, long[]? volumes = null)
    {
        var startDate = DateTime.Today;
        return closes.Select((close, i) => new CoreCandleData(
            startDate.AddDays(i),
            i > 0 ? closes[i - 1] : close,
            close + 1,
            close - 1,
            close,
            volumes != null && i < volumes.Length ? volumes[i] : 1000
        )).ToList();
    }

    [Fact]
    public void Calculate_WithPerfectlyCorrelatedData_ReturnsOne()
    {
        var seriesA = CreateTestCandles(new decimal[] { 10, 11, 12, 13, 14 });
        var seriesB = CreateTestCandles(new decimal[] { 20, 22, 24, 26, 28 });
        var indicator = new CoreCorrelationIndicator(5, seriesB);
        var result = indicator.Calculate(seriesA);

        Assert.True(result.IsSuccessful);
        Assert.NotNull(indicator.Values.Last());
        Assert.Equal(1.0m, indicator.Values.Last()!.Value, 4);
    }

    [Fact]
    public void Calculate_WithPerfectlyInverselyCorrelatedData_ReturnsMinusOne()
    {
        var seriesA = CreateTestCandles(new decimal[] { 10, 11, 12, 13, 14 });
        var seriesB = CreateTestCandles(new decimal[] { 30, 28, 26, 24, 22 });
        var indicator = new CoreCorrelationIndicator(5, seriesB);
        var result = indicator.Calculate(seriesA);

        Assert.True(result.IsSuccessful);
        Assert.NotNull(indicator.Values.Last());
        Assert.Equal(-1.0m, indicator.Values.Last()!.Value, 4);
    }

    [Fact]
    public void Calculate_WithUncorrelatedData_ReturnsZero()
    {
        // Orthogonal series: x = [1, 0, -1, 0, 1], y = [0, 1, 0, -1, 0]
        // sumX = 1, sumY = 0, sumXY = 0 => numerator = 5*0 - 1*0 = 0 => r = 0.0
        var seriesA = CreateTestCandles(new decimal[] { 1, 0, -1, 0, 1 });
        var seriesB = CreateTestCandles(new decimal[] { 0, 1, 0, -1, 0 });
        var indicator = new CoreCorrelationIndicator(5, seriesB);
        var result = indicator.Calculate(seriesA);

        Assert.True(result.IsSuccessful);
        Assert.NotNull(indicator.Values.Last());
        Assert.Equal(0.0m, indicator.Values.Last()!.Value, 4);
    }

    [Fact]
    public void Calculate_WithConstantSeries_ZeroVariance_ReturnsNull()
    {
        // Constant series has zero variance (denominator = 0) -> must return null, not 0.0m
        var seriesA = CreateTestCandles(new decimal[] { 10, 10, 10, 10, 10 });
        var seriesB = CreateTestCandles(new decimal[] { 20, 22, 21, 23, 20 });
        var indicator = new CoreCorrelationIndicator(5, seriesB);
        var result = indicator.Calculate(seriesA);

        Assert.True(result.IsSuccessful);
        Assert.Null(indicator.Values.Last());
    }

    [Fact]
    public void Calculate_WithNullsInWindow_PropagatesNull()
    {
        var seriesA = new decimal?[] { 10m, 11m, null, 13m, 14m, 15m };
        var seriesB = new decimal?[] { 20m, 22m, 24m, 26m, 28m, 30m };
        var res = IndicatorCalculationHelper.CalculateRollingPearsonCorrelation(seriesA, seriesB, 3);

        // Window [0..2]: contains null at index 2 => null
        Assert.Null(res[2]);
        // Window [1..3]: contains null at index 2 => null
        Assert.Null(res[3]);
        // Window [2..4]: contains null at index 2 => null
        Assert.Null(res[4]);
        // Window [3..5]: [13, 14, 15] vs [26, 28, 30] (no nulls) => perfectly correlated 1.0m
        Assert.NotNull(res[5]);
        Assert.Equal(1.0m, res[5]!.Value, 4);
    }

    [Fact]
    public void Calculate_WithWarmupPeriod_ReturnsNullForInitialBars()
    {
        var seriesA = CreateTestCandles(new decimal[] { 10, 11, 12, 13, 14, 15 });
        var seriesB = CreateTestCandles(new decimal[] { 20, 22, 24, 26, 28, 30 });
        var indicator = new CoreCorrelationIndicator(5, seriesB);
        indicator.Calculate(seriesA);

        Assert.Equal(6, indicator.Values.Count);
        for (int i = 0; i < 4; i++)
        {
            Assert.Null(indicator.Values[i]);
        }
        Assert.NotNull(indicator.Values[4]);
        Assert.NotNull(indicator.Values[5]);
    }

    [Fact]
    public void Calculate_WithEmptySeries_ReturnsEmptyResult()
    {
        var indicator = new CoreCorrelationIndicator(5);
        var result = indicator.Calculate(new List<CoreCandleData>());

        Assert.True(result.IsSuccessful);
        Assert.Empty(indicator.Values);
    }

    [Fact]
    public void Calculate_WithDifferentLengths_HandlesCorrectly()
    {
        var seriesA = CreateTestCandles(new decimal[] { 10, 11, 12, 13, 14, 15 });
        var seriesB = CreateTestCandles(new decimal[] { 20, 22, 24, 26, 28 });
        var indicator = new CoreCorrelationIndicator(5, seriesB);
        indicator.Calculate(seriesA);

        Assert.Equal(6, indicator.Values.Count);
        Assert.NotNull(indicator.Values[4]);
        Assert.Equal(1.0m, indicator.Values[4]!.Value, 4);
        Assert.Null(indicator.Values[5]);
    }

    [Fact]
    public void Calculate_SingleCandleSeries_CalculatesPriceVsVolume()
    {
        // Price and volume both strictly increasing => correlation = 1.0m
        var candles = CreateTestCandles(
            new decimal[] { 10, 11, 12, 13, 14 },
            new long[] { 100, 200, 300, 400, 500 }
        );
        var indicator = new CoreCorrelationIndicator(5);
        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.NotNull(indicator.Values.Last());
        Assert.Equal(1.0m, indicator.Values.Last()!.Value, 4);
    }

    [Fact]
    public void CalculateRollingPearsonCorrelation_MismatchedLength_ThrowsArgumentException()
    {
        var seriesA = new decimal?[] { 10m, 11m, 12m };
        var seriesB = new decimal?[] { 20m, 22m };

        Assert.Throws<ArgumentException>(() =>
            IndicatorCalculationHelper.CalculateRollingPearsonCorrelation(seriesA, seriesB, 2));
    }

    [Fact]
    public void CalculateRollingPearsonCorrelation_PeriodLessThanTwo_ThrowsArgumentOutOfRangeException()
    {
        var seriesA = new decimal?[] { 10m, 11m };
        var seriesB = new decimal?[] { 20m, 22m };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            IndicatorCalculationHelper.CalculateRollingPearsonCorrelation(seriesA, seriesB, 1));
    }

    [Fact]
    public void IndicatorFactory_Create_ReturnsCoreCorrelationIndicatorWithParameters()
    {
        var param = new CoreCorrelationParameter { Period = 10 };
        var indicator = IndicatorFactory.Default.Create(IndicatorType.Correlation, param);

        Assert.NotNull(indicator);
        var corr = Assert.IsType<CoreCorrelationIndicator>(indicator);
        Assert.Equal(10, corr.Period);
        Assert.Equal("Correlation(10)", corr.Name);
        Assert.False(corr.IsOverlay);
    }

    [Fact]
    public void Calculate_WithSecondaryCandles_CrossTickerCorrelation_ReturnsCorrectValues()
    {
        var seriesA = CreateTestCandles(new decimal[] { 10, 11, 12, 13, 14 });
        var secondaryCandles = CreateTestCandles(new decimal[] { 20, 22, 24, 26, 28 });

        var param = new CoreCorrelationParameter
        {
            Period = 5,
            ComparisonSymbol = "MSFT",
            ComparisonPriceSource = PriceType.Close
        };

        var indicator = new CoreCorrelationIndicator();
        indicator.Configure(param);
        indicator.SetSecondaryCandles(secondaryCandles);

        var result = indicator.Calculate(seriesA);

        Assert.True(result.IsSuccessful);
        Assert.Equal("Correlation(5, MSFT)", indicator.Name);
        Assert.NotNull(indicator.Values.Last());
        Assert.Equal(1.0m, indicator.Values.Last()!.Value, 4);
    }

    [Fact]
    public void Calculate_WithSecondaryCandles_InverselyCorrelated_ReturnsMinusOne()
    {
        var seriesA = CreateTestCandles(new decimal[] { 10, 11, 12, 13, 14 });
        var secondaryCandles = CreateTestCandles(new decimal[] { 30, 28, 26, 24, 22 });

        var param = new CoreCorrelationParameter
        {
            Period = 5,
            ComparisonSymbol = "SPY"
        };

        var indicator = new CoreCorrelationIndicator();
        indicator.Configure(param);
        indicator.SetSecondaryCandles(secondaryCandles);

        var result = indicator.Calculate(seriesA);

        Assert.True(result.IsSuccessful);
        Assert.Equal("Correlation(5, SPY)", indicator.Name);
        Assert.NotNull(indicator.Values.Last());
        Assert.Equal(-1.0m, indicator.Values.Last()!.Value, 4);
    }

    [Fact]
    public void CoreCorrelationParameter_DisplayName_WithAndWithoutSymbol_FormatsCorrectly()
    {
        var param = new CoreCorrelationParameter { Period = 20, ComparisonSymbol = "" };
        Assert.Equal("Correlation (20)", param.GetDisplayName("Correlation"));

        param.ComparisonSymbol = "AAPL";
        Assert.Equal("Correlation (20, AAPL)", param.GetDisplayName("Correlation"));

        param.ComparisonSymbol = " msft ";
        Assert.Equal("Correlation (20, MSFT)", param.GetDisplayName("Correlation"));
        Assert.Equal("MSFT", param.ComparisonSymbol);
    }

    [Fact]
    public void Calculate_WithSecondaryCandles_ContainsNull_PropagatesNull()
    {
        var seriesA = CreateTestCandles(new decimal[] { 10, 11, 12, 13, 14, 15 });
        var secList = new List<CoreCandleData?>
        {
            new CoreCandleData(DateTime.Today.AddDays(0), 20, 21, 19, 20, 100),
            new CoreCandleData(DateTime.Today.AddDays(1), 22, 23, 21, 22, 100),
            null, // missing / exceeded LOCF limit at index 2
            new CoreCandleData(DateTime.Today.AddDays(3), 26, 27, 25, 26, 100),
            new CoreCandleData(DateTime.Today.AddDays(4), 28, 29, 27, 28, 100),
            new CoreCandleData(DateTime.Today.AddDays(5), 30, 31, 29, 30, 100)
        };

        var param = new CoreCorrelationParameter { Period = 3, ComparisonSymbol = "MSFT" };
        var indicator = new CoreCorrelationIndicator();
        indicator.Configure(param);
        indicator.SetSecondaryCandles(secList);

        var result = indicator.Calculate(seriesA);
        Assert.True(result.IsSuccessful);

        // Period = 3
        // index 0: warmup -> null
        // index 1: warmup -> null
        // index 2: [0..2] contains null -> null
        // index 3: [1..3] contains null -> null
        // index 4: [2..4] contains null -> null
        // index 5: [3..5] [13, 14, 15] vs [26, 28, 30] -> 1.0m
        Assert.Null(indicator.Values[0]);
        Assert.Null(indicator.Values[1]);
        Assert.Null(indicator.Values[2]);
        Assert.Null(indicator.Values[3]);
        Assert.Null(indicator.Values[4]);
        Assert.NotNull(indicator.Values[5]);
        Assert.Equal(1.0m, indicator.Values[5]!.Value, 4);
    }

    [Fact]
    public void Calculate_WithComparisonSymbol_ButNullSecondaryCandles_ReturnsAllNulls()
    {
        var seriesA = CreateTestCandles(new decimal[] { 10, 11, 12, 13, 14 });
        var param = new CoreCorrelationParameter { Period = 3, ComparisonSymbol = "NONEXISTENT" };

        var indicator = new CoreCorrelationIndicator();
        indicator.Configure(param);
        // secondary candles not provided / failed to load
        indicator.SetSecondaryCandles(null);

        var result = indicator.Calculate(seriesA);
        Assert.True(result.IsSuccessful);

        Assert.Equal(5, indicator.Values.Count);
        foreach (var val in indicator.Values)
        {
            Assert.Null(val);
        }
    }

    [Fact]
    public void ConvertToLogReturns_Correctness()
    {
        var prices = new decimal?[] { 100m, 110m, 121m };
        var returns = IndicatorCalculationHelper.ConvertToLogReturns(prices);

        Assert.Equal(3, returns.Count);
        Assert.Null(returns[0]);
        // ln(110/100) = ln(1.1) ~= 0.0953101798
        Assert.NotNull(returns[1]);
        Assert.Equal(0.09531m, returns[1]!.Value, 4);
        // ln(121/110) = ln(1.1) ~= 0.0953101798
        Assert.NotNull(returns[2]);
        Assert.Equal(0.09531m, returns[2]!.Value, 4);
    }

    [Fact]
    public void ConvertToLogReturns_HandlesZerosAndNulls()
    {
        var prices = new decimal?[] { 100m, 0m, 110m, null, 120m };
        var returns = IndicatorCalculationHelper.ConvertToLogReturns(prices);

        Assert.Equal(5, returns.Count);
        Assert.Null(returns[0]); // first element
        Assert.Null(returns[1]); // current is 0
        Assert.Null(returns[2]); // previous was 0
        Assert.Null(returns[3]); // current is null
        Assert.Null(returns[4]); // previous was null
    }

    [Fact]
    public void Calculate_LogReturnMode_SelfCorrelation_ReturnsOne()
    {
        var seriesA = CreateTestCandles(new decimal[] { 100, 102, 101, 105, 108, 106, 110 });
        var param = new CoreCorrelationParameter
        {
            Period = 4,
            ComparisonSymbol = "SELF",
            CalculationMode = CorrelationCalculationMode.LogReturn
        };

        var indicator = new CoreCorrelationIndicator();
        indicator.Configure(param);
        indicator.SetSecondaryCandles(seriesA);

        var result = indicator.Calculate(seriesA);
        Assert.True(result.IsSuccessful);

        // Warmup:
        // Returns are [null, r1, r2, r3, r4, r5, r6]
        // Rolling correlation with Period 4 needs 4 valid returns -> first valid at index 4 (using r1..r4)
        Assert.Null(indicator.Values[0]);
        Assert.Null(indicator.Values[1]);
        Assert.Null(indicator.Values[2]);
        Assert.Null(indicator.Values[3]);
        Assert.NotNull(indicator.Values[4]);
        Assert.Equal(1.0m, indicator.Values[4]!.Value, 4);
        Assert.NotNull(indicator.Values[5]);
        Assert.Equal(1.0m, indicator.Values[5]!.Value, 4);
        Assert.NotNull(indicator.Values[6]);
        Assert.Equal(1.0m, indicator.Values[6]!.Value, 4);
    }

    [Fact]
    public void DisplayName_LogReturnMode_FormatsCorrectly()
    {
        var paramCross = new CoreCorrelationParameter
        {
            Period = 20,
            ComparisonSymbol = "AAPL",
            CalculationMode = CorrelationCalculationMode.LogReturn
        };
        Assert.Equal("Correlation (20, AAPL, Return)", paramCross.GetDisplayName("Correlation"));

        var paramSingle = new CoreCorrelationParameter
        {
            Period = 20,
            ComparisonSymbol = "",
            CalculationMode = CorrelationCalculationMode.LogReturn
        };
        Assert.Equal("Correlation (20, Return)", paramSingle.GetDisplayName("Correlation"));

        var paramDefault = new CoreCorrelationParameter
        {
            Period = 20,
            ComparisonSymbol = "AAPL",
            CalculationMode = CorrelationCalculationMode.PriceLevel
        };
        Assert.Equal("Correlation (20, AAPL)", paramDefault.GetDisplayName("Correlation"));
    }
}




