using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Statistics;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

public class CoreRollingBetaIndicatorTests
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
        Assert.True(IndicatorFactory.Default.IsRegistered(IndicatorType.RollingBeta));
        var indicator = IndicatorFactory.Default.Create(IndicatorType.RollingBeta);
        Assert.NotNull(indicator);
        Assert.IsType<CoreRollingBetaIndicator>(indicator);
    }

    [Fact]
    public void DefaultSettings_HasCorrectConfiguration()
    {
        var settingsList = DefaultCoreIndicatorSettings.GetDefault();
        var settings = settingsList.FirstOrDefault(s => s.TypeEnum == IndicatorType.RollingBeta);
        Assert.NotNull(settings);
        Assert.False(settings.IsOverlay);
        // Dynamic auto-scaling: MinValue and MaxValue must be null
        Assert.Null(settings.MinValue);
        Assert.Null(settings.MaxValue);
        Assert.IsType<CoreRollingBetaParameter>(settings.ParameterObject);
        var param = (CoreRollingBetaParameter)settings.ParameterObject!;
        Assert.Equal(IndicatorDefaultConstants.RollingBetaPeriod, param.Period);
        Assert.Equal(BetaCalculationMode.SimpleReturn, param.CalculationMode);

        // Verify SeriesColors contain Beta and Correlation
        Assert.NotNull(settings.SeriesColors);
        Assert.Contains(settings.SeriesColors, sc => sc.Name == "Values" && sc.DisplayName == "Beta");
        Assert.Contains(settings.SeriesColors, sc => sc.Name == CoreRollingBetaIndicator.CorrelationSeriesName && sc.DisplayName == "Correlation");
    }

    [Fact]
    public void Parameter_Validation_EnforcesConstraints()
    {
        var param = new CoreRollingBetaParameter
        {
            Period = 60,
            ComparisonSymbol = "SPY"
        };
        param.Validate();

        param.Period = 4;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());

        param.Period = 1001;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
    }

    [Fact]
    public void Calculate_WithBenchmarkEqualToAsset_ReturnsBetaOneAndAlphaZero()
    {
        // 7 prices with non-constant returns
        var closes = new decimal[] { 100m, 105m, 102m, 108m, 104m, 110m, 107m };
        var seriesA = CreateTestCandles(closes);
        var seriesB = CreateTestCandles(closes);

        var indicator = new CoreRollingBetaIndicator(5, seriesB);
        var result = indicator.Calculate(seriesA);

        Assert.True(result.IsSuccessful);
        Assert.NotNull(indicator.Values.Last());
        Assert.Equal(1.0m, indicator.Values.Last()!.Value, 4);

        // Rolling correlation should also be 1.0
        Assert.NotNull(indicator.RollingCorrelation.Last());
        Assert.Equal(1.0m, indicator.RollingCorrelation.Last()!.Value, 4);
    }

    [Fact]
    public void Calculate_WithDoubleVolatilityAsset_ReturnsBetaTwo()
    {
        // Bench prices
        var benchCloses = new decimal[] { 100m, 110m, 99m, 108.9m, 98.01m, 107.811m };
        // Log return mode: P_asset = 100 * (P_bench / 100)^2 gives exact 2x log returns
        var assetCloses = benchCloses.Select(b => 100m * (b / 100m) * (b / 100m)).ToArray();

        var seriesA = CreateTestCandles(assetCloses);
        var seriesB = CreateTestCandles(benchCloses);

        var indicator = new CoreRollingBetaIndicator(4, seriesB)
        {
            CalculationMode = BetaCalculationMode.LogReturn
        };
        var result = indicator.Calculate(seriesA);

        Assert.True(result.IsSuccessful);
        Assert.NotNull(indicator.Values.Last());
        Assert.Equal(2.0m, indicator.Values.Last()!.Value, 4);
    }

    [Fact]
    public void Calculate_WithInverseBenchmark_ReturnsNegativeBeta()
    {
        var benchCloses = new decimal[] { 100m, 110m, 99m, 108.9m, 98.01m, 107.811m };
        var assetCloses = benchCloses.Select(b => 10000m / b).ToArray();

        var seriesA = CreateTestCandles(assetCloses);
        var seriesB = CreateTestCandles(benchCloses);

        var indicator = new CoreRollingBetaIndicator(4, seriesB)
        {
            CalculationMode = BetaCalculationMode.LogReturn
        };
        var result = indicator.Calculate(seriesA);

        Assert.True(result.IsSuccessful);
        Assert.NotNull(indicator.Values.Last());
        Assert.Equal(-1.0m, indicator.Values.Last()!.Value, 4);
    }

    [Fact]
    public void Calculate_SimpleReturnMode_CalculatesCorrectBeta()
    {
        // Bench simple returns: r = (P_t - P_{t-1}) / P_{t-1}
        var benchCloses = new decimal[] { 100m, 110m, 105m, 115m, 110m, 120m };
        // Exact 2x linear returns: asset prices such that r_asset = 2 * r_bench
        var assetCloses = new decimal[benchCloses.Length];
        assetCloses[0] = 100m;
        for (int i = 1; i < benchCloses.Length; i++)
        {
            decimal benchReturn = (benchCloses[i] - benchCloses[i - 1]) / benchCloses[i - 1];
            decimal assetReturn = 2m * benchReturn;
            assetCloses[i] = assetCloses[i - 1] * (1m + assetReturn);
        }

        var seriesA = CreateTestCandles(assetCloses);
        var seriesB = CreateTestCandles(benchCloses);

        var indicator = new CoreRollingBetaIndicator(5, seriesB)
        {
            CalculationMode = BetaCalculationMode.SimpleReturn
        };
        var result = indicator.Calculate(seriesA);

        Assert.True(result.IsSuccessful);
        Assert.NotNull(indicator.Values.Last());
        Assert.Equal(2.0m, indicator.Values.Last()!.Value, 4);
    }

    [Fact]
    public void Calculate_MathematicalParity_BetaEqualsRhoTimesStdDevRatio()
    {
        // Semi-random data to verify β = ρ * (σ_y / σ_x)
        var benchCloses = new decimal[] { 100m, 102m, 98m, 103m, 99m, 104m, 101m, 105m };
        var assetCloses = new decimal[] { 50m, 52m, 48m, 53m, 51m, 55m, 53m, 56m };

        var seriesA = CreateTestCandles(assetCloses);
        var seriesB = CreateTestCandles(benchCloses);

        int period = 5;
        var indicator = new CoreRollingBetaIndicator(period, seriesB)
        {
            CalculationMode = BetaCalculationMode.SimpleReturn
        };
        var result = indicator.Calculate(seriesA);

        Assert.True(result.IsSuccessful);
        decimal beta = indicator.Values.Last()!.Value;
        decimal rho = indicator.RollingCorrelation.Last()!.Value;

        // Compute sample standard deviations manually on the last window
        var returnsA = new List<decimal>();
        var returnsB = new List<decimal>();
        for (int i = assetCloses.Length - period; i < assetCloses.Length; i++)
        {
            returnsA.Add((assetCloses[i] - assetCloses[i - 1]) / assetCloses[i - 1]);
            returnsB.Add((benchCloses[i] - benchCloses[i - 1]) / benchCloses[i - 1]);
        }
        decimal meanA = returnsA.Average();
        decimal meanB = returnsB.Average();
        decimal sxx = returnsB.Sum(x => (x - meanB) * (x - meanB));
        decimal syy = returnsA.Sum(y => (y - meanA) * (y - meanA));
        decimal stdDevRatio = (decimal)(Math.Sqrt((double)syy) / Math.Sqrt((double)sxx));

        decimal expectedBeta = rho * stdDevRatio;
        Assert.Equal(expectedBeta, beta, 4);
    }

    [Fact]
    public void Calculate_WarmupPeriodReturnsNulls()
    {
        var closes = new decimal[] { 100m, 105m, 102m, 108m, 104m, 110m, 107m };
        var seriesA = CreateTestCandles(closes);
        var seriesB = CreateTestCandles(closes);

        int period = 5;
        var indicator = new CoreRollingBetaIndicator(period, seriesB);
        var result = indicator.Calculate(seriesA);

        Assert.True(result.IsSuccessful);
        // Returns are null at index 0. So rolling window of 5 returns needs indices 1..5.
        // Therefore indices 0..4 (first 5 bars) must be null.
        for (int i = 0; i < period; i++)
        {
            Assert.Null(indicator.Values[i]);
            Assert.Null(indicator.RollingCorrelation[i]);
        }
        // At index 5, there are 5 returns (from indices 1, 2, 3, 4, 5) -> valid value
        Assert.NotNull(indicator.Values[period]);
        Assert.NotNull(indicator.RollingCorrelation[period]);
    }

    [Fact]
    public void Calculate_WithEmptyCandles_ReturnsEmpty()
    {
        var indicator = new CoreRollingBetaIndicator(5);
        var result = indicator.Calculate(new List<CoreCandleData>());
        Assert.True(result.IsSuccessful);
        Assert.Empty(result);
    }

    [Fact]
    public void Calculate_WithNullCandles_ReturnsFailure()
    {
        var indicator = new CoreRollingBetaIndicator(5);
        var result = indicator.Calculate(null!);
        Assert.False(result.IsSuccessful);
    }

    [Fact]
    public void Calculate_WithSingleCandle_ReturnsAllNulls()
    {
        var series = CreateTestCandles(new decimal[] { 100m });
        var seriesB = CreateTestCandles(new decimal[] { 100m });
        var indicator = new CoreRollingBetaIndicator(5, seriesB);
        var result = indicator.Calculate(series);

        Assert.True(result.IsSuccessful);
        Assert.Single(indicator.Values);
        Assert.Null(indicator.Values[0]);
    }

    [Fact]
    public void Calculate_WithConstantBenchmark_ReturnsNullBeta()
    {
        // Benchmark has 0 variance (all prices same)
        var assetCloses = new decimal[] { 100m, 105m, 102m, 108m, 104m, 110m };
        var benchCloses = new decimal[] { 100m, 100m, 100m, 100m, 100m, 100m };

        var seriesA = CreateTestCandles(assetCloses);
        var seriesB = CreateTestCandles(benchCloses);

        var indicator = new CoreRollingBetaIndicator(5, seriesB);
        var result = indicator.Calculate(seriesA);

        Assert.True(result.IsSuccessful);
        Assert.Null(indicator.Values.Last());
        Assert.Null(indicator.RollingCorrelation.Last());
    }

    [Fact]
    public void Calculate_WithNoBenchmarkSymbol_ReturnsAllNulls()
    {
        // When ComparisonSymbol is empty and no explicit series:
        // Must NOT fallback to Volume; must strictly return nulls.
        var seriesA = CreateTestCandles(new decimal[] { 100m, 105m, 102m, 108m, 104m, 110m });
        var indicator = new CoreRollingBetaIndicator(5);
        var result = indicator.Calculate(seriesA);

        Assert.True(result.IsSuccessful);
        Assert.Equal(seriesA.Count, indicator.Values.Count);
        Assert.All(indicator.Values, Assert.Null);
        Assert.All(indicator.RollingCorrelation, Assert.Null);
    }

    [Fact]
    public void Calculate_WithComparisonSymbolUnloaded_ReturnsAllNulls()
    {
        var seriesA = CreateTestCandles(new decimal[] { 100m, 105m, 102m, 108m, 104m, 110m });
        var indicator = new CoreRollingBetaIndicator(5)
        {
            ComparisonSymbol = "SPY"
        };
        // ComparisonSymbol is set but secondary candles not yet loaded
        var result = indicator.Calculate(seriesA);

        Assert.True(result.IsSuccessful);
        Assert.Equal(seriesA.Count, indicator.Values.Count);
        Assert.All(indicator.Values, Assert.Null);
    }

    [Fact]
    public void Calculate_WithMismatchedLengths_AlignsFromEndSoLatestBarsAreEvaluated()
    {
        var closesA = new decimal[] { 100m, 105m, 102m, 108m, 104m, 110m, 107m, 112m }; // 8 bars
        var closesB = new decimal[] { 100m, 105m, 102m, 108m, 104m, 110m }; // 6 bars

        var seriesA = CreateTestCandles(closesA);
        var seriesB = CreateTestCandles(closesB);

        var indicator = new CoreRollingBetaIndicator(5, seriesB);
        var result = indicator.Calculate(seriesA);

        Assert.True(result.IsSuccessful);
        Assert.Equal(closesA.Length, indicator.Values.Count);
        // Oldest bars lacking seriesB data are null
        Assert.Null(indicator.Values[0]);
        Assert.Null(indicator.Values[1]);
        // Latest bars ARE evaluated and never cut off!
        Assert.NotNull(indicator.Values[7]);
    }

    [Fact]
    public void Calculate_SecondaryCandlesViaCrossTickerIndicator()
    {
        var closes = new decimal[] { 100m, 105m, 102m, 108m, 104m, 110m, 107m };
        var seriesA = CreateTestCandles(closes);
        var seriesB = CreateTestCandles(closes);

        var indicator = new CoreRollingBetaIndicator(5);
        indicator.ComparisonSymbol = "SPY";
        indicator.SetSecondaryCandles(seriesB);

        var result = indicator.Calculate(seriesA);

        Assert.True(result.IsSuccessful);
        Assert.NotNull(indicator.Values.Last());
        Assert.Equal(1.0m, indicator.Values.Last()!.Value, 4);
    }

    [Fact]
    public void Configure_WithCoreRollingBetaParameter_SetsProperties()
    {
        var indicator = new CoreRollingBetaIndicator();
        var param = new CoreRollingBetaParameter
        {
            Period = 45,
            ComparisonSymbol = "spy",
            ComparisonPriceSource = PriceType.High,
            CalculationMode = BetaCalculationMode.LogReturn
        };

        indicator.Configure(param);

        Assert.Equal(45, indicator.Period);
        Assert.Equal("SPY", indicator.ComparisonSymbol);
        Assert.Equal(PriceType.High, indicator.ComparisonPriceSource);
        Assert.Equal(BetaCalculationMode.LogReturn, indicator.CalculationMode);
    }

    [Fact]
    public void Configure_WithCoreSmaParameter_SetsPeriodOnly()
    {
        var indicator = new CoreRollingBetaIndicator();
        var param = new CoreSmaParameter { Period = 30 };

        indicator.Configure(param);

        Assert.Equal(30, indicator.Period);
    }
}
