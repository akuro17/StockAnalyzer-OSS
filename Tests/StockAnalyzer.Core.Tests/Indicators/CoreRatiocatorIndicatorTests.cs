#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

public class CoreRatiocatorIndicatorTests
{
    private static List<CoreCandleData> CreateTestCandles(decimal[] closes, long[]? volumes = null)
    {
        var startDate = new DateTime(2020, 1, 1);
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
    public void Factory_IsRegistered_ReturnsCorrectType()
    {
        Assert.True(IndicatorFactory.Default.IsRegistered(IndicatorType.Ratiocator));
        var indicator = IndicatorFactory.Default.Create(IndicatorType.Ratiocator);
        Assert.NotNull(indicator);
        Assert.IsType<CoreRatiocatorIndicator>(indicator);
    }

    [Fact]
    public void DefaultSettings_HasCorrectConfiguration()
    {
        var settingsList = DefaultCoreIndicatorSettings.GetDefault();
        var settings = settingsList.FirstOrDefault(s => s.TypeEnum == IndicatorType.Ratiocator);
        Assert.NotNull(settings);
        Assert.False(settings.IsOverlay);
        Assert.Equal(CoreIndicatorCategory.Oscillator, settings.Category);
        Assert.IsType<CoreRatiocatorParameter>(settings.ParameterObject);

        var param = (CoreRatiocatorParameter)settings.ParameterObject!;
        Assert.Equal(IndicatorDefaultConstants.RatiocatorSmoothingPeriod, param.SmoothingPeriod);
        Assert.Equal(IndicatorDefaultConstants.RatiocatorLookbackPeriod, param.LookbackPeriod);
        Assert.Equal(RatiocatorCalculationMode.RollingRatio, param.CalculationMode);
        Assert.Equal(string.Empty, param.ComparisonSymbol);
        Assert.Equal(PriceType.Close, param.ComparisonPriceSource);

        Assert.NotNull(settings.SeriesColors);
        Assert.Contains(settings.SeriesColors, sc => sc.Name == "Ratio" && sc.DisplayName == "Ratiocator");
        Assert.Contains(settings.SeriesColors, sc => sc.Name == "Signal" && sc.DisplayName == "Signal (EMA)");
    }

    [Fact]
    public void Parameter_Validation_EnforcesConstraints()
    {
        var param = new CoreRatiocatorParameter { SmoothingPeriod = 10 };
        param.Validate(); // Should not throw

        var invalidNegative = new CoreRatiocatorParameter { SmoothingPeriod = -1 };
        Assert.Throws<ArgumentOutOfRangeException>(() => invalidNegative.Validate());

        var invalidTooLarge = new CoreRatiocatorParameter { SmoothingPeriod = 1001 };
        Assert.Throws<ArgumentOutOfRangeException>(() => invalidTooLarge.Validate());
    }

    [Fact]
    public void Calculate_WithValidData_ReturnsCorrectRatio()
    {
        var primaryCloses = new decimal[] { 100m, 150m, 200m, 250m };
        var benchmarkCloses = new decimal[] { 50m, 75m, 100m, 125m };

        var primaryCandles = CreateTestCandles(primaryCloses);
        var benchmarkCandles = CreateTestCandles(benchmarkCloses);

        var indicator = new CoreRatiocatorIndicator();
        indicator.CalculationMode = RatiocatorCalculationMode.RawRatio;
        indicator.ComparisonSymbol = "BENCH";
        indicator.SetSecondaryCandles(benchmarkCandles);

        var result = indicator.Calculate(primaryCandles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(4, indicator.Values.Count);

        // Expected: (100/50)*100 = 200, (150/75)*100 = 200, (200/100)*100 = 200, (250/125)*100 = 200
        for (int i = 0; i < 4; i++)
        {
            Assert.NotNull(indicator.Values[i]);
            Assert.Equal(200m, indicator.Values[i]!.Value);
        }
    }

    [Fact]
    public void Calculate_WithExplicitSeriesB_ReturnsCorrectRatio()
    {
        var primaryCloses = new decimal[] { 120m, 140m, 160m };
        var benchmarkSeries = new decimal?[] { 60m, 70m, 80m };

        var primaryCandles = CreateTestCandles(primaryCloses);
        var indicator = new CoreRatiocatorIndicator(benchmarkSeries)
        {
            CalculationMode = RatiocatorCalculationMode.RawRatio
        };

        var result = indicator.Calculate(primaryCandles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(3, indicator.Values.Count);
        Assert.Equal(200m, indicator.Values[0]);
        Assert.Equal(200m, indicator.Values[1]);
        Assert.Equal(200m, indicator.Values[2]);
    }

    [Fact]
    public void Calculate_WithBenchmarkZero_ReturnsNull()
    {
        var primaryCloses = new decimal[] { 100m, 120m, 150m };
        var benchmarkCloses = new decimal[] { 50m, 0m, 50m };

        var primaryCandles = CreateTestCandles(primaryCloses);
        var benchmarkCandles = CreateTestCandles(benchmarkCloses);

        var indicator = new CoreRatiocatorIndicator();
        indicator.CalculationMode = RatiocatorCalculationMode.RawRatio;
        indicator.ComparisonSymbol = "BENCH";
        indicator.SetSecondaryCandles(benchmarkCandles);

        var result = indicator.Calculate(primaryCandles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(200m, indicator.Values[0]);
        Assert.Null(indicator.Values[1]); // zero division avoided
        Assert.Equal(300m, indicator.Values[2]);
    }

    [Fact]
    public void Calculate_WithNullBenchmark_ReturnsNull()
    {
        var primaryCloses = new decimal[] { 100m, 120m, 150m };
        var primaryCandles = CreateTestCandles(primaryCloses);

        var benchmarkCandles = new List<CoreCandleData?>
        {
            new CoreCandleData(DateTime.Today, 50m, 50m, 50m, 50m, 1000),
            null,
            new CoreCandleData(DateTime.Today.AddDays(2), 50m, 50m, 50m, 50m, 1000)
        };

        var indicator = new CoreRatiocatorIndicator();
        indicator.CalculationMode = RatiocatorCalculationMode.RawRatio;
        indicator.ComparisonSymbol = "BENCH";
        indicator.SetSecondaryCandles(benchmarkCandles);

        var result = indicator.Calculate(primaryCandles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(200m, indicator.Values[0]);
        Assert.Null(indicator.Values[1]);
        Assert.Equal(300m, indicator.Values[2]);
    }

    [Fact]
    public void Calculate_WithoutComparisonSymbol_ReturnsAllNull()
    {
        var primaryCloses = new decimal[] { 100m, 120m, 150m };
        var primaryCandles = CreateTestCandles(primaryCloses);

        var indicator = new CoreRatiocatorIndicator();
        var result = indicator.Calculate(primaryCandles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(3, indicator.Values.Count);
        Assert.All(indicator.Values, v => Assert.Null(v));
    }

    [Fact]
    public void Calculate_WithDifferentLengthSeries_AlignsFromEnd()
    {
        // Primary has 4 bars, secondary has 2 bars -> last 2 bars should align
        var primaryCloses = new decimal[] { 80m, 90m, 100m, 120m };
        var benchmarkCloses = new decimal[] { 50m, 60m };

        var primaryCandles = CreateTestCandles(primaryCloses);
        var benchmarkCandles = CreateTestCandles(benchmarkCloses);

        var indicator = new CoreRatiocatorIndicator();
        indicator.CalculationMode = RatiocatorCalculationMode.RawRatio;
        indicator.ComparisonSymbol = "BENCH";
        indicator.SetSecondaryCandles(benchmarkCandles);

        var result = indicator.Calculate(primaryCandles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(4, indicator.Values.Count);
        Assert.Null(indicator.Values[0]);
        Assert.Null(indicator.Values[1]);
        Assert.Equal(200m, indicator.Values[2]); // 100 / 50 * 100 = 200
        Assert.Equal(200m, indicator.Values[3]); // 120 / 60 * 100 = 200
    }

    [Fact]
    public void Calculate_WithSmoothingPeriod_ReturnsSignalLine()
    {
        var primaryCloses = new decimal[] { 100m, 110m, 120m, 130m, 140m };
        var benchmarkCloses = new decimal[] { 50m, 50m, 50m, 50m, 50m };

        var primaryCandles = CreateTestCandles(primaryCloses);
        var benchmarkCandles = CreateTestCandles(benchmarkCloses);

        var indicator = new CoreRatiocatorIndicator(smoothingPeriod: 3);
        indicator.CalculationMode = RatiocatorCalculationMode.RawRatio;
        indicator.ComparisonSymbol = "BENCH";
        indicator.SetSecondaryCandles(benchmarkCandles);

        var result = indicator.Calculate(primaryCandles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(5, indicator.SignalValues.Count);
        // With smoothing period 3, first 2 are null (warmup)
        Assert.Null(indicator.SignalValues[0]);
        Assert.Null(indicator.SignalValues[1]);
        Assert.NotNull(indicator.SignalValues[2]);
        Assert.NotNull(indicator.SignalValues[3]);
        Assert.NotNull(indicator.SignalValues[4]);
    }

    [Fact]
    public void Calculate_WithSmoothingPeriodZero_SignalLineAllNull()
    {
        var primaryCloses = new decimal[] { 100m, 110m, 120m };
        var benchmarkCloses = new decimal[] { 50m, 50m, 50m };

        var primaryCandles = CreateTestCandles(primaryCloses);
        var benchmarkCandles = CreateTestCandles(benchmarkCloses);

        var indicator = new CoreRatiocatorIndicator(smoothingPeriod: 0);
        indicator.CalculationMode = RatiocatorCalculationMode.RawRatio;
        indicator.ComparisonSymbol = "BENCH";
        indicator.SetSecondaryCandles(benchmarkCandles);

        var result = indicator.Calculate(primaryCandles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(3, indicator.SignalValues.Count);
        Assert.All(indicator.SignalValues, v => Assert.Null(v));
    }

    [Fact]
    public void Calculate_EmptyCandles_ReturnsEmpty()
    {
        var indicator = new CoreRatiocatorIndicator();
        var result = indicator.Calculate(new List<CoreCandleData>());
        Assert.Empty(result);
    }

    [Fact]
    public void Configure_WithRatiocatorParameter_SetsProperties()
    {
        var indicator = new CoreRatiocatorIndicator();
        var param = new CoreRatiocatorParameter
        {
            ComparisonSymbol = "spy",
            ComparisonPriceSource = PriceType.Open,
            CalculationMode = RatiocatorCalculationMode.RawRatio,
            SmoothingPeriod = 14
        };

        indicator.Configure(param);

        Assert.Equal("SPY", indicator.ComparisonSymbol);
        Assert.Equal(PriceType.Open, indicator.ComparisonPriceSource);
        Assert.Equal(14, indicator.SmoothingPeriod);
        Assert.Equal("Ratiocator(SPY, EMA14)", indicator.Name);
    }

    [Fact]
    public void Configure_WithSmaParameter_SetsSmoothingPeriod()
    {
        var indicator = new CoreRatiocatorIndicator();
        var param = new CoreSmaParameter { Period = 21 };

        indicator.Configure(param);

        Assert.Equal(21, indicator.SmoothingPeriod);
    }

    [Fact]
    public void Calculate_PrimaryPriceZero_ReturnsZero()
    {
        var primaryCloses = new decimal[] { 0m, 100m };
        var benchmarkCloses = new decimal[] { 50m, 50m };

        var primaryCandles = CreateTestCandles(primaryCloses);
        var benchmarkCandles = CreateTestCandles(benchmarkCloses);

        var indicator = new CoreRatiocatorIndicator();
        indicator.CalculationMode = RatiocatorCalculationMode.RawRatio;
        indicator.ComparisonSymbol = "BENCH";
        indicator.SetSecondaryCandles(benchmarkCandles);

        var result = indicator.Calculate(primaryCandles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(0m, indicator.Values[0]);
        Assert.Equal(200m, indicator.Values[1]);
    }

    [Fact]
    public void Calculate_WithNormalizedMode_FirstValidBarIs100()
    {
        var primaryCloses = new decimal[] { 200m, 220m, 260m };
        var benchmarkCloses = new decimal[] { 100m, 100m, 100m };

        var primaryCandles = CreateTestCandles(primaryCloses);
        var benchmarkCandles = CreateTestCandles(benchmarkCloses);

        var indicator = new CoreRatiocatorIndicator
        {
            ComparisonSymbol = "BENCH",
            CalculationMode = RatiocatorCalculationMode.NormalizedRatio
        };
        indicator.SetSecondaryCandles(benchmarkCandles);

        var result = indicator.Calculate(primaryCandles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(3, indicator.Values.Count);
        // Base ratio is 200/100*100 = 200
        // Normalized: (200/200)*100 = 100, (220/200)*100 = 110, (260/200)*100 = 130
        Assert.Equal(100m, indicator.Values[0]);
        Assert.Equal(110m, indicator.Values[1]);
        Assert.Equal(130m, indicator.Values[2]);
    }

    [Fact]
    public void Calculate_WithBenchmarkNegativeOrZero_ReturnsNull()
    {
        var primaryCloses = new decimal[] { 100m, 100m, 100m };
        var benchmarkCloses = new decimal[] { 50m, 0m, -10m };

        var primaryCandles = CreateTestCandles(primaryCloses);
        var benchmarkCandles = CreateTestCandles(benchmarkCloses);

        var indicator = new CoreRatiocatorIndicator();
        indicator.CalculationMode = RatiocatorCalculationMode.RawRatio;
        indicator.ComparisonSymbol = "BENCH";
        indicator.SetSecondaryCandles(benchmarkCandles);

        var result = indicator.Calculate(primaryCandles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(200m, indicator.Values[0]);
        Assert.Null(indicator.Values[1]);
        Assert.Null(indicator.Values[2]);
    }

    [Fact]
    public void Calculate_WithPrimaryNegative_ReturnsNull()
    {
        var primaryCloses = new decimal[] { 100m, -50m, 100m };
        var benchmarkCloses = new decimal[] { 50m, 50m, 50m };

        var primaryCandles = CreateTestCandles(primaryCloses);
        var benchmarkCandles = CreateTestCandles(benchmarkCloses);

        var indicator = new CoreRatiocatorIndicator();
        indicator.CalculationMode = RatiocatorCalculationMode.RawRatio;
        indicator.ComparisonSymbol = "BENCH";
        indicator.SetSecondaryCandles(benchmarkCandles);

        var result = indicator.Calculate(primaryCandles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(200m, indicator.Values[0]);
        Assert.Null(indicator.Values[1]);
        Assert.Equal(200m, indicator.Values[2]);
    }

    [Fact]
    public void Configure_WithNormalizedMode_SetsNameAndProperties()
    {
        var indicator = new CoreRatiocatorIndicator();
        var param = new CoreRatiocatorParameter
        {
            ComparisonSymbol = "SPY",
            CalculationMode = RatiocatorCalculationMode.NormalizedRatio,
            SmoothingPeriod = 14
        };

        indicator.Configure(param);

        Assert.Equal("SPY", indicator.ComparisonSymbol);
        Assert.Equal(RatiocatorCalculationMode.NormalizedRatio, indicator.CalculationMode);
        Assert.Equal(14, indicator.SmoothingPeriod);
        Assert.Equal("Ratiocator(SPY, Norm, EMA14)", indicator.Name);
    }

    [Fact]
    public void Parameter_ImplementsICrossTickerParameter()
    {
        var param = new CoreRatiocatorParameter
        {
            ComparisonSymbol = "spy",
            ComparisonPriceSource = PriceType.High
        };

        Assert.IsAssignableFrom<ICrossTickerParameter>(param);
        var crossParam = (ICrossTickerParameter)param;
        Assert.Equal("SPY", crossParam.ComparisonSymbol);
        Assert.Equal(PriceType.High, crossParam.ComparisonPriceSource);
    }

    [Theory]
    [InlineData("", RatiocatorCalculationMode.RawRatio, 20, 0, "Ratiocator")]
    [InlineData("SPY", RatiocatorCalculationMode.RawRatio, 20, 0, "Ratiocator(SPY)")]
    [InlineData("SPY", RatiocatorCalculationMode.NormalizedRatio, 20, 0, "Ratiocator(SPY, Norm)")]
    [InlineData("SPY", RatiocatorCalculationMode.NormalizedRatio, 20, 14, "Ratiocator(SPY, Norm, EMA14)")]
    [InlineData("SPY", RatiocatorCalculationMode.RollingRatio, 20, 0, "Ratiocator(SPY, Roll20)")]
    [InlineData("SPY", RatiocatorCalculationMode.RollingRatio, 25, 10, "Ratiocator(SPY, Roll25, EMA10)")]
    [InlineData("", RatiocatorCalculationMode.RollingRatio, 15, 0, "Ratiocator(Roll15)")]
    [InlineData("", RatiocatorCalculationMode.NormalizedRatio, 20, 20, "Ratiocator(Norm, EMA20)")]
    public void Parameter_GetDisplayName_FormatsCorrectly(string symbol, RatiocatorCalculationMode mode, int lookback, int smoothing, string expected)
    {
        var param = new CoreRatiocatorParameter
        {
            ComparisonSymbol = symbol,
            CalculationMode = mode,
            LookbackPeriod = lookback,
            SmoothingPeriod = smoothing
        };

        Assert.Equal(expected, param.GetDisplayName("Ratiocator"));
    }

    [Fact]
    public void Calculate_RollingRatio_CalculatesFormulaCorrectly()
    {
        // N = 3, formula: RAT_t = (P_t / M_t) / (P_{t-N} / M_{t-N}) * 100
        var primaryCloses = new decimal[] { 100m, 110m, 120m, 130m, 150m, 160m };
        var benchmarkCloses = new decimal[] { 50m, 50m, 50m, 50m, 60m, 80m };
        var benchmarkSeries = benchmarkCloses.Select(c => (decimal?)c).ToList();

        var indicator = new CoreRatiocatorIndicator(lookbackPeriod: 3, smoothingPeriod: 0, benchmarkSeries)
        {
            CalculationMode = RatiocatorCalculationMode.RollingRatio
        };

        var candles = CreateTestCandles(primaryCloses);
        var result = indicator.Calculate(candles);

        Assert.Equal(6, result.Count);
        // t = 0, 1, 2 (< N=3) must be null
        Assert.Null(result[0]);
        Assert.Null(result[1]);
        Assert.Null(result[2]);

        // t = 3: A_3 = 130/50 = 2.6; A_0 = 100/50 = 2.0; RAT_3 = 2.6 / 2.0 * 100 = 130.0m
        Assert.NotNull(result[3]);
        Assert.Equal(130.0m, result[3]!.Value);

        // t = 4: A_4 = 150/60 = 2.5; A_1 = 110/50 = 2.2; RAT_4 = 2.5 / 2.2 * 100 = 113.63636363636363636363636364m
        Assert.NotNull(result[4]);
        Assert.Equal(2.5m / 2.2m * 100m, result[4]!.Value);

        // t = 5: A_5 = 160/80 = 2.0; A_2 = 120/50 = 2.4; RAT_5 = 2.0 / 2.4 * 100 = 83.33333333333333333333333333m
        Assert.NotNull(result[5]);
        Assert.Equal(2.0m / 2.4m * 100m, result[5]!.Value);
    }

    [Fact]
    public void Calculate_RollingRatio_FirstNBarsAreNull()
    {
        int N = 5;
        var primaryCloses = Enumerable.Range(1, 10).Select(i => (decimal)i * 10m).ToArray();
        var benchmarkCloses = Enumerable.Repeat(50m, 10).ToArray();
        var benchmarkSeries = benchmarkCloses.Select(c => (decimal?)c).ToList();

        var indicator = new CoreRatiocatorIndicator(lookbackPeriod: N, smoothingPeriod: 0, benchmarkSeries)
        {
            CalculationMode = RatiocatorCalculationMode.RollingRatio
        };

        var candles = CreateTestCandles(primaryCloses);
        var result = indicator.Calculate(candles);

        for (int i = 0; i < N; i++)
        {
            Assert.Null(result[i]);
        }
        for (int i = N; i < 10; i++)
        {
            Assert.NotNull(result[i]);
        }
    }

    [Fact]
    public void Calculate_RollingRatio_NonPositiveGuard_ReturnsNull()
    {
        var primaryCloses = new decimal[] { 100m, 0m, 100m, 100m };
        var benchmarkCloses = new decimal[] { 50m, 50m, 50m, 50m };
        var benchmarkSeries = benchmarkCloses.Select(c => (decimal?)c).ToList();

        var indicator = new CoreRatiocatorIndicator(lookbackPeriod: 2, smoothingPeriod: 0, benchmarkSeries)
        {
            CalculationMode = RatiocatorCalculationMode.RollingRatio
        };

        var candles = CreateTestCandles(primaryCloses);
        var result = indicator.Calculate(candles);

        // t = 2: past bar is t=0 (valid), primary is 100, bench is 50 -> valid
        Assert.NotNull(result[2]);
        Assert.Equal(100.0m, result[2]!.Value);

        // t = 3: past bar is t=1 (primary is 0m -> pastA is 0m) -> division by zero guard yields null
        Assert.Null(result[3]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1001)]
    public void Parameter_LookbackPeriod_Validation_ThrowsOutOfRange(int invalidLookback)
    {
        var param = new CoreRatiocatorParameter { LookbackPeriod = invalidLookback };
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
    }

    [Fact]
    public void Configure_WithRatiocatorParameter_RollingRatio_SetsProperties()
    {
        var indicator = new CoreRatiocatorIndicator();
        var param = new CoreRatiocatorParameter
        {
            ComparisonSymbol = "spy",
            ComparisonPriceSource = PriceType.Close,
            CalculationMode = RatiocatorCalculationMode.RollingRatio,
            LookbackPeriod = 30,
            SmoothingPeriod = 10
        };

        indicator.Configure(param);

        Assert.Equal("SPY", indicator.ComparisonSymbol);
        Assert.Equal(RatiocatorCalculationMode.RollingRatio, indicator.CalculationMode);
        Assert.Equal(30, indicator.LookbackPeriod);
        Assert.Equal(10, indicator.SmoothingPeriod);
        Assert.Equal("Ratiocator(SPY, Roll30, EMA10)", indicator.Name);
    }

    [Fact]
    public void Parameter_Defaults_MatchExpectedValues()
    {
        var param = new CoreRatiocatorParameter();

        Assert.Equal(RatiocatorCalculationMode.RollingRatio, param.CalculationMode);
        Assert.Equal(IndicatorDefaultConstants.RatiocatorLookbackPeriod, param.LookbackPeriod);
        Assert.Equal(IndicatorDefaultConstants.RatiocatorSmoothingPeriod, param.SmoothingPeriod);
        Assert.Equal(string.Empty, param.ComparisonSymbol);
        Assert.Equal(PriceType.Close, param.ComparisonPriceSource);
    }

    [Fact]
    public void Indicator_Defaults_MatchExpectedValues()
    {
        var indicator = new CoreRatiocatorIndicator();

        Assert.Equal(RatiocatorCalculationMode.RollingRatio, indicator.CalculationMode);
        Assert.Equal(IndicatorDefaultConstants.RatiocatorLookbackPeriod, indicator.LookbackPeriod);
        Assert.Equal(IndicatorDefaultConstants.RatiocatorSmoothingPeriod, indicator.SmoothingPeriod);
    }

    [Theory]
    [InlineData(RatiocatorCalculationMode.RollingRatio, 20, 10, 30)] // 20 + 10 = 30
    [InlineData(RatiocatorCalculationMode.RollingRatio, 20, 0, 20)]  // 20 + 0 = 20
    [InlineData(RatiocatorCalculationMode.RollingRatio, 20, 1, 20)]  // smoothing 1 is inactive
    [InlineData(RatiocatorCalculationMode.RawRatio, 20, 10, 10)]     // raw ratio only needs smoothing
    [InlineData(RatiocatorCalculationMode.RawRatio, 20, 0, 0)]       // no smoothing -> 0
    [InlineData(RatiocatorCalculationMode.NormalizedRatio, 20, 10, 10)] // normalized only needs smoothing
    [InlineData(RatiocatorCalculationMode.NormalizedRatio, 20, 0, 0)]  // no smoothing -> 0
    public void Parameter_GetRequiredWarmupBars_ReturnsExpectedBars(
        RatiocatorCalculationMode mode, int lookback, int smoothing, int expectedWarmup)
    {
        var param = new CoreRatiocatorParameter
        {
            CalculationMode = mode,
            LookbackPeriod = lookback,
            SmoothingPeriod = smoothing
        };

        Assert.Equal(expectedWarmup, param.GetRequiredWarmupBars());
    }
}

