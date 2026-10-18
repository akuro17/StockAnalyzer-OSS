using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using StockAnalyzer.Core.Models.Indicators.Volume;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class Batch3IndicatorNullGuardsTests
{
    private static List<CoreCandleData> MakeCandles(params decimal[] closes)
    {
        var list = new List<CoreCandleData>();
        var date = new DateTime(2025, 1, 1);
        for (int i = 0; i < closes.Length; i++)
        {
            decimal c = closes[i];
            list.Add(new CoreCandleData(date.AddDays(i), c, c + 1, c - 1, c, 1000));
        }
        return list;
    }

    [Fact]
    public void VarIndicator_ZeroOrNegativePreviousPrice_OutputsNull()
    {
        var indicator = new CoreVarIndicator { Period = 3 };
        var candles = MakeCandles(10m, 0m, 10m, 20m, 30m);
        indicator.Calculate(candles);

        Assert.Equal(5, indicator.Values.Count);
        // Returns: [ (0-10)/10=-1, (10-0)/0=>null, (20-10)/10=1, (30-20)/20=0.5 ]
        // Window ending at bar 3: returns at index 0,1,2 -> contains null -> output null
        Assert.Null(indicator.Values[3]);
    }

    [Fact]
    public void VolumeRocIndicator_ZeroPreviousVolume_OutputsNull()
    {
        var indicator = new CoreVolumeRocIndicator { Period = 1 };
        var candles = new List<CoreCandleData>
        {
            new CoreCandleData(DateTime.Today, 10, 11, 9, 10, 0),
            new CoreCandleData(DateTime.Today.AddDays(1), 10, 11, 9, 10, 1000)
        };
        indicator.Calculate(candles);

        Assert.Equal(2, indicator.Values.Count);
        Assert.Null(indicator.Values[0]);
        Assert.Null(indicator.Values[1]); // prevVolume is 0 -> null
    }

    [Fact]
    public void KstIndicator_ZeroPastPrice_RocBecomesNullAndPropagates()
    {
        var indicator = new CoreKstIndicator();
        // Zero price series
        var candles = MakeCandles(Enumerable.Repeat(0m, 60).ToArray());
        indicator.Calculate(candles);

        Assert.Equal(60, indicator.Values.Count);
        Assert.All(indicator.Values, Assert.Null);
    }

    [Fact]
    public void MassIndexIndicator_FlatCandles_DoubleEmaZeroOutputsNull()
    {
        var indicator = new CoreMassIndexIndicator { Period = 5, EmaPeriod = 2 };
        // Zero range candles (High == Low)
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < 20; i++)
        {
            candles.Add(new CoreCandleData(DateTime.Today.AddDays(i), 100m, 100m, 100m, 100m, 1000));
        }
        indicator.Calculate(candles);

        Assert.Equal(20, indicator.Values.Count);
        // When High == Low, range is 0, EMA2 is 0 -> ratio is null -> all null
        Assert.All(indicator.Values, Assert.Null);
    }

    [Fact]
    public void VortexSignalIndicator_ZeroTr_OutputsNull()
    {
        var indicator = new CoreVortexSignalIndicator { Period = 3 };
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < 10; i++)
        {
            candles.Add(new CoreCandleData(DateTime.Today.AddDays(i), 50m, 50m, 50m, 50m, 0));
        }
        indicator.Calculate(candles);

        Assert.Equal(10, indicator.Values.Count);
        Assert.All(indicator.Values, Assert.Null);
    }

    [Fact]
    public void UltimateOscillatorIndicator_ZeroTr_OutputsNull()
    {
        var indicator = new CoreUltimateOscillatorIndicator { Period1 = 2, Period2 = 3, Period3 = 4 };
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < 10; i++)
        {
            candles.Add(new CoreCandleData(DateTime.Today.AddDays(i), 50m, 50m, 50m, 50m, 0));
        }
        indicator.Calculate(candles);

        Assert.Equal(10, indicator.Values.Count);
        Assert.All(indicator.Values, Assert.Null);
    }

    [Fact]
    public void VarianceIndicator_SampleVarianceVsPopulationVariance()
    {
        var closes = new decimal[] { 10m, 12m, 11m, 13m, 10m };
        var candles = MakeCandles(closes);

        var sample = new CoreVarianceIndicator { Period = 5, IsSampleVariance = true };
        sample.Calculate(candles);
        Assert.Equal(1.7m, sample.Values.Last()!.Value, 4);

        var pop = new CoreVarianceIndicator { Period = 5, IsSampleVariance = false };
        pop.Calculate(candles);
        Assert.Equal(1.36m, pop.Values.Last()!.Value, 4);
    }

    [Fact]
    public void MaDeviationRateIndicator_SmaZero_OutputsNull()
    {
        var indicator = new CoreMaDeviationRateIndicator { Period = 2 };
        var candles = MakeCandles(0m, 0m, 0m);
        indicator.Calculate(candles);

        Assert.Equal(3, indicator.Values.Count);
        Assert.Null(indicator.Values[1]);
        Assert.Null(indicator.Values[2]);
    }

    [Fact]
    public void PsychologicalLineIndicator_ValidCalculations()
    {
        var indicator = new CorePsychologicalLineIndicator { Period = 3 };
        // 10 -> 12 (up), 12 -> 11 (down), 11 -> 13 (up)
        var candles = MakeCandles(10m, 12m, 11m, 13m);
        indicator.Calculate(candles);

        Assert.Equal(4, indicator.Values.Count);
        Assert.Null(indicator.Values[0]);
        Assert.Null(indicator.Values[1]);
        Assert.Null(indicator.Values[2]);
        // At index 3: transitions are (10->12: up), (12->11: down), (11->13: up) -> 2 up out of 3 = 66.6666...%
        Assert.NotNull(indicator.Values[3]);
        Assert.Equal(200m / 3m, indicator.Values[3]!.Value);
    }

    [Fact]
    public void RviIndicator_FlatCandles_DenSumZeroNumSumZero_OutputsZero()
    {
        var indicator = new CoreRviIndicator { Period = 4 };
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < 15; i++)
        {
            candles.Add(new CoreCandleData(DateTime.Today.AddDays(i), 50m, 50m, 50m, 50m, 100));
        }
        indicator.Calculate(candles);

        // When completely flat (num=0 and den=0), outputs 0m
        Assert.Equal(0m, indicator.Values.Last()!.Value);
    }

    [Fact]
    public void LinearRegression_And_RegressionSlope_ExactDecimalPrecision()
    {
        // Pure linear line: y = 2 * x + 5, for x = 0..4: y = 5, 7, 9, 11, 13
        var candles = MakeCandles(5m, 7m, 9m, 11m, 13m);

        var lr = new CoreLinearRegressionIndicator { Period = 5 };
        lr.Calculate(candles);
        Assert.Equal(13m, lr.Values.Last()!.Value); // at x=4, y=13

        var slope = new CoreRegressionSlopeIndicator { Period = 5 };
        slope.Calculate(candles);
        Assert.Equal(2m, slope.Values.Last()!.Value); // slope is 2
    }
}
