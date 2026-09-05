using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class CoreHmmIndicatorTests
{
    private static List<CoreCandleData> GenerateCandles(int count, decimal startPrice = 100m, double seed = 42)
    {
        var candles = new List<CoreCandleData>(count);
        var date = new DateTime(2023, 1, 1);
        var rand = new Random((int)seed);
        decimal price = startPrice;

        for (int i = 0; i < count; i++)
        {
            // Create regime switching behavior (first half bull, second half bear)
            double drift = (i < count / 2) ? 0.002 : -0.002;
            double shock = (rand.NextDouble() - 0.5) * 0.02;
            price *= (decimal)(1.0 + drift + shock);
            if (price <= 1.0m) price = 1.0m;

            candles.Add(new CoreCandleData(
                Timestamp: date.AddDays(i),
                Open: price,
                High: price * 1.01m,
                Low: price * 0.99m,
                Close: price,
                Volume: 10000
            ));
        }

        return candles;
    }

    [Fact]
    public void Factory_IsRegistered_AndCreatesInstance()
    {
        var factory = IndicatorFactory.Default;
        Assert.True(factory.IsRegistered(IndicatorType.HiddenMarkovModel));

        var indicator = factory.Create(IndicatorType.HiddenMarkovModel);
        Assert.NotNull(indicator);
        Assert.IsType<CoreHmmIndicator>(indicator);
    }

    [Fact]
    public void Calculate_NullOrEmptyCandles_ReturnsEmptyOrFailure()
    {
        var indicator = new CoreHmmIndicator();

        var nullResult = indicator.Calculate(null!);
        Assert.False(nullResult.IsSuccessful);

        var emptyResult = indicator.Calculate(new List<CoreCandleData>());
        Assert.True(emptyResult.IsSuccessful);
        Assert.Empty(emptyResult.MainValues);
    }

    [Fact]
    public void Calculate_InsufficientData_ReturnsAllNulls()
    {
        int period = 20;
        var indicator = new CoreHmmIndicator { Period = period, States = 2 };
        var candles = GenerateCandles(period); // Exactly period candles, need > period

        var result = indicator.Calculate(candles);
        Assert.True(result.IsSuccessful);
        Assert.Equal(period, result.MainValues.Count);
        Assert.All(result.MainValues, v => Assert.Null(v));
    }

    [Fact]
    public void Calculate_WarmUpBars_AreNull_AndFirstValidBarIsInRange()
    {
        int period = 30;
        var indicator = new CoreHmmIndicator { Period = period, States = 2 };
        var candles = GenerateCandles(50);

        var result = indicator.Calculate(candles);
        Assert.True(result.IsSuccessful);
        Assert.Equal(50, result.MainValues.Count);

        // First `period` bars (indices 0 .. period - 1) must be null
        for (int i = 0; i < period; i++)
        {
            Assert.Null(result.MainValues[i]);
        }

        // Subsequent bars (indices period .. count - 1) must have non-null values in [0, 100]
        for (int i = period; i < 50; i++)
        {
            Assert.NotNull(result.MainValues[i]);
            Assert.InRange(result.MainValues[i]!.Value, 0.0000m, 100.0000m);
        }
    }

    [Fact]
    public void Calculate_FlatPrices_ReturnsDeterministicValidOutput()
    {
        int period = 20;
        var indicator = new CoreHmmIndicator { Period = period, States = 2 };
        var date = new DateTime(2023, 1, 1);
        var candles = Enumerable.Range(0, 40).Select(i => new CoreCandleData(
            date.AddDays(i), 100m, 100m, 100m, 100m, 1000
        )).ToList();

        var result = indicator.Calculate(candles);
        Assert.True(result.IsSuccessful);

        for (int i = period; i < 40; i++)
        {
            Assert.NotNull(result.MainValues[i]);
            Assert.InRange(result.MainValues[i]!.Value, 0.0000m, 100.0000m);
        }
    }

    [Fact]
    public void Calculate_CausalityInvariant_FutureBarMutationDoesNotAffectPast()
    {
        int period = 25;
        var indicator1 = new CoreHmmIndicator { Period = period, States = 2 };
        var indicator2 = new CoreHmmIndicator { Period = period, States = 2 };

        var candles1 = GenerateCandles(60, 100m, 1234);
        var candles2 = GenerateCandles(60, 100m, 1234);

        // Mutate future candles (index 45 to 59) in candles2
        for (int i = 45; i < 60; i++)
        {
            candles2[i] = new CoreCandleData(
                candles2[i].Timestamp,
                candles2[i].Open * 2.5m,
                candles2[i].High * 2.5m,
                candles2[i].Low * 2.5m,
                candles2[i].Close * 2.5m,
                candles2[i].Volume * 10
            );
        }

        var result1 = indicator1.Calculate(candles1);
        var result2 = indicator2.Calculate(candles2);

        Assert.True(result1.IsSuccessful);
        Assert.True(result2.IsSuccessful);

        // Bars before mutation (0 to 44) must match exactly
        for (int i = 0; i < 45; i++)
        {
            Assert.Equal(result1.MainValues[i], result2.MainValues[i]);
        }
    }

    [Fact]
    public void Calculate_Deterministic_SameInputProducesIdenticalOutput()
    {
        var indicator1 = new CoreHmmIndicator { Period = 20, States = 3, MaxIterations = 40, Tolerance = 1e-5 };
        var indicator2 = new CoreHmmIndicator { Period = 20, States = 3, MaxIterations = 40, Tolerance = 1e-5 };

        var candles = GenerateCandles(70, 100m, 999);

        var result1 = indicator1.Calculate(candles);
        var result2 = indicator2.Calculate(candles);

        Assert.True(result1.IsSuccessful);
        Assert.True(result2.IsSuccessful);
        Assert.Equal(result1.MainValues.Count, result2.MainValues.Count);

        for (int i = 0; i < result1.MainValues.Count; i++)
        {
            Assert.Equal(result1.MainValues[i], result2.MainValues[i]);
        }
    }

    [Fact]
    public void Calculate_With3States_ExecutesSuccessfullyAndOutputsValidRange()
    {
        int period = 20;
        var indicator = new CoreHmmIndicator { Period = period, States = 3 };
        var candles = GenerateCandles(45, 100m, 555);

        var result = indicator.Calculate(candles);
        Assert.True(result.IsSuccessful);

        for (int i = period; i < 45; i++)
        {
            Assert.NotNull(result.MainValues[i]);
            Assert.InRange(result.MainValues[i]!.Value, 0.0000m, 100.0000m);
        }
    }

    [Fact]
    public void Parameter_Validation_EnforcesValidRanges()
    {
        var param = new CoreHmmParameter();

        // Default valid
        param.Validate();

        // States out of range
        param.States = 1;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.States = 4;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.States = 2;

        // Period out of range
        param.Period = 5;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.Period = 1001;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.Period = 100;

        // MaxIterations out of range
        param.MaxIterations = 0;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.MaxIterations = 201;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.MaxIterations = 30;

        // Tolerance out of range
        param.Tolerance = 1e-7;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.Tolerance = 2e-2;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.Tolerance = 1e-4;

        param.Validate();
    }

    [Fact]
    public void Configure_AppliesParametersCorrectly()
    {
        var indicator = new CoreHmmIndicator();
        var param = new CoreHmmParameter
        {
            Period = 40,
            States = 3,
            MaxIterations = 50,
            Tolerance = 1e-5
        };

        indicator.Configure(param);

        Assert.Equal(40, indicator.Period);
        Assert.Equal(3, indicator.States);
        Assert.Equal(50, indicator.MaxIterations);
        Assert.Equal(1e-5, indicator.Tolerance);
        Assert.Equal("Hidden Markov Model (40,3)", indicator.Name);
    }

    [Fact]
    public void Calculate_InvalidOrNegativePrice_OutputsNullForAffectedWindows()
    {
        int period = 15;
        var indicator = new CoreHmmIndicator { Period = period, States = 2 };
        var candles = GenerateCandles(40, 100m, 123);

        // Inject invalid price at index 25
        candles[25] = new CoreCandleData(
            candles[25].Timestamp,
            Open: 0m,
            High: 0m,
            Low: 0m,
            Close: 0m,
            Volume: 1000
        );

        var result = indicator.Calculate(candles);
        Assert.True(result.IsSuccessful);

        // Any window containing candle 25 (i.e. t from 25 to 25 + period - 1) must be null
        for (int t = 25; t <= Math.Min(39, 25 + period - 1); t++)
        {
            Assert.Null(result.MainValues[t]);
        }

        // Before candle 25 (from index period up to 24), windows are unaffected and non-null
        for (int t = period; t < 25; t++)
        {
            Assert.NotNull(result.MainValues[t]);
        }
    }

    [Fact]
    public void Calculate_ExtremeOutlier_DoesNotProduceNaN()
    {
        int period = 20;
        var indicator = new CoreHmmIndicator { Period = period, States = 2 };
        var candles = GenerateCandles(50, 100m, 777);

        // Single bar massive spike (+500%)
        candles[30] = new CoreCandleData(
            candles[30].Timestamp,
            Open: 600m,
            High: 650m,
            Low: 590m,
            Close: 600m,
            Volume: 50000
        );

        var result = indicator.Calculate(candles);
        Assert.True(result.IsSuccessful);

        for (int i = period; i < 50; i++)
        {
            if (result.MainValues[i].HasValue)
            {
                Assert.InRange(result.MainValues[i]!.Value, 0.0000m, 100.0000m);
            }
        }
    }

    [Fact]
    public void Calculate_ZeroOccupancyDegenerateState_PreservesStability()
    {
        int period = 25;
        // 3 states on a completely monotonic trend where state 0 or 2 will have near zero occupancy
        var indicator = new CoreHmmIndicator { Period = period, States = 3, MaxIterations = 30 };
        var date = new DateTime(2023, 1, 1);
        var candles = Enumerable.Range(0, 50).Select(i => new CoreCandleData(
            date.AddDays(i), 100m + i * 2m, 100m + i * 2m + 1m, 100m + i * 2m - 1m, 100m + i * 2m, 1000
        )).ToList();

        var result = indicator.Calculate(candles);
        Assert.True(result.IsSuccessful);

        for (int i = period; i < 50; i++)
        {
            Assert.NotNull(result.MainValues[i]);
            Assert.InRange(result.MainValues[i]!.Value, 0.0000m, 100.0000m);
        }
    }

    [Fact]
    public void Calculate_StrictDatasetCausality_ExtendedDatasetMatchesPrefix()
    {
        int period = 20;
        var indicator = new CoreHmmIndicator { Period = period, States = 2 };

        var candlesShort = GenerateCandles(40, 100m, 888);
        var candlesLong = new List<CoreCandleData>(candlesShort);
        // Extend with 20 additional arbitrary candles
        var extraCandles = GenerateCandles(20, candlesShort.Last().Close, 999);
        candlesLong.AddRange(extraCandles.Select((c, idx) => new CoreCandleData(
            candlesShort.Last().Timestamp.AddDays(idx + 1), c.Open, c.High, c.Low, c.Close, c.Volume
        )));

        var resultShort = indicator.Calculate(candlesShort);
        var resultLong = indicator.Calculate(candlesLong);

        Assert.True(resultShort.IsSuccessful);
        Assert.True(resultLong.IsSuccessful);

        // All 40 bars in short dataset must match prefix of long dataset exactly
        for (int i = 0; i < 40; i++)
        {
            Assert.Equal(resultShort.MainValues[i], resultLong.MainValues[i]);
        }
    }
}
